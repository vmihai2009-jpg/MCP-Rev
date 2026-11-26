using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BarrGazetas.RevitMcp.Survey
{
    /// <summary>
    /// The Revit-free arithmetic behind the model_* analysis tools (ModelAnalysisTools.cs): summaries of many values,
    /// what a wall is used for, which band a thickness falls in, a window's reveal from its geometry. Kept here so the
    /// tests can check it without Revit. Nothing in it knows any one building: names are matched by generic words only.
    /// </summary>
    public static class ModelStats
    {
        /// <summary>count, min, the middle half (p25..p75), median and max, rounded to <paramref name="round"/>.</summary>
        public sealed class Summary
        {
            public int n { get; set; }
            public double min { get; set; }
            public double p25 { get; set; }
            public double median { get; set; }
            public double p75 { get; set; }
            public double max { get; set; }
        }

        public static Summary Of(IEnumerable<double> values, int round = 0)
        {
            var v = (values ?? Enumerable.Empty<double>()).Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).OrderBy(x => x).ToList();
            if (v.Count == 0) return null;
            double R(double x) => Math.Round(x, round);
            return new Summary { n = v.Count, min = R(v[0]), p25 = R(Quantile(v, 0.25)), median = R(Quantile(v, 0.5)), p75 = R(Quantile(v, 0.75)), max = R(v[v.Count - 1]) };
        }

        /// <summary>Linear interpolation between the closest ranks of a sorted list.</summary>
        public static double Quantile(IReadOnlyList<double> sorted, double q)
        {
            if (sorted == null || sorted.Count == 0) return double.NaN;
            if (sorted.Count == 1) return sorted[0];
            double pos = Math.Clamp(q, 0, 1) * (sorted.Count - 1);
            int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
        }

        /// <summary>A thickness band such as "100-149" (width <paramref name="step"/> mm), so like walls group together.</summary>
        public static string Band(double mm, double step = 50)
        {
            if (double.IsNaN(mm) || mm <= 0) return "?";
            double lo = Math.Floor(mm / step) * step;
            return $"{lo:0}-{lo + step - 1:0}";
        }

        static readonly Regex Party = new Regex(@"\bparty\b|separating", RegexOptions.IgnoreCase);
        static readonly Regex Partition = new Regex(@"partition|stud|plasterboard|drywall|\bgyp|metsec|lining", RegexOptions.IgnoreCase);
        static readonly Regex External = new Regex(@"\bext(ernal|erior)?\b|facade|façade|cavity|\bbrick\b|stone|render", RegexOptions.IgnoreCase);
        static readonly Regex Internal = new Regex(@"\bint(ernal|erior)?\b", RegexOptions.IgnoreCase);

        /// <summary>
        /// What a wall is for: external, party, internal, partition, curtain or retaining. From the type's Function
        /// (Revit's own field) and generic words in the type name; the thickness only separates a thin internal wall
        /// (a partition, 150 mm and under) from a thick one.
        /// </summary>
        /// <param name="function">WallType.Function as text: Exterior, Interior, Foundation, Retaining, Soffit, CoreShaft</param>
        public static string WallUse(string typeName, string function, bool curtain, double thicknessMm)
        {
            if (curtain) return "curtain";
            string n = typeName ?? "";
            if (Party.IsMatch(n)) return "party";
            if (string.Equals(function, "Retaining", StringComparison.OrdinalIgnoreCase) || string.Equals(function, "Foundation", StringComparison.OrdinalIgnoreCase)) return "retaining";
            if (Partition.IsMatch(n)) return "partition";
            bool ext = string.Equals(function, "Exterior", StringComparison.OrdinalIgnoreCase);
            if (Internal.IsMatch(n)) ext = false;
            else if (!ext && External.IsMatch(n) && thicknessMm > 150) ext = true;
            if (ext) return "external";
            return thicknessMm > 0 && thicknessMm <= 150 ? "partition" : "internal";
        }

        /// <summary>
        /// A window's or door's reveal: how far in from the wall's outer face its first piece of frame sits. Takes the
        /// depths of the opening's geometry measured inward from the outer face (negative = in front of the wall:
        /// sills, architraves) and the wall's thickness; only depths inside the wall, more than <paramref name="skinMm"/>
        /// from either face, count as frame. Null when nothing lies inside the wall.
        /// </summary>
        public static double? Reveal(IEnumerable<double> depthsFromOuterFaceMm, double wallThicknessMm, double skinMm = 5)
        {
            if (depthsFromOuterFaceMm == null || wallThicknessMm <= 2 * skinMm) return null;
            var inside = depthsFromOuterFaceMm.Where(d => d > skinMm && d < wallThicknessMm - skinMm).ToList();
            return inside.Count == 0 ? (double?)null : Math.Round(inside.Min(), 1);
        }

        /// <summary>Floor-to-floor heights: each level's elevation to the next one up (mm), named by the lower level.</summary>
        public static List<(string level, double elevationMm, double? storeyMm)> Storeys(IEnumerable<(string name, double elevationMm)> levels)
        {
            var l = (levels ?? Enumerable.Empty<(string, double)>()).OrderBy(x => x.Item2).ToList();
            var r = new List<(string, double, double?)>();
            for (int i = 0; i < l.Count; i++)
                r.Add((l[i].Item1, Math.Round(l[i].Item2), i + 1 < l.Count ? Math.Round(l[i + 1].Item2 - l[i].Item2) : (double?)null));
            return r;
        }

        /// <summary>The level whose elevation is closest above <paramref name="baseMm"/> by more than <paramref name="minGapMm"/>; null at the top.</summary>
        public static double? NextLevelAbove(IEnumerable<double> levelElevationsMm, double baseMm, double minGapMm = 500)
        {
            var above = (levelElevationsMm ?? Enumerable.Empty<double>()).Where(e => e > baseMm + minGapMm).ToList();
            return above.Count == 0 ? (double?)null : above.Min();
        }

        /// <summary>A compound build-up as one short line, outside to inside: "Brick 102.5 | Air 50 | Block 100".</summary>
        public static string BuildUp(IEnumerable<(string material, double thicknessMm)> layers)
        {
            var parts = (layers ?? Enumerable.Empty<(string, double)>()).Where(p => p.Item2 > 0)
                .Select(p => $"{(string.IsNullOrWhiteSpace(p.Item1) ? "?" : p.Item1.Trim())} {Math.Round(p.Item2, 1):0.#}").ToList();
            return parts.Count == 0 ? null : string.Join(" | ", parts);
        }

        /// <summary>Most common values first, at most <paramref name="top"/>, as value → count.</summary>
        public static Dictionary<string, int> Top(IEnumerable<string> values, int top = 10) =>
            (values ?? Enumerable.Empty<string>()).Select(v => string.IsNullOrWhiteSpace(v) ? "(empty)" : v)
                .GroupBy(v => v).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(top)
                .ToDictionary(g => g.Key, g => g.Count());

        /// <summary>
        /// Survey to Revit and this hub mark what they build (Comments starting "SurveyToRevit|" or "MCP|").
        /// A model mostly made of such elements was generated, and must not be learned from as a person's work.
        /// </summary>
        public static bool IsGeneratedMark(string comments) =>
            !string.IsNullOrEmpty(comments) && (comments.StartsWith("SurveyToRevit|", StringComparison.OrdinalIgnoreCase)
                || comments.StartsWith("MCP|", StringComparison.OrdinalIgnoreCase));
    }
}
