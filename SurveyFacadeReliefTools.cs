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
