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

            // 1.36.11 (Mount Row, On Centre Surveys): the only height on the sheet is a datum mark "20.00m A.O.D." on a plain
            // TEXT layer, its line ~400 mm ABOVE the text and a triangle's apex on the line
            T.Check(ElevationFit.LevelValueMm("20.00m A.O.D.") == 20000 && ElevationFit.LevelValueMm("+20.00 AOD") == 20000 && ElevationFit.LevelValueMm("20.00 OD") == 20000,
                    "datum mark: 'A.O.D.' / 'AOD' / 'OD' suffixes read");
            T.Check(ElevationFit.IsDatumMark("20.00m A.O.D.") && !ElevationFit.IsDatumMark("20.00") && !ElevationFit.IsDatumMark("Flat 20.00"),
                    "datum mark: only a height that says it is above datum");
            var dt = new List<DwgText> { new DwgText { At = new Pt(73641, 42773), Value = "20.00m A.O.D.", Layer = "TEXT" }, new DwgText { At = new Pt(70000, 30000), Value = "Note", Layer = "TEXT" } };
            var all = new List<DwgLine>
            {
                new DwgLine { A = new Pt(65175, 43173), B = new Pt(73441, 43173), Layer = "TEXT" },                     // the datum line above the text
                new DwgLine { A = new Pt(73000, 43173), B = new Pt(72800, 42900), Layer = "TEXT" },                     // the triangle
                new DwgLine { A = new Pt(60000, 50000), B = new Pt(80000, 50000), Layer = "WALL" },                     // other linework
            };
            var lvlTexts = dt.Where(t => ElevationFit.IsLevelText(t, t.Layer.Contains("Level"))).ToList();
            var lvlLines = all.Where(l => ElevationFit.IsLevelLine(l, l.Layer.Contains("Level"), dt)).ToList();
            T.Check(lvlTexts.Count == 1 && lvlLines.Count == 2, $"datum mark: picked up off the TEXT layer ({lvlTexts.Count} text, {lvlLines.Count} lines)");
            var hf = ElevationFit.Heights(lvlTexts, lvlLines, 60000, 80000);
            T.Check(T.Near(hf.A, 1, 1e-9) && T.Near(hf.B, 20000 - 43173, 1), $"datum mark: line above the text gives B = 20000 - 43173 ({hf.B})");

            // 1.36.13: missing walls outside the building (the neighbours on the plan) are not counted
            var hull = Fidelity.Hull(new List<Pt> { new Pt(0, 0), new Pt(10000, 0), new Pt(10000, 8000), new Pt(0, 8000), new Pt(5000, 4000) });
            T.Check(hull.Count == 4, $"building hull: the 4 corners ({hull.Count})");
            T.Check(Fidelity.InOrNear(hull, new Pt(5000, 4000), 1000) && Fidelity.InOrNear(hull, new Pt(10800, 4000), 1000) && !Fidelity.InOrNear(hull, new Pt(14000, 4000), 1000),
                    "building hull: inside and 0.8 m out count, a neighbour 4 m out doesn't");
        }
    }
}
