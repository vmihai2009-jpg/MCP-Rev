// Revit-free. Puts one elevation from a survey sheet onto its facade in the model: heights from the level marks,
// position along the facade from the window jambs.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace BarrGazetas.RevitMcp.Survey
{
    public class HeightFit
    {
        /// <summary>Height above datum (mm) = A * sheet y + B.</summary>
        public double A = 1, B;
        public int Marks;
        public double WorstMm;
        public string Note;
        public double Height(double sheetY) => A * sheetY + B;
    }

    public class AlongFit
    {
        /// <summary>Distance along the facade (mm) = Dir * sheet x + Offset.</summary>
        public int Dir = 1;
        public double Offset;
        public int Matched, ElevationJambs, ModelJambs;
        public bool Ambiguous;
        public string Note;
        public double Along(double sheetX) => Dir * sheetX + Offset;
        public double SheetX(double along) => (along - Offset) / Dir;
    }

    public static class ElevationFit
    {
        private static readonly Regex Level = new Regex(@"^\s*\+?(-?\d{1,3}\.\d{2,3})\s*(m)?\s*$", RegexOptions.CultureInvariant);

        /// <summary>A level mark's value in mm above datum ("48.97", "+26.47", "48.97m"), or null.</summary>
        public static double? LevelValueMm(string text)
        {
            var m = Level.Match(text ?? "");
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 1000 : (double?)null;
        }

        /// <summary>
        /// Sheet y -> height above datum, from the level marks: each mark's value is paired with the level line drawn
        /// just under its text (the text sits above its line). Least squares over all pairs; with one pair the scale
        /// is taken as 1:1; with none, the sheet is assumed drawn 1:1 to datum (and the note says so).
        /// </summary>
        public static HeightFit Heights(IEnumerable<DwgText> texts, IEnumerable<DwgLine> levelLines, double xMin, double xMax)
        {
            var all = levelLines.ToList();
            var flat = all.Where(l => Math.Abs(l.A.Y - l.B.Y) < 1 && Math.Abs(l.A.X - l.B.X) > 150).ToList();
            var pairs = new List<(double y, double z)>();
            foreach (var t in texts)
            {
                if (t.At.X < xMin - 2000 || t.At.X > xMax + 2000) continue;
                if (!(LevelValueMm(t.Value) is double z)) continue;
                var line = flat.Where(l => l.A.Y <= t.At.Y + 50 && l.A.Y >= t.At.Y - 600
                                           && Math.Min(l.A.X, l.B.X) <= t.At.X + 3000 && Math.Max(l.A.X, l.B.X) >= t.At.X - 3000)
                               .OrderBy(l => t.At.Y - l.A.Y).FirstOrDefault();
                if (line != null) { pairs.Add((line.A.Y, z)); continue; }
                // a spot-level symbol (a small triangle pointing at the level) next to the text: its lowest point
                var ends = all.Where(l => Math.Abs(l.A.X - l.B.X) < 400 && Math.Abs(l.A.Y - l.B.Y) < 400)
                              .SelectMany(l => new[] { l.A, l.B })
                              .Where(p => Math.Abs(p.X - t.At.X) <= 1000 && p.Y <= t.At.Y + 50 && p.Y >= t.At.Y - 600).ToList();
                if (ends.Count >= 3) pairs.Add((ends.Min(p => p.Y), z));
            }
            var fit = new HeightFit();
            if (pairs.Count == 0) { fit.A = 1; fit.B = 0; fit.Note = "No level marks with their lines in that part of the sheet: assumed drawn 1:1 to datum."; return fit; }
            if (pairs.Count == 1) { fit.A = 1; fit.B = pairs[0].z - pairs[0].y; }
            else
            {
                double my = pairs.Average(p => p.y), mz = pairs.Average(p => p.z);
                double sxx = pairs.Sum(p => (p.y - my) * (p.y - my)), sxy = pairs.Sum(p => (p.y - my) * (p.z - mz));
                fit.A = sxx < 1 ? 1 : sxy / sxx;
                fit.B = mz - fit.A * my;
                // a mark on the wrong line spoils a fit: drop the worst once and refit if it is far off
                var worst = pairs.OrderByDescending(p => Math.Abs(fit.Height(p.y) - p.z)).First();
                if (pairs.Count > 3 && Math.Abs(fit.Height(worst.y) - worst.z) > 50)
                {
                    pairs.Remove(worst);
                    my = pairs.Average(p => p.y); mz = pairs.Average(p => p.z);
                    sxx = pairs.Sum(p => (p.y - my) * (p.y - my)); sxy = pairs.Sum(p => (p.y - my) * (p.z - mz));
                    fit.A = sxx < 1 ? 1 : sxy / sxx; fit.B = mz - fit.A * my;
                }
            }
            fit.Marks = pairs.Count;
            fit.WorstMm = Math.Round(pairs.Max(p => Math.Abs(fit.Height(p.y) - p.z)), 1);
            fit.Note = $"{pairs.Count} level marks; scale {fit.A:0.0000}; worst mark off by {fit.WorstMm:0.#} mm.";
            return fit;
        }

        /// <summary>
        /// Sheet x -> distance along the facade, by matching the elevation's window jambs to the model's windows on
        /// that facade (each window's two jambs, as distances along the facade). Both directions are tried (an
        /// elevation is seen from outside, so it may run either way along the facade line); the offset with most jambs
        /// within <paramref name="tolMm"/> wins, then is refined to their mean.
        /// </summary>
        public static AlongFit Along(IList<double> elevationJambs, IList<double> modelJambs, double scale, double tolMm = 60, int? onlyDir = null)
        {
            var fit = new AlongFit { ElevationJambs = elevationJambs.Count, ModelJambs = modelJambs.Count };
            if (elevationJambs.Count == 0 || modelJambs.Count == 0) { fit.Note = "No jambs to match."; return fit; }
            int bestN = 0; double bestOff = 0; int bestDir = 1;
            var bestPerDir = new Dictionary<int, int> { [1] = 0, [-1] = 0 };
            foreach (int dir in onlyDir is int d ? new[] { d } : new[] { 1, -1 })
            {
                var votes = new Dictionary<long, int>();
                foreach (var e in elevationJambs)
                    foreach (var m in modelJambs)
                    {
                        long bin = (long)Math.Round((m - dir * scale * e) / 20);
                        votes.TryGetValue(bin, out int n); votes[bin] = n + 1;
                    }
                foreach (var kv in votes.OrderByDescending(v => v.Value).Take(40))
                {
                    // count matches for this offset (neighbouring bins too)
                    double off = kv.Key * 20;
                    int matched = elevationJambs.Count(e => modelJambs.Any(m => Math.Abs(m - (dir * scale * e + off)) <= tolMm));
                    if (matched > bestPerDir[dir]) bestPerDir[dir] = matched;
                    if (matched > bestN) { bestN = matched; bestOff = off; bestDir = dir; }
                }
            }
            // refine: the mean residual of the matched jambs
            var res = new List<double>();
            foreach (var e in elevationJambs)
            {
                double u = bestDir * scale * e + bestOff;
                var near = modelJambs.OrderBy(m => Math.Abs(m - u)).First();
                if (Math.Abs(near - u) <= tolMm) res.Add(near - u);
            }
            if (res.Count > 0) bestOff += res.Average();
            fit.Dir = bestDir; fit.Offset = bestOff; fit.Matched = bestN;
            fit.Ambiguous = onlyDir == null && bestPerDir[1] == bestPerDir[-1];
            fit.Note = $"{bestN} of {elevationJambs.Count} elevation jambs land on the model's windows (direction {(bestDir > 0 ? "same as" : "against")} the facade line)." +
                       (fit.Ambiguous ? " The facade is symmetrical: both directions fit equally, so which way it runs could not be told. Heights are unaffected; give the direction (along_direction) if positions matter." : "");
            return fit;
        }

        /// <summary>
        /// The jambs of the windows drawn in an elevation: vertical window-layer lines at least <paramref name="minHeightMm"/>
        /// tall, merged when within 20 mm of each other (a frame drawn as several lines is one jamb).
        /// </summary>
        public static List<double> Jambs(IEnumerable<DwgLine> windowLines, double minHeightMm = 400)
        {
            var xs = windowLines.Where(l => Math.Abs(l.A.X - l.B.X) < 2 && Math.Abs(l.A.Y - l.B.Y) >= minHeightMm)
                                .Select(l => (l.A.X + l.B.X) / 2).OrderBy(x => x).ToList();
            var merged = new List<double>();
            foreach (var x in xs)
                if (merged.Count == 0 || x - merged[merged.Count - 1] > 20) merged.Add(x);
            return merged;
        }

        /// <summary>
        /// Every opening an elevation draws between two sheet x's, one per storey: the window-layer jambs (vertical
        /// lines at least 300 tall) there are grouped where their heights overlap, so a column of windows stacked
        /// floor above floor gives one opening each. Left/right are the outermost jambs of each, bottom/top their ends.
        /// </summary>
        public static List<(double left, double right, double bottom, double top)> Openings(IEnumerable<DwgLine> windowLines, double x0, double x1)
        {
            double lo = Math.Min(x0, x1), hi = Math.Max(x0, x1), pad = Math.Max(50, (hi - lo) * 0.15);
            var lines = windowLines as IList<DwgLine> ?? windowLines.ToList();
            var jambs = lines.Where(l => Math.Abs(l.A.X - l.B.X) < 2 && Math.Abs(l.A.Y - l.B.Y) >= 300
                                               && l.A.X >= lo - pad && l.A.X <= hi + pad)
                                   .Select(l => (x: (l.A.X + l.B.X) / 2, y0: Math.Min(l.A.Y, l.B.Y), y1: Math.Max(l.A.Y, l.B.Y)))
                                   .OrderBy(j => j.y0).ToList();
            var result = new List<(double, double, double, double)>();
            int i = 0;
            while (i < jambs.Count)
            {
                double b = jambs[i].y0, t = jambs[i].y1, l = jambs[i].x, r = jambs[i].x;
                int j = i + 1;
                for (; j < jambs.Count && jambs[j].y0 <= t + 50; j++)
                { t = Math.Max(t, jambs[j].y1); l = Math.Min(l, jambs[j].x); r = Math.Max(r, jambs[j].x); }
                if (r - l >= (hi - lo) * 0.5) // a single jamb line is not an opening
                {
                    // a sill or head drawn as a line across, a little past the jambs' ends, belongs to it
                    double width = r - l;
                    var across = lines.Where(x => Math.Abs(x.A.Y - x.B.Y) < 2 && Math.Abs(x.A.X - x.B.X) >= width * 0.5
                                                  && Math.Min(x.A.X, x.B.X) <= l + pad && Math.Max(x.A.X, x.B.X) >= r - pad).ToList();
                    double bb = across.Where(x => x.A.Y >= b - 200 && x.A.Y <= b + 200).Select(x => x.A.Y).DefaultIfEmpty(b).Min();
                    double tt = across.Where(x => x.A.Y >= t - 200 && x.A.Y <= t + 200).Select(x => x.A.Y).DefaultIfEmpty(t).Max();
                    result.Add((l, r, Math.Min(b, bb), Math.Max(t, tt)));
                }
                i = j;
            }
            return result;
        }

        /// <summary>
        /// The opening an elevation draws between two sheet x's: its outermost jambs, and the lowest and highest
        /// window-layer lines across it (sill and head as drawn). Null when nothing is drawn there.
        /// </summary>
        public static (double left, double right, double bottom, double top)? Opening(IEnumerable<DwgLine> windowLines, double x0, double x1)
        {
            double lo = Math.Min(x0, x1), hi = Math.Max(x0, x1), pad = Math.Max(50, (hi - lo) * 0.15);
            var inside = windowLines.Where(l => Math.Min(l.A.X, l.B.X) >= lo - pad && Math.Max(l.A.X, l.B.X) <= hi + pad).ToList();
            var across = inside.Where(l => Math.Abs(l.A.Y - l.B.Y) < 2 && Math.Abs(l.A.X - l.B.X) >= (hi - lo) * 0.5).ToList();
            var jambs = inside.Where(l => Math.Abs(l.A.X - l.B.X) < 2 && Math.Abs(l.A.Y - l.B.Y) >= 300).ToList();
            if (across.Count < 2 || jambs.Count < 2) return null;
            return (jambs.Min(l => l.A.X), jambs.Max(l => l.A.X), across.Min(l => l.A.Y), across.Max(l => l.A.Y));
        }
    }
}
