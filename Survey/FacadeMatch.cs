// Revit-free. 1.18.0: the model against the survey elevation, line by line, for any facade of any survey.
// Both sides are lines in the facade's own frame (u = mm along the facade, z = mm height): the elevation's drawn lines,
// and the model's edges projected onto the facade plane. Each side is sampled every few cm and each sample looks for
// a line of the other side within a tolerance, running the same way. From that: how much of the drawing the model
// covers (gaps), how much of the model the drawing explains (spurious detail), and for each model element whether it
// matches, matches after one shift along or up (a whole bay or a storey out), or matches nothing.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    /// <summary>A line on the facade plane: u along, z up (mm). Model edges carry their element; drawn lines their layer.</summary>
    public class FacadeEdge
    {
        public double U0, Z0, U1, Z1;
        public long Element;
        public string Layer, Kind;
        public double Len => Math.Sqrt((U1 - U0) * (U1 - U0) + (Z1 - Z0) * (Z1 - Z0));
        public FacadeEdge() { }
        public FacadeEdge(double u0, double z0, double u1, double z1, long element = 0, string layer = null, string kind = null)
        { U0 = u0; Z0 = z0; U1 = u1; Z1 = z1; Element = element; Layer = layer; Kind = kind; }
    }

    public class FacadeMatchOptions
    {
        /// <summary>A sample is matched when a line of the other side runs within this distance (mm).</summary>
        public double TolMm = 20;
        /// <summary>And within this angle (degrees) of the same direction.</summary>
        public double AngleDeg = 15;
        /// <summary>Sampling step along each line (mm).</summary>
        public double StepMm = 25;
        /// <summary>Furthest a whole element is searched for a shift, along and up (mm): a storey is up to ~4.5 m.</summary>
        public double MaxShiftMm = 5000;
        /// <summary>An element is fine at this matched share, shifted when one shift brings it to ShiftedShare.</summary>
        public double OkShare = 0.8, ShiftedShare = 0.7, SpuriousShare = 0.2;
        /// <summary>Kinds that are only measured for coverage, never called spurious (a wall's plain edges have no drawn line).</summary>
        public HashSet<string> CoverageOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "wall", "floor", "roof" };
    }

    public class ElementMatch
    {
        public long Element;
        public string Kind, Verdict;   // ok | shifted | spurious | partial
        public double LengthMm, MatchedShare, ShiftU, ShiftZ, ShiftedShare;
        public double U0, U1, Z0, Z1;
    }

    public class DrawnGap
    {
        public double U0, U1, Z0, Z1, LengthMm;
        public string Layers;
    }

    public class FacadeMatchResult
    {
        /// <summary>Share of the drawing's line length with a model edge within tolerance.</summary>
        public double Coverage;
        /// <summary>Share of the model's (detail) edge length with a drawn line within tolerance.</summary>
        public double Precision;
        public double DrawnMm, ModelMm;
        public List<ElementMatch> Elements = new List<ElementMatch>();
        public List<DrawnGap> Gaps = new List<DrawnGap>();
        /// <summary>Per drawn line: matched or not (same order as given), for pictures.</summary>
        public bool[] DrawnMatched;
        public bool[] ModelMatched;
    }

    public static class FacadeMatch
    {
        private struct Sample { public double U, Z, Du, Dz; public int Line; }

        public static FacadeMatchResult Match(IList<FacadeEdge> drawn, IList<FacadeEdge> model, FacadeMatchOptions o = null)
        {
            o ??= new FacadeMatchOptions();
            var r = new FacadeMatchResult { DrawnMatched = new bool[drawn.Count], ModelMatched = new bool[model.Count] };
            var dIdx = new LineIndex(drawn, o.TolMm * 4);
            var mIdx = new LineIndex(model, o.TolMm * 4);
            double cosMin = Math.Cos(o.AngleDeg * Math.PI / 180);

            // the drawing against the model: coverage
            double dTot = 0, dHit = 0;
            var dHitPerLine = new double[drawn.Count];
            for (int i = 0; i < drawn.Count; i++)
            {
                var s = Samples(drawn[i], i, o.StepMm);
                double each = drawn[i].Len / Math.Max(1, s.Count);
                foreach (var p in s)
                {
                    dTot += each;
                    if (Near(mIdx, model, p, 0, 0, o.TolMm, cosMin, null)) { dHit += each; dHitPerLine[i] += each; }
                }
                r.DrawnMatched[i] = dHitPerLine[i] >= 0.5 * drawn[i].Len;
            }
            r.DrawnMm = dTot; r.Coverage = dTot > 0 ? dHit / dTot : 0;

            // the model against the drawing: per element, and its best single shift
            double mTot = 0, mHit = 0;
            foreach (var g in Enumerable.Range(0, model.Count).GroupBy(i => model[i].Element))
            {
                var lines = g.ToList();
                var samples = lines.SelectMany(i => Samples(model[i], i, o.StepMm)).ToList();
                if (samples.Count == 0) continue;
                double len = lines.Sum(i => model[i].Len), each = len / samples.Count;
                int hit = 0;
                var hitPerLine = new Dictionary<int, int>(); var perLine = new Dictionary<int, int>();
                foreach (var p in samples)
                {
                    perLine[p.Line] = perLine.TryGetValue(p.Line, out var n) ? n + 1 : 1;
                    if (Near(dIdx, drawn, p, 0, 0, o.TolMm, cosMin, null)) { hit++; hitPerLine[p.Line] = hitPerLine.TryGetValue(p.Line, out var h) ? h + 1 : 1; }
                }
                foreach (var i in lines) r.ModelMatched[i] = hitPerLine.TryGetValue(i, out var h) && h >= 0.5 * perLine[i];
                string kind = model[lines[0]].Kind;
                var em = new ElementMatch
                {
                    Element = g.Key, Kind = kind, LengthMm = Math.Round(len), MatchedShare = (double)hit / samples.Count,
                    U0 = lines.Min(i => Math.Min(model[i].U0, model[i].U1)), U1 = lines.Max(i => Math.Max(model[i].U0, model[i].U1)),
                    Z0 = lines.Min(i => Math.Min(model[i].Z0, model[i].Z1)), Z1 = lines.Max(i => Math.Max(model[i].Z0, model[i].Z1)),
                };
                bool coverageOnly = kind != null && o.CoverageOnly.Contains(kind);
                if (!coverageOnly) { mTot += len; mHit += hit * each; }
                if (em.MatchedShare >= o.OkShare) em.Verdict = "ok";
                else
                {
                    var (du, dz, share) = BestShift(samples, drawn, dIdx, o, cosMin);
                    em.ShiftU = du; em.ShiftZ = dz; em.ShiftedShare = share;
                    if (share >= o.ShiftedShare && share > em.MatchedShare + 0.2) em.Verdict = "shifted";
                    else if (em.MatchedShare < o.SpuriousShare) em.Verdict = coverageOnly ? "undrawn" : "spurious";
                    else em.Verdict = "partial";
                }
                r.Elements.Add(em);
            }
            r.ModelMm = mTot; r.Precision = mTot > 0 ? mHit / mTot : 0;

            // gaps: unmatched drawn lines, grouped where they touch
            var miss = Enumerable.Range(0, drawn.Count).Where(i => !r.DrawnMatched[i] && drawn[i].Len >= 50).ToList();
            var groups = Groups(miss, drawn, 30);
            foreach (var grp in groups)
            {
                double len = grp.Sum(i => drawn[i].Len);
                if (len < 300) continue;
                r.Gaps.Add(new DrawnGap
                {
                    U0 = Math.Round(grp.Min(i => Math.Min(drawn[i].U0, drawn[i].U1))), U1 = Math.Round(grp.Max(i => Math.Max(drawn[i].U0, drawn[i].U1))),
                    Z0 = Math.Round(grp.Min(i => Math.Min(drawn[i].Z0, drawn[i].Z1))), Z1 = Math.Round(grp.Max(i => Math.Max(drawn[i].Z0, drawn[i].Z1))),
                    LengthMm = Math.Round(len), Layers = string.Join(", ", grp.Select(i => drawn[i].Layer).Where(x => x != null).Distinct().Take(3)),
                });
            }
            r.Gaps = r.Gaps.OrderByDescending(x => x.LengthMm).ToList();
            return r;
        }

        /// <summary>
        /// The one shift, straight along or straight up, that matches most of an element's samples: votes from each
        /// sample to the drawn lines crossing its own column (for horizontal-ish samples, shift up) or row (vertical-ish,
        /// shift along), binned at the tolerance; the best bin is then checked properly.
        /// </summary>
        private static (double du, double dz, double share) BestShift(List<Sample> samples, IList<FacadeEdge> drawn, LineIndex idx, FacadeMatchOptions o, double cosMin)
        {
            var votesZ = new Dictionary<long, int>(); var votesU = new Dictionary<long, int>();
            double bin = Math.Max(5, o.TolMm);
            var pick = samples.Count > 400 ? samples.Where((_, k) => k % (samples.Count / 400 + 1) == 0).ToList() : samples;
            foreach (var p in pick)
            {
                bool horiz = Math.Abs(p.Du) >= Math.Abs(p.Dz);
                foreach (int i in idx.Along(horiz ? p.U : p.Z, horiz))
                {
                    var d = drawn[i];
                    double lu = d.U1 - d.U0, lz = d.Z1 - d.Z0, len = d.Len;
                    if (len < 1 || Math.Abs((p.Du * lu + p.Dz * lz) / len) < cosMin) continue;
                    if (horiz)
                    {
                        if (Math.Abs(lu) < 1e-6 || (p.U - d.U0) / lu < 0 || (p.U - d.U0) / lu > 1) continue;
                        double z = d.Z0 + lz * (p.U - d.U0) / lu, dz = z - p.Z;
                        if (Math.Abs(dz) > o.MaxShiftMm || Math.Abs(dz) <= o.TolMm) continue;
                        long k = (long)Math.Round(dz / bin); votesZ[k] = votesZ.TryGetValue(k, out var n) ? n + 1 : 1;
                    }
                    else
                    {
                        if (Math.Abs(lz) < 1e-6 || (p.Z - d.Z0) / lz < 0 || (p.Z - d.Z0) / lz > 1) continue;
                        double u = d.U0 + lu * (p.Z - d.Z0) / lz, du = u - p.U;
                        if (Math.Abs(du) > o.MaxShiftMm || Math.Abs(du) <= o.TolMm) continue;
                        long k = (long)Math.Round(du / bin); votesU[k] = votesU.TryGetValue(k, out var n) ? n + 1 : 1;
                    }
                }
            }
            (double, double, double) best = (0, 0, 0);
            foreach (var (k, isZ) in votesZ.OrderByDescending(v => v.Value).Take(3).Select(v => (v.Key, true))
                                     .Concat(votesU.OrderByDescending(v => v.Value).Take(3).Select(v => (v.Key, false))))
            {
                double du = isZ ? 0 : k * bin, dz = isZ ? k * bin : 0;
                int hit = samples.Count(p => Near(idx, drawn, p, du, dz, o.TolMm, cosMin, null));
                double share = (double)hit / samples.Count;
                if (share > best.Item3) best = (du, dz, share);
            }
            return best;
        }

        private static List<Sample> Samples(FacadeEdge e, int line, double step)
        {
            var res = new List<Sample>();
            double len = e.Len;
            if (len < 1) return res;
            int n = Math.Max(1, (int)Math.Round(len / step));
            double du = (e.U1 - e.U0) / len, dz = (e.Z1 - e.Z0) / len;
            for (int k = 0; k < n; k++)
            {
                double t = (k + 0.5) / n;
                res.Add(new Sample { U = e.U0 + (e.U1 - e.U0) * t, Z = e.Z0 + (e.Z1 - e.Z0) * t, Du = du, Dz = dz, Line = line });
            }
            return res;
        }

        private static bool Near(LineIndex idx, IList<FacadeEdge> lines, Sample p, double du, double dz, double tol, double cosMin, HashSet<int> only)
        {
            double u = p.U + du, z = p.Z + dz;
            foreach (int i in idx.Around(u, z))
            {
                var l = lines[i];
                double lu = l.U1 - l.U0, lz = l.Z1 - l.Z0, len = l.Len;
                if (len < 1e-6) continue;
                if (Math.Abs((p.Du * lu + p.Dz * lz) / len) < cosMin) continue;
                double t = Math.Max(0, Math.Min(1, ((u - l.U0) * lu + (z - l.Z0) * lz) / (len * len)));
                double x = l.U0 + lu * t - u, y = l.Z0 + lz * t - z;
                if (x * x + y * y <= tol * tol) return true;
            }
            return false;
        }

        private static List<List<int>> Groups(List<int> ids, IList<FacadeEdge> lines, double tol)
        {
            var parent = ids.ToDictionary(i => i, i => i);
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            var idx = new LineIndex(ids.Select(i => lines[i]).ToList(), tol * 4);
            for (int a = 0; a < ids.Count; a++)
            {
                var l = lines[ids[a]];
                foreach (var (u, z) in new[] { (l.U0, l.Z0), (l.U1, l.Z1) })
                    foreach (int b in idx.Around(u, z))
                    {
                        var m = lines[ids[b]];
                        if (Math.Min(Dist(u, z, m.U0, m.Z0), Dist(u, z, m.U1, m.Z1)) <= tol || PtSeg(u, z, m) <= tol)
                        { int ra = Find(ids[a]), rb = Find(ids[b]); if (ra != rb) parent[ra] = rb; }
                    }
            }
            return ids.GroupBy(Find).Select(g => g.ToList()).ToList();
        }

        private static double Dist(double a, double b, double c, double d) => Math.Sqrt((a - c) * (a - c) + (b - d) * (b - d));
        private static double PtSeg(double u, double z, FacadeEdge l)
        {
            double lu = l.U1 - l.U0, lz = l.Z1 - l.Z0, l2 = lu * lu + lz * lz;
            double t = l2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, ((u - l.U0) * lu + (z - l.Z0) * lz) / l2));
            return Dist(l.U0 + lu * t, l.Z0 + lz * t, u, z);
        }

        /// <summary>A grid over the lines (each line in every cell it passes), and strips along u and z for shift votes.</summary>
        private sealed class LineIndex
        {
            private readonly double _cell;
            private readonly Dictionary<(long, long), List<int>> _grid = new Dictionary<(long, long), List<int>>();
            private readonly Dictionary<long, List<int>> _byU = new Dictionary<long, List<int>>(), _byZ = new Dictionary<long, List<int>>();
            private const double Strip = 250;

            public LineIndex(IList<FacadeEdge> lines, double cell)
            {
                _cell = Math.Max(10, cell);
                for (int i = 0; i < lines.Count; i++)
                {
                    var l = lines[i];
                    int n = Math.Max(1, (int)Math.Ceiling(l.Len / (_cell / 2)));
                    var seen = new HashSet<(long, long)>();
                    for (int k = 0; k <= n; k++)
                    {
                        double t = (double)k / n;
                        var c = ((long)Math.Floor((l.U0 + (l.U1 - l.U0) * t) / _cell), (long)Math.Floor((l.Z0 + (l.Z1 - l.Z0) * t) / _cell));
                        if (seen.Add(c)) { if (!_grid.TryGetValue(c, out var list)) _grid[c] = list = new List<int>(); list.Add(i); }
                    }
                    for (long s = (long)Math.Floor(Math.Min(l.U0, l.U1) / Strip); s <= (long)Math.Floor(Math.Max(l.U0, l.U1) / Strip); s++)
                    { if (!_byU.TryGetValue(s, out var list)) _byU[s] = list = new List<int>(); list.Add(i); }
                    for (long s = (long)Math.Floor(Math.Min(l.Z0, l.Z1) / Strip); s <= (long)Math.Floor(Math.Max(l.Z0, l.Z1) / Strip); s++)
                    { if (!_byZ.TryGetValue(s, out var list)) _byZ[s] = list = new List<int>(); list.Add(i); }
                }
            }

            public IEnumerable<int> Around(double u, double z)
            {
                long cu = (long)Math.Floor(u / _cell), cz = (long)Math.Floor(z / _cell);
                var seen = new HashSet<int>();
                for (long a = cu - 1; a <= cu + 1; a++)
                    for (long b = cz - 1; b <= cz + 1; b++)
                        if (_grid.TryGetValue((a, b), out var list))
                            foreach (var i in list) if (seen.Add(i)) yield return i;
            }

            /// <summary>Lines crossing the column at u (horizontal: true) or the row at z.</summary>
            public IEnumerable<int> Along(double v, bool column)
            {
                var d = column ? _byU : _byZ;
                return d.TryGetValue((long)Math.Floor(v / Strip), out var list) ? list : Enumerable.Empty<int>();
            }
        }
    }
}
