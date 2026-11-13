// 1.21.0: curves built as curves. curve_check (read-only) finds where the survey draws a curved wall face (a true arc or a
// chain of short lines) and the model has a staircase of short straight walls, or nothing. curve_walls replaces each
// such staircase with one curved wall of the same type, level and height, moves its windows and doors onto it and
// re-hosts its wall sweeps, so a cornice runs round the curve in one piece and a person can edit it as a curve.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        public static IEnumerable<RevitTool> CurveTools()
        {
            yield return new RevitTool
            {
                Name = "curve_check",
                Description = "Are the survey's curves built as curves: for every curved wall face on each floor's plan (true arcs and bulged " +
                              "polylines, and chains of short straight lines that stay on one circle, which is how many surveys draw a curve), " +
                              "the model's walls along it: one curved wall (curved), a run of short straight walls (chorded: a staircase, " +
                              "and everything hosted on it, sweeps and relief, steps with it), partly built, or missing. A chorded run comes " +
                              "with the curved wall to build instead (centre, radius, angles, type); curve_walls does it. Changes nothing.",
                InputSchema = Schema(
                    ("levels", Arr("string", "Only these floors (default all with a plan)"), false),
                    ("tol_mm", S("number", "How far a drawn piece may sit off its circle (default 5) and a wall off the curve's band (default 30)"), false),
                    ("source", S("string", "As survey_fidelity"), false),
                    ("survey_folder", S("string", "As survey_fidelity"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var all = CurveFindings(app, doc, a);
                    var answer = new
                    {
                        floors = all.Select(x => new
                        {
                            level = x.level.Name,
                            curves = x.found.Count, curved = x.found.Count(f => f.State == "curved"), chorded = x.found.Count(f => f.State == "chorded"),
                            partial = x.found.Count(f => f.State == "partial"), missing = x.found.Count(f => f.State == "missing"),
                            items = x.found.Where(f => f.State != "curved").Select(f => new
                            {
                                f.State, f.Source, centre = new[] { f.Cx, f.Cy }, faceRadiusMm = f.FaceR, f.StartDeg, f.SweepDeg, f.DrawnPieces, f.Covered,
                                straightWalls = f.StraightWalls, curvedWalls = f.CurvedWalls,
                                build = f.State == "chorded" ? new { radiusMm = f.NewR, startDeg = Math.Round(f.NewStart * 180 / Math.PI, 1), sweepDeg = Math.Round(f.NewSweep * 180 / Math.PI, 1), f.Type, thicknessMm = f.ThicknessMm } : null,
                            }).ToList(),
                        }).ToList(),
                        next = "chorded: curve_walls (dry run first) replaces each run with one curved wall, keeping its windows, doors and sweeps. " +
                               "missing/partial: the plan reading left the curve out; build it with Survey to Revit or create_wall with an arc.",
                    };
                    File.WriteAllText(Path.Combine(ReportsFolder(doc), "curve-check.json"), JsonSerializer.Serialize(answer, JsonOpts));
                    return answer;
                }
            };

            yield return new RevitTool
            {
                Name = "curve_walls",
                Write = true,
                Description = "Replace each run of short straight walls that curve_check finds along a curved face with one curved wall: same " +
                              "type, level, base and top, on the walls' side of the face, ending where the run ended (it meets the straight " +
                              "walls there). Windows and doors on the run are placed again on the curve at the same point and sill; wall sweeps " +
                              "on it are made again on the curved wall, so they run round in one piece. Runs are left alone (and listed) when " +
                              "their walls differ in level or height, or a hosted element can't be moved. apply: false (default) only says " +
                              "what it would do. Undo is one step per floor.",
                InputSchema = Schema(
                    ("levels", Arr("string", "Only these floors (default all with a plan)"), false),
                    ("apply", S("boolean", "Make the changes (default false: report only)"), false),
                    ("tol_mm", S("number", "As curve_check"), false),
                    ("source", S("string", "As survey_fidelity"), false),
                    ("survey_folder", S("string", "As survey_fidelity"), false)),
                Run = (app, a) =>
                {
                    var doc = Args.Bool(a, "apply") == true ? Writable(app) : Doc(app);
                    bool apply = Args.Bool(a, "apply") == true;
                    var results = new List<object>();
                    foreach (var (level, found) in CurveFindings(app, doc, a))
                    {
                        var done = new HashSet<long>();
                        var floor = new List<object>();
                        foreach (var f in found.Where(x => x.State == "chorded").OrderByDescending(x => x.StraightWalls.Count))
                        {
                            if (f.StraightWalls.Any(done.Contains)) continue;   // the other face of the same wall
                            var walls = f.StraightWalls.Select(id => doc.GetElement(new ElementId(id)) as Wall).Where(w => w != null).ToList();
                            string why = CanCurve(doc, walls);
                            var inserts = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                                .Where(fi => fi.Host is Wall h && f.StraightWalls.Contains(h.Id.Value)).ToList();
                            var sweeps = new FilteredElementCollector(doc).OfClass(typeof(WallSweep)).Cast<WallSweep>()
                                .Where(s => s.GetHostIds().Any(h => f.StraightWalls.Contains(h.Value))).ToList();
                            var item = new Dictionary<string, object>
                            {
                                ["walls"] = f.StraightWalls, ["radiusMm"] = f.NewR, ["sweepDeg"] = Math.Round(f.NewSweep * 180 / Math.PI, 1), ["type"] = f.Type,
                                ["windowsAndDoors"] = inserts.Count, ["wallSweeps"] = sweeps.Count,
                            };
                            if (why != null) { item["leftAlone"] = why; floor.Add(item); continue; }
                            foreach (var id in f.StraightWalls) done.Add(id);
                            if (!apply) { item["would"] = "replace with one curved wall"; floor.Add(item); continue; }
                            try
                            {
                                var notes = BuildTools.Tx.Run(doc, $"MCP: curve walls {level.Name}", () => item["made"] = Curve(doc, f, walls, inserts, sweeps));
                                if (notes.Count > 0) item["notes"] = notes.Take(5).ToList();
                            }
                            catch (Exception ex) { item["failed"] = McpServer.Unwrap(ex).Message; }
                            floor.Add(item);
                        }
                        results.Add(new { level = level.Name, runs = floor });
                    }
                    return new { applied = apply, floors = results, next = apply ? "curve_check again; then survey_fidelity and the facade checks" : "apply: true to make the changes" };
                }
            };
        }

        private static List<(Level level, List<CurveWallFinding> found)> CurveFindings(UIApplication app, Document doc, JsonObject a)
        {
            var source = ReadingFor(app, doc, a);
            double tol = Args.Dbl(a, "tol_mm") ?? 5;
            double datumMm = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).Elevation * Mm;
            var plans = CadLink.All(doc).Where(i => i.ViewSpecific && doc.GetElement(i.OwnerViewId) is ViewPlan vp && vp.GenLevel != null)
                .GroupBy(i => ((ViewPlan)doc.GetElement(i.OwnerViewId)).GenLevel.Id.Value).ToDictionary(g => g.Key, g => g.First());
            var wanted = Args.Strings(a, "levels");
            var res = new List<(Level, List<CurveWallFinding>)>();
            foreach (var level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation))
            {
                if (!source.Has(level, plans) || (wanted.Count > 0 && !wanted.Any(w => w.Equals(level.Name, StringComparison.OrdinalIgnoreCase)))) continue;
                FidelityInput input;
                try { input = FidelityInputFor(doc, level, plans.TryGetValue(level.Id.Value, out var imp) ? imp : null, datumMm, source); } catch { continue; }
                var curves = new List<(string, double, double, double, double, double, int)>();
                foreach (var w in input.WallArcs.Where(x => x != null && x.Length >= 5 && x[2] > 0))
                {
                    double s0 = w[3] * Math.PI / 180, sw = ((w[4] - w[3]) % 360 + 360) % 360;
                    if (sw < 1e-6 || sw > 359) continue;   // circles are columns, not walls
                    curves.Add(("arc", w[0], w[1], w[2], s0, sw * Math.PI / 180, 1));
                }
                foreach (var c in CurveChains.Find(input.WallFaces.Select(s => (s.A, s.B)).ToList(), new CurveChainOptions { TolMm = tol }))
                    if (!curves.Any(x => Math.Sqrt((x.Item2 - c.Cx) * (x.Item2 - c.Cx) + (x.Item3 - c.Cy) * (x.Item3 - c.Cy)) < 100 && Math.Abs(x.Item4 - c.R) < 50))
                        curves.Add(("chain", c.Cx, c.Cy, c.R, c.Start, c.Sweep, c.Members.Count));
                var found = CurveWalls.Check(curves, input.Walls, input.ArcWalls, Args.Dbl(a, "tol_mm") is double t ? Math.Max(t, 30) : 30);
                // the two faces of one wall: keep the face that covers most
                var kept = new List<CurveWallFinding>();
                foreach (var f in found.OrderByDescending(x => x.Covered))
                    if (!kept.Any(k => (k.StraightWalls.Count > 0 && k.StraightWalls.Intersect(f.StraightWalls).Any()) || (k.CurvedWalls.Count > 0 && k.CurvedWalls.Intersect(f.CurvedWalls).Any())))
                        kept.Add(f);
                res.Add((level, kept));
            }
            return res;
        }

        /// <summary>Null when the walls can become one curved wall; otherwise why not.</summary>
        private static string CanCurve(Document doc, List<Wall> walls)
        {
            if (walls.Count < 2) return "fewer than two walls found";
            if (walls.Select(w => w.LevelId.Value).Distinct().Count() > 1) return "the walls are on different levels";
            double P(Wall w, BuiltInParameter p) => w.get_Parameter(p)?.AsDouble() ?? 0;
            if (walls.Select(w => Math.Round(P(w, BuiltInParameter.WALL_BASE_OFFSET) * Mm / 10)).Distinct().Count() > 1) return "the walls have different base offsets";
            if (walls.Select(w => Math.Round((w.get_BoundingBox(null)?.Max.Z ?? 0) * Mm / 10)).Distinct().Count() > 1) return "the walls have different tops";
            if (walls.Any(w => w.CurtainGrid != null)) return "a curtain wall";
            return null;
        }

        private static object Curve(Document doc, CurveWallFinding f, List<Wall> walls, List<FamilyInstance> inserts, List<WallSweep> sweeps)
        {
            var first = walls.OrderByDescending(w => ((LocationCurve)w.Location).Curve.Length).First();
            var type = walls.GroupBy(w => w.WallType.Id.Value).OrderByDescending(g => g.Sum(w => ((LocationCurve)w.Location).Curve.Length)).First().First().WallType;
            double z = ((LocationCurve)first.Location).Curve.GetEndPoint(0).Z;
            var centre = new XYZ(f.Cx / Mm, f.Cy / Mm, z);
            var arc = Arc.Create(centre, f.NewR / Mm, f.NewStart, f.NewStart + f.NewSweep, XYZ.BasisX, XYZ.BasisY);
            double baseOff = first.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
            var topId = first.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() ?? ElementId.InvalidElementId;
            double topOff = first.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.AsDouble() ?? 0;
            double height = first.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 10 / 0.3048;
            // the exterior side: Revit puts it on the arc's outside for an anticlockwise arc; flip when the old walls faced in
            var oldOut = first.Orientation; var mid = ((LocationCurve)first.Location).Curve.Evaluate(0.5, true);
            bool facesOut = oldOut.DotProduct((mid - centre).Normalize()) > 0;
            var wall = Wall.Create(doc, arc, type.Id, first.LevelId, height, baseOff, false, false);
            if (topId != ElementId.InvalidElementId) { wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(topId); wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.Set(topOff); }
            var keyRef = first.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger();
            doc.Regenerate();
            if ((wall.Orientation.DotProduct((arc.Evaluate(0.5, true) - centre).Normalize()) > 0) != facesOut) wall.Flip();
            if (keyRef.HasValue) wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.Set(keyRef.Value);
            var comment = wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            comment?.Set($"MCP|curve_walls|{string.Join(",", walls.Select(w => w.Id.Value))}");

            // windows and doors: the same family type, at the same angle round the curve, same sill
            int moved = 0; var lost = new List<long>();
            var level = doc.GetElement(first.LevelId) as Level;
            foreach (var fi in inserts)
            {
                try
                {
                    if (!(fi.Location is LocationPoint lp)) { lost.Add(fi.Id.Value); continue; }
                    var p = lp.Point; double ang = Math.Atan2(p.Y - centre.Y, p.X - centre.X);
                    var at = new XYZ(centre.X + f.NewR / Mm * Math.Cos(ang), centre.Y + f.NewR / Mm * Math.Sin(ang), p.Z);
                    double? sill = fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble();
                    var nfi = doc.Create.NewFamilyInstance(at, fi.Symbol, wall, level, StructuralType.NonStructural);
                    if (sill.HasValue) nfi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(sill.Value);
                    if (fi.FacingFlipped != nfi.FacingFlipped && nfi.CanFlipFacing) nfi.flipFacing();
                    if (fi.HandFlipped != nfi.HandFlipped && nfi.CanFlipHand) nfi.flipHand();
                    foreach (Parameter prm in fi.Parameters)   // instance sizes and marks
                        if (!prm.IsReadOnly && prm.StorageType == StorageType.Double && prm.Definition != null)
                        {
                            var q = nfi.LookupParameter(prm.Definition.Name);
                            if (q != null && !q.IsReadOnly && q.StorageType == StorageType.Double && Math.Abs(q.AsDouble() - prm.AsDouble()) > 1e-9 && prm.Definition.Name != "Sill Height") q.Set(prm.AsDouble());
                        }
                    moved++;
                }
                catch { lost.Add(fi.Id.Value); }
            }
            // wall sweeps: each type and placement once, on the curved wall
            int remade = 0;
            foreach (var g in sweeps.GroupBy(s => (s.GetTypeId().Value, s.GetWallSweepInfo().Distance, s.GetWallSweepInfo().WallOffset, s.GetWallSweepInfo().WallSide, s.GetWallSweepInfo().DistanceMeasuredFrom)))
            {
                try { if (WallSweep.WallAllowsWallSweep(wall)) { WallSweep.Create(wall, g.First().GetTypeId(), g.First().GetWallSweepInfo()); remade++; } }
                catch { }
            }
            doc.Delete(sweeps.Select(s => s.Id).Concat(walls.Select(w => w.Id)).ToList());
            return new { wall = wall.Id.Value, windowsAndDoorsMoved = moved, windowsAndDoorsNotMoved = lost, wallSweepsRemade = remade };
        }
    }
}
