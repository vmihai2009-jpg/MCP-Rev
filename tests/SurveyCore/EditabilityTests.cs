// 1.33.0: editability audit score (Editability).
using System;
using System.Collections.Generic;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class EditabilityTests
    {
        public static void Run()
        {
            Console.WriteLine("Editability");
            var f = new List<EditFinding>
            {
                new EditFinding { Kind = "direct shape", Elements = { 1, 2 } },
                new EditFinding { Kind = "family without parameters", Elements = { 2, 3 } },
            };
            // element 2 counts once, at its worst (1.0): 1 - (1 + 1 + 0.5) / 10
            T.Check(Editability.Score(f, 10) == 0.75, $"editability: each element counted once at its worst ({Editability.Score(f, 10)})");
            T.Check(Editability.Score(new List<EditFinding>(), 10) == 1 && Editability.Score(f, 0) == 1, "editability: nothing hard = 1");
            T.Check(Editability.Score(f, 2) == 0, "editability: never below 0");
        }
    }
}
