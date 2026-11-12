// Ported from the mcp-servers-for-revit survey pipeline (survey-pipeline.patch, base 86cf705). Revit-free.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public enum DecimateMode { Average, Min, Max, NearestToCentre }

    public static class TopoSampler
    {
        /// <summary>Insert points along a polyline so that no gap exceeds maxSpacingMm (Z interpolated linearly).</summary>
        public static List<Pt> Densify(IList<Pt> pts, bool closed, double maxSpacingMm)
        {
            var res = new List<Pt>();
            if (pts.Count == 0) return res;
            int count = closed ? pts.Count : pts.Count - 1;
            for (int i = 0; i < count; i++)
            {
                Pt a = pts[i], b = pts[(i + 1) % pts.Count];
                res.Add(a);
                double d = a.Dist2D(b);
                if (maxSpacingMm > 0 && d > maxSpacingMm)
                {
                    int steps = (int)Math.Ceiling(d / maxSpacingMm);
                    for (int s = 1; s < steps; s++)
                    {
                        double t = (double)s / steps;
                        res.Add(new Pt(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t));
                    }
                }
            }
            if (!closed) res.Add(pts[pts.Count - 1]);
            return res;
        }

        /// <summary>One point per XY cell. Revit topography gets slow/unstable with huge point sets.</summary>
        public static List<Pt> Decimate(IEnumerable<Pt> pts, double cellMm, DecimateMode mode)
        {
            if (cellMm <= 0) return pts.Where(p => p.IsFinite()).ToList();
            var cells = new Dictionary<string, List<Pt>>();
            foreach (var p in pts)
            {
                if (!p.IsFinite()) continue;
                string k = (long)Math.Floor(p.X / cellMm) + "_" + (long)Math.Floor(p.Y / cellMm);
                List<Pt> l; if (!cells.TryGetValue(k, out l)) { l = new List<Pt>(); cells[k] = l; }
                l.Add(p);
            }
            var res = new List<Pt>(cells.Count);
            foreach (var kv in cells.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                var l = kv.Value;
                switch (mode)
                {
                    case DecimateMode.Min: res.Add(l.OrderBy(p => p.Z).First()); break;
                    case DecimateMode.Max: res.Add(l.OrderByDescending(p => p.Z).First()); break;
                    case DecimateMode.NearestToCentre:
                        double cx = l.Average(p => p.X), cy = l.Average(p => p.Y);
                        res.Add(l.OrderBy(p => (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)).First()); break;
                    default: res.Add(new Pt(l.Average(p => p.X), l.Average(p => p.Y), l.Average(p => p.Z))); break;
                }
            }
            return res;
        }

        /// <summary>Revit rejects topography with two points at the same XY (even at different Z). Keeps the average Z per XY position (rounded to toleranceMm).</summary>
        public static List<Pt> UniqueXY(IEnumerable<Pt> pts, double toleranceMm, out int removed)
        {
            double t = toleranceMm <= 0 ? 1.0 : toleranceMm;
            var map = new Dictionary<GridKey, List<Pt>>();
            var order = new List<GridKey>();
            int total = 0;
            foreach (var p in pts)
            {
                if (!p.IsFinite()) continue;
                total++;
                var k = new GridKey((long)Math.Round(p.X / t), (long)Math.Round(p.Y / t));
                List<Pt> l; if (!map.TryGetValue(k, out l)) { l = new List<Pt>(); map[k] = l; order.Add(k); }
                l.Add(p);
            }
            var res = order.Select(k => map[k].Count == 1 ? map[k][0] : new Pt(map[k].Average(p => p.X), map[k].Average(p => p.Y), map[k].Average(p => p.Z))).ToList();
            removed = total - res.Count;
            return res;
        }

        /// <summary>Grows the cell size until at most maxPoints remain. Returns the cell size used (0 = none needed).</summary>
        public static List<Pt> ThinToMax(List<Pt> pts, int maxPoints, DecimateMode mode, out double cellUsed)
        {
            cellUsed = 0;
            if (maxPoints <= 0 || pts.Count <= maxPoints) return pts;
            Pt min, max; GeoMath.BoundingBox(pts, out min, out max);
            double w = Math.Max(max.X - min.X, 1), h = Math.Max(max.Y - min.Y, 1);
            double cell = Math.Max(Math.Sqrt(w * h / maxPoints), 1.0);
            var cur = pts;
            for (int i = 0; i < 40; i++)
            {
                cur = Decimate(pts, cell, mode);
                cellUsed = cell;
                if (cur.Count <= maxPoints) return cur;
                cell *= 1.25;
            }
            return cur;
        }

        /// <summary>Indices of points whose Z is a gross outlier (median absolute deviation test).</summary>
        public static List<int> ZOutliers(IList<Pt> pts, double madFactor)
        {
            var res = new List<int>();
            if (pts.Count < 8) return res;
            var zs = pts.Select(p => p.Z).OrderBy(z => z).ToList();
            double med = zs[zs.Count / 2];
            var devs = pts.Select(p => Math.Abs(p.Z - med)).OrderBy(d => d).ToList();
            double mad = devs[devs.Count / 2];
            if (mad < 1e-6) mad = Math.Max(devs.Last() / 100.0, 1.0);
            for (int i = 0; i < pts.Count; i++)
                if (Math.Abs(pts[i].Z - med) > madFactor * mad * 1.4826) res.Add(i);
            return res;
        }

        public static bool IsCollinear2D(IList<Pt> pts, double tolMm)
        {
            if (pts.Count < 3) return true;
            Pt a = pts[0]; Pt b = pts.OrderByDescending(p => p.Dist2D(a)).First();
            double len = a.Dist2D(b); if (len < 1e-9) return true;
            foreach (var p in pts)
            {
                double d = Math.Abs((b.X - a.X) * (a.Y - p.Y) - (a.X - p.X) * (b.Y - a.Y)) / len;
                if (d > tolMm) return false;
            }
            return true;
        }
    }
}
