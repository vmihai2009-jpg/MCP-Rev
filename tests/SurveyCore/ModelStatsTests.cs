// 1.46.0: the arithmetic behind the model_* analysis tools (Model Library phase 1).
using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class ModelStatsTests
    {
        public static void Run()
        {
            Console.WriteLine("ModelStats");
            var s = ModelStats.Of(new double[] { 100, 215, 102.5, 100, 300 });
            T.Check(s.n == 5 && s.min == 100 && s.median == 102 && s.max == 300, "summary: n, min, median (rounded), max");
            T.Check(ModelStats.Of(new double[] { 1, 2, 3, 4 }, 1).median == 2.5 && ModelStats.Of(new double[] { 1, 2, 3, 4 }, 2).p25 == 1.75, "median and quartiles interpolate");
            T.Check(ModelStats.Of(new double[0]) == null && ModelStats.Of(new[] { double.NaN }) == null, "nothing to summarise gives null");
            T.Check(ModelStats.Of(new double[] { 7 }).p75 == 7, "one value is its own spread");

            T.Check(ModelStats.Band(100) == "100-149" && ModelStats.Band(149.9) == "100-149" && ModelStats.Band(215) == "200-249", "thickness bands of 50 mm");
            T.Check(ModelStats.Band(0) == "?" && ModelStats.Band(double.NaN) == "?", "no thickness, no band");

            T.Check(ModelStats.WallUse("Party wall 215", "Interior", false, 215) == "party", "party from the name");
            T.Check(ModelStats.WallUse("Generic - 300", "Exterior", false, 300) == "external", "external from the Function");
            T.Check(ModelStats.WallUse("Internal brick 215", "Exterior", false, 215) == "internal", "the name 'internal' wins over a wrong Function");
            T.Check(ModelStats.WallUse("Stud partition 100", "Exterior", false, 100) == "partition", "partition from the name");
            T.Check(ModelStats.WallUse("Generic - 100", "Interior", false, 100) == "partition", "a thin interior wall is a partition");
            T.Check(ModelStats.WallUse("Generic - 225", "Interior", false, 225) == "internal", "a thick interior wall is internal");
            T.Check(ModelStats.WallUse("Brick cavity 300", "Interior", false, 300) == "external", "external words on a thick wall");
            T.Check(ModelStats.WallUse("Basement", "Retaining", false, 400) == "retaining", "retaining from the Function");
            T.Check(ModelStats.WallUse("Storefront", "Exterior", true, 0) == "curtain", "curtain walls");

            // a 300 wall: architrave 60 in front, frame starting 115 in, glass at 150, a sill nosing on the face
            T.Check(ModelStats.Reveal(new double[] { -60, -40, 0, 2, 115, 150, 180, 300 }, 300) == 115, "reveal = first frame face inside the wall");
            T.Check(ModelStats.Reveal(new double[] { -60, 0, 300, 310 }, 300) == null, "nothing inside the wall: no reveal");
            T.Check(ModelStats.Reveal(new double[] { 50 }, 8) == null, "a wall too thin to tell");

            var st = ModelStats.Storeys(new[] { ("L01", 3300.0), ("B1", -3000.0), ("GF", 0.0) });
            T.Check(st.Count == 3 && st[0].level == "B1" && st[0].storeyMm == 3000 && st[1].storeyMm == 3300 && st[2].storeyMm == null, "storeys sorted, floor to floor, none above the top");
            T.Check(ModelStats.NextLevelAbove(new double[] { 0, 3300, 6600 }, 0) == 3300 && ModelStats.NextLevelAbove(new double[] { 0, 3300 }, 3300) == null, "next level above");
            T.Check(ModelStats.NextLevelAbove(new double[] { 0, 300, 3300 }, 0) == 3300, "a mezzanine step under the minimum gap is skipped");

            T.Check(ModelStats.BuildUp(new[] { ("Brick", 102.5), ("Air", 50.0), ("", 100.0), ("Membrane", 0.0) }) == "Brick 102.5 | Air 50 | ? 100", "build-up line, zero layers left out");
            T.Check(ModelStats.BuildUp(new (string, double)[0]) == null, "no layers, no build-up");

            var top = ModelStats.Top(new[] { "a", "b", "a", "", "c", "a", "b" }, 2);
            T.Check(top.Count == 2 && top["a"] == 3 && top["b"] == 2, "most common first, cut to top");

            T.Check(ModelStats.IsGeneratedMark("SurveyToRevit|L01|Wall") && ModelStats.IsGeneratedMark("MCP|facade_relief|F24"), "plugin marks are generated");
            T.Check(!ModelStats.IsGeneratedMark("Existing wall, check on site") && !ModelStats.IsGeneratedMark(null), "a person's comment is not");
        }
    }
}
