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

    /// <summary>A stretch of an elevation whose level marks were checked together (sheet x, mm as read).</summary>
    public class FitPart
    {
        public double X0, X1, OffsetMm, DiffMm, SpreadMm;
        public int Marks;
        public bool Agrees;
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

    /// <summary>
    /// One window as an elevation draws it, sheet units: the frame (window layer: sill and head as the survey's
    /// S/H labels measure them) and, where drawn, the masonry opening round it (reveal lines on the wall layer).
    /// </summary>
    public class ElevOpening
    {
        public double Left, Right, Bottom, Top;
        public bool HasReveal;
        public double RevealLeft, RevealRight, RevealBottom, RevealTop;
        /// <summary>Width of the opening: the masonry opening where drawn, else the frame.</summary>
        public double OpeningLeft => HasReveal ? RevealLeft : Left;
        public double OpeningRight => HasReveal ? RevealRight : Right;
    }

    /// <summary>A model window or door on a facade: along the facade line (centre and width), its level's height, sill and head (mm above the level).</summary>
    public class FacadeInsert
    {
        public long Id;
        public string Category, Level;
        public double U, W, LevelZ;
        public double? Sill, Head;
    }

    /// <summary>A model insert with the opening the elevation draws for it, all in model mm (heights above the insert's level).</summary>
    public class ElevMatch
    {
        public FacadeInsert Insert;
        /// <summary>The opening's ends along the facade (masonry opening where drawn, else frame).</summary>
        public double U0, U1;
        public double Sill, Head;
        public bool FromReveal;
        public double Width => Math.Abs(U1 - U0);
        /// <summary>What the elevation draws inside the opening (panes, arched head), seen from outside.</summary>
        public WindowPattern Pattern;
        /// <summary>The opening on the sheet (sheet units).</summary>
        public ElevOpening Sheet;
        /// <summary>The facade's direction in plan (unit, model X/Y): U grows along it. Set by whoever knows the facade line.</summary>
        public double DirX, DirY;
    }

    /// <summary>An opening the elevation draws with no model insert at that place and storey.</summary>
    public class ElevMissing
    {
        public double U, W, Z0, Z1;
        /// <summary>Where it is in plan (model mm), set by whoever knows the facade line.</summary>
        public double X, Y;
        public string Level;
        public double? Sill, Head;
        /// <summary>1.16.0: its panes as drawn (for a BG Window type) and the opening on the sheet.</summary>
        public WindowPattern Pattern;
        public ElevOpening Sheet;
    }

    public static class ElevationFit
    {
        /// <summary>
        /// Matches a facade's model inserts to the openings its elevation draws (on each insert's own storey), and
        /// lists the drawn openings no insert covers. modelZ maps a sheet y to a model height; along maps sheet x.
        /// storeys: the levels (name, model mm) a missing opening is put on.
        /// </summary>
        public static (List<ElevMatch> matched, List<FacadeInsert> notDrawn, List<ElevMissing> missing) MatchFacade(
            IList<DwgLine> windowLines, IList<DwgLine> revealLines, IList<FacadeInsert> inserts, HeightFit heights, AlongFit along,
            Func<double, double> modelZ, double facadeLength, IList<(string name, double z)> storeys)
        {
            var matched = new List<ElevMatch>();
            var notDrawn = new List<FacadeInsert>();
            foreach (var m in inserts.OrderBy(m => m.U))
            {
                double x0 = along.SheetX(m.U - m.W / 2) / heights.A, x1 = along.SheetX(m.U + m.W / 2) / heights.A;
                double wantLo = m.LevelZ + (m.Sill ?? 600), wantHi = m.LevelZ + (m.Head ?? (m.Sill ?? 600) + 1800);
                // only openings on the insert's own storey: from its level to the next (a window must not take the one
                // drawn above or below it on the elevation, however wrong its own sill is)
                double storeyTop = storeys.Where(s => s.z > m.LevelZ + 500).Select(s => s.z).DefaultIfEmpty(m.LevelZ + 6000).Min();
                ElevOpening op = null; double bestScore = double.MinValue;
                foreach (var o in Openings(windowLines, x0, x1, revealLines))
                {
                    double zb = modelZ(o.Bottom), zt = modelZ(o.Top);
                    if (zb < m.LevelZ - 150 || zt > storeyTop + 400) continue;
                    double overlap = Math.Min(zt, wantHi) - Math.Max(zb, wantLo);
                    double score = overlap > 0 ? overlap : -Math.Abs((zb + zt) / 2 - (wantLo + wantHi) / 2);
                    if (score > bestScore) { bestScore = score; op = o; }
                }
                if (op == null || bestScore < -1500) { notDrawn.Add(m); continue; }
                double u0 = along.Along(op.OpeningLeft * heights.A), u1 = along.Along(op.OpeningRight * heights.A);
                matched.Add(new ElevMatch
                {
                    Insert = m, U0 = Math.Min(u0, u1), U1 = Math.Max(u0, u1), FromReveal = op.HasReveal,
                    Sill = modelZ(op.Bottom) - m.LevelZ, Head = modelZ(op.Top) - m.LevelZ,
                    Pattern = WindowPattern.Read(windowLines, op, Math.Abs(heights.A)), Sheet = op,
                });
            }

            var missing = new List<ElevMissing>();
            var jambs = Jambs(windowLines);
            var seen = new List<(double lo, double hi, double z0, double z1)>();
            for (int i = 0; i + 1 < jambs.Count; i++)
            {
                double w = (jambs[i + 1] - jambs[i]) * heights.A;
                if (w < 400 || w > 4000) continue;
                double a0 = along.Along(jambs[i] * heights.A), a1 = along.Along(jambs[i + 1] * heights.A), lo = Math.Min(a0, a1), hi = Math.Max(a0, a1);
                if (lo < -200 || hi > facadeLength + 200) continue;
                foreach (var op in Openings(windowLines, jambs[i], jambs[i + 1], revealLines))
                {
                    double zs = modelZ(op.Bottom), zh = modelZ(op.Top);
                    if (zh - zs < 300) continue;
                    if (inserts.Any(m => m.U + m.W / 2 > lo + 50 && m.U - m.W / 2 < hi - 50
                                         && Math.Min(m.LevelZ + (m.Head ?? (m.Sill ?? 600) + 1800), zh) - Math.Max(m.LevelZ + (m.Sill ?? 600), zs) > 0)) continue;
                    if (seen.Any(s => s.hi > lo + 50 && s.lo < hi - 50 && Math.Min(s.z1, zh) - Math.Max(s.z0, zs) > 0)) continue;
                    seen.Add((lo, hi, zs, zh));
                    var lv = storeys.Where(s => s.z <= zs + 50).OrderByDescending(s => s.z).Select(s => ((string, double)?)s).FirstOrDefault();
                    missing.Add(new ElevMissing
                    {
                        U = (lo + hi) / 2, W = hi - lo, Z0 = zs, Z1 = zh, Level = lv?.Item1,
                        Sill = lv == null ? (double?)null : zs - lv.Value.Item2, Head = lv == null ? (double?)null : zh - lv.Value.Item2,
                        Pattern = WindowPattern.Read(windowLines, op, Math.Abs(heights.A)), Sheet = op,
                    });
                }
            }
            return (matched, notDrawn, missing);
        }

        // 1.36.11 (Mount Row, On Centre Surveys): a datum mark may say "20.00m A.O.D." / "20.00 AOD" / "+20.00 OD"
        private static readonly Regex Level = new Regex(@"^\s*\+?(-?\d{1,3}\.\d{2,3})\s*(m)?\s*(A\.?\s*O\.?\s*D\.?|O\.?\s*D\.?)?\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex Datum = new Regex(@"^\s*\+?-?\d{1,3}\.\d{2,3}\s*(m)?\s*(A\.?\s*O\.?\s*D\.?|O\.?\s*D\.?)\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>A level mark's value in mm above datum ("48.97", "+26.47", "48.97m", "20.00m A.O.D."), or null.</summary>
        public static double? LevelValueMm(string text)
        {
            var m = Level.Match(text ?? "");
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 1000 : (double?)null;
        }

        /// <summary>A height that says itself it is above datum ("20.00m A.O.D."): a level mark on whatever layer it sits
        /// (some surveyors put the datum mark on a plain TEXT layer, not a level layer).</summary>
        public static bool IsDatumMark(string text) => Datum.IsMatch(text ?? "");

        /// <summary>A text that is a level mark: on a level layer, or a datum mark on any layer.</summary>
        public static bool IsLevelText(DwgText t, bool onLevelLayer) => onLevelLayer || IsDatumMark(t.Value);

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, HashSet<string>> datumLayers = new();
        /// <summary>The layers that hold datum marks in this set of texts (cached per list): their lines are level lines too
        /// (the datum mark's line and triangle sit on the same layer as its text).</summary>
        public static HashSet<string> DatumLayers(IEnumerable<DwgText> texts)
            => datumLayers.GetValue(texts, k => new HashSet<string>(((IEnumerable<DwgText>)k).Where(t => IsDatumMark(t.Value) && t.Layer != null).Select(t => t.Layer), StringComparer.OrdinalIgnoreCase));

        /// <summary>A line that can mark a level: on a level layer, or on a layer that holds datum marks.</summary>
        public static bool IsLevelLine(DwgLine l, bool onLevelLayer, IEnumerable<DwgText> allTexts) => onLevelLayer || (l.Layer != null && DatumLayers(allTexts).Contains(l.Layer));

        /// <summary>
        /// Sheet y -> height above datum, from the level marks: each mark's value is paired with the level line drawn
        /// just under its text (the text sits above its line). Least squares over all pairs; with one pair the scale
        /// is taken as 1:1; with none, the sheet is assumed drawn 1:1 to datum (and the note says so).
        /// </summary>
        public static HeightFit Heights(IEnumerable<DwgText> texts, IEnumerable<DwgLine> levelLines, double xMin, double xMax)
            => FitPairs(MarkPairs(texts, levelLines, xMin, xMax).Select(p => (p.y, p.z)).ToList());

        /// <summary>
        /// 1.24.0: each level mark's value with the sheet height it marks, and where along the sheet it is (x): the text paired
        /// with the level line drawn just under it, or with the lowest point of a small level triangle beside it.
        /// </summary>
        public static List<(double x, double y, double z)> MarkPairs(IEnumerable<DwgText> texts, IEnumerable<DwgLine> levelLines, double xMin, double xMax)
        {
            var all = levelLines.ToList();
            var flat = all.Where(l => Math.Abs(l.A.Y - l.B.Y) < 1 && Math.Abs(l.A.X - l.B.X) > 150).ToList();
            var pairs = new List<(double x, double y, double z)>();
            foreach (var t in texts)
            {
                if (t.At.X < xMin - 2000 || t.At.X > xMax + 2000) continue;
                if (!(LevelValueMm(t.Value) is double z)) continue;
                var line = flat.Where(l => l.A.Y <= t.At.Y + 50 && l.A.Y >= t.At.Y - 600
                                           && Math.Min(l.A.X, l.B.X) <= t.At.X + 3000 && Math.Max(l.A.X, l.B.X) >= t.At.X - 3000)
                               .OrderBy(l => t.At.Y - l.A.Y).FirstOrDefault();
                if (line != null) { pairs.Add((t.At.X, line.A.Y, z)); continue; }
                // 1.36.11: some surveyors draw the datum line just ABOVE the text (Mount Row: "20.00m A.O.D." with its line
                // ~400 mm over it and a triangle's apex touching the line)
                line = flat.Where(l => l.A.Y > t.At.Y + 50 && l.A.Y <= t.At.Y + 800
                                       && Math.Min(l.A.X, l.B.X) <= t.At.X + 3000 && Math.Max(l.A.X, l.B.X) >= t.At.X - 3000)
                           .OrderBy(l => l.A.Y - t.At.Y).FirstOrDefault();
                if (line != null) { pairs.Add((t.At.X, line.A.Y, z)); continue; }
                // a spot-level symbol (a small triangle pointing at the level) next to the text: its lowest point
                var ends = all.Where(l => Math.Abs(l.A.X - l.B.X) < 400 && Math.Abs(l.A.Y - l.B.Y) < 400)
                              .SelectMany(l => new[] { l.A, l.B })
                              .Where(p => Math.Abs(p.X - t.At.X) <= 1000 && p.Y <= t.At.Y + 50 && p.Y >= t.At.Y - 600).ToList();
                if (ends.Count >= 3) pairs.Add((t.At.X, ends.Min(p => p.Y), z));
            }
            return pairs;
        }

        /// <summary>The height fit from (sheet y, value) pairs: see Heights.</summary>
        public static HeightFit FitPairs(List<(double y, double z)> pairs)
        {
            var fit = new HeightFit();
            if (pairs.Count == 0) { fit.A = 1; fit.B = 0; fit.Note = "No level marks with their lines in that part of the sheet: assumed drawn 1:1 to datum."; return fit; }
            if (pairs.Count == 1) { fit.A = 1; fit.B = pairs[0].z - pairs[0].y; }
            else if (OneToOne(pairs, out double b, out var inliers))
            {
                // drawn 1:1 (survey elevations are): only the offset is fitted, and marks paired with the wrong line
                // (a mark beside another mark's triangle) are left out instead of tilting the scale
                fit.A = 1; fit.B = b;
                int dropped = pairs.Count - inliers.Count;
                pairs = inliers;
                fit.Marks = pairs.Count;
                fit.WorstMm = Math.Round(pairs.Max(p => Math.Abs(fit.Height(p.y) - p.z)), 1);
                fit.Note = $"{pairs.Count} level marks agree the sheet is drawn 1:1 (offset {b:0} mm); worst off by {fit.WorstMm:0.#} mm" +
                           (dropped > 0 ? $"; {dropped} marks paired with the wrong line were left out." : ".");
                return fit;
            }
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
        /// 1.24.0: does one height fit hold along the whole drawing? The level marks are grouped along the sheet (a gap of more
        /// than gapMm between marks starts a new group: another part of the drawing, another block, another drawing beside
        /// it). Each group's own offset (sheet drawn 1:1, the median of value minus sheet height) against the fit for the
        /// whole drawing: a group off by more than disagreeMm is a part where the elevation's heights are not this fit's.
        /// </summary>
        public static List<FitPart> Parts(IList<(double x, double y, double z)> pairs, HeightFit whole, double gapMm = 4000, double disagreeMm = 50)
        {
            var res = new List<FitPart>();
            if (pairs.Count == 0) return res;
            var sorted = pairs.OrderBy(p => p.x).ToList();
            var groups = new List<List<(double x, double y, double z)>> { new List<(double, double, double)> { sorted[0] } };
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].x - sorted[i - 1].x > gapMm) groups.Add(new List<(double, double, double)>());
                groups[groups.Count - 1].Add(sorted[i]);
            }
            foreach (var g in groups)
            {
                var off = g.Select(p => p.z - p.y).OrderBy(v => v).ToList();
                double med = off[off.Count / 2];
                // against the whole fit, at this group's own marks
                var dev = g.Select(p => p.z - whole.Height(p.y)).OrderBy(v => v).ToList();
                double d = dev[dev.Count / 2];
                res.Add(new FitPart
                {
                    X0 = g.Min(p => p.x), X1 = g.Max(p => p.x), Marks = g.Count, OffsetMm = Math.Round(med, 1),
                    DiffMm = Math.Round(d, 1), SpreadMm = Math.Round(off.Last() - off.First(), 1),
                    Agrees = Math.Abs(d) <= disagreeMm,
                });
            }
            return res;
        }

        /// <summary>
        /// The separate drawings on a sheet of elevations side by side: window lines grouped along x where a clear
        /// gap of gapMm separates them. Each part's x and y extent (sheet units as read, mm).
        /// </summary>
        public static List<(double x0, double x1, double y0, double y1)> SheetParts(IEnumerable<DwgLine> windowLines, double gapMm = 2500)
        {
            var lines = windowLines.Where(l => Math.Abs(l.A.Y - l.B.Y) >= 300 || Math.Abs(l.A.X - l.B.X) >= 300).OrderBy(l => Math.Min(l.A.X, l.B.X)).ToList();
            var parts = new List<(double, double, double, double)>();
            if (lines.Count == 0) return parts;
            double x0 = Math.Min(lines[0].A.X, lines[0].B.X), x1 = Math.Max(lines[0].A.X, lines[0].B.X);
            double y0 = Math.Min(lines[0].A.Y, lines[0].B.Y), y1 = Math.Max(lines[0].A.Y, lines[0].B.Y);
            foreach (var l in lines.Skip(1))
            {
                double lx0 = Math.Min(l.A.X, l.B.X), lx1 = Math.Max(l.A.X, l.B.X);
                if (lx0 > x1 + gapMm) { parts.Add((x0, x1, y0, y1)); x0 = lx0; x1 = lx1; y0 = Math.Min(l.A.Y, l.B.Y); y1 = Math.Max(l.A.Y, l.B.Y); continue; }
                x1 = Math.Max(x1, lx1); y0 = Math.Min(y0, Math.Min(l.A.Y, l.B.Y)); y1 = Math.Max(y1, Math.Max(l.A.Y, l.B.Y));
            }
            parts.Add((x0, x1, y0, y1));
            return parts;
        }

        /// <summary>True when most marks (60%+) agree, within 30 mm, on a 1:1 scale: then b is their mean offset.</summary>
        private static bool OneToOne(List<(double y, double z)> pairs, out double b, out List<(double y, double z)> inliers)
        {
            var offs = pairs.Select(p => p.z - p.y).OrderBy(v => v).ToList();
            double med = offs[offs.Count / 2];
            inliers = pairs.Where(p => Math.Abs(p.z - p.y - med) <= 30).ToList();
            b = inliers.Count > 0 ? inliers.Average(p => p.z - p.y) : med;
            return inliers.Count >= Math.Max(2, pairs.Count * 0.6);
        }

        /// <summary>
        /// Sheet x -> distance along the facade, by matching the elevation's window jambs to the model's windows on
        /// that facade (each window's two jambs, as distances along the facade). Both directions are tried (an
        /// elevation is seen from outside, so it may run either way along the facade line); the offset with most jambs
        /// within <paramref name="tolMm"/> wins, then is refined to their mean.
        /// </summary>
        public static AlongFit Along(IList<double> elevationJambs, IList<double> modelJambs, double scale, double tolMm = 60, int? onlyDir = null,
                                     double? sheetX0 = null, double? sheetX1 = null, double? facadeLength = null)
        {
            var fit = new AlongFit { ElevationJambs = elevationJambs.Count, ModelJambs = modelJambs.Count };
            if (elevationJambs.Count == 0 || modelJambs.Count == 0) { fit.Note = "No jambs to match."; return fit; }
            int bestN = 0; double bestOff = 0; int bestDir = 1;
            var bestPerDir = new Dictionary<int, int> { [1] = 0, [-1] = 0 };
            var cands = new List<(int dir, double off, int n)>();
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
                    cands.Add((dir, off, matched));
                }
            }
            // 1.36.15: a regular facade matches almost as well one bay along (only the end jambs are lost): among the offsets within
            // two jambs of the best, the one that puts the drawing's ends on the facade's ends wins, when it is clearly better
            // (Mount Row: windows 1-2 bays off along the facade)
            string endNote = "";
            if (sheetX0 is double x0 && sheetX1 is double x1 && facadeLength is double fl && fl > 0)
            {
                double EndCost(int dir, double off)
                {
                    double a = dir * scale * x0 + off, b = dir * scale * x1 + off;
                    return Math.Abs(Math.Min(a, b) - 0) + Math.Abs(Math.Max(a, b) - fl);
                }
                double bestCost = EndCost(bestDir, bestOff);
                var alt = cands.Where(c => c.n >= bestN - 2 && c.dir == bestDir).OrderBy(c => EndCost(c.dir, c.off)).FirstOrDefault();
                if (alt.n > 0 && EndCost(alt.dir, alt.off) < bestCost - 500 && Math.Abs(alt.off - bestOff) > 300)
                {
                    endNote = $" The drawing's ends sit on the facade's ends {Math.Round((alt.off - bestOff) / 1000.0, 2)} m along from the best jamb match ({alt.n} vs {bestN} jambs): taken.";
                    bestOff = alt.off; bestN = alt.n;
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
                       (fit.Ambiguous ? " The facade is symmetrical: both directions fit equally, so which way it runs could not be told. Heights are unaffected; give the direction (along_direction) if positions matter." : "") + endNote;
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
        public static List<ElevOpening> Openings(IEnumerable<DwgLine> windowLines, double x0, double x1, IEnumerable<DwgLine> revealLines = null)
        {
            double lo = Math.Min(x0, x1), hi = Math.Max(x0, x1), pad = Math.Max(50, (hi - lo) * 0.15);
            var lines = windowLines as IList<DwgLine> ?? windowLines.ToList();
            var reveals = revealLines == null ? new List<(double x, double y0, double y1)>()
                : revealLines.Where(l => Math.Abs(l.A.X - l.B.X) < 2 && Math.Abs(l.A.Y - l.B.Y) >= 300 && l.A.X >= lo - pad - 250 && l.A.X <= hi + pad + 250)
                             .Select(l => (x: (l.A.X + l.B.X) / 2, y0: Math.Min(l.A.Y, l.B.Y), y1: Math.Max(l.A.Y, l.B.Y))).ToList();
            var jambs = lines.Where(l => Math.Abs(l.A.X - l.B.X) < 2 && Math.Abs(l.A.Y - l.B.Y) >= 300
                                               && l.A.X >= lo - pad && l.A.X <= hi + pad)
                                   .Select(l => (x: (l.A.X + l.B.X) / 2, y0: Math.Min(l.A.Y, l.B.Y), y1: Math.Max(l.A.Y, l.B.Y)))
                                   .OrderBy(j => j.y0).ToList();
            var result = new List<ElevOpening>();
            int i = 0;
            while (i < jambs.Count)
            {
                double b = jambs[i].y0, t = jambs[i].y1, l = jambs[i].x, r = jambs[i].x;
                int j = i + 1;
                // a frame drawn in pieces (sashes, a meeting rail, a transom) is one opening: gaps up to 150 mm close
                for (; j < jambs.Count && jambs[j].y0 <= t + 150; j++)
                { t = Math.Max(t, jambs[j].y1); l = Math.Min(l, jambs[j].x); r = Math.Max(r, jambs[j].x); }
                double width = r - l;
                // the two jambs are one window's only when its line work runs across between them (a pane's top or bottom,
                // a meeting rail): the facing jambs of two windows either side of a pier have nothing across the pier
                var cover = lines.Where(x => Math.Abs(x.A.Y - x.B.Y) < 2 && x.A.Y >= b - 200 && x.A.Y <= t + 200
                                             && Math.Min(x.A.X, x.B.X) >= l - 300 && Math.Max(x.A.X, x.B.X) <= r + 300)
                                 .Select(x => (a: Math.Max(Math.Min(x.A.X, x.B.X), l), z: Math.Min(Math.Max(x.A.X, x.B.X), r)))
                                 .Where(s => s.z - s.a > 1).OrderBy(s => s.a).ToList();
                double covered = 0, reach = l;
                foreach (var (sa, sz) in cover) { if (sz > reach) { covered += sz - Math.Max(sa, reach); reach = sz; } }
                bool spanned = width > 0 && covered >= width * 0.5;
                if (width >= (hi - lo) * 0.5 && spanned) // a single jamb line is not an opening
                {
                    // a sill or head drawn as a line across, a little past the jambs' ends, belongs to it
                    var across = lines.Where(x => Math.Abs(x.A.Y - x.B.Y) < 2 && Math.Abs(x.A.X - x.B.X) >= width * 0.5
                                                  && Math.Min(x.A.X, x.B.X) <= l + pad && Math.Max(x.A.X, x.B.X) >= r - pad).ToList();
                    double bb = across.Where(x => x.A.Y >= b - 200 && x.A.Y <= b + 200).Select(x => x.A.Y).DefaultIfEmpty(b).Min();
                    double tt = across.Where(x => x.A.Y >= t - 200 && x.A.Y <= t + 200).Select(x => x.A.Y).DefaultIfEmpty(t).Max();
                    // an arched head: the line work above the jambs that joins on to them (the arch), up to its crown
                    double crownCap = Math.Max(t, tt) + width * 0.75;
                    var above = lines.Where(x => Math.Min(x.A.X, x.B.X) >= l - 5 && Math.Max(x.A.X, x.B.X) <= r + 5
                                                 && Math.Max(x.A.Y, x.B.Y) > t + 5 && Math.Max(x.A.Y, x.B.Y) <= crownCap
                                                 && Math.Abs(x.A.X - x.B.X) < width * 0.6).ToList();
                    for (bool grew = true; grew;)
                    {
                        grew = false;
                        foreach (var x in above)
                            if (Math.Min(x.A.Y, x.B.Y) <= tt + 5 && Math.Max(x.A.Y, x.B.Y) > tt + 0.5) { tt = Math.Max(x.A.Y, x.B.Y); grew = true; }
                    }
                    var o = new ElevOpening { Left = l, Right = r, Bottom = Math.Min(b, bb), Top = Math.Max(t, tt) };
                    // the masonry opening round the frame: the outermost wall-layer line within 120 mm outside the glass
                    // each side, alongside most of it (Plowman Craven: 65 mm out, with a frame line at 30). Lines
                    // further out are the stone surround (on 7 Swallow Place 156-197 mm out all round), not the opening.
                    double fb = o.Bottom, ft = o.Top;
                    bool Beside((double x, double y0, double y1) v) => Math.Min(v.y1, ft) - Math.Max(v.y0, fb) >= (ft - fb) * 0.5;
                    var left = reveals.Where(v => v.x >= l - 120 && v.x <= l - 5 && Beside(v)).ToList();
                    var right = reveals.Where(v => v.x <= r + 120 && v.x >= r + 5 && Beside(v)).ToList();
                    if (left.Count > 0 && right.Count > 0)
                    {
                        o.HasReveal = true;
                        o.RevealLeft = left.Min(v => v.x); o.RevealRight = right.Max(v => v.x);
                        o.RevealBottom = left.Concat(right).Min(v => v.y0); o.RevealTop = left.Concat(right).Max(v => v.y1);
                    }
                    result.Add(o);
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
