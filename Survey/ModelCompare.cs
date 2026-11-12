// Revit-free. Comparing two models of the same building (ours against a reference built by hand): bring them
// together in plan (rotation + shift), then pair elements of a category by where they are and how big they are.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    /// <summary>An element reduced to what the comparison needs: its box centre and size (mm), in a common frame.</summary>
    public class CmpItem
    {
        public long Id;
        public string Key = "";   // category (and optionally level) the pairing stays within
        public string Label = ""; // family : type, for the report
        public Pt C;              // box centre
        public double Sx, Sy, Sz; // box size
        public double Z0 => C.Z - Sz / 2;
        public double Z1 => C.Z + Sz / 2;
    }

    public class CmpPair
    {
        public CmpItem Ref, Ours;
        public double Offset2D, OffsetZ, SizeDiff;
    }

    public class CmpResult
    {
        public List<CmpPair> Pairs = new List<CmpPair>();
        public List<CmpItem> Missing = new List<CmpItem>(); // in the reference, not in ours
        public List<CmpItem> Extra = new List<CmpItem>();   // in ours, not in the reference
    }

    /// <summary>A rigid plan move: rotate by Angle (radians) about the origin, then shift.</summary>
    public struct PlanMove
    {
        public double Angle, Dx, Dy;
        public Pt Apply(Pt p)
        {
            double c = Math.Cos(Angle), s = Math.Sin(Angle);
            return new Pt(c * p.X - s * p.Y + Dx, s * p.X + c * p.Y + Dy, p.Z);
        }
        public static PlanMove Identity => new PlanMove();
    }

    public static class ModelCompare
    {
        /// <summary>
        /// The plan move that best lays `from` onto `to` (iterative closest point, 2D, rigid). Starts from `start`,
        /// pairs each point with its nearest within `radius`, solves the best rotation and shift for those pairs,
        /// shrinks the radius as it settles. Returns the move, the rms of the last pairing and how many paired.
        /// </summary>
        public static (PlanMove move, double rms, int paired) Align(IList<Pt> from, IList<Pt> to, PlanMove start, double radius = 3000, int iterations = 30)
        {
            var move = start;
            double rms = double.NaN; int paired = 0;
            if (from.Count == 0 || to.Count == 0) return (move, rms, 0);
            var grid = new Dictionary<GridKey, List<Pt>>();
            double cell = Math.Max(500, radius);
            foreach (var p in to) Add(grid, cell, p);
            for (int it = 0; it < iterations; it++)
            {
                var pairs = new List<(Pt a, Pt b)>();
                foreach (var p in from)
                {
                    var q = move.Apply(p);
                    var n = Nearest(grid, cell, q, radius);
                    if (n.HasValue) pairs.Add((p, n.Value));
                }
                if (pairs.Count < 3) break;
                // trim the worst 20% so stray elements don't pull the fit
                var d = pairs.Select(x => move.Apply(x.a).Dist2D(x.b)).ToList();
                double cut = d.OrderBy(v => v).ElementAt((int)(d.Count * 0.8));
                var kept = pairs.Where((x, i) => d[i] <= cut + 1e-9).ToList();
                var next = Solve(kept);
                double change = Math.Abs(next.Angle - move.Angle) * 10000 + Math.Abs(next.Dx - move.Dx) + Math.Abs(next.Dy - move.Dy);
                move = next;
                rms = Math.Sqrt(kept.Average(x => { double e = move.Apply(x.a).Dist2D(x.b); return e * e; }));
                paired = kept.Count;
                radius = Math.Max(250, Math.Min(radius, rms * 4));
                if (change < 0.5) break;
            }
            return (move, rms, paired);
        }

        /// <summary>Least-squares rigid 2D move taking each a onto its b.</summary>
        public static PlanMove Solve(IList<(Pt a, Pt b)> pairs)
        {
            double ax = pairs.Average(p => p.a.X), ay = pairs.Average(p => p.a.Y), bx = pairs.Average(p => p.b.X), by = pairs.Average(p => p.b.Y);
            double sxx = 0, sxy = 0;
            foreach (var (a, b) in pairs)
            {
                double x1 = a.X - ax, y1 = a.Y - ay, x2 = b.X - bx, y2 = b.Y - by;
                sxx += x1 * x2 + y1 * y2;
                sxy += x1 * y2 - y1 * x2;
            }
            double ang = Math.Atan2(sxy, sxx), c = Math.Cos(ang), s = Math.Sin(ang);
            return new PlanMove { Angle = ang, Dx = bx - (c * ax - s * ay), Dy = by - (s * ax + c * ay) };
        }

        /// <summary>
        /// Pairs reference items with ours of the same Key: closest centres first (3D), within maxDist, sizes
        /// counted so a window does not pair with a neighbour of another size when both are close.
        /// </summary>
        public static CmpResult Match(IList<CmpItem> reference, IList<CmpItem> ours, double maxDist = 1000)
        {
            var result = new CmpResult();
            foreach (var key in reference.Select(r => r.Key).Concat(ours.Select(o => o.Key)).Distinct())
            {
                var rs = reference.Where(r => r.Key == key).ToList();
                var os = ours.Where(o => o.Key == key).ToList();
                var cands = new List<(double cost, int i, int j)>();
                for (int i = 0; i < rs.Count; i++)
                    for (int j = 0; j < os.Count; j++)
                    {
                        double d = rs[i].C.Dist3D(os[j].C);
                        if (d > maxDist) continue;
                        cands.Add((d + 0.25 * SizeDiff(rs[i], os[j]), i, j));
                    }
                var ri = new bool[rs.Count]; var oj = new bool[os.Count];
                foreach (var (cost, i, j) in cands.OrderBy(c => c.cost))
                {
                    if (ri[i] || oj[j]) continue;
                    ri[i] = oj[j] = true;
                    result.Pairs.Add(new CmpPair
                    {
                        Ref = rs[i], Ours = os[j],
                        Offset2D = rs[i].C.Dist2D(os[j].C), OffsetZ = os[j].C.Z - rs[i].C.Z, SizeDiff = SizeDiff(rs[i], os[j]),
                    });
                }
                result.Missing.AddRange(rs.Where((_, i) => !ri[i]));
                result.Extra.AddRange(os.Where((_, j) => !oj[j]));
            }
            return result;
        }

        /// <summary>Largest difference of the box sizes, plan sizes taken either way round (a box turned 90 degrees).</summary>
        public static double SizeDiff(CmpItem a, CmpItem b)
        {
            double plan = Math.Min(Math.Max(Math.Abs(a.Sx - b.Sx), Math.Abs(a.Sy - b.Sy)), Math.Max(Math.Abs(a.Sx - b.Sy), Math.Abs(a.Sy - b.Sx)));
            return Math.Max(plan, Math.Abs(a.Sz - b.Sz));
        }

        private static void Add(Dictionary<GridKey, List<Pt>> grid, double cell, Pt p)
        {
            var k = new GridKey((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell));
            if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<Pt>();
            l.Add(p);
        }

        private static Pt? Nearest(Dictionary<GridKey, List<Pt>> grid, double cell, Pt q, double radius)
        {
            long cx = (long)Math.Floor(q.X / cell), cy = (long)Math.Floor(q.Y / cell);
            int reach = (int)Math.Ceiling(radius / cell);
            Pt? best = null; double bd = radius;
            for (long x = cx - reach; x <= cx + reach; x++)
                for (long y = cy - reach; y <= cy + reach; y++)
                    if (grid.TryGetValue(new GridKey(x, y), out var l))
                        foreach (var p in l)
                        {
                            double d = p.Dist2D(q);
                            if (d <= bd) { bd = d; best = p; }
                        }
            return best;
        }
    }
}
