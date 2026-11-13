// 1.35.0: vertical cores (stairs, lifts) from the plans, and the slab openings they need floor to floor. A flight drawn on
// a floor's plan rises through the slab of the floor above, so that slab is open over it (and over the flights there that
// continue it); a lift drawn on a floor and the floor below is open in every slab between. No Revit types: tested.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public struct Rect
    {
        public double X0, Y0, X1, Y1;
        public Rect(double x0, double y0, double x1, double y1) { X0 = Math.Min(x0, x1); Y0 = Math.Min(y0, y1); X1 = Math.Max(x0, x1); Y1 = Math.Max(y0, y1); }
        public bool Overlaps(Rect o, double gap = 0) => X0 <= o.X1 + gap && o.X0 <= X1 + gap && Y0 <= o.Y1 + gap && o.Y0 <= Y1 + gap;
        public Rect Union(Rect o) => new Rect(Math.Min(X0, o.X0), Math.Min(Y0, o.Y0), Math.Max(X1, o.X1), Math.Max(Y1, o.Y1));
        public Rect Grow(double m) => new Rect(X0 - m, Y0 - m, X1 + m, Y1 + m);
        public Pt Centre => new Pt((X0 + X1) / 2, (Y0 + Y1) / 2);
        public double W => X1 - X0;
        public double H => Y1 - Y0;
    }

    public static class Cores
    {
        /// <summary>Rectangles that overlap (or come within gap_mm) merged until none do.</summary>
        public static List<Rect> Merge(IEnumerable<Rect> rects, double gapMm = 0)
        {
            var list = rects.ToList();
            for (bool again = true; again;)
            {
                again = false;
                for (int i = 0; i < list.Count && !again; i++)
                    for (int j = i + 1; j < list.Count; j++)
                        if (list[i].Overlaps(list[j], gapMm)) { list[i] = list[i].Union(list[j]); list.RemoveAt(j); again = true; break; }
            }
            return list;
        }

        /// <summary>Per floor (index into flights, lowest first), the openings its slab needs: the flights drawn on the floor
        /// below (they rise through it) and its own flights that overlap them (the same stair going on up), merged. The lowest
        /// floor needs none.</summary>
        public static List<List<Rect>> StairWells(IList<List<Rect>> flights, double gapMm = 300)
        {
            var wells = new List<List<Rect>>();
            for (int j = 0; j < flights.Count; j++)
            {
                if (j == 0) { wells.Add(new List<Rect>()); continue; }
                var below = flights[j - 1];
                var here = flights[j].Where(f => below.Any(b => b.Overlaps(f, gapMm)));
                wells.Add(Merge(below.Concat(here), gapMm));
            }
            return wells;
        }

        /// <summary>Per floor, the lift shafts its slab is open for: a lift drawn on this floor's plan and on the floor below's
        /// (overlapping), as the union of the two.</summary>
        public static List<List<Rect>> Shafts(IList<List<Rect>> lifts)
        {
            var res = new List<List<Rect>>();
            for (int j = 0; j < lifts.Count; j++)
                res.Add(j == 0 ? new List<Rect>() : Merge(lifts[j].SelectMany(l => lifts[j - 1].Where(b => b.Overlaps(l)).Select(b => b.Union(l)))));
            return res;
        }

        /// <summary>Closed shapes from loose pieces (ends within join_mm), as their boxes, kept when both sides are between
        /// min_mm and max_mm (a lift car or shaft, not a whole room or a button).</summary>
        public static List<Rect> ClosedBoxes(IList<(Pt a, Pt b)> segs, double joinMm = 5, double minMm = 900, double maxMm = 4000)
        {
            int n = segs.Count; var parent = Enumerable.Range(0, n).ToArray();
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    var (a, b) = segs[i]; var (c, d) = segs[j];
                    if (a.Dist2D(c) <= joinMm || a.Dist2D(d) <= joinMm || b.Dist2D(c) <= joinMm || b.Dist2D(d) <= joinMm) parent[Find(i)] = Find(j);
                }
            var boxes = new List<Rect>();
            foreach (var g in Enumerable.Range(0, n).GroupBy(Find).Where(g => g.Count() >= 4))
            {
                var pts = g.SelectMany(i => new[] { segs[i].a, segs[i].b }).ToList();
                // closed: every end meets another piece's end
                bool closed = pts.All(p => pts.Count(q => q.Dist2D(p) <= joinMm) >= 2);
                var r = new Rect(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
                if (closed && r.W >= minMm && r.H >= minMm && r.W <= maxMm && r.H <= maxMm) boxes.Add(r);
            }
            return Merge(boxes);
        }
    }
}
