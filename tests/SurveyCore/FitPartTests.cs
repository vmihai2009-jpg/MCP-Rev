// 1.24.0: one height fit per elevation, checked part by part (ElevationFit.MarkPairs / Parts).
using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class FitPartTests
    {
        public static void Run()
        {
            Console.WriteLine("FitParts");
            var texts = new List<DwgText>(); var lines = new List<DwgLine>();
            void Mark(double x, double y, double value)
            {
                lines.Add(new DwgLine { A = new Pt(x - 500, y), B = new Pt(x + 500, y), Layer = "Level" });
                texts.Add(new DwgText { At = new Pt(x, y + 100), Value = $"+{value / 1000:0.000}", Layer = "Level" });
            }
            // the main drawing: sheet drawn 1:1 with an offset of 30000 mm, five marks over 12 m
            foreach (var (x, y) in new[] { (0.0, 0.0), (3000, 3500), (6000, 7000), (9000, 10500), (12000, 14000) }) Mark(x, y, y + 30000);
            // another drawing beside it on the sheet (or another block), whose heights are 450 mm off this fit
            foreach (var (x, y) in new[] { (20000.0, 0.0), (23000, 3200) }) Mark(x, y, y + 30450);
            var pairs = ElevationFit.MarkPairs(texts, lines, -1000, 30000);
            T.Check(pairs.Count == 7, $"fit parts: every mark paired with its line ({pairs.Count})");
            var whole = ElevationFit.FitPairs(pairs.Select(p => (p.y, p.z)).ToList());
            T.Check(T.Near(whole.B, 30000, 1) && T.Near(whole.A, 1, 1e-9), $"fit parts: the whole fit follows the main drawing ({whole.A} {whole.B})");
            var parts = ElevationFit.Parts(pairs, whole);
            T.Check(parts.Count == 2 && parts[0].Agrees && parts[0].Marks == 5, "fit parts: the main part agrees");
            T.Check(parts.Count == 2 && !parts[1].Agrees && T.Near(parts[1].DiffMm, 450, 1) && parts[1].X0 >= 20000, $"fit parts: the part beside it is 450 mm off ({parts.ElementAtOrDefault(1)?.DiffMm})");
        }
    }
}
