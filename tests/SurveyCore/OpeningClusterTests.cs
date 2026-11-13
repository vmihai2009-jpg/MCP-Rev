// 1.34.0: opening clustering (OpeningClusters).
using System;
using System.Collections.Generic;
using System.Linq;
using BarrGazetas.RevitMcp.Survey;

namespace SurveyCoreTests
{
    internal static class OpeningClusterTests
    {
        public static void Run()
        {
            Console.WriteLine("OpeningClusters");
            ClusterType W(long id, int n, double w, double h, string key = "") => new ClusterType { Id = id, Instances = n, Key = key, Sizes = { ["Width"] = w, ["Height"] = h } };
            var c = OpeningClusters.Cluster(new[] { W(1, 1, 1200, 2400), W(2, 3, 1210, 2390), W(3, 1, 1190, 2405), W(4, 1, 900, 2400), W(5, 1, 1200, 2400, "oak") }, 25);
            var big = c.First(x => x.Members.Count == 3);
            T.Check(big.Seed.Id == 2 && big.Members.Select(m => m.Id).OrderBy(i => i).SequenceEqual(new long[] { 1, 2, 3 }), "clusters: alike sizes join the type with most windows");
            T.Check(big.MaxChangeMm == 20, $"clusters: largest change reported ({big.MaxChangeMm})");
            T.Check(c.Count == 3, "clusters: another size, or another material, stays its own type");
        }
    }
}
