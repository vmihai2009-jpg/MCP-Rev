// 1.16.0: a window an elevation draws that the model lacks carries its drawn panes (for a BG Window type).
using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class ElevMissingTests
    {
        static DwgLine L(double x1, double y1, double x2, double y2) => new DwgLine { A = new Pt(x1, y1), B = new Pt(x2, y2), Layer = "Window" };

        public static void Run()
        {
            Console.WriteLine("ElevationFit missing windows");
            // a 1200 x 2000 sash window drawn 1:1 at x 1000-2200, sill at sheet y 900 (model z = sheet y), no model window
            var lines = new List<DwgLine>();
            void Box(double x0, double y0, double x1, double y1) { lines.Add(L(x0, y0, x1, y0)); lines.Add(L(x1, y0, x1, y1)); lines.Add(L(x1, y1, x0, y1)); lines.Add(L(x0, y1, x0, y0)); }
            Box(1000, 900, 2200, 2900);
            Box(1060, 960, 2140, 1880); Box(1060, 1920, 2140, 2840); // two sashes' glass
            var (matched, notDrawn, missing) = ElevationFit.MatchFacade(lines, new List<DwgLine>(), new List<FacadeInsert>(),
                new HeightFit { A = 1, B = 0 }, new AlongFit { Dir = 1, Offset = 0 }, y => y, 5000, new List<(string, double)> { ("Ground", 0), ("First", 3500) });
            T.Check(matched.Count == 0 && missing.Count == 1, $"one drawn window, none modelled: one missing ({missing.Count})");
            var m = missing.FirstOrDefault();
            T.Check(m != null && m.Level == "Ground" && T.Near(m.Sill ?? 0, 900, 1) && T.Near(m.Head ?? 0, 2900, 1), $"missing: Ground, sill 900, head 2900 ({m?.Level} {m?.Sill} {m?.Head})");
            T.Check(m?.Pattern != null && m.Sheet != null, "missing: carries its drawn pattern and sheet opening");
            T.Check(m?.Pattern != null && m.Pattern.Panes.Count == 2, $"missing: two panes read ({m?.Pattern?.Panes.Count})");
        }
    }
}
