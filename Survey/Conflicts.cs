// 1.31.0: where the survey's drawings contradict each other. Each topic (a floor's height, a facade's width...) gets one
// value per source (plan spot heights, each elevation's level marks, the plan's facade length...); values further apart
// than the tolerance are a conflict, resolved by a declared precedence (the first source in the list that has a value).
// No Revit types: tested in tests/SurveyCore.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public sealed class ConflictValue
    {
        public string Topic, Source, Where;
        public double ValueMm;
    }

    public sealed class ConflictFinding
    {
        public string Topic;
        public List<ConflictValue> Values = new List<ConflictValue>();
        public double SpreadMm;
        public bool Conflict;
        public string ChosenSource, Rule;
        public double ChosenMm;
    }

    public static class Conflicts
    {
        /// <summary>The value most of the numbers agree on: numbers within band_mm of each other are one group; the median of
        /// the biggest group (ties: the lowest). Null for none.</summary>
        public static double? Commonest(IEnumerable<double> values, double bandMm = 60)
        {
            var v = values.OrderBy(x => x).ToList();
            if (v.Count == 0) return null;
            List<double> best = null, cur = new List<double> { v[0] };
            for (int i = 1; i <= v.Count; i++)
            {
                if (i < v.Count && v[i] - cur[0] <= bandMm) { cur.Add(v[i]); continue; }
                if (best == null || cur.Count > best.Count) best = cur;
                if (i < v.Count) cur = new List<double> { v[i] };
            }
            return Median(best);
        }

        public static double Median(IList<double> v)
        {
            var s = v.OrderBy(x => x).ToList();
            return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
        }

        /// <summary>Does a source ("elevation:Front") come under a precedence entry ("elevation")?</summary>
        public static bool Under(string source, string entry) =>
            string.Equals(source, entry, StringComparison.OrdinalIgnoreCase) || source.StartsWith(entry + ":", StringComparison.OrdinalIgnoreCase);

        /// <summary>One finding per topic: spread of its values, conflict when over tol_mm, and the value the precedence picks
        /// (the first entry with a value; several values under that entry: their median). A topic none of whose sources is
        /// in the precedence takes the median of all.</summary>
        public static List<ConflictFinding> Resolve(IEnumerable<ConflictValue> values, IList<string> precedence, double tolMm)
        {
            var list = new List<ConflictFinding>();
            foreach (var g in values.GroupBy(v => v.Topic))
            {
                var vs = g.ToList();
                var f = new ConflictFinding { Topic = g.Key, Values = vs, SpreadMm = vs.Max(v => v.ValueMm) - vs.Min(v => v.ValueMm) };
                f.Conflict = f.SpreadMm > tolMm && vs.Select(v => v.Source).Distinct().Count() > 1;
                foreach (var e in precedence)
                {
                    var mine = vs.Where(v => Under(v.Source, e)).ToList();
                    if (mine.Count == 0) continue;
                    f.ChosenSource = mine.Count == 1 ? mine[0].Source : e;
                    f.ChosenMm = Median(mine.Select(v => v.ValueMm).ToList());
                    f.Rule = $"precedence: {string.Join(" > ", precedence)}; {e} has a value";
                    break;
                }
                if (f.ChosenSource == null) { f.ChosenSource = "median"; f.ChosenMm = Median(vs.Select(v => v.ValueMm).ToList()); f.Rule = "no source in the precedence: median of all"; }
                list.Add(f);
            }
            return list;
        }
    }
}
