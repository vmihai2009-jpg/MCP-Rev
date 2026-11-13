// 1.30.0: line roles from geometry (LineRoles).
using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class LineRoleTests
    {
        public static void Run()
        {
            Console.WriteLine("LineRoles");
            var L = new List<DwgLine>();
            void Add(string layer, double x1, double y1, double x2, double y2) => L.Add(new DwgLine { Layer = layer, A = new Pt(x1, y1), B = new Pt(x2, y2) });
            // walls on a layer called "A-123" (no hint in the name): a 10 x 8 m room, 300 thick
            Add("A-123", 0, 0, 10000, 0); Add("A-123", 300, 300, 9700, 300); Add("A-123", 0, 8000, 10000, 8000); Add("A-123", 300, 7700, 9700, 7700);
            Add("A-123", 0, 0, 0, 8000); Add("A-123", 300, 300, 300, 7700); Add("A-123", 10000, 0, 10000, 8000); Add("A-123", 9700, 300, 9700, 7700);
            // a "WALLS" layer that only holds hatching: 45 degree lines 100 mm apart inside a 2 x 2 m patch
            for (int k = 1; k < 28; k++) { double c = k * 141.4; Add("WALLS", 2000 + Math.Max(0, c - 2000), 2000 + Math.Min(c, 2000), 2000 + Math.Min(c, 2000), 2000 + Math.Max(0, c - 2000)); }
            // a table: a closed 1.6 x 0.8 m rectangle
            Add("X1", 6000, 3000, 7600, 3000); Add("X1", 7600, 3000, 7600, 3800); Add("X1", 7600, 3800, 6000, 3800); Add("X1", 6000, 3800, 6000, 3000);
            var roles = LineRoles.Classify(L);
            var by = LineRoles.ByLayer(L, roles).ToDictionary(x => x.Layer);
            T.Check(by["A-123"].Guess == LineRoles.WallFace, $"roles: a layer with any name holding wall face pairs is walls ({by["A-123"].Guess})");
            T.Check(by["WALLS"].Guess == LineRoles.Hatch, $"roles: a layer named WALLS holding hatching is hatching ({by["WALLS"].Guess})");
            T.Check(by["X1"].Guess == LineRoles.Fitting, $"roles: a small closed shape is a fitting ({by["X1"].Guess})");
        }
    }
}
