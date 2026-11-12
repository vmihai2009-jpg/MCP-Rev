using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class DetailTests
    {
        static Seg S(double x1, double y1, double x2, double y2) => new Seg(new Pt(x1, y1), new Pt(x2, y2), 0);

        public static void Run()
        {
            Console.WriteLine("detail (1.9.0)");

            // planes
            var p = SurveyDetail.FitPlane(new List<Pt> { new Pt(0, 0, 1000), new Pt(4000, 0, 2000), new Pt(0, 4000, 1000), new Pt(4000, 4000, 2000) });
            T.Check(T.Near(p.A, 0.25, 1e-9) && T.Near(p.B, 0, 1e-9) && T.Near(p.At(2000, 0), 1500, 1e-6), "plane fit: slope along x, 1 in 4");
            T.Check(T.Near(p.SlopeDeg, Math.Atan(0.25) * 180 / Math.PI, 1e-6), "plane slope in degrees");
            var flat = SurveyDetail.FitPlane(new List<Pt> { new Pt(0, 0, 500), new Pt(3000, 0, 520) });
            T.Check(flat.Flat && T.Near(flat.C, 510, 1e-9), "two spots: flat at their mean");
            var line = SurveyDetail.FitPlane(new List<Pt> { new Pt(0, 0, 500), new Pt(1000, 0, 600), new Pt(2000, 0, 700) });
            T.Check(line.Flat, "spots in a line: no slope across them, so flat");

            // faces: a 10 x 6 m rectangle split by a ridge line into two, with a dangling line and a crossing line
            var segs = new List<Seg>
            {
                S(0, 0, 10000, 0), S(10000, 0, 10000, 6000), S(10000, 6000, 0, 6000), S(0, 6000, 0, 0),
                S(0, 3000, 10000, 3000),         // ridge: splits into two faces
                S(5000, 3000, 5000, 4000),       // dangling: dropped
            };
            var faces = SurveyDetail.Faces(segs);
            T.Check(faces.Count == 2, $"faces: a rectangle with a ridge is two faces ({faces.Count})");
            T.Check(faces.All(f => T.Near(GeoMath.SignedArea(f), 30e6, 1)), "faces: each 30 m2, counter-clockwise");
            T.Check(SurveyDetail.FaceAt(faces, new Pt(2000, 1000)) != SurveyDetail.FaceAt(faces, new Pt(2000, 5000)), "faces: a point finds its own face");
            var crossing = new List<Seg>(segs) { S(4000, -1000, 4000, 7000) }; // crosses the outline and the ridge
            T.Check(SurveyDetail.Faces(crossing).Count == 4, "faces: a line across splits where it crosses");

            // columns: two small squares among a long wall
            var col = new List<Seg> { S(0, 0, 500, 0), S(500, 0, 500, 400), S(500, 400, 0, 400), S(0, 400, 0, 0),
                                      S(3000, 0, 3700, 0), S(3700, 0, 3700, 700), S(3700, 700, 3000, 700), S(3000, 700, 3000, 0),
                                      S(-5000, 5000, 15000, 5000), S(-5000, 5300, 15000, 5300) };
            var cols = SurveyDetail.Columns(col);
            T.Check(cols.Count == 2, $"columns: two small outlines found, the open wall lines left ({cols.Count})");

            // stairs: 10 treads 1100 wide, 250 apart, plus a string line
            var st = new List<Seg>();
            for (int i = 0; i < 10; i++) st.Add(S(0, i * 250, 1100, i * 250));
            st.Add(S(0, 0, 0, 2250)); st.Add(S(1100, 0, 1100, 2250));
            var stairs = SurveyDetail.Stairs(st);
            T.Check(stairs.Count == 1 && stairs[0].Treads == 10 && T.Near(stairs[0].GoingMm, 250) && T.Near(stairs[0].WidthMm, 1100), "stairs: 10 treads, going 250, width 1100");

            // sections: verticals at x = 1000 + t for walls at t = 0, 230, 5000, 5230, 9000
            var walls = new List<double> { 0, 230, 5000, 5230, 9000 };
            var vert = walls.Select(t => 71000 - t).ToList(); // drawn the other way round
            var place = SurveyDetail.PlaceSection(walls, vert);
            T.Check(place != null && place.Dir == -1 && T.Near(place.Offset, 71000, 1) && place.Matched == 5, "section placed on its cut line, mirrored");
            T.Check(T.Near(place.T(71000 - 5000), 5000, 1e-6), "section x back to the plan");
            var soff = SurveyDetail.Soffit(new[] { (0.0, 5000.0, 2700.0), (0.0, 5000.0, 3100.0), (0.0, 5000.0, 300.0) }, 2500, 0, 3500);
            T.Check(soff == 2700, "soffit: the lowest line above head height");

            // model compare
            var a = new List<Pt> { new Pt(0, 0), new Pt(10000, 0), new Pt(10000, 5000), new Pt(0, 5000), new Pt(4000, 2000) };
            var mv = new PlanMove { Angle = 0.02, Dx = 300, Dy = -120 };
            var b = a.Select(mv.Apply).ToList();
            var (fit, rms, n) = ModelCompare.Align(a, b, PlanMove.Identity, 3000);
            T.Check(T.Near(fit.Angle, 0.02, 1e-6) && T.Near(fit.Dx, 300, 0.5) && T.Near(fit.Dy, -120, 0.5) && rms < 1, "align: rotation and shift recovered");
            var far = new PlanMove { Angle = Math.PI / 2 + 0.01, Dx = 1e6, Dy = -2e5 };
            var grid = new List<Pt>(); for (int i = 0; i < 12; i++) for (int j = 0; j < 6; j++) if (i == 0 || j == 0 || i == 11 || j == 5 || i == 4) grid.Add(new Pt(i * 1000, j * 700));
            var (any, rmsAny, nAny) = ModelCompare.AlignAny(grid, grid.Select(far.Apply).ToList());
            T.Check(T.Near(any.Angle, far.Angle, 1e-4) && T.Near(any.Dx, far.Dx, 1) && rmsAny < 1, "align with no start: a quarter turn and a km away found");
            var r = new List<CmpItem> { new CmpItem { Id = 1, Key = "Windows", C = new Pt(0, 0, 0), Sx = 1000, Sy = 200, Sz = 1500 },
                                        new CmpItem { Id = 2, Key = "Windows", C = new Pt(3000, 0, 0), Sx = 1000, Sy = 200, Sz = 1500 } };
            var o = new List<CmpItem> { new CmpItem { Id = 11, Key = "Windows", C = new Pt(20, 0, 0), Sx = 1000, Sy = 200, Sz = 1500 } };
            var m = ModelCompare.Match(r, o, 1000);
            T.Check(m.Pairs.Count == 1 && m.Pairs[0].Ref.Id == 1 && m.Missing.Count == 1 && m.Missing[0].Id == 2, "compare: nearest pairs, the other is missing");
        }
    }
}
