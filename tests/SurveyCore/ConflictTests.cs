// 1.31.0: survey conflict report (Conflicts).
using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class ConflictTests
    {
        public static void Run()
        {
            Console.WriteLine("Conflicts");
            T.Check(Conflicts.Commonest(new double[] { 3000, 3010, 3020, 3500, 2400 }) == 3010, "conflicts: commonest takes the biggest group's median");
            T.Check(Conflicts.Commonest(new double[0]) == null, "conflicts: commonest of nothing is null");
            var v = new List<ConflictValue>
            {
                new ConflictValue { Topic = "L1 height", Source = "plan", ValueMm = 3000 },
                new ConflictValue { Topic = "L1 height", Source = "elevation:Front", ValueMm = 3080 },
                new ConflictValue { Topic = "L1 height", Source = "elevation:Rear", ValueMm = 3070 },
                new ConflictValue { Topic = "L2 height", Source = "elevation:Front", ValueMm = 6000 },
                new ConflictValue { Topic = "L2 height", Source = "elevation:Rear", ValueMm = 6010 },
            };
            var r = Conflicts.Resolve(v, new[] { "plan", "elevation" }, 30).ToDictionary(f => f.Topic);
            T.Check(r["L1 height"].Conflict && r["L1 height"].ChosenSource == "plan" && r["L1 height"].ChosenMm == 3000, "conflicts: plan beats elevation when declared first");
            T.Check(!r["L2 height"].Conflict && r["L2 height"].ChosenSource == "elevation" && r["L2 height"].ChosenMm == 6005, "conflicts: two elevations within tolerance agree, median taken");
            var r2 = Conflicts.Resolve(v, new[] { "elevation", "plan" }, 30).ToDictionary(f => f.Topic);
            T.Check(r2["L1 height"].ChosenMm == 3075, "conflicts: the declared order decides");
            T.Check(Conflicts.Under("elevation:Front", "elevation") && !Conflicts.Under("elevations", "elevation"), "conflicts: source names by prefix");
        }
    }
}
