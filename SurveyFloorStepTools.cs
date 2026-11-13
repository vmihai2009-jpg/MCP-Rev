// 1.14.0: stepped floors. The survey's spot heights on a plan often show one floor at several heights (split levels,
// raised or sunken rooms); Survey to Revit builds one flat slab per floor. survey_floor_steps finds the steps
// (Survey/SlabSteps.cs, Revit-free and tested), shows them (a plan picture and a table), and on apply models them:
// split = each step its own floor, cut out of the slab (the slab keeps its id, its outline is edited); shape = shape
// points on the one slab (a 50 mm ramp at each step's edge). One undo step per run.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        public static IEnumerable<RevitTool> FloorSteps()
        {
            yield return new RevitTool
            {
                Name = "survey_floor_steps",
                Write = true,
                Description = "Stepped floors from the plans' spot heights (Zz_60_45-Level). Per floor slab: its spots grouped into heights (within band_tol_mm), " +
                              "the commonest is the slab, every other height a step; each step's area is its room (rooms in the model) or, without rooms, " +
                              "the area nearest its spots walking round the walls (steps end at walls and in doorways). Spots on or beside a stair are left out " +
                              "(landings), and so are spots inside a wall or its openings (sills). Shows a plan picture per floor (black rings: slab spots, red: higher step, blue: lower, grey: left out) and a table; " +
                              "changes nothing unless apply: true. apply: mode split (default) makes each step its own floor of the slab's type at the step's " +
                              "height and cuts it out of the slab (the slab keeps its id); mode shape adds shape points to the one slab instead. fix_main also " +
                              "sets the slab to its commonest height (survey_floor_levels only moves up to 300 mm). One undo step.",
                InputSchema = Schema(
                    ("levels", Arr("string", "Floors (default every floor with a plan DWG)"), false),
                    ("apply", S("boolean", "Model the steps (default false: preview only)"), false),
                    ("mode", S("string", "split (default: a floor per step, cut from the slab) or shape (shape points on the one slab)"), false),
                    ("regions", S("string", "auto (default: rooms when the floor has rooms, else nearest spot), rooms or nearest"), false),
                    ("steps", Arr("string", "Only these steps, as \"<floor id>:<step id>\" from the preview (default all)"), false),
                    ("fix_main", S("boolean", "Also set each slab to its commonest spot height (default true)"), false),
                    ("min_step_mm", S("number", "Smallest height difference that is a step (default 100)"), false),
                    ("band_tol_mm", S("number", "Spots within this of each other are one height (default 60)"), false),
                    ("min_spots", S("integer", "Spots a step needs (default 1; 2 ignores lone spots, which can be pits or typos)"), false),
                    ("min_area_m2", S("number", "Smallest step area (default 0.5)"), false),
                    ("max_reach_mm", S("number", "Nearest-spot areas: how far past a room a spot's height reaches (default 4000)"), false),
                    ("stair_margin_mm", S("number", "Spots within this of a stair flight are left out (default 1000; -1 keeps them)"), false),
                    ("image", S("boolean", "Return the plan pictures (default true)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    bool apply = Args.Bool(a, "apply") == true, fixMain = Args.Bool(a, "fix_main") != false;
                    string mode = (Args.Str(a, "mode") ?? "split").ToLowerInvariant(), regions = (Args.Str(a, "regions") ?? "auto").ToLowerInvariant();
                    if (mode != "split" && mode != "shape") throw new ArgumentException("mode is split or shape.");
                    var only = new HashSet<string>(Args.Strings(a, "steps"));
                    var o = new SlabStepOptions
                    {
                        MinStepMm = Args.Dbl(a, "min_step_mm") ?? 100, BandTolMm = Args.Dbl(a, "band_tol_mm") ?? 60,
                        MinSpots = Math.Max(1, (int)(Args.Dbl(a, "min_spots") ?? 1)), MinAreaM2 = Args.Dbl(a, "min_area_m2") ?? 0.5,
                        MaxReachMm = Args.Dbl(a, "max_reach_mm") ?? 4000,
                    };
                    double stairMargin = Args.Dbl(a, "stair_margin_mm") ?? 1000, datum = Datum(doc);
                    string folder = ReportsFolder(doc);
                    var report = new List<object>(); var images = new List<byte[]>();
                    var work = new List<(Floor f, Level level, SlabStepPlan plan, List<SlabRegion> steps, double moveMm)>();
                    foreach (var (level, imp) in PlanFloors(doc, Args.Strings(a, "levels")))
                    {
                        var spots = PlanSpots(doc, imp);
                        double levelMm = level.Elevation * Mm;
                        var floors = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().Where(f => f.LevelId == level.Id).ToList();
                        if (floors.Count == 0 || spots.Count == 0) { report.Add(new { level = level.Name, note = floors.Count == 0 ? "no floor on this level" : "no spot heights on its plan" }); continue; }
                        var flights = stairMargin < 0 ? new List<StairGroup>() : SurveyDetail.Stairs(CadLink.Segments(CadLink.Read(doc, imp, new ReadStats()), new[] { "*Stair*", "*Step*" }));
                        var walls = WallFaces(doc, level, out var axes);
                        var rooms = regions == "nearest" ? new List<(string, List<List<Pt>>)>() : RoomLoops(doc, level);
                        if (regions == "rooms" && rooms.Count == 0) throw new ArgumentException($"{level.Name} has no placed rooms: regions auto or nearest, or place_rooms first.");
                        // each spot belongs to one slab: of the slabs under it, the one whose top is nearest its height
                        // (1.14.1: overlapping slabs, e.g. a raised floor over the main one, all took the same spots)
                        var topsOf = floors.ToDictionary(f => f.Id.Value, f => Faces(f, top: true));
                        long? OwnerOf(Pt p, double spotZ)
                        {
                            long? best = null; double bestD = double.MaxValue;
                            foreach (var f in floors)
                                if (topsOf[f.Id.Value].Count > 0 && ZOn(topsOf[f.Id.Value], p) is double z && Math.Abs(z - spotZ) < bestD) { bestD = Math.Abs(z - spotZ); best = f.Id.Value; }
                            return best;
                        }
                        foreach (var f in floors)
                        {
                            var tops = topsOf[f.Id.Value];
                            if (tops.Count == 0) continue;
                            double offset = (f.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0) * Mm;
                            var mine = new List<SlabSpot>();
                            foreach (var (p, aod) in spots)
                            {
                                double rel = aod - datum - levelMm;
                                if (Math.Abs(rel) > 1500 || ZOn(tops, p) == null) continue; // another level's spot, or not over this slab
                                if (OwnerOf(p, aod - datum) != f.Id.Value) continue;          // nearer another slab's top
                                var s = new SlabSpot { At = p, Mm = rel };
                                if (flights.Any(g => p.X >= g.Min.X - stairMargin && p.X <= g.Max.X + stairMargin && p.Y >= g.Min.Y - stairMargin && p.Y <= g.Max.Y + stairMargin))
                                    s.Skipped = "on or beside a stair";
                                else if (axes.Any(w => SurveyDetail.PtSeg(p, w.axis) <= w.half + 50))
                                    s.Skipped = "in a wall or an opening (a sill or a wall top?)";
                                mine.Add(s);
                            }
                            if (mine.Count == 0) continue;
                            var slab = TopLoops(tops);
                            // rooms only where they hold most of the spots (auto): a model with few rooms reads by nearest spot
                            var usable = mine.Where(s => s.Skipped == null).ToList();
                            double inRooms = rooms.Count == 0 || usable.Count == 0 ? 0 : usable.Count(s => rooms.Any(r => r.Item2.Count > 0 && SlabSteps.Inside(s.At, r.Item2))) / (double)usable.Count;
                            bool byRooms = rooms.Count > 0 && (regions == "rooms" || inRooms >= 0.75);
                            var plan = byRooms ? SlabSteps.ByRooms(rooms, mine, o, offset) : SlabSteps.ByGrid(slab, mine, walls, o, offset);
                            if (rooms.Count > 0 && !byRooms) plan.Notes.Add($"Rooms hold only {inRooms:P0} of the spots: read by nearest spot round the walls instead.");
                            var chosen = plan.Steps.Where(s => only.Count == 0 || only.Contains($"{f.Id.Value}:{s.Id}")).ToList();
                            double move = plan.MainSpots > 0 ? plan.MainMm - offset : 0;
                            if (Math.Abs(move) <= 10 || !fixMain) move = 0;
                            // moving the slab needs at least 3 spots at the commonest height, 5 for more than 300 mm (the other heights become steps)
                            // (1.14.1: slabs were proposed 2-4 m down on one or two spots)
                            int used = usable.Count;
                            if (move != 0 && (plan.MainSpots < 3 || (Math.Abs(move) > 300 && plan.MainSpots < 5)))
                            {
                                plan.Notes.Add($"Not moved: {plan.MainSpots} of {used} spots at {plan.MainMm:0} is too few to move the slab {move:+0;-0} mm.");
                                move = 0;
                            }
                            if (chosen.Count > 0 || move != 0) work.Add((f, level, plan, chosen, move));
                            byte[] png = null; string file = null;
                            try
                            {
                                png = SlabSteps.Preview(slab, plan, walls);
                                file = Path.Combine(folder, $"floor-steps-{Safe(level.Name)}-{f.Id.Value}.png");
                                File.WriteAllBytes(file, png);
                            }
                            catch { }
                            if (png != null && plan.Steps.Count > 0 || move != 0) images.Add(png);
                            report.Add(new
                            {
                                level = level.Name, floor = f.Id.Value, spots = mine.Count, method = plan.Method, frameDeg = plan.FrameDeg,
                                slabTopMm = R(offset), commonestMm = R(plan.MainMm), slabSpots = plan.MainSpots, moveSlabMm = R(move),
                                bands = plan.Bands.Select(b => new { mm = R(b.Mm), spots = b.Spots.Count, spreadMm = R(b.SpreadMm), slab = b.Main }).ToList(),
                                steps = plan.Steps.Select(s => new
                                {
                                    id = $"{f.Id.Value}:{s.Id}", mm = s.Mm, vsSlabMm = R(s.Mm - plan.MainMm), s.Spots, areaM2 = s.AreaM2, s.Room,
                                    corners = s.Loops.Sum(l => l.Count), s.Note, chosen = chosen.Contains(s),
                                }).ToList(),
                                leftOut = mine.Where(s => s.Skipped != null).GroupBy(s => s.Skipped).Select(g => new { why = g.Key, spots = g.Count(), mm = g.Select(s => R(s.Mm)).Take(8).ToList() }).ToList(),
                                notes = plan.Notes.Count > 0 ? plan.Notes.Take(10).ToList() : null,
                                bigMove = Math.Abs(move) > 300 ? "the slab is far from its spots: check the level and the spots before applying" : null,
                                image = file,
                            });
                        }
                    }

                    var done = new List<object>(); var revit = new List<string>();
                    if (apply && work.Count > 0)
                        using (var g = new TransactionGroup(doc, $"MCP: stepped floors ({mode})"))
                        {
                            g.Start();
                            try
                            {
                                foreach (var w in work)
                                    done.Add(mode == "split" ? Split(doc, w.f, w.level, w.plan, w.steps, w.moveMm, revit) : Shape(doc, w.f, w.plan, w.steps, w.moveMm, revit));
                                g.Assimilate();
                            }
                            catch
                            {
                                if (g.HasStarted() && !g.HasEnded()) g.RollBack();
                                throw;
                            }
                        }
                    var answer = new
                    {
                        floors = report, applied = apply ? done : null, mode, revit = revit.Count > 0 ? revit.Distinct().Take(20).ToList() : null,
                        heights = "mm above the floor's level; vsSlabMm is the step against the slab's commonest height.",
                        next = apply ? "survey_floor_levels and survey_section to confirm; one undo step in Revit undoes the lot."
                             : work.Count > 0 ? "Look at the pictures; apply: true (steps: [...] for some only, min_spots: 2 to skip lone spots) to model them." : "Nothing to model.",
                    };
                    string text = JsonSerializer.Serialize(answer, JsonOpts);
                    if (Args.Bool(a, "image") == false || images.Count == 0) return text;
                    var c = new ToolContent { Text = text };
                    foreach (var png in images.Where(p => p != null).Take(4)) c.Images.Add(("image/png", png));
                    return c;
                }
            };
        }

        // ------------------------------------------------------------------ split: a floor per step, cut from the slab

        private static object Split(Document doc, Floor host, Level level, SlabStepPlan plan, List<SlabRegion> steps, double moveMm, List<string> revit)
        {
            var made = new List<object>(); var failed = new List<object>();
            var cutters = new List<Solid>();
            Solid hostSolid = null; double z0 = 0;
            string outline = steps.Count == 0 ? "unchanged" : null;
            revit.AddRange(BuildTools.Tx.Run(doc, "MCP: stepped floor: steps", () =>
            {
                if (moveMm != 0) host.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(plan.MainMm / Mm);
                if (steps.Count == 0) return;
                var sketch = doc.GetElement(host.SketchId) as Sketch ?? throw new InvalidOperationException($"Floor {host.Id.Value} has no sketch to cut.");
                var profile = new List<CurveLoop>();
                foreach (CurveArray arr in sketch.Profile)
                {
                    var loop = new CurveLoop();
                    foreach (Curve cv in arr) loop.Append(cv);
                    profile.Add(loop);
                }
                z0 = profile[0].First().GetEndPoint(0).Z;
                hostSolid = GeometryCreationUtilities.CreateExtrusionGeometry(profile, XYZ.BasisZ, 1);
                foreach (var s in steps)
                {
                    try
                    {
                        var loops = s.Loops.Select(l => Loop(l, z0)).ToList();
                        Solid stepSolid;
                        try { stepSolid = GeometryCreationUtilities.CreateExtrusionGeometry(loops, XYZ.BasisZ, 1); }
                        catch (Autodesk.Revit.Exceptions.ArgumentException)
                        {
                            // 1.14.1: a grid-traced outline can touch itself at a corner, or a hole touch it; then each outer
                            // loop on its own, joined (the holes that broke it are left out)
                            stepSolid = null;
                            foreach (var l in s.Loops.Where(l => GeoMath.SignedArea(l) > 0))
                                try
                                {
                                    var one = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { Loop(l, z0) }, XYZ.BasisZ, 1);
                                    stepSolid = stepSolid == null ? one : BooleanOperationsUtils.ExecuteBooleanOperation(stepSolid, one, BooleanOperationsType.Union);
                                }
                                catch { }
                            if (stepSolid == null) throw;
                            revit.Add($"Fallback: step {s.Id}'s outline touched itself; built from its outer loops only");
                        }
                        var clip = BooleanOperationsUtils.ExecuteBooleanOperation(hostSolid, stepSolid, BooleanOperationsType.Intersect);
                        var bottom = clip == null || clip.Volume < 1e-4 ? new List<CurveLoop>() : BottomLoops(clip);
                        if (bottom.Count == 0) { failed.Add(new { step = s.Id, why = "not on the slab" }); continue; }
                        var f = Floor.Create(doc, bottom, host.GetTypeId(), level.Id);
                        f.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(s.Mm / Mm);
                        f.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"Survey step {s.Mm:0} mm{(s.Room != null ? " (" + s.Room + ")" : "")} (survey_floor_steps)");
                        cutters.Add(clip);
                        made.Add(new { step = s.Id, floor = f.Id.Value, mm = s.Mm, s.Room, areaM2 = Math.Round(clip.Volume * 0.09290304, 2) });
                    }
                    catch (Exception e) { failed.Add(new { step = s.Id, why = e.Message }); }
                }
            }));
            if (cutters.Count > 0)
            {
                // the slab keeps its id: its outline becomes itself less the steps
                var rest = hostSolid;
                foreach (var c in cutters)
                    try { rest = BooleanOperationsUtils.ExecuteBooleanOperation(rest, c, BooleanOperationsType.Difference) ?? rest; } catch { }
                var loops = rest.Volume < 1e-4 ? new List<CurveLoop>() : BottomLoops(rest);
                try
                {
                    if (loops.Count == 0) throw new InvalidOperationException("the steps cover the whole slab");
                    using (var scope = new SketchEditScope(doc, "MCP: stepped floor: slab outline"))
                    {
                        scope.Start(host.SketchId);
                        using (var tx = new Transaction(doc, "MCP: slab outline"))
                        {
                            tx.Start();
                            var sk = (Sketch)doc.GetElement(host.SketchId);
                            foreach (var id in sk.GetAllElements().ToList()) if (doc.GetElement(id) is CurveElement) doc.Delete(id);
                            foreach (var l in loops) foreach (Curve cv in l) doc.Create.NewModelCurve(cv, sk.SketchPlane);
                            tx.Commit();
                        }
                        scope.Commit(BuildTools.Tx.Preprocessor(out var notes, out var blocking));
                        revit.AddRange(notes()); revit.AddRange(blocking());
                    }
                    outline = "edited (the steps cut out)";
                }
                catch (Exception e)
                {
                    // the outline could not be edited: openings through the slab where the steps are, instead
                    int cut = 0;
                    revit.AddRange(BuildTools.Tx.Run(doc, "MCP: stepped floor: openings", () =>
                    {
                        foreach (var c in cutters)
                            foreach (var l in BottomLoops(c))
                            {
                                var ca = new CurveArray(); foreach (Curve cv in l) ca.Append(cv);
                                try { doc.Create.NewOpening(host, ca, true); cut++; } catch { }
                            }
                    }));
                    outline = $"not edited ({e.Message}); {cut} opening(s) cut instead" + (cut < cutters.Count ? ": the slab still runs under some steps, cut it by hand" : "");
                }
            }
            return new { floor = host.Id.Value, movedSlabMm = R(moveMm), slabOutline = outline, steps = made, failed = failed.Count > 0 ? failed : null };
        }

        // ------------------------------------------------------------------ shape: points on the one slab

        private static object Shape(Document doc, Floor host, SlabStepPlan plan, List<SlabRegion> steps, double moveMm, List<string> revit)
        {
            int points = 0, missed = 0;
            revit.AddRange(BuildTools.Tx.Run(doc, "MCP: stepped floor: shape points", () =>
            {
                if (moveMm != 0) host.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(plan.MainMm / Mm);
                if (steps.Count == 0) return;
                doc.Regenerate();
                var ed = host.GetSlabShapeEditor();
                if (!ed.IsEnabled) ed.Enable();
                doc.Regenerate();
                var tops = Faces(host, top: true);
                // 25 mm inside each step's edge at the step's height, 25 mm outside at the slab's (or the next step's);
                // every point first, then the heights, so each point is drawn on the flat slab
                var set = new List<(SlabShapeVertex v, double d)>();
                foreach (var s in steps)
                    foreach (var l in s.Loops)
                    {
                        bool ccw = GeoMath.SignedArea(l) > 0;
                        foreach (var (p, nIn) in EdgeSamples(l, ccw, 500))
                            foreach (var (q, off) in new[] { (new Pt(p.X + nIn.X * 25, p.Y + nIn.Y * 25), s.Mm - plan.MainMm), (new Pt(p.X - nIn.X * 25, p.Y - nIn.Y * 25), double.NaN) })
                            {
                                double d = off;
                                if (double.IsNaN(d)) d = steps.FirstOrDefault(o => o != s && SlabSteps.Inside(q, o.Loops)) is SlabRegion other ? other.Mm - plan.MainMm : 0;
                                if (!(ZOn(tops, q) is double z)) { missed++; continue; }
                                var v = ed.AddPoint(new XYZ(q.X / Mm, q.Y / Mm, z / Mm));
                                if (v == null) { missed++; continue; }
                                set.Add((v, d)); points++;
                            }
                    }
                foreach (var (v, d) in set) if (Math.Abs(d) > 0.5) ed.ModifySubElement(v, d / Mm);
            }));
            return new
            {
                floor = host.Id.Value, movedSlabMm = R(moveMm), shapePoints = points, pointsOffTheSlab = missed,
                steps = steps.Select(s => new { step = s.Id, mm = s.Mm, s.Room }).ToList(),
                note = "Shape points make a 50 mm ramp at each step's edge. Check the slab type has a variable layer if the soffit must stay flat.",
            };
        }

        /// <summary>Points along a loop's edges (at most spacing apart, and each corner) with the unit normal pointing into the region.</summary>
        private static IEnumerable<(Pt p, Pt nIn)> EdgeSamples(List<Pt> l, bool ccw, double spacing)
        {
            int n = l.Count;
            Pt Normal(Pt a, Pt b)
            {
                double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9) return new Pt(0, 0);
                // left of the direction of travel is inside a counter-clockwise loop
                return ccw ? new Pt(-dy / len, dx / len) : new Pt(dy / len, -dx / len);
            }
            for (int i = 0; i < n; i++)
            {
                Pt a = l[i], b = l[(i + 1) % n], prev = l[(i - 1 + n) % n];
                Pt n1 = Normal(prev, a), n2 = Normal(a, b);
                double bx = n1.X + n2.X, by = n1.Y + n2.Y, bl = Math.Sqrt(bx * bx + by * by);
                if (bl > 1e-9) yield return (a, new Pt(bx / bl * Math.Sqrt(2), by / bl * Math.Sqrt(2)));
                double len = a.Dist2D(b);
                int k = (int)Math.Ceiling(len / spacing);
                for (int j = 1; j < k; j++) yield return (new Pt(a.X + (b.X - a.X) * j / k, a.Y + (b.Y - a.Y) * j / k), n2);
            }
        }

        // ------------------------------------------------------------------ helpers

        private static CurveLoop Loop(List<Pt> ring, double z)
        {
            var loop = new CurveLoop();
            for (int i = 0; i < ring.Count; i++)
            {
                var a = new XYZ(ring[i].X / Mm, ring[i].Y / Mm, z); var b = new XYZ(ring[(i + 1) % ring.Count].X / Mm, ring[(i + 1) % ring.Count].Y / Mm, z);
                if (a.DistanceTo(b) > 1e-3) loop.Append(Line.CreateBound(a, b));
            }
            return loop;
        }

        /// <summary>The edge loops of a solid's downward faces (its plan outline at the bottom).</summary>
        private static List<CurveLoop> BottomLoops(Solid s)
        {
            var list = new List<CurveLoop>();
            foreach (Face f in s.Faces)
                if (f is PlanarFace pf && pf.FaceNormal.Z < -0.99) list.AddRange(pf.GetEdgesAsCurveLoops());
            return list;
        }

        /// <summary>A slab's plan outline (outer loops and holes, model mm) from its top faces.</summary>
        private static List<List<Pt>> TopLoops(List<PlanarFace> tops)
        {
            var loops = new List<List<Pt>>();
            foreach (var f in tops)
                foreach (var cl in f.GetEdgesAsCurveLoops())
                {
                    var ring = new List<Pt>();
                    foreach (Curve c in cl)
                    {
                        var pts = c.Tessellate();
                        for (int i = 0; i + 1 < pts.Count; i++) ring.Add(new Pt(pts[i].X * Mm, pts[i].Y * Mm));
                    }
                    if (ring.Count >= 3) loops.Add(ring);
                }
            return loops;
        }

        /// <summary>Both faces of every wall standing on this storey, as plan segments (mm): the barriers between rooms.</summary>
        private static List<Seg> WallFaces(Document doc, Level level, out List<(Seg axis, double half)> axes)
        {
            var segs = new List<Seg>(); axes = new List<(Seg, double)>();
            double lz = level.Elevation;
            foreach (var w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
            {
                if (!(w.Location is LocationCurve lc) || !(w.get_BoundingBox(null) is BoundingBoxXYZ bb)) continue;
                if (bb.Min.Z > lz + 500 / Mm || bb.Max.Z < lz + 1000 / Mm) continue;
                double half = w.Width / 2;
                var ap = lc.Curve.Tessellate();
                for (int i = 0; i + 1 < ap.Count; i++) axes.Add((new Seg(new Pt(ap[i].X * Mm, ap[i].Y * Mm), new Pt(ap[i + 1].X * Mm, ap[i + 1].Y * Mm), 0), half * Mm));
                foreach (double side in new[] { -half, half })
                {
                    Curve c;
                    try { c = Math.Abs(side) < 1e-9 ? lc.Curve : lc.Curve.CreateOffset(side, XYZ.BasisZ); } catch { continue; }
                    var pts = c.Tessellate();
                    for (int i = 0; i + 1 < pts.Count; i++) segs.Add(new Seg(new Pt(pts[i].X * Mm, pts[i].Y * Mm), new Pt(pts[i + 1].X * Mm, pts[i + 1].Y * Mm), 0));
                }
            }
            return segs;
        }

        /// <summary>Placed rooms on a level, with their outlines at the wall centres (mm).</summary>
        private static List<(string name, List<List<Pt>> loops)> RoomLoops(Document doc, Level level)
        {
            var list = new List<(string, List<List<Pt>>)>();
            var opt = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Center };
            foreach (var r in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).OfType<Room>().Where(r => r.LevelId == level.Id && r.Area > 0))
            {
                var loops = new List<List<Pt>>();
                try
                {
                    foreach (var segList in r.GetBoundarySegments(opt) ?? new List<IList<BoundarySegment>>())
                    {
                        var ring = new List<Pt>();
                        foreach (var bs in segList)
                        {
                            var pts = bs.GetCurve().Tessellate();
                            for (int i = 0; i + 1 < pts.Count; i++) ring.Add(new Pt(pts[i].X * Mm, pts[i].Y * Mm));
                        }
                        if (ring.Count >= 3) loops.Add(ring);
                    }
                }
                catch { }
                if (loops.Count > 0) list.Add(($"{r.Number} {r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()}".Trim(), loops));
            }
            return list;
        }
    }
}
