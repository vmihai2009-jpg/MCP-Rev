// 1.21.0: curves drawn as short straight pieces become one arc (CurveChains); bulged polyline pieces are arcs (DwgFile.BulgeArc).
using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class CurveTests
    {
        public static void Run()
        {
            Console.WriteLine("Curves");
            var pieces = new List<(Pt, Pt)>();
            // a straight wall, then a 90 degree curve of radius 5 m in 20 pieces, then another straight wall
            pieces.Add((new Pt(-6000, 0), new Pt(0, 0)));
            Pt P(double deg) => new Pt(5000 * Math.Sin(deg * Math.PI / 180), 5000 - 5000 * Math.Cos(deg * Math.PI / 180));
            for (int i = 0; i < 20; i++) pieces.Add((P(i * 4.5), P((i + 1) * 4.5)));
            pieces.Add((new Pt(5000, 5000), new Pt(5000, 12000)));
            // drawn in mixed directions: flip a few
            pieces[5] = (pieces[5].Item2, pieces[5].Item1); pieces[12] = (pieces[12].Item2, pieces[12].Item1);
            // a zigzag (not a curve) and a straight run cut in pieces
            for (int i = 0; i < 8; i++) pieces.Add((new Pt(20000 + i * 300, i % 2 * 200), new Pt(20000 + (i + 1) * 300, (i + 1) % 2 * 200)));
            for (int i = 0; i < 8; i++) pieces.Add((new Pt(30000 + i * 500, 0), new Pt(30000 + (i + 1) * 500, 0)));
            var found = CurveChains.Find(pieces);
            T.Check(found.Count == 1, $"curves: one curve found among straight walls, a zigzag and a cut-up straight line ({found.Count})");
            var c = found.FirstOrDefault();
            T.Check(c != null && T.Near(c.R, 5000, 2) && T.Near(c.Cx, 0, 2) && T.Near(c.Cy, 5000, 2), $"curves: centre and radius ({c?.Cx:0},{c?.Cy:0} r{c?.R:0})");
            T.Check(c != null && T.Near(c.SweepDeg, 90, 0.5) && c.Members.Count == 20 && !c.Members.Contains(0) && !c.Members.Contains(21), $"curves: the 20 pieces, 90 degrees, not the straight walls ({c?.SweepDeg:0.0} {c?.Members.Count})");
            var s0 = c?.PointAt(0) ?? new Pt(); var s1 = c?.PointAt(1) ?? new Pt();
            T.Check(Math.Min(s0.Dist2D(new Pt(0, 0)), s1.Dist2D(new Pt(0, 0))) < 2 && Math.Min(s0.Dist2D(new Pt(5000, 5000)), s1.Dist2D(new Pt(5000, 5000))) < 2, "curves: the arc runs end to end");
            // a T junction in the middle breaks it into two arcs
            pieces.Add((P(45), new Pt(0, 9000)));
            var two = CurveChains.Find(pieces);
            T.Check(two.Count == 2 && two.All(x => T.Near(x.SweepDeg, 45, 0.5)), $"curves: a wall meeting the curve splits it ({two.Count})");

            // the model along a 90 degree face of radius 5 m (centre 0,5000, from -90 to 0 degrees): six straight 300 mm walls inside it
            var face = new[] { ("chain", 0.0, 5000.0, 5000.0, -Math.PI / 2, Math.PI / 2, 20) };
            Pt Q(double deg, double r) => new Pt(r * Math.Cos(deg * Math.PI / 180), 5000 + r * Math.Sin(deg * Math.PI / 180));
            var straight = Enumerable.Range(0, 6).Select(i => new FitWall { Id = 100 + i, A = Q(-90 + i * 15, 4850), B = Q(-90 + (i + 1) * 15, 4850), ThicknessMm = 300, Type = "Stone 300" }).ToList();
            straight.Add(new FitWall { Id = 200, A = new Pt(-6000, 150), B = new Pt(0, 150), ThicknessMm = 300, Type = "Stone 300" });   // the straight wall it meets
            var cw = CurveWalls.Check(face, straight, new List<ArcWall>());
            var f = cw[0];
            T.Check(f.State == "chorded" && f.StraightWalls.Count == 6 && !f.StraightWalls.Contains(200), $"curve walls: six short walls along a curve are chorded ({f.State} {f.StraightWalls.Count})");
            T.Check(T.Near(f.NewR, 4850, 1) && T.Near(f.NewSweep * 180 / Math.PI, 90, 1) && f.Type == "Stone 300", $"curve walls: one curved wall instead, on the walls' side of the face ({f.NewR} {f.NewSweep * 180 / Math.PI:0.0})");
            var arcOk = CurveWalls.Check(face, new List<FitWall> { straight[6] }, new List<ArcWall> { new ArcWall { Id = 300, Cx = 0, Cy = 5000, Radius = 4850, Start = -Math.PI / 2, Sweep = Math.PI / 2, ThicknessMm = 300 } });
            T.Check(arcOk[0].State == "curved" && arcOk[0].Covered > 0.95, $"curve walls: a curved wall there is right ({arcOk[0].State} {arcOk[0].Covered})");
            T.Check(CurveWalls.Check(face, new List<FitWall>(), new List<ArcWall>())[0].State == "missing", "curve walls: nothing there is missing");
            // a polyline piece with a bulge: a half circle from (0,0) to (2000,0), anticlockwise (bulge 1) = centre (1000,0) r 1000, below
            var g = DwgFile.BulgeArc(0, 0, 2000, 0, 1);
            T.Check(T.Near(g.cx, 1000, 1e-6) && T.Near(g.cy, 0, 1e-6) && T.Near(g.r, 1000, 1e-6) && T.Near(g.sweep, Math.PI, 1e-9), "bulge: a half circle");
            var q = DwgFile.BulgeArc(0, 0, 2000, 0, Math.Tan(Math.PI / 8));   // 90 degrees anticlockwise: bows below the chord
            double mid = q.start + q.sweep / 2;
            T.Check(T.Near(q.r, 2000 / Math.Sqrt(2), 1e-6) && q.cy > 0 && q.cy + q.r * Math.Sin(mid) < 0, "bulge: a quarter arc bows to the right side of a->b when anticlockwise");
        }
    }
}
