// 1.18.0: the model against the elevation (FacadeMatch) and relief cut back from openings (FacadeRelief.OutsideOpenings).
using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class FacadeCheckTests
    {
        static IEnumerable<FacadeEdge> Rect(double u0, double z0, double u1, double z1, long el = 0, string kind = null)
        {
            yield return new FacadeEdge(u0, z0, u1, z0, el, "L", kind); yield return new FacadeEdge(u1, z0, u1, z1, el, "L", kind);
            yield return new FacadeEdge(u1, z1, u0, z1, el, "L", kind); yield return new FacadeEdge(u0, z1, u0, z0, el, "L", kind);
        }

        public static void Run()
        {
            Console.WriteLine("FacadeMatch / opening mask");
            var drawn = new List<FacadeEdge>();
            drawn.Add(new FacadeEdge(0, 8000, 12000, 8000, 0, "Wall")); drawn.Add(new FacadeEdge(0, 8200, 12000, 8200, 0, "Wall")); // cornice
            drawn.AddRange(Rect(1000, 1000, 2000, 2500));   // window A
            drawn.AddRange(Rect(5000, 4000, 6000, 5000));   // window B (upper storey)
            drawn.Add(new FacadeEdge(9000, 0, 9000, 7000, 0, "Wall")); drawn.Add(new FacadeEdge(9400, 0, 9400, 7000, 0, "Wall")); // pilaster, not modelled

            var model = new List<FacadeEdge>();
            model.Add(new FacadeEdge(0, 8005, 12000, 8005, 1, null, "detail")); model.Add(new FacadeEdge(0, 8195, 12000, 8195, 1, null, "detail")); // cornice, 5 mm out: ok
            model.AddRange(Rect(1000, 1000, 2000, 2500, 2, "window"));          // window A: ok
            model.AddRange(Rect(5000, 1000, 6000, 2000, 3, "detail"));          // a surround a storey too low (3000)
            model.AddRange(Rect(3000, 5500, 3500, 6000, 4, "detail"));          // spurious: nothing drawn there
            model.Add(new FacadeEdge(0, 0, 12000, 0, 5, null, "wall"));          // wall base: coverage only

            var m = FacadeMatch.Match(drawn, model);
            ElementMatch E(long id) => m.Elements.First(e => e.Element == id);
            T.Check(E(1).Verdict == "ok" && E(2).Verdict == "ok", $"match: cornice 5 mm off and the window are ok ({E(1).Verdict}, {E(2).Verdict})");
            T.Check(E(3).Verdict == "shifted" && T.Near(E(3).ShiftZ, 3000, 25) && T.Near(E(3).ShiftU, 0, 1), $"match: a detail a storey low is shifted by +3000 ({E(3).Verdict} {E(3).ShiftU},{E(3).ShiftZ})");
            T.Check(E(4).Verdict == "spurious", $"match: detail with nothing drawn near is spurious ({E(4).Verdict})");
            T.Check(E(5).Verdict != "spurious", $"match: a wall's undrawn edge is not called spurious ({E(5).Verdict})");
            T.Check(m.Gaps.Any(g => g.U0 >= 8990 && g.U1 <= 9410 && g.Z1 >= 6900), $"match: the unmodelled pilaster is a gap ({string.Join("; ", m.Gaps.Select(g => $"{g.U0}-{g.U1} {g.Z0}-{g.Z1}"))})");
            T.Check(m.Gaps.Any(g => g.U0 >= 4990 && g.U1 <= 6010 && g.Z0 >= 3990), "match: window B's drawn place is a gap");
            T.Check(m.Coverage > 0.3 && m.Coverage < 0.8, $"match: coverage between ({m.Coverage:0.00})");
            T.Check(m.Precision > 0.3 && m.Precision < 0.9, $"match: precision counts the shifted and spurious detail ({m.Precision:0.00})");

            // relief cut back from an opening
            var face = new List<Pt> { new Pt(0, 0), new Pt(1000, 0), new Pt(1000, 1000), new Pt(0, 1000) };
            var open = new List<(double, double, double, double)> { (400, 1400, 200, 800) };
            var cut = FacadeRelief.OutsideOpenings(face, open, 1500, 10);
            double area = cut.Sum(r => Math.Abs(GeoMath.SignedArea(r)));
            T.Check(T.Near(area, 1e6 - 610 * 620, 1), $"mask: a face half over an opening keeps only the part outside it ({area:0})");
            bool overlaps = cut.Any(r => r.Any(p => p.X > 391 && p.X < 1000 && p.Y > 191 && p.Y < 809));
            T.Check(!overlaps, "mask: nothing left over the opening");
            var glass = new List<Pt> { new Pt(500, 300), new Pt(900, 300), new Pt(900, 700), new Pt(500, 700) };
            T.Check(FacadeRelief.OutsideOpenings(glass, open).Count == 0, "mask: a face inside the opening (the glass) is left out");
            var wide = new List<Pt> { new Pt(-500, 300), new Pt(1500, 300), new Pt(1500, 700), new Pt(-500, 700) };
            T.Check(FacadeRelief.OutsideOpenings(wide, new List<(double, double, double, double)> { (400, 1000, 0, 1000) }).Count == 2, "mask: a band through an opening is split either side of it");
        }
    }
}
