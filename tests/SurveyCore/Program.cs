// The survey core's tests from the mcp-servers-for-revit patch (62), run against BG MCP's copy of the core.
using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class T
    {
        public static int Pass, Fail;
        public static void Check(bool ok, string name)
        {
            if (ok) { Pass++; Console.WriteLine("  ok   " + name); }
            else { Fail++; Console.WriteLine("  FAIL " + name); }
        }
        public static bool Near(double a, double b, double eps = 1e-6) { return Math.Abs(a - b) <= eps; }
    }

    internal static class Program
    {
        static CadEntity Line(int i, string layer, double x1, double y1, double x2, double y2, double z = 0)
        {
            var e = new CadEntity { Index = i, Layer = layer, Kind = CadKind.Line };
            e.Points.Add(new Pt(x1, y1, z)); e.Points.Add(new Pt(x2, y2, z)); return e;
        }
        static CadEntity Poly(int i, string layer, bool closed, params double[] xy)
        {
            var e = new CadEntity { Index = i, Layer = layer, Kind = CadKind.Polyline, Closed = closed };
            for (int k = 0; k < xy.Length; k += 2) e.Points.Add(new Pt(xy[k], xy[k + 1], 0));
            return e;
        }
        static CadEntity Point(int i, string layer, double x, double y, double z)
        {
            var e = new CadEntity { Index = i, Layer = layer, Kind = CadKind.Point };
            e.Points.Add(new Pt(x, y, z)); return e;
        }

        static int Main()
        {
            SpeedTests.Run(); MoreTests.Run(); DetailTests.Run(); ElevMissingTests.Run(); FacadeMapTests.Run(); FacadeCheckTests.Run(); SlabStepTests.Run(); ReadingTests.Run(); SectionTests.Run(); CleanerTests(); SnapTests(); LoopTests(); WallTests(); TopoTests(); RuleTests(); PlannerTests(); SuggestTests(); StressTests();
            Console.WriteLine();
            Console.WriteLine("passed " + T.Pass + ", failed " + T.Fail);
            return T.Fail == 0 ? 0 : 1;
        }

        static void CleanerTests()
        {
            Console.WriteLine("GeometryCleaner");
            var st = new CleanStats(); bool closed = false;
            var pts = GeometryCleaner.CleanPolyline(new List<Pt> { new Pt(0, 0), new Pt(0, 0.5), new Pt(1000, 0), new Pt(2000, 0), new Pt(3000, 0) }, ref closed, 10, 50, 0.25, st);
            T.Check(pts != null && pts.Count == 2, "tiny segment merged and collinear mid-points removed -> 2 pts");
            T.Check(st.TinySegmentsMerged == 1 && st.CollinearRemoved == 2, "stats counted");

            closed = false; st = new CleanStats();
            pts = GeometryCleaner.CleanPolyline(new List<Pt> { new Pt(0, 0), new Pt(1000, 0), new Pt(1000, 1000), new Pt(0, 1000), new Pt(0, 20) }, ref closed, 10, 50, 0.25, st);
            T.Check(closed && pts.Count == 4 && st.AutoClosed == 1, "20 mm end gap auto-closes a ring");

            closed = false; st = new CleanStats();
            pts = GeometryCleaner.CleanPolyline(new List<Pt> { new Pt(0, 0), new Pt(1000, 0), new Pt(1000, 1000), new Pt(0, 0) }, ref closed, 10, 50, 0.25, st);
            T.Check(closed && pts.Count == 3, "explicit closing vertex removed, ring flagged closed");

            closed = false; st = new CleanStats();
            pts = GeometryCleaner.CleanPolyline(new List<Pt> { new Pt(0, 0, 0), new Pt(500, 0, 100), new Pt(1000, 0, 0) }, ref closed, 10, 50, 0.25, st);
            T.Check(pts.Count == 3, "collinear in plan but Z changes -> vertex KEPT (contour/profile preserved)");

            closed = false; st = new CleanStats();
            pts = GeometryCleaner.CleanPolyline(new List<Pt> { new Pt(0, 0), new Pt(double.NaN, 1) }, ref closed, 10, 50, 0.25, st);
            T.Check(pts == null && st.NonFiniteRemoved == 1, "NaN vertex removed, degenerate result dropped");

            var ents = new List<CadEntity>
            {
                Line(0, "A", 0, 0, 5000, 0), Line(1, "A", 5000, 0, 0, 0),                // same line, reversed
                Line(2, "A", 0, 0, 5000, 0.004),                                         // same after snapping
                Poly(3, "B", true, 0, 0, 1000, 0, 1000, 1000, 0, 1000),
                Poly(4, "B", true, 1000, 1000, 0, 1000, 0, 0, 1000, 0),                  // same ring, other start+direction
            };
            st = new CleanStats();
            var res = GeometryCleaner.CleanAll(ents, new Tolerances(), st);
            T.Check(res.Count == 2 && st.DuplicatesRemoved == 3, "duplicate lines + rotated/reversed ring de-duplicated (" + res.Count + " kept)");
        }

        static void SnapTests()
        {
            Console.WriteLine("Snapping");
            var a = new List<Pt> { new Pt(0, 0), new Pt(1000, 0) };
            var b = new List<Pt> { new Pt(1000.004, 0.003), new Pt(1000, 1000) };
            var st = new CleanStats();
            GeometryCleaner.SnapPoints(new[] { a, b }, 1.0, st);
            T.Check(a[1].X == b[0].X && a[1].Y == b[0].Y, "endpoints 0.005 mm apart share one coordinate");
            var c = new List<Pt> { new Pt(1000.0, 0.0) };
            var d = new List<Pt> { new Pt(1999.0, 0.0) };
            GeometryCleaner.SnapPoints(new[] { c, d }, 1.0, st);
            T.Check(d[0].X == 1999.0, "far points untouched");
            // cell-boundary case
            var e = new List<Pt> { new Pt(0.999, 0) }; var f = new List<Pt> { new Pt(1.001, 0) };
            GeometryCleaner.SnapPoints(new[] { e, f }, 1.0, st);
            T.Check(e[0].X == f[0].X, "points across a grid-cell boundary still snap");
        }

        static void LoopTests()
        {
            Console.WriteLine("LoopBuilder");
            var segs = new List<Seg>
            {
                new Seg(new Pt(0, 0), new Pt(5000, 0), 0), new Seg(new Pt(5002, 1), new Pt(5000, 4000), 1),
                new Seg(new Pt(5000, 4000), new Pt(0, 4001), 2), new Seg(new Pt(0, 4000), new Pt(0, 2), 3),
            };
            var r = LoopBuilder.Build(segs, 50);
            T.Check(r.Loops.Count == 1 && r.Loops[0].Count == 4, "4 loose lines with small gaps -> 1 loop of 4 vertices");
            T.Check(Math.Abs(Math.Abs(GeoMath.SignedArea(r.Loops[0])) - 20000000) < 50000, "loop area ~ 5 m x 4 m");

            segs.Add(new Seg(new Pt(0, 0), new Pt(-3000, 0), 4)); // dangling stub
            r = LoopBuilder.Build(segs, 50);
            T.Check(r.Loops.Count == 1 && r.DanglingSegments == 1, "dangling stub pruned and counted");

            // cross-line that touches the outline mid-edge WITHOUT a shared vertex: outline is still found, cross-line reported as dangling
            var unsplit = new List<Seg> { new Seg(new Pt(0, 0), new Pt(4000, 0), 0), new Seg(new Pt(4000, 0), new Pt(4000, 4000), 1), new Seg(new Pt(4000, 4000), new Pt(0, 4000), 2),
                                          new Seg(new Pt(0, 4000), new Pt(0, 0), 3), new Seg(new Pt(0, 2000), new Pt(4000, 2000), 4) };
            r = LoopBuilder.Build(unsplit, 50);
            T.Check(r.Loops.Count == 1 && r.DanglingSegments == 1, "unsplit T-line: outline found, cross-line reported dangling");

            // true junction (shared vertices at degree 3): reported, not guessed
            var split = new List<Seg> { new Seg(new Pt(0, 0), new Pt(4000, 0), 0), new Seg(new Pt(4000, 0), new Pt(4000, 2000), 1), new Seg(new Pt(4000, 2000), new Pt(4000, 4000), 2),
                                        new Seg(new Pt(4000, 4000), new Pt(0, 4000), 3), new Seg(new Pt(0, 4000), new Pt(0, 2000), 4), new Seg(new Pt(0, 2000), new Pt(0, 0), 5),
                                        new Seg(new Pt(0, 2000), new Pt(4000, 2000), 6) };
            r = LoopBuilder.Build(split, 50);
            T.Check(r.Loops.Count == 0 && r.ComplexComponents == 1, "junction network (degree-3 nodes) is reported, not guessed");
        }

        static void WallTests()
        {
            Console.WriteLine("WallDetector");
            var segs = new List<Seg> { new Seg(new Pt(0, 0), new Pt(5000, 0), 0), new Seg(new Pt(0, 200), new Pt(5000, 200), 1) };
            var w = WallDetector.FromParallelPairs(segs, 60, 600, 300, 1.0);
            T.Check(w.Count == 1 && T.Near(w[0].ThicknessMm, 200, 0.01), "two parallel lines 200 apart -> one 200 mm wall");
            T.Check(w.Count == 1 && T.Near(w[0].Start.Y, 100, 0.01) && T.Near(w[0].End.X, 5000, 0.01), "centre-line is midway");

            segs = new List<Seg> { new Seg(new Pt(0, 0), new Pt(5000, 0), 0), new Seg(new Pt(1000, 200), new Pt(3000, 200), 1) };
            w = WallDetector.FromParallelPairs(segs, 60, 600, 300, 1.0);
            T.Check(w.Count == 1 && T.Near(w[0].Start.X, 1000, 0.01) && T.Near(w[0].End.X, 3000, 0.01), "wall limited to the overlapping stretch");

            segs = new List<Seg> { new Seg(new Pt(0, 0), new Pt(5000, 0), 0), new Seg(new Pt(0, 3000), new Pt(5000, 3000), 1) };
            T.Check(WallDetector.FromParallelPairs(segs, 60, 600, 300, 1.0).Count == 0, "lines 3 m apart are not a wall");

            segs = new List<Seg> { new Seg(new Pt(0, 0), new Pt(5000, 0), 0), new Seg(new Pt(0, 200), new Pt(5000, 700), 1) };
            T.Check(WallDetector.FromParallelPairs(segs, 60, 600, 300, 1.0).Count == 0, "non-parallel lines are not paired");

            // rectangle room walls: outer 6000x4000 and inner offset 200 -> 4 walls
            var room = new List<Seg>
            {
                new Seg(new Pt(0,0), new Pt(6000,0), 0), new Seg(new Pt(6000,0), new Pt(6000,4000), 1), new Seg(new Pt(6000,4000), new Pt(0,4000), 2), new Seg(new Pt(0,4000), new Pt(0,0), 3),
                new Seg(new Pt(200,200), new Pt(5800,200), 4), new Seg(new Pt(5800,200), new Pt(5800,3800), 5), new Seg(new Pt(5800,3800), new Pt(200,3800), 6), new Seg(new Pt(200,3800), new Pt(200,200), 7),
            };
            w = WallDetector.FromParallelPairs(room, 60, 600, 300, 1.0);
            T.Check(w.Count == 4 && w.All(x => T.Near(x.ThicknessMm, 200, 0.01)), "closed room drawn with two outlines -> 4 walls of 200 mm (" + w.Count + ")");
        }

        static void TopoTests()
        {
            Console.WriteLine("TopoSampler");
            var rnd = new Random(7); var pts = new List<Pt>();
            for (int i = 0; i < 50000; i++) pts.Add(new Pt(rnd.NextDouble() * 200000, rnd.NextDouble() * 200000, rnd.NextDouble() * 5000));
            double cell; var th = TopoSampler.ThinToMax(pts, 5000, DecimateMode.Average, out cell);
            T.Check(th.Count <= 5000 && th.Count > 2000 && cell > 0, "50k points thinned to <= 5000 (" + th.Count + ", cell " + Math.Round(cell) + " mm)");
            var same = TopoSampler.ThinToMax(pts.Take(100).ToList(), 5000, DecimateMode.Average, out cell);
            T.Check(same.Count == 100 && cell == 0, "small sets untouched");

            var dens = TopoSampler.Densify(new List<Pt> { new Pt(0, 0, 0), new Pt(10000, 0, 100) }, false, 2500);
            T.Check(dens.Count == 5 && T.Near(dens[2].Z, 50, 0.01), "contour densified with interpolated Z");

            var z = new List<Pt>(); for (int i = 0; i < 30; i++) z.Add(new Pt(i * 1000, i * 500, 10000 + (i % 3) * 20)); z.Add(new Pt(5000, 5000, 0));
            var o = TopoSampler.ZOutliers(z, 8);
            T.Check(o.Count == 1 && o[0] == 30, "Z=0 typo detected among ~10 m survey points");
            int rem; var uq = TopoSampler.UniqueXY(new List<Pt> { new Pt(0, 0, 10), new Pt(0.2, 0.2, 30), new Pt(5000, 0, 5) }, 1.0, out rem);
            T.Check(uq.Count == 2 && rem == 1 && T.Near(uq[0].Z, 20, 0.01), "same-XY points merged (Revit rejects duplicate XY), Z averaged");
            T.Check(TopoSampler.IsCollinear2D(new List<Pt> { new Pt(0, 0), new Pt(10, 10), new Pt(20, 20) }, 1), "collinear detection");
        }

        static void RuleTests()
        {
            Console.WriteLine("LayerRuleSet");
            var rs = new LayerRuleSet(new[]
            {
                new LayerRule { Name = "walls", Match = { "A-WALL*" }, Exclude = { "*DEMO*" }, Action = Actions.WallDoubleLine },
                new LayerRule { Name = "demo", Match = { "*DEMO*" }, Action = Actions.Ignore },
                new LayerRule { Name = "wallsHi", Match = { "A-WALL-FIRE" }, Action = Actions.WallCentreline, Priority = 5 },
            });
            T.Check(rs.Resolve("a-wall-ext").Name == "walls", "case-insensitive glob");
            T.Check(rs.Resolve("A-WALL-DEMO").Name == "demo", "exclude falls through to next rule");
            T.Check(rs.Resolve("A-WALL-FIRE").Name == "wallsHi", "higher priority wins");
            T.Check(rs.Resolve("XREF|A-WALL-X").Name == "walls", "xref prefix stripped ('|')");
            T.Check(rs.Resolve("Site$0$A-WALL-X").Name == "walls", "bound-xref prefix stripped ('$')");
            T.Check(rs.Resolve("C-TOPO") == null, "unmatched layer -> null");
            var bad = new LayerRuleSet(new[] { new LayerRule { Name = "x", Match = { "A*" }, Action = "wall" }, new LayerRule { Name = "y", Action = Actions.Floor } });
            T.Check(bad.ConfigProblems.Count == 2, "invalid action / empty match reported");
        }


        static void SuggestTests()
        {
            Console.WriteLine("ConfigSuggester");
            var inv = new List<LayerInventory>
            {
                new LayerInventory { Name = "A-WALL-EXT", Lines = 40, Polylines = 10 },
                new LayerInventory { Name = "XREF|A-WALL-INT", Lines = 20 },
                new LayerInventory { Name = "C-TOPO-SPOT", Points = 300 },
                new LayerInventory { Name = "SiteContours", Polylines = 50, ZMinMm = 10000, ZMaxMm = 12000 },
                new LayerInventory { Name = "S-GRID", Lines = 8 },
                new LayerInventory { Name = "L-TREE", Blocks = 12 },
                new LayerInventory { Name = "A-ANNO-TEXT", Polylines = 100 },
                new LayerInventory { Name = "A-SLAB", ClosedPolylines = 3, Polylines = 3 },
                new LayerInventory { Name = "ZZZ-WEIRD", Lines = 5 },
                new LayerInventory { Name = "EMPTY", },
            };
            var s = ConfigSuggester.Suggest(inv);
            Func<string, LayerRule> rule = n => s.Config.Rules.FirstOrDefault(r => r.Name == n);
            T.Check(rule("walls") != null && rule("walls").Match.Count == 2 && rule("walls").Action == "wall_double_line", "wall layers grouped (incl. xref-prefixed)");
            T.Check(rule("topo_points") != null && rule("topo_points").Match.SequenceEqual(new[] { "C-TOPO-SPOT" }), "spot heights -> topo_points");
            T.Check(rule("topo_contours") != null && rule("topo_contours").Match.Contains("SiteContours"), "camel-case 'SiteContours' with varying Z -> topo_contours");
            T.Check(rule("ignore_annotation") != null && rule("ignore_annotation").Priority == 100, "annotation ignored with high priority");
            T.Check(rule("grids") != null && rule("floors") != null && rule("point_objects") != null, "grids, floors, point objects detected");
            T.Check(s.Notes.Any(n => n.Contains("ZZZ-WEIRD")) && !s.Notes.Any(n => n.Contains("'EMPTY'")), "unknown layer noted, empty layer skipped");
            T.Check(s.Config.Levels.Count == 1 && s.Config.DefaultLevel == "Level 1", "default level added");
            // the suggested config must itself be valid for the planner
            T.Check(new LayerRuleSet(s.Config.Rules).ConfigProblems.Count == 0, "suggested config passes rule validation");
        }


        static void StressTests()
        {
            Console.WriteLine("Stress (scale)");
            var rnd = new Random(42); var ents = new List<CadEntity>(); int idx = 0;
            // 6,000 double-line rooms (4 walls x 2 faces = 48k lines) on a 100 x 60 grid of buildings, + loose slab lines + 150k topo points + 20k contour vertices
            for (int bx = 0; bx < 100; bx++)
                for (int by = 0; by < 60; by++)
                {
                    double x = bx * 30000, y = by * 30000;
                    ents.Add(Poly(idx++, "A-WALL", true, x, y, x + 8000, y, x + 8000, y + 6000, x, y + 6000));
                    ents.Add(Poly(idx++, "A-WALL", true, x + 200, y + 200, x + 7800, y + 200, x + 7800, y + 5800, x + 200, y + 5800));
                    ents.Add(Line(idx++, "A-SLAB", x, y, x + 8000, y)); ents.Add(Line(idx++, "A-SLAB", x + 8000, y, x + 8000, y + 6000));
                    ents.Add(Line(idx++, "A-SLAB", x + 8000, y + 6000, x, y + 6000)); ents.Add(Line(idx++, "A-SLAB", x, y + 6000, x, y));
                }
            for (int i = 0; i < 150000; i++) ents.Add(Point(idx++, "C-TOPO", rnd.NextDouble() * 3000000, rnd.NextDouble() * 1800000, 10000 + rnd.NextDouble() * 3000));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var plan = BuildPlanner.Plan(ents, Cfg());
            sw.Stop();
            var c = plan.CountsByAction();
            Console.WriteLine("       " + ents.Count + " entities -> " + plan.Ops.Count + " ops in " + sw.ElapsedMilliseconds + " ms");
            T.Check(c["wall_double_line"] == 24000 && c["floor"] == 6000, "6,000 buildings -> 24,000 walls and 6,000 floors (" + c["wall_double_line"] + "/" + c["floor"] + ")");
            T.Check(plan.Ops.First(o => o.Action == "topo_points").Points.Count <= 30000, "150k topo points capped to maxPoints");
            T.Check(sw.ElapsedMilliseconds < 20000, "plans a 200k-entity drawing in < 20 s");
        }

        static BuildConfig Cfg()
        {
            return new BuildConfig
            {
                DefaultLevel = "Level 1",
                Rules =
                {
                    new LayerRule { Name = "walls", Match = { "A-WALL" }, Action = Actions.WallDoubleLine, HeightMm = 3000 },
                    new LayerRule { Name = "slab", Match = { "A-SLAB" }, Action = Actions.Floor, TypeName = "Generic 150mm" },
                    new LayerRule { Name = "topo", Match = { "C-TOPO" }, Action = Actions.TopoPoints, TopoCellMm = 1000 },
                    new LayerRule { Name = "grids", Match = { "S-GRID" }, Action = Actions.Grid },
                    new LayerRule { Name = "trees", Match = { "L-TREE" }, Action = Actions.PointFamily, FamilyName = "Tree", TypeName = "Oak" },
                    new LayerRule { Name = "junk", Match = { "DEFPOINTS" }, Action = Actions.Ignore },
                }
            };
        }

        static List<CadEntity> Drawing()
        {
            var e = new List<CadEntity>();
            int i = 0;
            // double-line walls
            e.Add(Line(i++, "A-WALL", 0, 0, 6000, 0)); e.Add(Line(i++, "A-WALL", 0, 200, 6000, 200));
            e.Add(Line(i++, "A-WALL", 0, 0, 6000, 0)); // duplicate drawn twice
            // slab as 4 loose lines
            e.Add(Line(i++, "A-SLAB", 0, 0, 6000, 0)); e.Add(Line(i++, "A-SLAB", 6000, 0, 6000, 4000)); e.Add(Line(i++, "A-SLAB", 6000, 4000, 0, 4000)); e.Add(Line(i++, "A-SLAB", 0, 4000, 0, 0));
            // bow-tie slab
            e.Add(Poly(i++, "A-SLAB", true, 10000, 0, 12000, 2000, 12000, 0, 10000, 2000));
            // topo grid 20x20 @ 500 mm spacing with a typo
            for (int x = 0; x < 20; x++) for (int y = 0; y < 20; y++) e.Add(Point(i++, "C-TOPO", x * 500, y * 500, 10000 + x * 30 + y * 20));
            e.Add(Point(i++, "C-TOPO", 5000, 5000, 0));
            e.Add(Line(i++, "S-GRID", 0, -1000, 0, 9000)); e.Add(Line(i++, "S-GRID", 100, 0, 100.5, 0)); // 2nd is noise
            e.Add(Point(i++, "L-TREE", 3000, 3000, 0));
            e.Add(Line(i++, "DEFPOINTS", 0, 0, 1, 1)); e.Add(Line(i++, "MYSTERY", 0, 0, 5000, 5000));
            return e;
        }

        static void PlannerTests()
        {
            Console.WriteLine("BuildPlanner");
            var plan = BuildPlanner.Plan(Drawing(), Cfg());
            var c = plan.CountsByAction();
            Func<string, int> n = k => c.ContainsKey(k) ? c[k] : 0;
            T.Check(n("wall_double_line") == 1, "double line wall (duplicate removed) -> 1 wall");
            T.Check(n("floor") == 1, "loose slab lines -> 1 floor; bow-tie skipped");
            T.Check(plan.Report.Issues.Any(x => x.Code == "SELF_INTERSECTING"), "bow-tie reported");
            T.Check(n("topo_points") == 1, "topo -> 1 surface op");
            T.Check(n("grid") == 1, "tiny grid noise dropped");
            T.Check(n("point_family") == 1, "tree point -> family op");
            var wall = plan.Ops.First(o => o.Action == "wall_double_line");
            T.Check(wall.Level == "Level 1" && T.Near(wall.HeightMm, 3000) && T.Near(wall.ThicknessMm, 200), "default level, height & detected thickness applied");
            T.Check(plan.Report.Issues.Any(x => x.Code == "UNMAPPED_LAYER" && x.Layer == "MYSTERY"), "unmapped layer reported");
            T.Check(!plan.Report.Issues.Any(x => x.Layer == "DEFPOINTS"), "explicitly ignored layer is NOT reported as unmapped");
            T.Check(plan.Report.Issues.Any(x => x.Code == "DUPLICATES"), "duplicates reported");
            T.Check(plan.Report.Issues.Any(x => x.Code == "TOPO_Z_OUTLIERS"), "topo Z typo flagged");
            var floor = plan.Ops.First(o => o.Action == "floor");
            T.Check(floor.Points.Count == 4 && GeoMath.SignedArea(floor.Points) > 0, "floor ring is CCW with 4 vertices");

            var plan2 = BuildPlanner.Plan(Drawing().AsEnumerable().Reverse().ToList(), Cfg());
            T.Check(string.Join(",", plan.Ops.Select(o => o.Id).OrderBy(x => x)) == string.Join(",", plan2.Ops.Select(o => o.Id).OrderBy(x => x)), "op ids are deterministic and independent of entity order");

            // far from origin
            var far = Drawing(); foreach (var e in far) for (int k = 0; k < e.Points.Count; k++) e.Points[k] = new Pt(e.Points[k].X + 6e6, e.Points[k].Y + 6e6, e.Points[k].Z);
            T.Check(BuildPlanner.Plan(far, Cfg()).Report.Issues.Any(x => x.Code == "FAR_FROM_ORIGIN"), "survey-coordinate DWG (6,000 km?) -> FAR_FROM_ORIGIN warning");
            var huge = Drawing(); huge.Add(Line(9999, "A-WALL", 0, 0, 5e7, 0));
            T.Check(BuildPlanner.Plan(huge, Cfg()).Report.HasErrors, "stray entity 50 km away -> EXTENTS_TOO_LARGE error");
            T.Check(BuildPlanner.Plan(new List<CadEntity>(), Cfg()).Report.Issues.Any(x => x.Code == "EMPTY"), "empty input -> EMPTY error, no crash");

            // flat topo
            var flat = new List<CadEntity>(); for (int x = 0; x < 6; x++) for (int y = 0; y < 6; y++) flat.Add(Point(x * 6 + y, "C-TOPO", x * 2000, y * 2000, 0));
            T.Check(BuildPlanner.Plan(flat, Cfg()).Report.Issues.Any(x => x.Code == "TOPO_FLAT"), "2D topo (all Z=0) -> TOPO_FLAT warning");
        }
    }
}
