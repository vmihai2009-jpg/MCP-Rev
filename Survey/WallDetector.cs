// Ported from the mcp-servers-for-revit survey pipeline (survey-pipeline.patch, base 86cf705). Revit-free.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public class WallCandidate
    {
        public Pt Start, End;
        public double ThicknessMm;
        public int SegA, SegB;
        public double OverlapMm;
    }

    /// <summary>Finds wall centre-lines from pairs of parallel lines (the usual "double line" wall drawing).</summary>
    public static class WallDetector
    {
        public static List<WallCandidate> FromParallelPairs(IList<Seg> segs, double minThickMm, double maxThickMm, double minOverlapMm, double angleTolDeg)
        {
            var result = new List<WallCandidate>();
            int n = segs.Count;
            if (n < 2) return result;
            double sinTol = Math.Sin(angleTolDeg * Math.PI / 180.0);

            // spatial hash on bounding boxes expanded by maxThick
            double cell = Math.Max(1000.0, maxThickMm * 4);
            var grid = new Dictionary<GridKey, List<int>>();
                        for (int i = 0; i < n; i++)
            {
                double x0 = Math.Min(segs[i].A.X, segs[i].B.X) - maxThickMm, x1 = Math.Max(segs[i].A.X, segs[i].B.X) + maxThickMm;
                double y0 = Math.Min(segs[i].A.Y, segs[i].B.Y) - maxThickMm, y1 = Math.Max(segs[i].A.Y, segs[i].B.Y) + maxThickMm;
                for (long cx = (long)Math.Floor(x0 / cell); cx <= (long)Math.Floor(x1 / cell); cx++)
                    for (long cy = (long)Math.Floor(y0 / cell); cy <= (long)Math.Floor(y1 / cell); cy++)
                    {
                        List<int> l; var k = new GridKey(cx, cy);
                        if (!grid.TryGetValue(k, out l)) { l = new List<int>(); grid[k] = l; }
                        l.Add(i);
                    }
            }

            var candidates = new List<WallCandidate>();
            var tested = new HashSet<GridKey>();
            foreach (var bucket in grid.Values)
            {
                for (int bi = 0; bi < bucket.Count; bi++)
                    for (int bj = bi + 1; bj < bucket.Count; bj++)
                    {
                        int i = Math.Min(bucket[bi], bucket[bj]), j = Math.Max(bucket[bi], bucket[bj]);
                        if (!tested.Add(new GridKey(i, j))) continue;
                        var c = Pair(segs[i], segs[j], i, j, minThickMm, maxThickMm, minOverlapMm, sinTol);
                        if (c != null) candidates.Add(c);
                    }
            }

            // greedy: longest overlap first; a stretch of a segment can only be used once
            var used = new Dictionary<int, List<double[]>>();
            foreach (var c in candidates.OrderByDescending(x => x.OverlapMm).ThenBy(x => x.SegA).ThenBy(x => x.SegB))
            {
                double[] ia = Interval(segs[c.SegA], c.Start, c.End), ib = Interval(segs[c.SegB], c.Start, c.End);
                if (Covered(used, c.SegA, ia) > 0.25 * c.OverlapMm || Covered(used, c.SegB, ib) > 0.25 * c.OverlapMm) continue;
                Mark(used, c.SegA, ia); Mark(used, c.SegB, ib);
                result.Add(c);
            }
            return result;
        }

        private static WallCandidate Pair(Seg a, Seg b, int ia, int ib, double minT, double maxT, double minOverlap, double sinTol)
        {
            double ax = a.B.X - a.A.X, ay = a.B.Y - a.A.Y, la = Math.Sqrt(ax * ax + ay * ay);
            double bx = b.B.X - b.A.X, by = b.B.Y - b.A.Y, lb = Math.Sqrt(bx * bx + by * by);
            if (la < minOverlap || lb < minOverlap) return null;
            double dx = ax / la, dy = ay / la;
            if (Math.Abs(dx * (by / lb) - dy * (bx / lb)) > sinTol) return null; // not parallel
            double nx = -dy, ny = dx;
            double s0 = (b.A.X - a.A.X) * nx + (b.A.Y - a.A.Y) * ny, s1 = (b.B.X - a.A.X) * nx + (b.B.Y - a.A.Y) * ny;
            double s = (s0 + s1) / 2.0, thick = Math.Abs(s);
            if (thick < minT || thick > maxT) return null;
            if (Math.Abs(s0 - s1) > Math.Max(thick * 0.15, 2.0)) return null; // converging, not a wall face pair
            double t0 = (b.A.X - a.A.X) * dx + (b.A.Y - a.A.Y) * dy, t1 = (b.B.X - a.A.X) * dx + (b.B.Y - a.A.Y) * dy;
            double lo = Math.Max(0, Math.Min(t0, t1)), hi = Math.Min(la, Math.Max(t0, t1));
            if (hi - lo < minOverlap) return null;
            double z = (a.A.Z + a.B.Z + b.A.Z + b.B.Z) / 4.0;
            return new WallCandidate
            {
                Start = new Pt(a.A.X + dx * lo + nx * s / 2, a.A.Y + dy * lo + ny * s / 2, z),
                End = new Pt(a.A.X + dx * hi + nx * s / 2, a.A.Y + dy * hi + ny * s / 2, z),
                ThicknessMm = thick, SegA = ia, SegB = ib, OverlapMm = hi - lo
            };
        }

        private static double[] Interval(Seg s, Pt p0, Pt p1)
        {
            double dx = s.B.X - s.A.X, dy = s.B.Y - s.A.Y, l = Math.Sqrt(dx * dx + dy * dy);
            double t0 = ((p0.X - s.A.X) * dx + (p0.Y - s.A.Y) * dy) / l, t1 = ((p1.X - s.A.X) * dx + (p1.Y - s.A.Y) * dy) / l;
            return new[] { Math.Min(t0, t1), Math.Max(t0, t1) };
        }

        private static double Covered(Dictionary<int, List<double[]>> used, int seg, double[] iv)
        {
            List<double[]> l; if (!used.TryGetValue(seg, out l)) return 0;
            double sum = 0;
            foreach (var u in l) sum += Math.Max(0, Math.Min(u[1], iv[1]) - Math.Max(u[0], iv[0]));
            return sum;
        }

        private static void Mark(Dictionary<int, List<double[]>> used, int seg, double[] iv)
        {
            List<double[]> l; if (!used.TryGetValue(seg, out l)) { l = new List<double[]>(); used[seg] = l; }
            l.Add(iv);
        }
    }
}
