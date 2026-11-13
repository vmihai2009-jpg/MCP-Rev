// 1.26.0: one family per repeated detail (FacadeTypes).
using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class FacadeTypeTests
    {
        public static void Run()
        {
            Console.WriteLine("FacadeTypes");
            var lines = new List<(Pt, Pt)>();
            void Box(double u0, double z0, double u1, double z1) { lines.Add((new Pt(u0, z0), new Pt(u1, z0))); lines.Add((new Pt(u1, z0), new Pt(u1, z1))); lines.Add((new Pt(u1, z1), new Pt(u0, z1))); lines.Add((new Pt(u0, z1), new Pt(u0, z0))); }
            // a console 300 wide x 500 high at u 2000, its line reaching into the window opening beside it
            Box(1850, 3000, 2150, 3500);
            var it = new FacadeItem { Id = "C1", Kind = "console", U0 = 1850, U1 = 2150, Z0 = 3000, Z1 = 3500 };
            var faces = FacadeTypes.Faces(it, lines, new List<(double, double, double, double)> { (2100, 3500, 1000, 4000) });
            T.Check(faces.Count == 1, $"types: the console is one face ({faces.Count})");
            var f = faces.FirstOrDefault() ?? new List<Pt>();
            T.Check(f.Count > 0 && T.Near(f.Min(p => p.X), -150, 1) && T.Near(f.Min(p => p.Y), 0, 1) && T.Near(f.Max(p => p.Y), 500, 1), "types: relative to the item's middle and bottom");
            T.Check(f.Count > 0 && f.Max(p => p.X) <= 100 - 10 + 1, $"types: cut back from the drawn opening ({f.Max(p => p.X):0})");
            T.Check(!FacadeTypes.IsPieceType(new FacadeType { Kind = "rustication", Count = 3 }) && FacadeTypes.IsPieceType(new FacadeType { Kind = "keystone", Count = 4 }), "types: runs are not pieces, keystones are");
        }
    }
}
