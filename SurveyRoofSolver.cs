// 1.36.0: roof_solver: the roof's structure from the survey roof plan (the same faces and fitted planes as
// roof_from_survey): every edge named, each face's pitch and fall, eaves against the walls under them, and, on build, each
// face as a native Revit roof sloping from its eave (pitch editable in properties), instead of a solid.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        public static IEnumerable<RevitTool> RoofSolverTools()
        {
            yield return new RevitTool
            {
                Name = "roof_solver",
                Write = true,
                Description = "The roof's structure from the survey roof plan: its faces with planes fitted to their spot heights (as roof_from_survey), " +
                              "every edge named (ridge, hip, valley, valley gutter, eave, verge, abutment, step, pitch change), each face's pitch " +
                              "and the way it falls, and each eave's height against the top of the wall under it. build: true makes each roof face " +
                              "as a native footprint roof sloping from its eave at the fitted pitch (a person changes the pitch or outline in " +
                              "properties / edit footprint) and removes roof_from_survey's solid for that face; chimneys and rooflights are left " +
                              "to roof_from_survey. Reports how far each native face sits off its fitted plane. One undo step.",
                InputSchema = Schema(
                    ("path", S("string", "Roof plan DWG (default: as roof_from_survey)"), false),
                    ("build", S("boolean", "Make native roofs (default false: report)"), false),
                    ("roof_type", S("string", "Roof type name (default: the first basic roof type)"), false),
                    ("min_area_m2", S("number", "Smallest face (default 0.3)"), false),
                    ("keep_shapes", S("boolean", "Keep roof_from_survey's solids (default false: delete the ones for faces built here)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    double datum = Datum(doc), minArea = (Args.Dbl(a, "min_area_m2") ?? 0.3) * 1e6;
                    string path = Args.Str(a, "path") ?? FindRoofPlan(doc) ?? throw new InvalidOperationException("No roof plan found beside the plans; give path.");
                    var file = DwgFile.Read(path);
                    var map = SurveyMap(doc, file, out string mapNote);
                    Seg M(DwgLine l) => new Seg(map(l.A), map(l.B), 0);
                    bool On(DwgLine l, params string[] g) => DwgFile.LayerMatches(l.Layer, g);
                    var segs = file.Lines.Where(l => On(l, "*Roof*", "EF_25*", "*Chimney*", "*Skylight*")).Select(M).ToList();
                    var chimneys = LoopBuilder.Build(file.Lines.Where(l => On(l, "*Chimney*")).Select(M).ToList(), 10).Loops;
                    var lights = LoopBuilder.Build(file.Lines.Where(l => On(l, "*Skylight*")).Select(M).ToList(), 10).Loops;
                    var spots = file.Texts.Where(t => DwgFile.LayerMatches(t.Layer, new[] { "*Level*" }) && ElevationFit.LevelValueMm(t.Value) is double)
                        .Select(t => { var p = map(t.At); return new Pt(p.X, p.Y, ElevationFit.LevelValueMm(t.Value).Value); }).ToList();
                    var rings = SurveyDetail.Faces(segs, 10, minArea / 3);
                    var bySpot = spots.GroupBy(s => SurveyDetail.FaceAt(rings, s)).Where(g => g.Key >= 0).ToDictionary(g => g.Key, g => g.ToList());
                    var faces = new List<RoofFace>(); var noHeight = 0;
                    for (int i = 0; i < rings.Count; i++)
                    {
                        var c = new Pt(rings[i].Average(p => p.X), rings[i].Average(p => p.Y));
                        if (chimneys.Any(r => GeoMath.PointInPolygon(c, r)) || lights.Any(r => GeoMath.PointInPolygon(c, r))) continue;
                        if (Math.Abs(GeoMath.SignedArea(rings[i])) < minArea) continue;
                        if (!bySpot.TryGetValue(i, out var ss)) { noHeight++; continue; }
                        faces.Add(new RoofFace { Id = i, Ring = rings[i], Plane = SurveyDetail.FitPlane(ss) });
                    }
                    var edges = RoofSolver.Edges(faces);
                    // eaves against the walls under them (wall top, model mm)
                    var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.Location is LocationCurve lc && lc.Curve is Line && w.get_BoundingBox(null) != null)
                        .Select(w => { var c = ((LocationCurve)w.Location).Curve; return (id: w.Id.Value, s: new Seg(CadLink.ToMm(c.GetEndPoint(0)), CadLink.ToMm(c.GetEndPoint(1)), 0), top: w.get_BoundingBox(null).Max.Z * Mm); }).ToList();
                    var eaves = edges.Where(e => e.Kind == "eave").Select(e =>
                    {
                        var mid = new Pt((e.P0.X + e.P1.X) / 2, (e.P0.Y + e.P1.Y) / 2);
                        double z = faces[e.A].Plane.At(mid.X, mid.Y) - datum;
                        var under = walls.Where(w => SurveyDetail.PtSeg(mid, w.s) <= 600).OrderBy(w => Math.Abs(w.top - z)).FirstOrDefault();
                        return new { face = "R" + faces[e.A].Id, lengthMm = Math.Round(e.LengthMm), eaveMm = Math.Round(z), wall = under.id == 0 ? (long?)null : under.id, eaveMinusWallTopMm = under.id == 0 ? (double?)null : Math.Round(z - under.top) };
                    }).ToList();
                    // build: one native footprint roof per face, sloping from its eave
                    var made = new List<object>(); var problems = new List<string>(); int removed = 0;
                    if (Args.Bool(a, "build") == true)
                    {
                        string tn = Args.Str(a, "roof_type");
                        var rtype = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().FirstOrDefault(t => tn == null || t.Name.Equals(tn, StringComparison.OrdinalIgnoreCase))
                                    ?? throw new ArgumentException("No roof type" + (tn == null ? "" : " called " + tn) + ".");
                        double thick = (rtype.get_Parameter(BuiltInParameter.ROOF_ATTR_DEFAULT_THICKNESS_PARAM)?.AsDouble() ?? 0) * Mm;
                        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                        bool keep = Args.Bool(a, "keep_shapes") == true;
                        BuildTools.Tx.Run(doc, "MCP: roof solver", () =>
                        {
                            foreach (var f in faces)
                            {
                                try
                                {
                                    var ev = RoofSolver.Eave(f); if (ev == null) continue;
                                    double pitch = Math.Atan(Math.Abs(ev.Slope));
                                    double baseZ = ev.BaseZ - datum - thick / Math.Max(0.2, Math.Cos(pitch)); // the survey spots are the top surface
                                    var lv = levels.LastOrDefault(l => l.Elevation * Mm <= baseZ + 1) ?? levels.First();
                                    var fp = new CurveArray();
                                    var r = f.Ring;
                                    for (int k = 0; k < r.Count; k++)
                                    {
                                        var p0 = new XYZ(r[k].X / Mm, r[k].Y / Mm, lv.Elevation); var p1 = new XYZ(r[(k + 1) % r.Count].X / Mm, r[(k + 1) % r.Count].Y / Mm, lv.Elevation);
                                        if (p0.DistanceTo(p1) > 0.003) fp.Append(Line.CreateBound(p0, p1));
                                    }
                                    var roof = doc.Create.NewFootPrintRoof(fp, lv, rtype, out ModelCurveArray mca);
                                    var ea = r[ev.Edge]; var eb = r[(ev.Edge + 1) % r.Count];
                                    var em = new XYZ((ea.X + eb.X) / 2 / Mm, (ea.Y + eb.Y) / 2 / Mm, lv.Elevation);
                                    ModelCurve eave = null; double bestD = double.MaxValue;
                                    foreach (ModelCurve mc in mca)
                                    {
                                        double d = mc.GeometryCurve.Distance(em);
                                        if (d < bestD) { bestD = d; eave = mc; }
                                    }
                                    foreach (ModelCurve mc in mca) roof.set_DefinesSlope(mc, mc.Id == eave?.Id && ev.Slope > 1e-4);
                                    if (eave != null && ev.Slope > 1e-4) roof.set_SlopeAngle(eave, Math.Abs(ev.Slope));
                                    roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)?.Set((baseZ - lv.Elevation * Mm) / Mm);
                                    roof.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"MCP|roof_solver|R{f.Id}: pitch {f.PitchDeg:0.0} deg from its eave, off plane {ev.OffPlaneMm:0} mm");
                                    made.Add(new { face = "R" + f.Id, roof = roof.Id.Value, level = lv.Name, pitchDeg = Math.Round(pitch * 180 / Math.PI, 1), offPlaneMm = ev.OffPlaneMm });
                                    if (!keep)
                                        foreach (var ds in new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Where(d => (d.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "").StartsWith($"MCP|roof_from_survey|roof R{f.Id}:")).Select(d => d.Id).ToList())
                                        { doc.Delete(ds); removed++; }
                                }
                                catch (Exception ex) { problems.Add($"R{f.Id}: {McpServer.Unwrap(ex).Message}"); }
                            }
                        });
                    }
                    var answer = new
                    {
                        path, placed = mapNote, faces = faces.Count, facesWithoutHeights = noHeight,
                        edgeCounts = edges.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count()),
                        faceList = faces.OrderByDescending(f => Math.Abs(GeoMath.SignedArea(f.Ring))).Take(40).Select(f => new
                        {
                            face = "R" + f.Id, areaM2 = Math.Round(Math.Abs(GeoMath.SignedArea(f.Ring)) / 1e6, 2), pitchDeg = Math.Round(f.PitchDeg, 1), fallsTowardDeg = f.FallsTowardDeg,
                            spots = f.Plane.Spots, rmsMm = Math.Round(f.Plane.RmsMm), edges = edges.Where(e => e.A == faces.IndexOf(f) || e.B == faces.IndexOf(f)).Select(e => e.Kind).ToList(),
                            nativeOffPlaneMm = RoofSolver.Eave(f)?.OffPlaneMm,
                        }).ToList(),
                        steps = edges.Where(e => e.Kind == "step").Select(e => new { faces = new[] { "R" + faces[e.A].Id, "R" + faces[e.B].Id }, gapMm = e.GapMm, lengthMm = Math.Round(e.LengthMm) }).ToList(),
                        eaves,
                        built = Args.Bool(a, "build") == true ? made : null, solidsRemoved = Args.Bool(a, "build") == true ? removed : (int?)null, problems = problems.Count > 0 ? problems : null,
                        next = "eaveMinusWallTopMm far from the roof's build-up: the wall top or the roof plan's heights are off (survey_section to see). nativeOffPlaneMm over ~20: that face is not one plane sloping from its eave; leave it as roof_from_survey's solid or split it. Sections are not yet checked here: survey_section_autofix for roof heights.",
                    };
                    File.WriteAllText(Path.Combine(ReportsFolder(doc), "roof-solver.json"), JsonSerializer.Serialize(answer, JsonOpts));
                    return answer;
                }
            };
        }
    }
}
