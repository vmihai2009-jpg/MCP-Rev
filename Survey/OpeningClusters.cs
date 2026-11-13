// 1.34.0: window_look gives every window its own type; most of a facade's windows are a few designs. Types of one family
// whose other settings are equal and whose sizes are all within tol_mm of a seed join the seed's cluster; the seed is the
// type with the most windows, so the windows that move to it change by at most tol_mm. No Revit types: tested.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public sealed class ClusterType
    {
        public long Id;
        public int Instances;
        /// <summary>Settings that must match exactly (materials, yes/no...), as one key.</summary>
        public string Key = "";
        /// <summary>Sizes by parameter name, mm.</summary>
        public Dictionary<string, double> Sizes = new Dictionary<string, double>();
    }

    public sealed class TypeCluster
    {
        public ClusterType Seed;
        public List<ClusterType> Members = new List<ClusterType>();
        /// <summary>The largest size change any member's windows take on moving to the seed.</summary>
        public double MaxChangeMm;
    }

    public static class OpeningClusters
    {
        public static List<TypeCluster> Cluster(IEnumerable<ClusterType> types, double tolMm)
        {
            var left = types.OrderByDescending(t => t.Instances).ThenBy(t => t.Id).ToList();
            var result = new List<TypeCluster>();
            while (left.Count > 0)
            {
                var seed = left[0];
                var c = new TypeCluster { Seed = seed };
                foreach (var t in left)
                {
                    if (t.Key != seed.Key || !t.Sizes.Keys.OrderBy(k => k).SequenceEqual(seed.Sizes.Keys.OrderBy(k => k))) continue;
                    double worst = t.Sizes.Count == 0 ? 0 : t.Sizes.Max(kv => Math.Abs(kv.Value - seed.Sizes[kv.Key]));
                    if (worst > tolMm) continue;
                    c.Members.Add(t); c.MaxChangeMm = Math.Max(c.MaxChangeMm, worst);
                }
                foreach (var m in c.Members) left.Remove(m);
                result.Add(c);
            }
            return result;
        }
    }
}
