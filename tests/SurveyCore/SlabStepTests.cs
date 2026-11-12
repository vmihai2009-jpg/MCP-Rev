using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class SlabStepTests
    {
        static SlabSpot Sp(double x, double y, double mm) => new SlabSpot { At = new Pt(x, y), Mm = mm };
        static List<Pt> Rect(double x0, double y0, double x1, double y1) => new List<Pt> { new Pt(x0, y0), new Pt(x1, y0), new Pt(x1, y1), new Pt(x0, y1) };
        static Pt Rot(Pt p, double deg) { double a = deg * Math.PI / 180; return new Pt(p.X * Math.Cos(a) - p.Y * Math.Sin(a), p.X * Math.Sin(a) + p.Y * Math.Cos(a)); }

        // a 10 x 6 m slab, a wall across at x = 5 m (200 thick, as two faces) with a 1 m doorway at y 2-3 m
        static List<Seg> Walls(Func<Pt, Pt> f) => new List<(double, double, double, double)>
        {
            (4900, 0, 4900, 2000), (5100, 0, 5100, 2000), (4900, 3000, 4900, 6000), (5100, 3000, 5100, 6000),
            (0, 0, 10000, 0), (10000, 0, 10000, 6000), (10000, 6000, 0, 6000), (0, 6000, 0, 0),
        }.Select(t => new Seg(f(new Pt(t.Item1, t.Item2)), f(new Pt(t.Item3, t.Item4)), 0)).ToList();

        static List<SlabSpot> Spots(Func<Pt, Pt> f) => new List<SlabSpot>
        {
            Sp(1000, 1000, 0), Sp(3000, 4000, 10), Sp(2000, 5000, -10),   // left room: the slab
            Sp(7000, 1500, -600), Sp(8500, 4500, -610),                    // right room: 600 lower
        }.Select(s => { s.At = f(s.At); return s; }).ToList();

        public static void Run()
        {
            Console.WriteLine("slab steps (1.14.0)");

            // bands
            var spots = new List<SlabSpot> { Sp(0, 0, 0), Sp(0, 0, 10), Sp(0, 0, 20), Sp(0, 0, 50), Sp(0, 0, -900), Sp(0, 0, -910), Sp(0, 0, 1300) };
            var bands = SlabSteps.Bands(spots, 60, 100, 0);
            T.Check(bands.Count == 3, $"bands: three heights ({bands.Count})");
            T.Check(bands.Single(b => b.Main).Spots.Count == 4 && T.Near(bands.Single(b => b.Main).Mm, 20, 1e-9), "bands: the commonest is the slab, at its median");
            var near = SlabSteps.Bands(new List<SlabSpot> { Sp(0, 0, 0), Sp(0, 0, 5), Sp(0, 0, 80), Sp(0, 0, 85) }, 60, 100, 0);
            T.Check(near.Count == 2 && near.All(b => b.Main), "bands: a band within the smallest step joins the slab");

            // trace: an L of cells, and a ring with a hole
            var l = new HashSet<(int, int)> { (0, 0), (1, 0), (2, 0), (0, 1) };
            var tl = SlabSteps.Trace(l, 100, new Pt(0, 0));
            T.Check(tl.Count == 1 && tl[0].Count == 6 && T.Near(GeoMath.SignedArea(tl[0]), 40000, 1e-6), $"trace: an L is one six-corner loop, 0.04 m2 ({tl.Count}, {tl.FirstOrDefault()?.Count})");
            var ring = new HashSet<(int, int)>();
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) if (!(i == 1 && j == 1)) ring.Add((i, j));
            var tr = SlabSteps.Trace(ring, 100, new Pt(0, 0));
            T.Check(tr.Count == 2 && tr.Count(x => GeoMath.SignedArea(x) > 0) == 1 && T.Near(tr.Sum(x => GeoMath.SignedArea(x)), 80000, 1e-6), "trace: a ring is an outline and a clockwise hole");
            var diag = SlabSteps.Trace(new HashSet<(int, int)> { (0, 0), (1, 1) }, 100, new Pt(0, 0));
            T.Check(diag.Count == 2 && diag.All(x => x.Count == 4), "trace: cells touching at a corner give two loops");

            // by grid: the right room is a step, ending at the wall and in the doorway
            var slab = new List<List<Pt>> { Rect(0, 0, 10000, 6000) };
            var plan = SlabSteps.ByGrid(slab, Spots(p => p), Walls(p => p), new SlabStepOptions(), 0);
            T.Check(T.Near(plan.MainMm, 0, 1e-9) && plan.Steps.Count == 1, $"grid: slab at 0 and one step ({plan.Steps.Count})");
            var st = plan.Steps.FirstOrDefault();
            if (st != null)
            {
                GeoMath.BoundingBox(st.Loops.SelectMany(x => x), out var mn, out var mx);
                T.Check(st.Mm == -605 && st.Spots == 2, $"grid: the step is at the right room's spots ({st.Mm}, {st.Spots})");
                T.Check(T.Near(mx.X, 10000, 1) && T.Near(mn.Y, 0, 1) && T.Near(mx.Y, 6000, 1), "grid: the step reaches the slab edge under the outside walls");
                T.Check(mn.X >= 4900 && mn.X <= 5200, $"grid: the step stops at the dividing wall ({mn.X})");
                T.Check(st.AreaM2 > 28 && st.AreaM2 < 31, $"grid: about 30 m2 ({st.AreaM2})");
            }
            // the same building turned 12.6 degrees: the grid follows the walls
            var rp = SlabSteps.ByGrid(new List<List<Pt>> { slab[0].Select(p => Rot(p, 12.6)).ToList() }, Spots(p => Rot(p, 12.6)), Walls(p => Rot(p, 12.6)), new SlabStepOptions(), 0);
            T.Check(T.Near(rp.FrameDeg, 12.6, 0.01), $"grid: frame along the walls ({rp.FrameDeg})");
            T.Check(rp.Steps.Count == 1 && rp.Steps[0].Loops.Count == 1 && rp.Steps[0].Loops[0].Count <= 8 && rp.Steps[0].AreaM2 > 28 && rp.Steps[0].AreaM2 < 31,
                $"grid, turned: one clean step outline ({rp.Steps.FirstOrDefault()?.Loops.FirstOrDefault()?.Count} corners, {rp.Steps.FirstOrDefault()?.AreaM2} m2)");
            // one spot with two needed: no step
            var one = SlabSteps.ByGrid(slab, Spots(p => p).Take(4).ToList(), Walls(p => p), new SlabStepOptions { MinSpots = 2 }, 0);
            T.Check(one.Steps.Count == 0 && one.Notes.Count == 1, "grid: a lone spot is not a step when two are asked for");
            // no walls at all: the nearest spot still splits the slab
            var open = SlabSteps.ByGrid(slab, Spots(p => p), new List<Seg>(), new SlabStepOptions(), 0);
            T.Check(open.Steps.Count == 1 && open.Steps[0].AreaM2 > 15 && open.Steps[0].AreaM2 < 45, $"grid without walls: one step by nearest spot ({open.Steps.FirstOrDefault()?.AreaM2})");

            // by rooms
            var rooms = new List<(string, List<List<Pt>>)> { ("Left", new List<List<Pt>> { Rect(0, 0, 4900, 6000) }), ("Right", new List<List<Pt>> { Rect(5100, 0, 10000, 6000) }) };
            var rs = Spots(p => p); rs.Add(Sp(20000, 0, -900)); // outside every room
            var byRoom = SlabSteps.ByRooms(rooms, rs, new SlabStepOptions(), 0);
            T.Check(byRoom.Steps.Count == 1 && byRoom.Steps[0].Room == "Right" && T.Near(byRoom.Steps[0].AreaM2, 29.4, 0.01), "rooms: the right room is the step, its own outline");
            T.Check(rs.Last().Skipped != null, "rooms: a spot outside every room is left out");

            // the preview is a PNG
            var png = SlabSteps.Preview(slab, plan, Walls(p => p), 400);
            T.Check(png.Length > 100 && png[1] == (byte)'P' && png[2] == (byte)'N' && png[3] == (byte)'G', "preview: a PNG");
        }
    }
}
