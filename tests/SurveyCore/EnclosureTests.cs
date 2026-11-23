// 1.37.0: gap closing, enclosures and the plan self-compare (Enclosures).
using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class EnclosureTests
    {
        static Seg L(double x1, double y1, double x2, double y2) => new Seg(new Pt(x1, y1), new Pt(x2, y2), 0);

        /// <summary>10 x 6 m, 300 walls, a front door 2-3 m along the bottom, a 100 partition at x 5 m with a door 2.0-2.9 m.</summary>
        static List<Seg> Plan(bool partition = true)
        {
            var f = new List<Seg>
            {
                L(0, 0, 2000, 0), L(3000, 0, 10000, 0), L(10000, 0, 10000, 6000), L(10000, 6000, 0, 6000), L(0, 6000, 0, 0),
                L(300, 300, 2000, 300), L(3000, 300, 9700, 300), L(9700, 300, 9700, 5700), L(9700, 5700, 300, 5700), L(300, 5700, 300, 300),
                L(2000, 0, 2000, 300), L(3000, 0, 3000, 300),
            };
            if (partition)
                f.AddRange(new[]
                {
                    L(4950, 300, 4950, 2000), L(4950, 2900, 4950, 5700), L(5050, 300, 5050, 2000), L(5050, 2900, 5050, 5700),
                    L(4950, 2000, 5050, 2000), L(4950, 2900, 5050, 2900),
                });
            return f;
        }

        static List<FitWall> Model(bool partition = true)
        {
            var w = new List<FitWall>
            {
                new FitWall { Id = 1, A = new Pt(150, 150), B = new Pt(9850, 150), ThicknessMm = 300 },
                new FitWall { Id = 2, A = new Pt(9850, 150), B = new Pt(9850, 5850), ThicknessMm = 300 },
                new FitWall { Id = 3, A = new Pt(9850, 5850), B = new Pt(150, 5850), ThicknessMm = 300 },
                new FitWall { Id = 4, A = new Pt(150, 5850), B = new Pt(150, 150), ThicknessMm = 300 },
            };
            if (partition) w.Add(new FitWall { Id = 5, A = new Pt(5000, 150), B = new Pt(5000, 5850), ThicknessMm = 100 });
            return w;
        }

        /// <summary>A grid of n x n rooms, 3 m bays, 200 walls drawn as face pairs, a 900 door in the middle of every wall run.</summary>
        static List<Seg> Grid(int n)
        {
            var f = new List<Seg>(); double bay = 3000, t = 100;
            for (int k = 0; k <= n; k++)
            {
                double c = k * bay;
                for (int r = 0; r < n; r++)
                {
                    double a0 = r * bay + t, a1 = (r + 1) * bay - t, m = (a0 + a1) / 2, d0 = m - 450, d1 = m + 450;
                    foreach (double side in new[] { -t, t })
                    {
                        f.Add(L(c + side, a0, c + side, d0)); f.Add(L(c + side, d1, c + side, a1)); // vertical run with a door
                        f.Add(L(a0, c + side, d0, c + side)); f.Add(L(d1, c + side, a1, c + side)); // horizontal run with a door
                    }
                    f.Add(L(c - t, d0, c + t, d0)); f.Add(L(c - t, d1, c + t, d1)); f.Add(L(d0, c - t, d0, c + t)); f.Add(L(d1, c - t, d1, c + t));
                }
            }
            return f;
        }

        public static void Run()
        {
            Console.WriteLine("Enclosures");
            var o = new EnclosureOptions();
            var doorLines = new List<Seg> { L(2000, 150, 3000, 150), L(5000, 2000, 5000, 2900) };

            var open = Enclosures.Build(Plan(), doorLines, o, closeGaps: false);
            T.Check(open.Rooms.Count() == 0, $"without gap closing the doors leave no room closed ({open.Rooms.Count()})");

            var s = Enclosures.Build(Plan(), doorLines, o, closeGaps: true, names: new[] { (new Pt(2000, 3000), "Living"), (new Pt(7000, 3000), "Kitchen") });
            var rooms = s.Rooms.OrderBy(r => r.Centroid.X).ToList();
            T.Check(rooms.Count == 2, $"gap closing: two rooms ({rooms.Count})");
            T.Check(rooms.All(r => Math.Abs(r.AreaM2 - 25.11) < 0.01), "gap closing: each room 4.65 x 5.4 m = 25.11 m2 (" + string.Join(", ", rooms.Select(r => r.AreaM2.ToString("0.00"))) + ")");
            T.Check(rooms[0].Name == "Living" && rooms[1].Name == "Kitchen", "room names from the plan's labels");
            T.Check(s.Bridges.Count == 4 && s.Bridges.All(b => b.Kind == "opening"), $"four bridges (two per door), all on door lines ({s.Bridges.Count}: {string.Join(",", s.Bridges.Select(b => b.Kind))})");
            T.Check(Math.Abs(s.FootprintM2 - 60) < 0.01, $"footprint 60 m2 ({s.FootprintM2:0.00})");
            var inner = s.Faces.Where(f => f.Kind == "opening").FirstOrDefault(f => f.Centroid.X > 4900 && f.Centroid.X < 5100);
            T.Check(inner != null && inner.Joins.Count == 2 && inner.Joins.Contains(rooms[0].Id) && inner.Joins.Contains(rooms[1].Id), "the partition door joins the two rooms");
            var front = s.Faces.Where(f => f.Kind == "opening").FirstOrDefault(f => f.Centroid.Y < 300);
            T.Check(front != null && front.Joins.Count == 1 && front.Joins[0] == rooms[0].Id, "the front door joins the room to the outside (one room)");

            var m = Enclosures.Build(Enclosures.WallFaceLines(Model()), null, o, closeGaps: false);
            T.Check(m.Rooms.Count() == 2 && Math.Abs(m.FootprintM2 - 60) < 0.05, $"model walls: two rooms, 60 m2 footprint ({m.Rooms.Count()}, {m.FootprintM2:0.00})");
            var c = Enclosures.Compare(s, m);
            T.Check(c.Paired == 2 && c.PairedWithin == 2 && c.MissingInModel == 0 && c.ExtraInModel == 0, $"compare: both rooms paired at IoU >= 0.9 (paired {c.Paired}, mean IoU {c.MeanIou})");
            T.Check(Math.Abs(c.FootprintDiffPct) < 0.1 && c.FootprintIou > 0.98, $"compare: footprint matches ({c.FootprintDiffPct}%, IoU {c.FootprintIou})");

            var mm = Enclosures.Build(Enclosures.WallFaceLines(Model(partition: false)), null, o, closeGaps: false);
            var c2 = Enclosures.Compare(s, mm);
            T.Check(c2.MissingInModel >= 1 && c2.ModelRooms == 1, $"compare: a model without the partition misses a room ({c2.MissingInModel} missing, {c2.ExtraInModel} extra)");

            // a short corner: two faces that stop 120 mm short of meeting
            var corner = new List<Seg> { L(0, 0, 4000, 0), L(4000, 0, 4000, 3880), L(3880, 4000, 0, 4000), L(0, 4000, 0, 0) };
            var cs = Enclosures.Build(corner, null, o, closeGaps: true);
            T.Check(cs.Bridges.Count(b => b.Kind == "corner") == 2 && cs.Rooms.Count() == 1 && Math.Abs(cs.Rooms.First().AreaM2 - 16) < 0.01, $"corner closing: a corner drawn short is run on to meet ({cs.Bridges.Count} bridges, {cs.Rooms.Count()} rooms)");

            // speed: 20 x 20 rooms (some 5 000 lines)
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var big = Enclosures.Build(Grid(20), null, o, closeGaps: true);
            clock.Stop();
            T.Check(big.Rooms.Count() == 400 && clock.ElapsedMilliseconds < 20000, $"speed: a 20 x 20 grid of rooms with a door in every wall traces 400 rooms ({big.Rooms.Count()}, {clock.ElapsedMilliseconds} ms, {big.Segments} lines)");

            // curves stay curves elsewhere; here an arc is cut into chords only to trace the room
            var ch = Enclosures.ArcChords(new double[] { 0, 0, 3000, 0, 180 }, 150);
            T.Check(ch.Count >= 60 && Math.Abs(ch[0].A.X - 3000) < 1e-6 && Math.Abs(ch.Last().B.X + 3000) < 1e-6, $"arc chords: a half circle from 0 to 180 degrees ({ch.Count} chords)");
            var half = new List<Seg>(ch) { L(-3000, 0, 3000, 0) };
            var hs = Enclosures.Build(half, null, o, closeGaps: false);
            T.Check(hs.Rooms.Count() == 1 && Math.Abs(hs.Rooms.First().AreaM2 - Math.PI * 9 / 2) < 0.05, $"a half-round room traces to its area ({hs.Rooms.FirstOrDefault()?.AreaM2:0.00} m2)");
        }
    }
}
