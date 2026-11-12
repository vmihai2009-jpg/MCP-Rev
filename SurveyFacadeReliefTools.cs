// 1.14.2: facade_relief, the facade's stonework from its survey elevation (Survey/FacadeRelief.cs does the faces and
// depths). Mihai: "I want the facade to look like the DWG, columns and all the details."
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        private static readonly string[] ReliefSkip = { "Zz_*", "*Text*", "*Level*", "*Window*", "*Door*", "*Roof*", "*Chimney*", "*Skylight*", "*Sect*",
                                                        "Defpoints", "*Light*", "*Security*", "*Service*", "*StreetFurn*", "*Sign*", "*HVAC*", "*Access*", "*Frame*", "*Border*" };

        public static IEnumerable<RevitTool> FacadeReliefTools()
        {
            yield return new RevitTool
            {
                Name = "facade_profile",
                Write = true,
                Description = "A facade's cornices, bands, balustrade base, parapet and mansard from the survey section that crosses it " +
                              "(section-index.json): the section's outermost line at every 10 mm of height, placed with the section's own " +
                              "fit and level marks, swept along the facade. Below the facade's wall top, what stands proud of the wall face; " +
                              "from the wall top up, the outline with a roof_mm skin (the mansard, Roofs category). Replaces this facade's " +
                              "earlier profile. dry_run (default true) returns the profile. One undo step.",
                InputSchema = Schema(
                    ("facade", S("string", "Facade id (F29...)"), true),
                    ("section", S("string", "Section name (default: the one whose cut crosses the facade nearest its middle)"), false),
                    ("roof_mm", S("number", "Mansard/parapet skin thickness (default 250)"), false),
                    ("dry_run", S("boolean", "Only report (default true)"), false)),
                Run = (app, a) =>
                {
                    bool dry = Args.Bool(a, "dry_run") != false;
                    var doc = dry ? Doc(app) : Writable(app);
                    string fid = Args.Str(a, "facade") ?? throw new ArgumentException("facade is needed.");
                    var (fa, fb) = SavedFacade(doc, fid);
                    double fl = fa.Dist2D(fb), ux = (fb.X - fa.X) / fl, uy = (fb.Y - fa.Y) / fl;
                    var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.Location is LocationCurve).ToList();
                    double cx = walls.Average(w => ((LocationCurve)w.Location).Curve.Evaluate(0.5, true).X) * Mm, cy = walls.Average(w => ((LocationCurve)w.Location).Curve.Evaluate(0.5, true).Y) * Mm;
                    double nx = -uy, ny = ux;
                    if ((cx - (fa.X + ux * fl / 2)) * nx + (cy - (fa.Y + uy * fl / 2)) * ny > 0) { nx = -nx; ny = -ny; }
                    double wallTop = walls.Where(w => { var c = ((LocationCurve)w.Location).Curve.Evaluate(0.5, true); double v = (c.X * Mm - fa.X) * nx + (c.Y * Mm - fa.Y) * ny, uu = (c.X * Mm - fa.X) * ux + (c.Y * Mm - fa.Y) * uy; return Math.Abs(v) < 1500 && uu > -500 && uu < fl + 500; })
                        .Select(w => w.get_BoundingBox(null)?.Max.Z * Mm ?? double.MinValue).DefaultIfEmpty(0).Max();

                    // the section crossing the facade
                    string file = Path.Combine(ReportsFolder(doc), "section-index.json");
                    if (!File.Exists(file)) throw new ArgumentException("No section-index.json: section_index first.");
                    var secs = (JsonNode.Parse(File.ReadAllText(file))["sections"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
                    string want = Args.Str(a, "section");
                    var cands = new List<(JsonObject s, double t, double uAlong, double cos)>();
                    foreach (var s in secs)
                    {
                        if (want != null && !string.Equals(Args.Str(s, "name"), want, StringComparison.OrdinalIgnoreCase)) continue;
                        var c = Args.Numbers(s, "cut"); if (c.Count != 4 || s["offset"] == null) continue;
                        double dx = c[2] - c[0], dy = c[3] - c[1], len = Math.Sqrt(dx * dx + dy * dy);
                        double den = dx * uy - dy * ux; if (Math.Abs(den) < 1e-9) continue;
                        // c0 + t*d = fa + u*f
                        double t = ((fa.X - c[0]) * uy - (fa.Y - c[1]) * ux) / den * len, uAt = ((fa.X - c[0]) * dy - (fa.Y - c[1]) * dx) / den;
                        if (t < -500 || t > len + 500 || uAt < 0 || uAt > fl) continue;
                        double cos = Math.Abs((dx * nx + dy * ny) / len);
                        if (cos < 0.7) continue; // cuts the facade at a slant
                        cands.Add((s, t, uAt, (dx * nx + dy * ny) / len));
                    }
                    if (cands.Count == 0) throw new InvalidOperationException($"No survey section crosses {fid} square enough (section_index lists them).");
                    var pick = cands.OrderBy(x => Math.Abs(x.uAlong - fl / 2)).First();
                    var sec = pick.s;
                    int sdir = (int)(Args.Dbl(sec, "dir") ?? 1); double soff = Args.Dbl(sec, "offset") ?? 0;
                    double sheetX0 = sdir * pick.t + soff;
                    double outSign = sdir * Math.Sign(pick.cos); // sheet x per mm out of the building
                    var xr = Args.Numbers(sec, "sheet_x_range"); var yr = Args.Numbers(sec, "sheet_y_range");
                    var dwg = DwgFile.Read(Args.Str(sec, "path"));
                    bool In(Pt p) => (xr.Count != 2 || p.X >= Math.Min(xr[0], xr[1]) && p.X <= Math.Max(xr[0], xr[1])) && (yr.Count != 2 || p.Y >= Math.Min(yr[0], yr[1]) && p.Y <= Math.Max(yr[0], yr[1]));
                    var lines = dwg.Lines.Where(l => In(l.A) && In(l.B)).ToList();
                    var heights = ElevationFit.Heights(dwg.Texts.Where(tx => In(tx.At) && DwgFile.LayerMatches(tx.Layer, new[] { "*Level*" })),
                                                       lines.Where(l => DwgFile.LayerMatches(l.Layer, new[] { "*Level*" })), xr.Count == 2 ? Math.Min(xr[0], xr[1]) : double.MinValue, xr.Count == 2 ? Math.Max(xr[0], xr[1]) : double.MaxValue);
                    double datumMm = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).Elevation * Mm;
                    var prof = lines.Where(l => DwgFile.LayerMatches(l.Layer, new[] { "*Profile*", "*Wall*" }))
                        .Select(l => (new Pt(outSign * (l.A.X - sheetX0), heights.Height(l.A.Y) - datumMm), new Pt(outSign * (l.B.X - sheetX0), heights.Height(l.B.Y) - datumMm)))
                        .Where(l => Math.Max(l.Item1.X, l.Item2.X) > -3000 && Math.Min(l.Item1.X, l.Item2.X) < 2500).ToList();
                    if (prof.Count == 0) throw new InvalidOperationException($"{Args.Str(sec, "name")}: no profile lines near {fid}.");
                    // the section's own wall face: its fit along the cut can be out by a reveal or more; the face is where it
                    // draws the most vertical wall over the storeys
                    double face = SectionProfile.WallFace(prof, 0, wallTop);
                    prof = prof.Select(l => (new Pt(l.Item1.X - face, l.Item1.Y), new Pt(l.Item2.X - face, l.Item2.Y))).ToList();
                    double z0 = prof.Min(l => Math.Min(l.Item1.Y, l.Item2.Y)), z1 = prof.Max(l => Math.Max(l.Item1.Y, l.Item2.Y));
                    var env = SectionProfile.Envelope(prof, z0, z1);
                    double roofMm = Args.Dbl(a, "roof_mm") ?? 250;
                    var why = new List<string>(); var regions = SectionProfile.Regions(env, z0, 10, wallTop, roofMm, why: why);
                    // cross-check with the elevation: a section cuts one bay, so a part of its profile is swept only where the
                    // elevation draws it as a band (long horizontal lines at its top or bottom); parts it doesn't (a pediment,
                    // a balcony at the cut) stay out. Below ground (vaults, pavement) is left out; the mansard runs the length.
                    var espec = ElevationSpecs(doc, null).FirstOrDefault(s => string.Equals(Args.Str(s, "facade"), fid, StringComparison.OrdinalIgnoreCase));
                    if (espec == null) throw new InvalidOperationException($"{fid} has no survey elevation in elevation-index.json to cross-check the section against; facade_profile only builds where one draws the bands.");
                    var horiz = new List<(double u0, double u1, double z)>();
                    if (espec != null)
                    {
                        Func<IEnumerable<string>, Dictionary<string, string>> rl = null; try { rl = ReadingFor(app, doc, a)?.Roles; } catch { }
                        var efr = ElevationFrame(doc, espec, rl);
                        foreach (var l in efr.Lines.Where(l => !DwgFile.LayerMatches(l.Layer, ReliefSkip) && Math.Abs(l.A.Y - l.B.Y) < 2))
                        {
                            double ua = efr.U(l.A.X), ub = efr.U(l.B.X);
                            if (Math.Abs(ub - ua) >= 200) horiz.Add((Math.Min(ua, ub), Math.Max(ua, ub), efr.Z(l.A.Y)));
                        }
                    }
                    double baseZ = 0;
                    List<(double a, double b)> RunsAt(double zTop, double zBot)
                    {
                        // long horizontal lines anywhere within its height (a cornice's top and bottom edges are often broken by dentils or consoles)
                        var iv = horiz.Where(h => h.z >= zBot - 80 && h.z <= zTop + 80 && h.u1 - h.u0 >= 500).Select(h => (a: Math.Max(0, h.u0), b: Math.Min(fl, h.u1))).Where(x => x.b > x.a).OrderBy(x => x.a).ToList();
                        var runs = new List<(double a, double b)>();
                        foreach (var x in iv) { if (runs.Count > 0 && x.a <= runs[^1].b + 300) runs[^1] = (runs[^1].a, Math.Max(runs[^1].b, x.b)); else runs.Add(x); }
                        return runs.Where(r => r.b - r.a >= 1000).ToList();
                    }
                    var placed = new List<(List<Pt> r, List<(double a, double b)> runs, bool roof)>();
                    foreach (var r in regions)
                    {
                        double zb = r.Min(p => p.Y), zt = r.Max(p => p.Y);
                        bool roof = zb >= wallTop - 1;
                        if (zt <= baseZ + 200 || zb < baseZ - 100) { why.Add($"{zb:0}-{zt:0}: below ground (vaults, area, pavement)"); continue; }
                        if (!roof && r.Max(p => p.X) > 1300) { why.Add($"{zb:0}-{zt:0} ({r.Max(p => p.X):0} out): more than 1.3 m proud, not a facade band (check the section's fit)"); continue; }
                        var runs = roof ? new List<(double a, double b)> { (0, fl) } : RunsAt(zt, zb);
                        if (runs.Count == 0) { why.Add($"{zb:0}-{zt:0} ({r.Max(p => p.X):0} out): not drawn as a band on the elevation"); continue; }
                        placed.Add((r, runs, roof));
                    }
                    var summary = placed.Select(x => new { z = new[] { Math.Round(x.r.Min(p => p.Y)), Math.Round(x.r.Max(p => p.Y)) }, outMm = Math.Round(x.r.Max(p => p.X)), points = x.r.Count, x.roof,
                        runs = x.runs.Select(q => new[] { Math.Round(q.a), Math.Round(q.b) }).ToList(), coverPct = Math.Round(100 * x.runs.Sum(q => q.b - q.a) / fl) }).ToList();

                    var ids = new List<long>(); List<string> revit = null; int made = 0, failed = 0;
                    if (!dry && placed.Count > 0)
                    {
                        string tag = $"MCP|facade_profile|{fid}";
                        revit = BuildTools.Tx.Run(doc, $"MCP: facade profile {fid}", () =>
                        {
                            var old = new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>()
                                .Where(d => (d.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "").StartsWith(tag)).Select(d => d.Id).ToList();
                            if (old.Count > 0) doc.Delete(old);
                            var along = new XYZ(ux, uy, 0); var nrm = new XYZ(nx, ny, 0);
                            XYZ P(Pt q) => new XYZ(fa.X / Mm, fa.Y / Mm, 0) + nrm * (q.X / Mm) + XYZ.BasisZ * (q.Y / Mm);
                            var wallSolids = new List<GeometryObject>(); var roofSolids = new List<GeometryObject>();
                            foreach (var (r, runs, roof) in placed)
                                foreach (var (ra, rb) in runs)
                                    try
                                    {
                                        var start = along * (ra / Mm);
                                        var loop = new CurveLoop();
                                        for (int i = 0; i < r.Count; i++) loop.Append(Line.CreateBound(P(r[i]) + start, P(r[(i + 1) % r.Count]) + start));
                                        var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, along, (rb - ra) / Mm);
                                        (roof ? roofSolids : wallSolids).Add(solid); made++;
                                    }
                                    catch { failed++; }
                            if (wallSolids.Count > 0) ids.Add(Shape(doc, BuiltInCategory.OST_GenericModel, wallSolids, $"Facade profile {fid}", $"{tag}|cornices"));
                            if (roofSolids.Count > 0) ids.Add(Shape(doc, BuiltInCategory.OST_Roofs, roofSolids, $"Mansard {fid}", $"{tag}|mansard"));
                        });
                    }
                    return new
                    {
                        facade = fid, section = Args.Str(sec, "name"), crossesAtMm = Math.Round(pick.uAlong), wallTopMm = Math.Round(wallTop),
                        heights = heights.Note, lines = prof.Count, wallFaceShiftMm = Math.Round(face), rejected = why.Take(15).ToList(), regions = summary, made, failed, shapes = ids, revit,
                        next = dry ? "Check the regions (heights and how far out), then dry_run false." : "check_view facade + view_image; a section view across it shows the profile.",
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "phase_fix",
                Write = true,
                Description = "A survey model is the existing building: every model element should be in the survey's phase (\"Existing\", else " +
                              "the phase most walls are in). Lists the elements created in another phase, by category; dry_run false moves " +
                              "them (elements with a demolished phase are left). Since 1.14.3 every write tool puts what it makes in that phase " +
                              "itself; this is for models built before. One undo step.",
                InputSchema = Schema(("dry_run", S("boolean", "Only list (default true)"), false)),
                Run = (app, a) =>
                {
                    bool dry = Args.Bool(a, "dry_run") != false;
                    var doc = dry ? Doc(app) : Writable(app);
                    var target = PhaseKeeper.SurveyPhase(doc) ?? throw new InvalidOperationException("The model has no phases.");
                    var wrong = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                        .Where(e => PhaseKeeper.IsBuilding(e))
                        .Where(e =>
                        {
                            var p = e.get_Parameter(BuiltInParameter.PHASE_CREATED);
                            var d = e.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                            return p != null && !p.IsReadOnly && p.AsElementId() != target.Id && (d == null || d.AsElementId() == ElementId.InvalidElementId);
                        }).ToList();
                    var list = wrong.GroupBy(e => e.Category.Name).Select(g => new { category = g.Key, count = g.Count(), ids = g.Take(5).Select(e => e.Id.Value).ToList() }).OrderByDescending(x => x.count).ToList();
                    int moved = 0; var notes = new List<string>();
                    if (!dry && wrong.Count > 0) moved = PhaseKeeper.Apply(doc, wrong.Select(e => e.Id), notes);
                    return new { surveyPhase = target.Name, inAnotherPhase = wrong.Count, list, moved, revit = notes.Count > 0 ? notes : null,
                                 next = dry ? $"dry_run: false to put them in {target.Name}." : "save_model." };
                }
            };

            yield return new RevitTool
            {
                Name = "facade_relief",
                Write = true,
                Description = "A facade that looks like its survey elevation, columns and all: the elevation's stonework lines (wall, decorative " +
                              "detail, panel, railing, profile layers) are split where they cross into faces, placed on the facade with the same " +
                              "fit as the windows (elevation-index.json), and each face pushed out from the wall face by its projection: what the " +
                              "storey's plan draws in front of the facade there (columns, pilasters, piers), else by shape (bands 120, tall strips 80, " +
                              "small pieces 50, panels 30 mm). The plain wall and the glass are left out. Built as Generic Model DirectShapes, one " +
                              "per storey band, replacing this facade's earlier relief. dry_run (default true) reports the faces. One undo step.",
                InputSchema = Schema(
                    ("facade", S("string", "Facade id from facade_list / elevation-index.json (F29...)"), true),
                    ("z_range_mm", Arr("number", "Only this height range (model mm)"), false),
                    ("u_range_mm", Arr("number", "Only this stretch along the facade (mm from its start)"), false),
                    ("layers", Arr("string", "Layers to use (default: all but text, levels, windows, doors, roofs, services...)"), false),
                    ("max_face_m2", S("number", "Faces bigger than this are the plain wall (default 4)"), false),
                    ("band_mm", S("number", "Depth of a band with nothing on the plan (default 120)"), false),
                    ("strip_mm", S("number", "Depth of a tall strip (default 80)"), false),
                    ("dry_run", S("boolean", "Only report (default true)"), false)),
                Run = (app, a) =>
                {
                  string stage = "start";
                  try
                  {
                    bool dry = Args.Bool(a, "dry_run") != false;
                    var doc = dry ? Doc(app) : Writable(app);
                    string fid = Args.Str(a, "facade") ?? throw new ArgumentException("facade is needed.");
                    var spec = ElevationSpecs(doc, null).FirstOrDefault(s => string.Equals(Args.Str(s, "facade"), fid, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"No elevation for {fid} in elevation-index.json: elevation_index first.");
                    Func<IEnumerable<string>, Dictionary<string, string>> roles = null;
                    try { roles = ReadingFor(app, doc, a)?.Roles; } catch { }
                    stage = "elevation frame"; var fr = ElevationFrame(doc, spec, roles);
                    var o = new ReliefOptions
                    {
                        MaxFaceM2 = Args.Dbl(a, "max_face_m2") ?? 4, BandDepth = Args.Dbl(a, "band_mm") ?? 120, StripDepth = Args.Dbl(a, "strip_mm") ?? 80,
                    };
                    var zr = Args.Numbers(a, "z_range_mm"); var ur = Args.Numbers(a, "u_range_mm");
                    var layers = Args.Strings(a, "layers");

                    stage = "lines";
                    // the elevation's lines on the facade: U along it, Y = model height
                    var segs = fr.Lines.Where(l => layers.Count > 0 ? DwgFile.LayerMatches(l.Layer, layers) : !DwgFile.LayerMatches(l.Layer, ReliefSkip))
                        .Select(l => new Seg(new Pt(fr.U(l.A.X), fr.Z(l.A.Y)), new Pt(fr.U(l.B.X), fr.Z(l.B.Y)), 0))
                        .Where(s => (zr.Count != 2 || Math.Max(s.A.Y, s.B.Y) >= Math.Min(zr[0], zr[1]) && Math.Min(s.A.Y, s.B.Y) <= Math.Max(zr[0], zr[1]))
                                 && (ur.Count != 2 || Math.Max(s.A.X, s.B.X) >= Math.Min(ur[0], ur[1]) && Math.Min(s.A.X, s.B.X) <= Math.Max(ur[0], ur[1]))
                                 && Math.Max(s.A.X, s.B.X) >= -1000 && Math.Min(s.A.X, s.B.X) <= fr.Length + 1000)
                        .ToList();

                    stage = "outward";
                    // outward: away from the building's walls
                    var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.Location is LocationCurve).ToList();
                    double cx = walls.Average(w => ((LocationCurve)w.Location).Curve.Evaluate(0.5, true).X) * Mm, cy = walls.Average(w => ((LocationCurve)w.Location).Curve.Evaluate(0.5, true).Y) * Mm;
                    double nx = -fr.Uy, ny = fr.Ux;
                    var mid = new Pt(fr.A.X + fr.Ux * fr.Length / 2, fr.A.Y + fr.Uy * fr.Length / 2);
                    if ((cx - mid.X) * nx + (cy - mid.Y) * ny > 0) { nx = -nx; ny = -ny; }

                    stage = "plans";
                    // each storey's plan: how far it draws things in front of the facade
                    var profiles = new List<(double z0, double z1, ProjectionProfile p, string level)>();
                    var floors = PlanFloors(doc, new List<string>()).OrderBy(x => x.level.Elevation).ToList();
                    for (int i = 0; i < floors.Count; i++)
                    {
                        try
                        {
                            var plan = CadLink.Segments(CadLink.Read(doc, floors[i].imp, new ReadStats()), new[] { "EF_25*", "*Wall*", "*Column*" });
                            var prof = FacadeRelief.Profile(plan, fr.A, fr.Ux, fr.Uy, nx, ny, fr.Length);
                            double z0 = floors[i].level.Elevation * Mm, z1 = i + 1 < floors.Count ? floors[i + 1].level.Elevation * Mm : z0 + 4000;
                            profiles.Add((z0, z1, prof, floors[i].level.Name));
                        }
                        catch { }
                    }
                    // a plan is cut about 1 m above its floor: its projection belongs to faces that cross that height
                    ProjectionProfile PlanAt(double fz0, double fz1) => profiles.Where(p => fz0 <= p.z0 + 1000 && fz1 >= p.z0 + 1000).Select(p => p.p).FirstOrDefault();

                    stage = "model wall face";
                    // the model's own outer wall face along the facade, per storey: the relief starts there (where a model
                    // wall stands in front of the facade line, the relief would otherwise be buried in it)
                    var faceProfiles = new List<(double z0, double z1, ProjectionProfile p)>();
                    foreach (var pr in profiles)
                    {
                        var faceSegs = new List<Seg>();
                        foreach (var w in walls)
                        {
                            var bb = w.get_BoundingBox(null);
                            if (bb == null || bb.Max.Z * Mm < pr.z0 + 100 || bb.Min.Z * Mm > pr.z1 - 100) continue;
                            if (!(((LocationCurve)w.Location).Curve is Line wl)) continue;
                            var a0 = CadLink.ToMm(wl.GetEndPoint(0)); var b0 = CadLink.ToMm(wl.GetEndPoint(1));
                            double dx = b0.X - a0.X, dy = b0.Y - a0.Y, len = Math.Sqrt(dx * dx + dy * dy);
                            if (len < 1) continue;
                            double px = -dy / len, py = dx / len, half = w.Width * Mm / 2;
                            if (px * nx + py * ny < 0) { px = -px; py = -py; }   // the wall's outward side
                            faceSegs.Add(new Seg(new Pt(a0.X + px * half, a0.Y + py * half), new Pt(b0.X + px * half, b0.Y + py * half), 0));
                        }
                        faceProfiles.Add((pr.z0, pr.z1, FacadeRelief.Profile(faceSegs, fr.A, fr.Ux, fr.Uy, nx, ny, fr.Length, 25, 5, 1500)));
                    }
                    ProjectionProfile FaceAt(double z) => faceProfiles.FirstOrDefault(p => z >= p.z0 && z < p.z1).p;

                    stage = "windows";
                    // the glass: the model's windows on this facade
                    var windows = new List<(double u0, double u1, double z0, double z1)>();
                    foreach (var fi in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Windows).WhereElementIsNotElementType().OfType<FamilyInstance>())
                    {
                        if (!(fi.Location is LocationPoint lp)) continue;
                        var p = CadLink.ToMm(lp.Point);
                        double u = (p.X - fr.A.X) * fr.Ux + (p.Y - fr.A.Y) * fr.Uy, off = Math.Abs((p.X - fr.A.X) * nx + (p.Y - fr.A.Y) * ny);
                        if (off > 800 || u < -500 || u > fr.Length + 500) continue;
                        double w = WidthOf(fi), lz = (doc.GetElement(fi.LevelId) as Level)?.Elevation * Mm ?? 0;
                        double sill = LenOf(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM) ?? 0, head = LenOf(fi, BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM) ?? sill;
                        windows.Add((u - w / 2, u + w / 2, lz + sill, lz + head));
                    }

                    stage = "faces"; var faces = FacadeRelief.Build(segs, windows, PlanAt, o);
                    // above the facade's walls the roof slopes back (mansard, chimneys): those lines are not on the facade plane;
                    double wallTop = walls.Where(w => { var c = ((LocationCurve)w.Location).Curve.Evaluate(0.5, true); double v = (c.X * Mm - fr.A.X) * nx + (c.Y * Mm - fr.A.Y) * ny, uu = (c.X * Mm - fr.A.X) * fr.Ux + (c.Y * Mm - fr.A.Y) * fr.Uy; return Math.Abs(v) < 1500 && uu > -500 && uu < fr.Length + 500; })
                        .Select(w => w.get_BoundingBox(null)?.Max.Z * Mm ?? double.MinValue).DefaultIfEmpty(double.MaxValue).Max();
                    // dormers rise up to ~1.8 m above the wall top and are kept; chimney pots and roof edges above that are not
                    int aboveRoof = faces.RemoveAll(f => f.Z0 > wallTop + 1800);
                    stage = "report";
                    var bySource = faces.GroupBy(f => f.Source).ToDictionary(g => g.Key, g => new { faces = g.Count(), m2 = Math.Round(g.Sum(f => f.AreaM2), 1), depthMm = new[] { Math.Round(g.Min(f => f.Depth)), Math.Round(g.Max(f => f.Depth)) } });

                    int made = 0, failed = 0, onWall = 0; var ids = new List<long>(); List<string> revit = null; int removed = 0;
                    if (!dry && faces.Count > 0)
                    {
                        string tag = $"MCP|facade_relief|{fid}";
                        revit = BuildTools.Tx.Run(doc, $"MCP: facade relief {fid}", () =>
                        {
                            var old = new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>()
                                .Where(d => (d.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "").StartsWith(tag)
                                            && (zr.Count != 2 && ur.Count != 2)).Select(d => d.Id).ToList();
                            if (old.Count > 0) { doc.Delete(old); removed = old.Count; }
                            var nrm = new XYZ(nx, ny, 0);
                            XYZ P(Pt q) => new XYZ((fr.A.X + fr.Ux * q.X) / Mm, (fr.A.Y + fr.Uy * q.X) / Mm, q.Y / Mm);
                            // one shape per storey band, so a storey can be hidden or rebuilt on its own
                            foreach (var grp in faces.GroupBy(f => profiles.FirstOrDefault(p => (f.Z0 + f.Z1) / 2 >= p.z0 && (f.Z0 + f.Z1) / 2 < p.z1).level ?? "other"))
                            {
                                var solids = new List<GeometryObject>();
                                foreach (var f in grp)
                                {
                                    try
                                    {
                                        var pts = new List<Pt>();
                                        foreach (var q in f.Ring) if (pts.Count == 0 || q.Dist2D(pts[^1]) >= 2) pts.Add(q);
                                        while (pts.Count > 3 && pts[^1].Dist2D(pts[0]) < 2) pts.RemoveAt(pts.Count - 1);
                                        if (pts.Count < 3) { failed++; continue; }
                                        // from the model's wall face there, out to the plan's front (or the face's own depth beyond the wall)
                                        double zc = (f.Z0 + f.Z1) / 2;
                                        // the outermost model wall face over every storey the face spans (a column runs through several)
                                        double wallV = faceProfiles.Where(p => p.z1 > f.Z0 + 50 && p.z0 < f.Z1 - 50).Select(p => p.p.Over(f.U0, f.U1)).DefaultIfEmpty(FaceAt(zc)?.Over(f.U0, f.U1) ?? 0).Max();
                                        double front = f.Source == "plan" ? Math.Max(f.Depth, wallV + 20) : wallV + f.Depth;
                                        var off = nrm * (wallV / Mm);
                                        var loop = new CurveLoop();
                                        for (int i = 0; i < pts.Count; i++) loop.Append(Line.CreateBound(P(pts[i]) + off, P(pts[(i + 1) % pts.Count]) + off));
                                        solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, nrm, (front - wallV) / Mm));
                                        if (wallV > 5) onWall++;
                                        made++;
                                    }
                                    catch { failed++; }
                                }
                                if (solids.Count > 0) ids.Add(Shape(doc, BuiltInCategory.OST_GenericModel, solids, $"Facade relief {fid} {grp.Key}", $"{tag}|{grp.Key}"));
                            }
                        });
                    }
                    return new
                    {
                        facade = fid, elevation = Args.Str(spec, "name"), lines = segs.Count, faces = faces.Count, bySource,
                        storeysWithPlan = profiles.Select(p => new { p.level, projectsMm = Math.Round(p.p.Depth.Max()) }).ToList(),
                        windowsLeftOut = windows.Count, aboveWallTop = aboveRoof, wallTopMm = Math.Round(wallTop), made, failed, facesOnWallsInFront = onWall, removed, shapes = ids, revit,
                        next = dry ? "dry_run: false to build it (check_view + view_image to compare with the elevation)." : "check_view facade + view_image, side by side with the elevation.",
                    };
                  }
                  catch (Exception ex) when (!(ex is ArgumentException))
                  { throw new InvalidOperationException($"facade_relief at {stage}: {ex.Message} {ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains(".cs:"))?.Trim()}", ex); }
                }
            };
        }

        /// <summary>An elevation drawing put on its facade: its lines, and sheet -> facade (U along, model height) mappings.</summary>
        internal class ElevFrame
        {
            public List<DwgLine> Lines;
            public Pt A; public double Ux, Uy, Length;
            public Func<double, double> U, Z;
        }

        internal static ElevFrame ElevationFrame(Document doc, JsonObject spec, Func<IEnumerable<string>, Dictionary<string, string>> roles)
        {
            string path = Args.Str(spec, "path") ?? throw new ArgumentException("an elevation needs path");
            var xr = Args.Numbers(spec, "sheet_x_range");
            double xMin = Math.Min(xr[0], xr[1]), xMax = Math.Max(xr[0], xr[1]);
            var yr = Args.Numbers(spec, "sheet_y_range");
            double yMin = yr.Count == 2 ? Math.Min(yr[0], yr[1]) : double.MinValue, yMax = yr.Count == 2 ? Math.Max(yr[0], yr[1]) : double.MaxValue;
            bool InSheet(Pt p) => p.X >= xMin && p.X <= xMax && p.Y >= yMin && p.Y <= yMax;
            var (fa, fb) = FacadeLine(doc, spec);
            double fl = fa.Dist2D(fb), ux = (fb.X - fa.X) / fl, uy = (fb.Y - fa.Y) / fl;
            var file = DwgFile.Read(path);
            var lines = file.Lines.Where(l => InSheet(l.A) && InSheet(l.B)).ToList();
            var role = roles?.Invoke(file.Lines.Select(l => l.Layer).Concat(file.Texts.Select(t => t.Layer)));
            bool Is(string layer, string[] names, string[] globs) => role != null && layer != null && role.TryGetValue(layer, out var r) && names.Contains(r) || DwgFile.LayerMatches(layer, globs);
            string[] winRole = { "Window" }, levelRole = { "LevelText", "HeightText" };
            var winLines = lines.Where(l => Is(l.Layer, winRole, new[] { "*Window*" })).ToList();
            var heights = ElevationFit.Heights(file.Texts.Where(t => Is(t.Layer, levelRole, new[] { "*Level*" }) && t.At.Y >= yMin && t.At.Y <= yMax),
                                               lines.Where(l => Is(l.Layer, levelRole, new[] { "*Level*" })), xMin, xMax);
            double datumMm = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).Elevation * Mm;
            var inserts = new List<double>();
            foreach (var cat in new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors })
                foreach (var fi in new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().OfType<FamilyInstance>())
                {
                    if (!(fi.Location is LocationPoint lp)) continue;
                    var p = CadLink.ToMm(lp.Point);
                    double u = (p.X - fa.X) * ux + (p.Y - fa.Y) * uy, off = Math.Abs((p.X - fa.X) * -uy + (p.Y - fa.Y) * ux);
                    if (off > 800 || u < -500 || u > fl + 500) continue;
                    double w = WidthOf(fi); if (w <= 0) continue;
                    inserts.Add(u - w / 2); inserts.Add(u + w / 2);
                }
            int? dir = Args.Dbl(spec, "along_direction") is double dg ? (dg < 0 ? -1 : 1) : 1;
            var along = ElevationFit.Along(ElevationFit.Jambs(winLines), inserts, heights.A, 60, dir);
            return new ElevFrame
            {
                Lines = lines, A = fa, Ux = ux, Uy = uy, Length = fl,
                U = x => along.Along(x * heights.A), Z = y => heights.Height(y) - datumMm,
            };
        }
    }
}
