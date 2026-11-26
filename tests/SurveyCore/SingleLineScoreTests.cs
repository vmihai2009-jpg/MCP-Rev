// 1.42.1: one drawn line scores one wall face only.
using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class SingleLineScoreTests
    {
        public static void Run()
        {
            Console.WriteLine("Single drawn line scores one face (1.42.1)");
            var gate = new FidelityGate();

            // a 100 mm wall 6 m long whose face (y = 0) sits on the only drawn line; its body is y 0..100
            var f = new FidelityInput { Level = "L" };
            f.Walls.Add(new FitWall { Id = 1, A = new Pt(0, 50), B = new Pt(6000, 50), ThicknessMm = 100 });
            f.WallFaces.Add(new Seg(new Pt(0, 0), new Pt(6000, 0), 0));
            var (s, issues) = Fidelity.Measure(f, gate);
            T.Check(s.Walls.WithinTolPct == 100 && !issues.Any(i => i.Kind == "wall_face"),
                $"a single-line wall with its face on the line is within tolerance ({s.Walls.WithinTolPct}%), not half a wall off");

            // both faces drawn, wall built 40 mm off: both faces still checked and wrong
            var g = new FidelityInput { Level = "L" };
            g.Walls.Add(new FitWall { Id = 2, A = new Pt(0, 90), B = new Pt(6000, 90), ThicknessMm = 100 });
            g.WallFaces.Add(new Seg(new Pt(0, 0), new Pt(6000, 0), 0));
            g.WallFaces.Add(new Seg(new Pt(0, 100), new Pt(6000, 100), 0));
            var (s2, i2) = Fidelity.Measure(g, gate);
            T.Check(s2.Walls.WithinTolPct == 0 && i2.Any(i => i.Kind == "wall_face" && Math.Abs(i.DevMm - 40) < 1),
                $"a wall 40 mm off its two drawn faces still fails on both ({s2.Walls.WithinTolPct}%)");

            // single line, wall built 30 mm off it: the drawn face is still found and fails
            var h = new FidelityInput { Level = "L" };
            h.Walls.Add(new FitWall { Id = 3, A = new Pt(0, 80), B = new Pt(6000, 80), ThicknessMm = 100 });
            h.WallFaces.Add(new Seg(new Pt(0, 0), new Pt(6000, 0), 0));
            var (s3, i3) = Fidelity.Measure(h, gate);
            T.Check(s3.Walls.WithinTolPct == 0 && i3.Any(i => i.Kind == "wall_face" && Math.Abs(i.DevMm - 30) < 1),
                $"a single-line wall 30 mm off its line fails at 30 mm, not at 70 ({s3.Walls.WithinTolPct}%)");

            T.Check(Fidelity.OnFaceSide(5, 1) && !Fidelity.OnFaceSide(5, -1) && Fidelity.OnFaceSide(0, -1) && !Fidelity.OnFaceSide(0, 1),
                "a line is matched to the face on its side; on the centre line it goes to one face only");

            // curved: a 100 mm arc wall whose outer face (r 5000) sits on the only drawn arc
            var arc = new ArcWall { Id = 4, Cx = 0, Cy = 0, Radius = 4950, Start = 0, Sweep = Math.PI / 2, ThicknessMm = 100 };
            var pieces = new List<Seg>();
            for (int k = 0; k < 90; k++)
            {
                double a0 = k * Math.PI / 180, a1 = (k + 1) * Math.PI / 180;
                pieces.Add(new Seg(new Pt(5000 * Math.Cos(a0), 5000 * Math.Sin(a0)), new Pt(5000 * Math.Cos(a1), 5000 * Math.Sin(a1)), 0));
            }
            var r = CurvedFit.Measure(arc, pieces, 10);
            T.Check(r.Verified && r.WorstFaceMm < 10 && r.InTolLengthMm >= 0.99 * r.FaceLengthMm,
                $"a curved single-line wall is scored on its drawn face only (worst {r.WorstFaceMm:0} mm)");
        }
    }
}
