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
                Name = "facade_columns",
                Write = true,
                Description = "Round columns on a facade, as a hand model builds them (tapered fluted shaft, revolved base and capital), from the " +
                              "survey: each column's axis and radius from the storey plans (an arc in front of the facade line), its silhouette " +
                              "from the facade's elevation (a round thing's outline is its profile): base and capital revolved from it, the shaft " +
                              "a blend between fluted sections at its bottom and top radii (flutes 24 by default). facade_relief leaves out the " +
                              "faces these replace. Replaces this facade's earlier columns. dry_run (default true) lists them. One undo step.",
                InputSchema = Schema(
                    ("facade", S("string", "Facade id (F29...)"), true),
                    ("flutes", S("integer", "Flutes round the shaft (default 24; 0 = plain)"), false),
                    ("dry_run", S("boolean", "Only list (default true)"), false)),
                Run = (app, a) =>
                {
                    bool dry = Args.Bool(a, "dry_run") != false;
                    var doc = dry ? Doc(app) : Writable(app);
                    string fid = Args.Str(a, "facade") ?? throw new ArgumentException("facade is needed.");
                    int flutes = Math.Max(0, Args.Int(a, "flutes", 24));
                    var spec = ElevationSpecs(doc, null).FirstOrDefault(s => string.Equals(Args.Str(s, "facade"), fid, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"No elevation for {fid} in elevation-index.json.");
                    Func<IEnumerable<string>, Dictionary<string, string>> roles = null; try { roles = ReadingFor(app, doc, a)?.Roles; } catch { }
                    var fr = ElevationFrame(doc, spec, roles);
                    var (nx, ny, wallTop) = FacadeSide(doc, fr);
                    var segs = fr.Lines.Where(l => !DwgFile.LayerMatches(l.Layer, ReliefSkip))
                        .Select(l => new Seg(new Pt(fr.U(l.A.X), fr.Z(l.A.Y)), new Pt(fr.U(l.B.X), fr.Z(l.B.Y)), 0)).ToList();
                    var cols = ColumnsFor(doc, fr, nx, ny, segs, wallTop);
                    PieceLog = new List<string>(); var pieces = PiecesFor(doc, fr, nx, ny, segs); var pieceLog = PieceLog; PieceLog = null;
                    var list = cols.Select(c => { var (zb, zt) = ColumnEnds(c); return new { alongMm = Math.Round(c.U), outMm = Math.Round(c.V), radiusMm = Math.Round(c.Radius), baseMm = Math.Round(zb), shaft = new[] { Math.Round(c.ShaftZ0), Math.Round(c.ShaftZ1) }, shaftRadius = new[] { Math.Round(c.ShaftR0), Math.Round(c.ShaftR1) }, topMm = Math.Round(zt), plans = c.Level }; }).ToList();

                    var ids = new List<long>(); List<string> revit = null; int made = 0, failed = 0; var problems = new List<string>();
                    if (!dry && (cols.Count > 0 || pieces.Count > 0))
                    {
                        string tag = $"MCP|facade_columns|{fid}";
                        revit = BuildTools.Tx.Run(doc, $"MCP: facade columns {fid}", () =>
                        {
                            var old = new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>()
                                .Where(d => (d.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "").StartsWith(tag)).Select(d => d.Id).ToList();
                            if (old.Count > 0) doc.Delete(old);
                            foreach (var c in cols)
                            {
                                var (zb, zt) = ColumnEnds(c);
                                var axis = new XYZ((fr.A.X + fr.Ux * c.U + nx * c.V) / Mm, (fr.A.Y + fr.Uy * c.U + ny * c.V) / Mm, 0);
                                var solids = new List<GeometryObject>();
                                // base and capital: the silhouette revolved about the axis (capital capped at 1.3 R: the volutes stay relief)
                                foreach (var (z0, z1, cap) in new[] { (zb, c.ShaftZ0, 1.6), (c.ShaftZ1, zt, 1.3) })
                                {
                                    if (z1 - z0 < 30) continue;
                                    try { solids.Add(Revolved(c, axis, z0, z1, cap * c.Radius)); }
                                    catch (Exception ex) { problems.Add($"column at {c.U:0}: {(z0 == zb ? "base" : "capital")}: {McpServer.Unwrap(ex).Message}"); }
                                }
                                // shaft: a blend between fluted sections (the taper from the silhouette)
                                try { solids.Add(Shaft(axis, c.ShaftZ0, c.ShaftZ1, c.ShaftR0, c.ShaftR1, flutes)); }
                                catch (Exception ex) { problems.Add($"column at {c.U:0}: shaft: {McpServer.Unwrap(ex).Message}"); }
                                if (solids.Count == 0) { failed++; continue; }
                                ids.Add(Shape(doc, BuiltInCategory.OST_Columns, solids, $"Column {fid} {c.U:0}", $"{tag}|{c.U:0}"));
                                made++;
                            }
                            // plan pieces: the plan's own section (pilaster flutes, piers, nibs) extruded between the heights the elevation draws its edges
                            var pieceSolids = new List<GeometryObject>();
                            foreach (var (ring, u0, u1, z0, z1, lv) in pieces)
                                try
                                {
                                    var pts = new List<Pt>(); foreach (var q in ring) if (pts.Count == 0 || q.Dist2D(pts[^1]) >= 2) pts.Add(q);
                                    while (pts.Count > 3 && pts[^1].Dist2D(pts[0]) < 2) pts.RemoveAt(pts.Count - 1);
                                    XYZ W(Pt q) => new XYZ((fr.A.X + fr.Ux * q.X + nx * q.Y) / Mm, (fr.A.Y + fr.Uy * q.X + ny * q.Y) / Mm, z0 / Mm);
                                    var loop = new CurveLoop();
                                    for (int i = 0; i < pts.Count; i++) loop.Append(Line.CreateBound(W(pts[i]), W(pts[(i + 1) % pts.Count])));
                                    pieceSolids.Add(GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, (z1 - z0) / Mm));
                                    made++;
                                }
                                catch (Exception ex) { failed++; problems.Add($"piece {u0:0}-{u1:0}: {McpServer.Unwrap(ex).Message}"); }
                            if (pieceSolids.Count > 0) ids.Add(Shape(doc, BuiltInCategory.OST_GenericModel, pieceSolids, $"Facade pilasters and piers {fid}", $"{tag}|pieces"));
                        });
                    }
                    return new { facade = fid, columns = list, planPieces = pieces.Count, candidates = pieceLog, pieceList = pieces.Take(40).Select(x => new { along = new[] { Math.Round(x.u0), Math.Round(x.u1) }, outMm = Math.Round(x.ring.Max(p => p.Y)), z = new[] { Math.Round(x.z0), Math.Round(x.z1) }, points = x.ring.Count, x.level }).ToList(), made, failed, problems = problems.Count > 0 ? problems : null, shapes = ids, revit,
                                 next = dry ? "dry_run: false to build them; then facade_relief again (it leaves out what they replace)." : "facade_relief dry_run:false (to drop the faces the columns replace), then check_view + view_image." };
                }
            };

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
                    ("as", S("string", "sweeps (default: profile families + native wall sweeps, editable) or shapes (DirectShapes)"), false),
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
                    bool asSweeps = !string.Equals(Args.Str(a, "as"), "shapes", StringComparison.OrdinalIgnoreCase);
                    var sweepLog = new List<object>();
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
                            foreach (var (r, runs, roof) in placed.Where(x => !asSweeps || x.roof))
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
                        // a wall sweep runs a whole wall: only continuous bands become sweeps (drawn along >= 50% and <= 1 m tall, or >= 90%);
                        // local pieces at the section's bay (pediments, balconies) stay relief until they have their own families
                        bool Continuous((List<Pt> r, List<(double a, double b)> runs, bool roof) x) { double cover = x.runs.Sum(q => q.b - q.a) / fl, h = x.r.Max(p => p.Y) - x.r.Min(p => p.Y); return cover >= 0.9 || cover >= 0.5 && h <= 1000; }
                        if (asSweeps) revit.AddRange(BuildSweeps(app, doc, fid, placed.Where(x => !x.roof && Continuous(x)).Select(x => (x.r, x.runs)).ToList(), fa, ux, uy, nx, ny, sweepLog, ref made, ref failed));
                    }
                    return new
                    {
                        facade = fid, section = Args.Str(sec, "name"), crossesAtMm = Math.Round(pick.uAlong), wallTopMm = Math.Round(wallTop),
                        heights = heights.Note, lines = prof.Count, wallFaceShiftMm = Math.Round(face), rejected = why.Take(15).ToList(), regions = summary, made, failed, shapes = ids,
                        sweeps = sweepLog.Count > 0 ? sweepLog : null, revit,
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
                    ("columns", S("boolean", "Leave out the faces round columns replace (facade_columns; default true)"), false),
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
                    // the bands facade_profile made as wall sweeps (types "BG F.. z"): their long band faces are left out here
                    // (only those: pediments, dentils and consoles in the same heights stay)
                    int bySweeps = 0;
                    foreach (var sw in new FilteredElementCollector(doc).OfClass(typeof(WallSweep)).Where(e => (doc.GetElement(e.GetTypeId())?.Name ?? "").StartsWith($"BG {fid} ")))
                    {
                        var bb = sw.get_BoundingBox(null); if (bb == null) continue;
                        var us = new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Max.Y, 0), new XYZ(bb.Max.X, bb.Min.Y, 0) }.Select(p => (p.X * Mm - fr.A.X) * fr.Ux + (p.Y * Mm - fr.A.Y) * fr.Uy).ToList();
                        double su0 = us.Min(), su1 = us.Max(), sz0 = bb.Min.Z * Mm, sz1 = bb.Max.Z * Mm;
                        bySweeps += faces.RemoveAll(f => f.Source == "band" && f.U1 - f.U0 >= 600 && (f.U0 + f.U1) / 2 > su0 && (f.U0 + f.U1) / 2 < su1 && f.Z0 >= sz0 - 30 && f.Z1 <= sz1 + 30);
                    }
                    // round columns (facade_columns builds them): their base and shaft faces are left out here; the capital's stay (volutes)
                    int byColumns = 0;
                    if (Args.Bool(a, "columns") != false)
                        try
                        {
                            foreach (var pc in PiecesFor(doc, fr, nx, ny, segs))
                                byColumns += faces.RemoveAll(f => (f.U0 + f.U1) / 2 > pc.u0 && (f.U0 + f.U1) / 2 < pc.u1 && (f.Z0 + f.Z1) / 2 > pc.z0 && (f.Z0 + f.Z1) / 2 < pc.z1 && f.Source == "plan");
                            foreach (var col in ColumnsFor(doc, fr, nx, ny, segs, wallTop))
                            {
                                var (zb, _) = ColumnEnds(col);
                                byColumns += faces.RemoveAll(f => (f.U0 + f.U1) / 2 > col.U - col.Radius * 1.15 && (f.U0 + f.U1) / 2 < col.U + col.Radius * 1.15
                                                                   && (f.Z0 + f.Z1) / 2 > zb && (f.Z0 + f.Z1) / 2 < col.ShaftZ1);
                            }
                        }
                        catch { }
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
                        windowsLeftOut = windows.Count, aboveWallTop = aboveRoof, leftForColumns = byColumns, leftForSweeps = bySweeps, wallTopMm = Math.Round(wallTop), made, failed, facesOnWallsInFront = onWall, removed, shapes = ids, revit,
                        next = dry ? "dry_run: false to build it (check_view + view_image to compare with the elevation)." : "check_view facade + view_image, side by side with the elevation.",
                    };
                  }
                  catch (Exception ex) when (!(ex is ArgumentException))
                  { throw new InvalidOperationException($"facade_relief at {stage}: {ex.Message} {ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains(".cs:"))?.Trim()}", ex); }
                }
            };
        }

        /// <summary>
        /// A facade's round columns: axes and radii from the storey plans (arcs in front of the facade line), silhouettes
        /// from its elevation (segs: the elevation's lines, U along, Y height), shaft found where the radius holds.
        /// </summary>
        internal static List<FacadeColumn> ColumnsFor(Document doc, ElevFrame fr, double nx, double ny, IList<Seg> segs, double wallTop)
        {
            var found = new List<(double u, double v, double r, string level)>();
            foreach (var (level, imp) in PlanFloors(doc, new List<string>()))
            {
                try
                {
                    var plan = CadLink.Segments(CadLink.Read(doc, imp, new ReadStats()), new[] { "EF_25*", "*Wall*", "*Column*" });
                    foreach (var c in FacadeColumns.FromPlan(plan, fr.A, fr.Ux, fr.Uy, nx, ny, fr.Length)) found.Add((c.u, c.v, c.r, level.Name));
                }
                catch { }
            }
            // one column per axis (it is drawn on every storey it passes)
            var cols = new List<FacadeColumn>();
            foreach (var g in found.OrderBy(f => f.u).GroupBy(f => Math.Round(f.u / 300)))
            {
                var list = g.ToList();
                double R = list.Select(x => x.r).OrderBy(x => x).ElementAt(list.Count / 2);
                var c = new FacadeColumn { U = list.Average(x => x.u), V = list.Average(x => x.v), Radius = R, Level = string.Join(",", list.Select(x => x.level).Distinct()), Z0 = 0 };
                c.Profile = FacadeColumns.Silhouette(segs, c.U, R, 0, wallTop + 2000);
                FacadeColumns.Shaft(c);
                if (c.ShaftZ1 - c.ShaftZ0 < 1500) continue; // not a column the elevation draws
                cols.Add(c);
            }
            return cols;
        }

        /// <summary>
        /// What the storey plans draw standing in front of the facade (pilasters with their flutes, piers, engaged
        /// columns, surrounds' nibs), each as its plan outline with the heights its elevation draws its edges between.
        /// One piece per element (the same one cut on several storeys is kept once, from the plan that cuts it lowest).
        /// </summary>
        [ThreadStatic] internal static List<string> PieceLog;
        internal static List<(List<Pt> ring, double u0, double u1, double z0, double z1, string level)> PiecesFor(Document doc, ElevFrame fr, double nx, double ny, IList<Seg> segs)
        {
            var all = new List<(List<Pt> ring, double u0, double u1, double z0, double z1, string level)>();
            foreach (var (level, imp) in PlanFloors(doc, new List<string>()).OrderBy(x => x.level.Elevation))
            {
                try
                {
                    var plan = CadLink.Segments(CadLink.Read(doc, imp, new ReadStats()), new[] { "EF_25*", "*Wall*", "*Column*" });
                    double cut = level.Elevation * Mm + 1000;
                    foreach (var ring in FacadeColumns.PlanPieces(plan, fr.A, fr.Ux, fr.Uy, nx, ny, fr.Length))
                    {
                        double u0 = ring.Min(p => p.X), u1 = ring.Max(p => p.X);
                        // the shaft's edges are its front face's extent (the outline's ends include the wall nibs either side)
                        double dmax = ring.Max(p => p.Y);
                        var front = ring.Where(p => p.Y >= 0.6 * dmax).ToList();
                        var run = FacadeColumns.EdgeRun(segs, front.Min(p => p.X), front.Max(p => p.X), cut, 40, 400) ?? FacadeColumns.EdgeRun(segs, u0, u1, cut, 40, 400);
                        PieceLog?.Add($"{level.Name}: {u0:0}-{u1:0} out {dmax:0}, {ring.Count} pts, front {front.Min(p => p.X):0}-{front.Max(p => p.X):0}: " + (run == null ? "no edges on the elevation at the cut" : $"{run.Value.z0:0}-{run.Value.z1:0}"));
                        if (run == null) continue;
                        if (all.Any(x => Math.Min(x.u1, u1) - Math.Max(x.u0, u0) > 0.7 * (u1 - u0) && Math.Min(x.z1, run.Value.z1) - Math.Max(x.z0, run.Value.z0) > 300)) continue;
                        all.Add((ring, u0, u1, run.Value.z0, run.Value.z1, level.Name));
                    }
                }
                catch { }
            }
            return all;
        }

        /// <summary>
        /// Editable bands: each surveyed profile becomes a Profile family (reports\profiles), a duplicated wall sweep type
        /// "BG F.. z" and native wall sweeps on the facade's walls (facades.json) whose middle lies on a run the elevation
        /// draws and whose height holds the band, at the band's height above the wall's base. Re-running replaces them.
        /// </summary>
        internal static List<string> BuildSweeps(Autodesk.Revit.UI.UIApplication app, Document doc, string fid, List<(List<Pt> r, List<(double a, double b)> runs)> bands,
                                                 Pt fa, double ux, double uy, double nx, double ny, List<object> log, ref int made, ref int failed)
        {
            var notes = new List<string>();
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(ReportsFolder(doc), "facades.json")));
            var wallIds = root["facades"].AsArray().OfType<JsonObject>().FirstOrDefault(f => string.Equals(f["id"]?.GetValue<string>(), fid, StringComparison.OrdinalIgnoreCase))?["wallIds"]?.AsArray()
                .Select(v => v.GetValue<long>()).ToList() ?? new List<long>();
            string folder = Path.Combine(ReportsFolder(doc), "profiles");
            var files = new List<(string name, string path, double zb, List<(double a, double b)> runs, double outMm)>();
            foreach (var (r, runs) in bands)
            {
                double zb = r.Min(p => p.Y);
                string name = $"BG profile {fid} {zb:0}";
                try { files.Add((name, ProfileFamilyBuilder.Build(app, r.Select(p => new Pt(p.X, p.Y - zb)).ToList(), Path.Combine(folder, name + ".rfa")), zb, runs, r.Max(p => p.X))); }
                catch (Exception ex) { failed++; notes.Add($"{name}: {McpServer.Unwrap(ex).Message}"); }
            }
            int madeHere = 0, failedHere = 0;
            notes.AddRange(BuildTools.Tx.Run(doc, $"MCP: facade sweeps {fid}", () =>
            {
                string prefix = $"BG {fid} ";
                var old = new FilteredElementCollector(doc).OfClass(typeof(WallSweep)).Where(e => (doc.GetElement(e.GetTypeId())?.Name ?? "").StartsWith(prefix)).Select(e => e.Id).ToList();
                if (old.Count > 0) doc.Delete(old);
                var baseType = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Cornices).WhereElementIsElementType().Cast<ElementType>().FirstOrDefault()
                    ?? throw new InvalidOperationException("The model has no wall sweep type to start from.");
                var nrm = new XYZ(nx, ny, 0);
                bool? flip = null;
                foreach (var f in files)
                {
                    if (!doc.LoadFamily(f.path, new Overwrite(), out Family fam))
                        fam = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(x => x.Name == f.name);
                    var prof = fam?.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>().FirstOrDefault();
                    if (prof == null) { failedHere++; notes.Add($"{f.name}: did not load"); continue; }
                    if (!prof.IsActive) prof.Activate();
                    string tname = $"{prefix}{f.zb:0}";
                    var stype = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Cornices).WhereElementIsElementType().Cast<ElementType>().FirstOrDefault(x => x.Name == tname)
                                ?? baseType.Duplicate(tname);
                    // the type carries the profile (the one passed with a new sweep is not kept)
                    stype.get_Parameter(BuiltInParameter.WALL_SWEEP_PROFILE_PARAM)?.Set(prof.Id);
                    int onWalls = 0;
                    foreach (var wid in wallIds)
                    {
                        if (!(doc.GetElement(new ElementId(wid)) is Wall w) || !(w.Location is LocationCurve lc) || !WallSweep.WallAllowsWallSweep(w)) continue;
                        var mid = lc.Curve.Evaluate(0.5, true);
                        double um = (mid.X * Mm - fa.X) * ux + (mid.Y * Mm - fa.Y) * uy;
                        if (!f.runs.Any(q => um >= q.a && um <= q.b)) continue;
                        double wb = ((doc.GetElement(w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() ?? ElementId.InvalidElementId) as Level)?.Elevation ?? 0) * Mm
                                    + (w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0) * Mm;
                        double wt = (w.get_BoundingBox(null)?.Max.Z ?? 0) * Mm;
                        if (f.zb < wb || f.zb >= wt) continue;
                        // the wall's outside face against the facade line: the profile is measured from the line, so the
                        // sweep is offset out (or in) to it; a wall far behind the line is not this facade's
                        double faceV = (mid.X * Mm - fa.X) * nx + (mid.Y * Mm - fa.Y) * ny + w.Width * Mm / 2;
                        if (Math.Abs(faceV) > 1500) continue;
                        try
                        {
                            WallSweep Make(bool flipped, double offset, WallSide side)
                            {
                                var info = new WallSweepInfo(WallSweepType.Sweep, false)
                                {
                                    ProfileId = prof.Id, Distance = (f.zb - wb) / Mm, DistanceMeasuredFrom = DistanceMeasuredFrom.Base,
                                    WallSide = side, WallOffset = offset / Mm, IsProfileFlipped = flipped,
                                };
                                return WallSweep.Create(w, stype.Id, info);
                            }
                            // which side, which way and how far out depends on how the wall was drawn: try them and keep the one
                            // that runs from the facade line out to the profile's projection, as the section draws it
                            double Miss(WallSweep s)
                            {
                                doc.Regenerate();
                                var bb = s.get_BoundingBox(null);
                                if (bb == null) return double.MaxValue;
                                var vs = new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Max.Y, 0), new XYZ(bb.Max.X, bb.Min.Y, 0) }
                                    .Select(p => (p.X * Mm - fa.X) * nx + (p.Y * Mm - fa.Y) * ny).ToList();
                                return Math.Abs(vs.Min()) + Math.Abs(vs.Max() - f.outMm);
                            }
                            (bool flip, double off, WallSide side)? best = null; double bestMiss = double.MaxValue;
                            foreach (var side in new[] { WallSide.Exterior, WallSide.Interior })
                            {
                                foreach (var off in new[] { -faceV, faceV, 0.0 }.Distinct())
                                {
                                    foreach (var fl in new[] { false, true })
                                    {
                                        try
                                        {
                                            var trial = Make(fl, off, side);
                                            double miss = Miss(trial);
                                            doc.Delete(trial.Id);
                                            if (miss < bestMiss) { bestMiss = miss; best = (fl, off, side); }
                                        }
                                        catch { }
                                        if (bestMiss < 30) break;
                                    }
                                    if (bestMiss < 30) break;
                                }
                                if (bestMiss < 30) break;
                            }
                            if (best == null) throw new InvalidOperationException("no placement of the sweep worked");
                            Make(best.Value.flip, best.Value.off, best.Value.side);
                            if (bestMiss > 150) notes.Add($"wall {wid}, {tname}: best placement still {bestMiss:0} mm off the section's");
                            onWalls++; madeHere++;
                        }
                        catch (Exception ex) { failedHere++; notes.Add($"wall {wid}, {tname}: {McpServer.Unwrap(ex).Message}"); }
                    }
                    log.Add(new { profile = f.name, type = tname, heightMm = Math.Round(f.zb), outMm = Math.Round(f.outMm), walls = onWalls });
                }
            }));
            made += madeHere; failed += failedHere;
            return notes;
        }

        /// <summary>The facade's outward normal (away from the building's walls) and the top of its walls (mm).</summary>
        internal static (double nx, double ny, double wallTop) FacadeSide(Document doc, ElevFrame fr)
        {
            var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.Location is LocationCurve).ToList();
            double cx = walls.Average(w => ((LocationCurve)w.Location).Curve.Evaluate(0.5, true).X) * Mm, cy = walls.Average(w => ((LocationCurve)w.Location).Curve.Evaluate(0.5, true).Y) * Mm;
            double nx = -fr.Uy, ny = fr.Ux;
            if ((cx - (fr.A.X + fr.Ux * fr.Length / 2)) * nx + (cy - (fr.A.Y + fr.Uy * fr.Length / 2)) * ny > 0) { nx = -nx; ny = -ny; }
            double top = walls.Where(w => { var c = ((LocationCurve)w.Location).Curve.Evaluate(0.5, true); double v = (c.X * Mm - fr.A.X) * nx + (c.Y * Mm - fr.A.Y) * ny, uu = (c.X * Mm - fr.A.X) * fr.Ux + (c.Y * Mm - fr.A.Y) * fr.Uy; return Math.Abs(v) < 1500 && uu > -500 && uu < fr.Length + 500; })
                .Select(w => w.get_BoundingBox(null)?.Max.Z * Mm ?? double.MinValue).DefaultIfEmpty(0).Max();
            return (nx, ny, top);
        }

        /// <summary>The column's silhouette between two heights revolved about its axis (radius capped at maxR).</summary>
        internal static Solid Revolved(FacadeColumn c, XYZ axis, double z0, double z1, double maxR)
        {
            var pts = new List<Pt>();
            double last = c.Radius;
            for (double z = z0; z <= z1 + 1e-6; z += c.Step)
            {
                int i = (int)Math.Round((z - c.Z0) / c.Step);
                double r = i >= 0 && i < c.Profile.Length && !double.IsNaN(c.Profile[i]) ? c.Profile[i] : last;
                r = Math.Clamp(r, 20, maxR); last = r;
                pts.Add(new Pt(r, z));
            }
            var ring = new List<Pt>(pts) { new Pt(0, z1), new Pt(0, z0) };
            ring = SectionProfile.Simplify(ring, 3);
            var loop = new CurveLoop();
            XYZ P(Pt q) => new XYZ(axis.X + q.X / Mm, axis.Y, q.Y / Mm);
            for (int i = 0; i < ring.Count; i++) loop.Append(Line.CreateBound(P(ring[i]), P(ring[(i + 1) % ring.Count])));
            var frame = new Frame(new XYZ(axis.X, axis.Y, z0 / Mm), XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ);
            return GeometryCreationUtilities.CreateRevolvedGeometry(frame, new List<CurveLoop> { loop }, 0, 2 * Math.PI);
        }

        /// <summary>A shaft: a blend between fluted (or round) sections of radius r0 at z0 and r1 at z1.</summary>
        internal static Solid Shaft(XYZ axis, double z0, double z1, double r0, double r1, int flutes)
        {
            CurveLoop Section(double z, double r)
            {
                int n = flutes > 0 ? flutes * 2 : 48;
                var p = new List<XYZ>();
                for (int i = 0; i < n; i++)
                {
                    double a = 2 * Math.PI * i / n, rr = flutes > 0 && i % 2 == 1 ? r * 0.93 : r;
                    p.Add(new XYZ(axis.X + rr * Math.Cos(a) / Mm, axis.Y + rr * Math.Sin(a) / Mm, z / Mm));
                }
                var loop = new CurveLoop();
                for (int i = 0; i < n; i++) loop.Append(Line.CreateBound(p[i], p[(i + 1) % n]));
                return loop;
            }
            return GeometryCreationUtilities.CreateBlendGeometry(Section(z0, r0), Section(z1, r1), null);
        }

        /// <summary>A column's ends: the base below the shaft and the capital above it, as far as the silhouette runs (1.5 m at most).</summary>
        internal static (double zb, double zt) ColumnEnds(FacadeColumn c)
        {
            int i0 = (int)((c.ShaftZ0 - c.Z0) / c.Step), i1 = (int)((c.ShaftZ1 - c.Z0) / c.Step);
            int b = i0; while (b > 0 && !double.IsNaN(c.Profile[b - 1]) && (i0 - b) * c.Step < 1500) b--;
            int t = i1; while (t < c.Profile.Length && !double.IsNaN(c.Profile[t]) && (t - i1) * c.Step < 1500) t++;
            return (c.Z0 + b * c.Step, c.Z0 + t * c.Step);
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
