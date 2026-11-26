using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BarrGazetas.RevitMcp.Survey
{
    /// <summary>
    /// The Revit-free part of the facade and profile analysis (model_profiles, model_cut, model_facade): a 2D outline
    /// made of lines and arcs (arcs stay arcs, Mihai's rule), its size and its shape normalised to height 1, and what
    /// kind of classical detail a facade element is, from generic words in its name or else from its proportions.
    /// No building's names or sizes are known here.
    /// </summary>
    public static class ModelShapes
    {
        /// <summary>One piece of an outline in a 2D frame (mm): a line a→b, or an arc a→b round centre c with radius r.</summary>
        public sealed class Seg
        {
            public string k { get; set; } = "line";
            public double[] a { get; set; }
            public double[] b { get; set; }
            public double[] c { get; set; }
            public double? r { get; set; }
            /// <summary>For an arc: a point halfway along it, so its side is never in doubt.</summary>
            public double[] m { get; set; }

            public static Seg Line(double ax, double ay, double bx, double by) => new Seg { a = new[] { ax, ay }, b = new[] { bx, by } };
            public static Seg Arc(double ax, double ay, double bx, double by, double cx, double cy, double r, double mx, double my) =>
                new Seg { k = "arc", a = new[] { ax, ay }, b = new[] { bx, by }, c = new[] { cx, cy }, r = r, m = new[] { mx, my } };

            public IEnumerable<double[]> Points()
            {
                yield return a; yield return b;
                if (m != null) yield return m;
            }
        }

        public sealed class Size
        {
            public double widthMm { get; set; }
            public double heightMm { get; set; }
            public double minX { get; set; }
            public double minY { get; set; }
            /// <summary>width / height: how far the profile stands out for each unit of its height.</summary>
            public double depthRatio { get; set; }
            public int lines { get; set; }
            public int arcs { get; set; }
        }

        /// <summary>The box of an outline (arcs counted by their ends and middle) and how many lines and arcs it has.</summary>
        public static Size Measure(IReadOnlyCollection<Seg> segs)
        {
            var pts = (segs ?? Array.Empty<Seg>()).SelectMany(s => s.Points()).Where(p => p != null).ToList();
            if (pts.Count == 0) return null;
            double x0 = pts.Min(p => p[0]), x1 = pts.Max(p => p[0]), y0 = pts.Min(p => p[1]), y1 = pts.Max(p => p[1]);
            double w = x1 - x0, h = y1 - y0;
            return new Size
            {
                widthMm = Math.Round(w, 1), heightMm = Math.Round(h, 1), minX = Math.Round(x0, 1), minY = Math.Round(y0, 1),
                depthRatio = h > 1e-6 ? Math.Round(w / h, 3) : 0,
                lines = segs.Count(s => s.k != "arc"), arcs = segs.Count(s => s.k == "arc"),
            };
        }

        /// <summary>
        /// The outline moved so its box starts at (0, 0) and scaled so its height is 1, rounded to 4 places: the same
        /// moulding at any size gives the same numbers, so profiles from different buildings can be compared and reused.
        /// </summary>
        public static List<Seg> Normalise(IReadOnlyCollection<Seg> segs)
        {
            var s = Measure(segs);
            if (s == null || s.heightMm <= 1e-6) return new List<Seg>();
            double k = 1 / s.heightMm;
            double[] T(double[] p) => p == null ? null : new[] { Math.Round((p[0] - s.minX) * k, 4), Math.Round((p[1] - s.minY) * k, 4) };
            return segs.Select(g => new Seg { k = g.k, a = T(g.a), b = T(g.b), c = T(g.c), m = T(g.m), r = g.r == null ? null : Math.Round(g.r.Value * k, 4) }).ToList();
        }

        /// <summary>Rounds every coordinate to <paramref name="digits"/> (mm outlines go out to 0.1 mm).</summary>
        public static List<Seg> Round(IEnumerable<Seg> segs, int digits = 1)
        {
            double[] T(double[] p) => p == null ? null : p.Select(v => Math.Round(v, digits)).ToArray();
            return (segs ?? Enumerable.Empty<Seg>()).Select(g => new Seg { k = g.k, a = T(g.a), b = T(g.b), c = T(g.c), m = T(g.m), r = g.r == null ? null : Math.Round(g.r.Value, digits) }).ToList();
        }

        // ---------------------------------------------------------------- classical detail kinds

        /// <summary>The kinds model_facade reports, in the order a name is tested (the first word found wins).</summary>
        static readonly (string kind, Regex words)[] Words =
        {
            ("pediment", new Regex(@"pediment|tympan", RegexOptions.IgnoreCase)),
            ("cornice", new Regex(@"cornice|eaves?\b|entablature|crown\s*mould", RegexOptions.IgnoreCase)),
            ("balustrade", new Regex(@"balust|parapet", RegexOptions.IgnoreCase)),
            ("architrave", new Regex(@"architrave|surround|window\s*(frame|trim)|door\s*(frame|trim)|casing", RegexOptions.IgnoreCase)),
            ("console", new Regex(@"console|corbel|bracket|modillion|ancon|cantilever\s*block", RegexOptions.IgnoreCase)),
            ("keystone", new Regex(@"keystone|key\s*stone|voussoir", RegexOptions.IgnoreCase)),
            ("pilaster", new Regex(@"pilaster|column|pier\b|capital|pillar|shaft", RegexOptions.IgnoreCase)),
            ("string course", new Regex(@"string|band\b|course|belt|frieze|dentil", RegexOptions.IgnoreCase)),
            ("sill", new Regex(@"\bsill|cill", RegexOptions.IgnoreCase)),
            ("plinth", new Regex(@"plinth|skirting|base\s*course", RegexOptions.IgnoreCase)),
            ("quoin", new Regex(@"quoin|rustic", RegexOptions.IgnoreCase)),
            ("hood", new Regex(@"hood|drip\s*mould|label\s*mould", RegexOptions.IgnoreCase)),
        };

        /// <summary>
        /// What a facade element is: from generic words in its family/type name first, else from its proportions on the
        /// facade: long and low near the top = cornice, long and low lower down = string course, tall and narrow =
        /// pilaster, small = ornament (console, keystone...). <paramref name="fromTopMm"/> is how far its top is below the
        /// facade's wall top. Returns the kind and whether it came from the name (sure) or the shape (a guess).
        /// </summary>
        public static (string kind, bool byName) DetailKind(string name, double heightMm, double lengthMm, double fromTopMm)
        {
            foreach (var (kind, words) in Words)
                if (!string.IsNullOrEmpty(name) && words.IsMatch(name)) return (kind, true);
            if (heightMm <= 0 || lengthMm <= 0) return ("other", false);
            if (lengthMm >= 4 * heightMm && lengthMm >= 1000)
                return (fromTopMm <= Math.Max(1000, 2 * heightMm) ? "cornice" : "string course", false);
            if (heightMm >= 3 * lengthMm && heightMm >= 1500) return ("pilaster", false);
            if (heightMm <= 600 && lengthMm <= 600) return ("ornament", false);
            return ("other", false);
        }

        /// <summary>
        /// Groups facade planes: an outward direction (degrees, 0 = +X, anticlockwise) and the plane's offset along it (mm).
        /// Two walls are on the same facade when they face within <paramref name="angleTol"/> degrees and their outer faces
        /// are within <paramref name="offsetTolMm"/>. Returns a group index per input, in input order.
        /// </summary>
        public static int[] FacadeGroups(IReadOnlyList<(double angleDeg, double offsetMm)> faces, double angleTol = 3, double offsetTolMm = 400)
        {
            var g = new int[faces.Count];
            var reps = new List<(double ang, double off)>();
            for (int i = 0; i < faces.Count; i++)
            {
                var (ang, off) = faces[i];
                int found = -1;
                for (int j = 0; j < reps.Count && found < 0; j++)
                {
                    double d = Math.Abs(((ang - reps[j].ang) % 360 + 540) % 360 - 180);
                    if (d <= angleTol && Math.Abs(off - reps[j].off) <= offsetTolMm) found = j;
                }
                if (found < 0) { reps.Add((ang, off)); found = reps.Count - 1; }
                g[i] = found;
            }
            return g;
        }

        /// <summary>The compass word for an outward direction in model axes (+Y = north unless the project is rotated).</summary>
        public static string Facing(double angleDeg)
        {
            string[] names = { "E", "NE", "N", "NW", "W", "SW", "S", "SE" };
            int i = (int)Math.Round((((angleDeg % 360) + 360) % 360) / 45) % 8;
            return names[i];
        }
    }
}
