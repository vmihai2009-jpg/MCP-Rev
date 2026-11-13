// 1.28.0: drawings in one frame (Registration.Align).
using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class RegistrationTests
    {
        public static void Run()
        {
            Console.WriteLine("Registration");
            List<Seg> Plan(double dx, double dy, bool stepped)
            {
                var l = new List<Seg>();
                void W(double x1, double y1, double x2, double y2) => l.Add(new Seg(new Pt(x1 + dx, y1 + dy), new Pt(x2 + dx, y2 + dy), 0));
                // outer walls 20 x 12 m, both faces, 300 thick; two inner walls; a stair wall that only one floor has
                W(0, 0, 20000, 0); W(300, 300, 19700, 300); W(0, 12000, 20000, 12000); W(300, 11700, 19700, 11700);
                W(0, 0, 0, 12000); W(300, 300, 300, 11700); W(20000, 0, 20000, 12000); W(19700, 300, 19700, 11700);
                W(8000, 300, 8000, 11700); W(8200, 300, 8200, 11700); W(300, 6000, 7900, 6000); W(300, 6150, 7900, 6150);
                if (stepped) { W(12000, 300, 12000, 5000); W(12100, 300, 12100, 5000); W(300, 9000, 7900, 9050); }
                return l;
            }
            var below = Plan(0, 0, false);
            var above = Plan(7, -4, true);   // the sheet sits 7 mm east and 4 mm south, and has walls the floor below hasn't
            var r = Registration.Align(above, below);
            T.Check(T.Near(r.Dx, -7, 0.6) && T.Near(r.Dy, 4, 0.6), $"registration: the sheet's shift is found ({r.Dx}, {r.Dy})");
            T.Check(r.ResidualMm < 1 && T.Near(r.RotDeg, 0, 0.01), $"registration: nothing left after it ({r.ResidualMm} mm, {r.RotDeg} deg)");
            var same = Registration.Align(below, below);
            T.Check(same.ShiftMm < 0.5, "registration: a drawing on itself has no shift");
        }
    }
}
