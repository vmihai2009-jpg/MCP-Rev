// Revit-free. The survey measuring behind the detail tools (1.9.0): roof faces and their planes from a roof plan's
// line work and spot heights, stairs from a plan's tread lines, columns from small closed outlines, and where a
// section drawing sits on the plan (so its ceilings and floors can be read at a plan position).
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    /// <summary>z = A x + B y + C, fitted to spot heights (mm).</summary>
    public class PlaneZ
    {
        public double A, B, C, RmsMm;
        public int Spots;
        /// <summary>Spots left out of the fit as not on the plane (FitPlaneRobust).</summary>
        public int Outliers;
        public bool Flat => Math.Abs(A) < 1e-9 && Math.Abs(B) < 1e-9;
        public double At(double x, double y) => A * x + B * y + C;
        public double SlopeDeg => Math.Atan(Math.Sqrt(A * A + B * B)) * 180 / Math.PI;
    }

    public class StairGroup
    {
        public Pt Min, Max;
        public int Treads;
        public double GoingMm, WidthMm;
        /// <summary>Unit direction of travel in plan (along the treads' spacing).</summary>
        public double Dx, Dy;
    }

    public class SectionPlacement
    {
        /// <summary>sheet x = Dir * t + Offset, t = mm along the cut line from its first end.</summary>
        public int Dir;
        public double Offset;
        public int Matched, Of;
        public double T(double sheetX) => (sheetX - Offset) / Dir;
    }

    public static class SurveyDetail
    {
        // ------------------------------------------------------------------ planes

        /// <summary>Least-squares plane through spot heights; one or two spots, or spots in a line, give a flat plane at their mean.</summary>
        public static PlaneZ FitPlane(IList<Pt> spots)
        {
            var p = new PlaneZ { Spots = spots.Count };
            if (spots.Count == 0) return null;
            double mx = spots.Average(s => s.X), my = spots.Average(s => s.Y), mz = spots.Average(s => s.Z);
            if (spots.Count >= 3)
            {
                double sxx = 0, sxy = 0, syy = 0, sxz = 0, syz = 0;
                foreach (var s in spots)
                {
                    double x = s.X - mx, y = s.Y - my, z = s.Z - mz;
                    sxx += x * x; sxy += x * y; syy += y * y; sxz += x * z; syz += y * z;
                }
                double det = sxx * syy - sxy * sxy;
                // spots nearly in a line (det small against their spread): no slope across them can be known
                if (det > 1e-6 * Math.Max(1, (sxx + syy) * (sxx + syy)) && sxx + syy > 250000)
                {
                    p.A = (sxz * syy - syz * sxy) / det;
                    p.B = (syz * sxx - sxz * sxy) / det;
                }
            }
            p.C = mz - p.A * mx - p.B * my;
            p.RmsMm = Math.Sqrt(spots.Average(s => Math.Pow(s.Z - p.At(s.X, s.Y), 2)));
            return p;
        }

        /// <summary>
        /// 1.36.15: a plane that a stray spot can't tilt. With 4+ spots, every plane through three of them is tried; the one
        /// most spots lie on (within inlierMm) wins, ties to the one closest to all of them (median residual), and the plane
        /// is fitted again through its inliers. Mount Row: a shallow lead roof whose face took a neighbouring roof's spots on
        /// a shared edge came out at 64.5° (rms 400 mm) with the plain fit. Fewer than 4 spots: the plain fit.
        /// </summary>
        public static PlaneZ FitPlaneRobust(IList<Pt> spots, double inlierMm = 60)
        {
            if (spots.Count < 4) return FitPlane(spots);
            var pts = spots.Take(40).ToList(); int n = pts.Count;
            List<Pt> best = null; int bestIn = 0; double bestMed = double.MaxValue;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    for (int k = j + 1; k < n; k++)
                    {
                        var p = FitPlane(new[] { pts[i], pts[j], pts[k] });
                        if (p == null || (p.A == 0 && p.B == 0 && Math.Abs(pts[i].Z - pts[j].Z) + Math.Abs(pts[j].Z - pts[k].Z) > inlierMm)) continue; // collinear triple
                        var res = pts.Select(s => Math.Abs(s.Z - p.At(s.X, s.Y))).ToList();
                        int inl = res.Count(r => r <= inlierMm);
                        var sorted = res.OrderBy(r => r).ToList(); double med = sorted[sorted.Count / 2];
                        if (inl > bestIn || (inl == bestIn && med < bestMed)) { bestIn = inl; bestMed = med; best = pts.Where((s, m) => res[m] <= inlierMm).ToList(); }
                    }
            if (best == null || best.Count < 3) return FitPlane(spots);
            var fit = FitPlane(best);
            fit.Spots = spots.Count; fit.Outliers = spots.Count - best.Count;
            return fit;
        }

        // ------------------------------------------------------------------ faces of a line drawing

        /// <summary>
        /// The closed faces a plan's line work divides the plane into (a roof plan: each roof slope, flat, light).
        /// Lines are split where they cross, ends within tolMm are joined, dangling lines are dropped, then every
        /// face is walked. Returns the bounded faces as counter-clockwise rings, smallest first.
        /// </summary>
        public static List<List<Pt>> Faces(IList<Seg> segs, double tolMm = 10, double minAreaMm2 = 100000)
        {
            // 1) split at crossings
            var pieces = new List<(Pt a, Pt b)>();
            var list = segs.Where(s => s.Length2D > tolMm).ToList();
            var cuts = list.Select(_ => new List<double> { 0, 1 }).ToList();
            var cell = 2000.0;
            var grid = new Dictionary<GridKey, List<int>>();
            for (int i = 0; i < list.Count; i++)
                foreach (var k in Cells(list[i], cell)) { if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>(); l.Add(i); }
            var done = new HashSet<(int, int)>();
            foreach (var l in grid.Values)
                for (int x = 0; x < l.Count; x++)
                    for (int y = x + 1; y < l.Count; y++)
                    {
                        int i = Math.Min(l[x], l[y]), j = Math.Max(l[x], l[y]);
                        if (!done.Add((i, j))) continue;
                        if (Cross(list[i], list[j], tolMm, out double ti, out double tj)) { cuts[i].Add(ti); cuts[j].Add(tj); }
                    }
            for (int i = 0; i < list.Count; i++)
            {
                var ts = cuts[i].Distinct().OrderBy(t => t).ToList();
                for (int k = 0; k + 1 < ts.Count; k++)
                {
                    var a = Lerp(list[i], ts[k]); var b = Lerp(list[i], ts[k + 1]);
                    if (a.Dist2D(b) > tolMm / 2) pieces.Add((a, b));
                }
            }

            // 2) nodes by snapping, edges without repeats
            var nodes = new List<Pt>();
            var ngrid = new Dictionary<GridKey, List<int>>();
            int Node(Pt p)
            {
                long cx = (long)Math.Floor(p.X / tolMm), cy = (long)Math.Floor(p.Y / tolMm);
                for (long dx = -1; dx <= 1; dx++)
                    for (long dy = -1; dy <= 1; dy++)
                        if (ngrid.TryGetValue(new GridKey(cx + dx, cy + dy), out var b))
                            foreach (int id in b) if (nodes[id].Dist2D(p) <= tolMm) return id;
                nodes.Add(p);
                var key = new GridKey(cx, cy);
                if (!ngrid.TryGetValue(key, out var bb)) ngrid[key] = bb = new List<int>();
                bb.Add(nodes.Count - 1);
                return nodes.Count - 1;
            }
            var adj = new Dictionary<int, HashSet<int>>();
            foreach (var (a, b) in pieces)
            {
                int u = Node(a), v = Node(b);
                if (u == v) continue;
                if (!adj.TryGetValue(u, out var su)) adj[u] = su = new HashSet<int>();
                if (!adj.TryGetValue(v, out var sv)) adj[v] = sv = new HashSet<int>();
                su.Add(v); sv.Add(u);
            }
            // 3) drop dangling ends
            var q = new Queue<int>(adj.Where(kv => kv.Value.Count == 1).Select(kv => kv.Key));
            while (q.Count > 0)
            {
                int n = q.Dequeue();
                if (!adj.TryGetValue(n, out var s) || s.Count != 1) continue;
                int o = s.First();
                adj.Remove(n);
                if (adj.TryGetValue(o, out var so)) { so.Remove(n); if (so.Count == 1) q.Enqueue(o); else if (so.Count == 0) adj.Remove(o); }
            }
            // 4) walk faces: from each directed edge, turn as far right as possible at each node (gives CCW bounded faces)
            var sorted = adj.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(v => Math.Atan2(nodes[v].Y - nodes[kv.Key].Y, nodes[v].X - nodes[kv.Key].X)).ToList());
            var used = new HashSet<(int, int)>();
            var faces = new List<List<Pt>>();
            foreach (var u in sorted.Keys)
                foreach (var v in sorted[u])
                {
                    if (used.Contains((u, v))) continue;
                    var ring = new List<Pt>();
                    int a = u, b = v, guard = 0;
                    while (used.Add((a, b)) && guard++ < 100000)
                    {
                        ring.Add(nodes[a]);
                        var outs = sorted[b];
                        int back = outs.IndexOf(a);
                        // next edge: the one just before the way back, going round counter-clockwise (the tightest left turn
                        // seen from the edge's own side keeps the face on the left)
                        int c = outs[(back - 1 + outs.Count) % outs.Count];
                        a = b; b = c;
                    }
                    if (ring.Count >= 3 && GeoMath.SignedArea(ring) > minAreaMm2) faces.Add(ring);
                }
            return faces.OrderBy(f => GeoMath.SignedArea(f)).ToList();
        }

        private static IEnumerable<GridKey> Cells(Seg s, double cell)
        {
            long x0 = (long)Math.Floor(Math.Min(s.A.X, s.B.X) / cell), x1 = (long)Math.Floor(Math.Max(s.A.X, s.B.X) / cell);
            long y0 = (long)Math.Floor(Math.Min(s.A.Y, s.B.Y) / cell), y1 = (long)Math.Floor(Math.Max(s.A.Y, s.B.Y) / cell);
            for (long x = x0; x <= x1; x++) for (long y = y0; y <= y1; y++) yield return new GridKey(x, y);
        }

        private static Pt Lerp(Seg s, double t) => new Pt(s.A.X + (s.B.X - s.A.X) * t, s.A.Y + (s.B.Y - s.A.Y) * t);

        /// <summary>Where two segments cross (or one ends on the other, within tol), as parameters along each.</summary>
        private static bool Cross(Seg p, Seg q, double tol, out double tp, out double tq)
        {
            tp = tq = 0;
            double rx = p.B.X - p.A.X, ry = p.B.Y - p.A.Y, sx = q.B.X - q.A.X, sy = q.B.Y - q.A.Y;
            double den = rx * sy - ry * sx;
            if (Math.Abs(den) < 1e-9) return false;
            double qpx = q.A.X - p.A.X, qpy = q.A.Y - p.A.Y;
            tp = (qpx * sy - qpy * sx) / den; tq = (qpx * ry - qpy * rx) / den;
            double ep = tol / Math.Sqrt(rx * rx + ry * ry), eq = tol / Math.Sqrt(sx * sx + sy * sy);
            if (tp < -ep || tp > 1 + ep || tq < -eq || tq > 1 + eq) return false;
            tp = Math.Clamp(tp, 0, 1); tq = Math.Clamp(tq, 0, 1);
            return true;
        }

        /// <summary>The smallest face containing a point, or -1.</summary>
        public static int FaceAt(IList<List<Pt>> faces, Pt p)
        {
            for (int i = 0; i < faces.Count; i++) if (GeoMath.PointInPolygon(p, faces[i])) return i; // smallest first
            return -1;
        }

        // ------------------------------------------------------------------ stepped flat roofs

        /// <summary>
        /// A face whose spot heights are not one plane (flat roofs at several levels inside one outline): spots grouped by
        /// height (within tolMm), every cell of a grid over the face given its nearest spot's group, and each group's
        /// cells merged into rectangles. Returns per group its height (mm, the group's mean) and rectangles.
        /// </summary>
        public static List<(double z, List<(double x0, double y0, double x1, double y1)> rects)> Terraces(IList<Pt> ring, IList<Pt> spots, double cellMm = 100, double tolMm = 80)
        {
            var result = new List<(double, List<(double, double, double, double)>)>();
            if (spots.Count == 0 || ring.Count < 3) return result;
            var sorted = spots.OrderBy(s => s.Z).ToList();
            var groups = new List<List<Pt>> { new List<Pt> { sorted[0] } };
            foreach (var s in sorted.Skip(1)) { if (s.Z - groups[^1][^1].Z <= tolMm) groups[^1].Add(s); else groups.Add(new List<Pt> { s }); }
            var gOf = new Dictionary<Pt, int>();
            for (int g = 0; g < groups.Count; g++) foreach (var s in groups[g]) gOf[s] = g;
            GeoMath.BoundingBox(ring, out var mn, out var mx);
            int nx = Math.Max(1, (int)Math.Ceiling((mx.X - mn.X) / cellMm)), ny = Math.Max(1, (int)Math.Ceiling((mx.Y - mn.Y) / cellMm));
            if ((long)nx * ny > 4_000_000) { cellMm *= Math.Sqrt((double)nx * ny / 4_000_000); nx = (int)Math.Ceiling((mx.X - mn.X) / cellMm); ny = (int)Math.Ceiling((mx.Y - mn.Y) / cellMm); }
            var cell = new int[nx, ny];
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                {
                    var c = new Pt(mn.X + (i + 0.5) * cellMm, mn.Y + (j + 0.5) * cellMm);
                    if (!GeoMath.PointInPolygon(c, ring)) { cell[i, j] = -1; continue; }
                    Pt best = spots[0]; double bd = double.MaxValue;
                    foreach (var s in spots) { double d = (s.X - c.X) * (s.X - c.X) + (s.Y - c.Y) * (s.Y - c.Y); if (d < bd) { bd = d; best = s; } }
                    cell[i, j] = gOf[best];
                }
            for (int g = 0; g < groups.Count; g++)
            {
                // runs along x per row, then runs stacked where rows repeat
                var open = new Dictionary<(int, int), int>(); // (i0, i1) -> start row
                var rects = new List<(double, double, double, double)>();
                void Close((int i0, int i1) k, int j0, int j1) => rects.Add((mn.X + k.i0 * cellMm, mn.Y + j0 * cellMm, mn.X + (k.i1 + 1) * cellMm, mn.Y + (j1 + 1) * cellMm));
                for (int j = 0; j <= ny; j++)
                {
                    var runs = new HashSet<(int, int)>();
                    if (j < ny)
                        for (int i = 0; i < nx; i++)
                        {
                            if (cell[i, j] != g) continue;
                            int s = i; while (i + 1 < nx && cell[i + 1, j] == g) i++;
                            runs.Add((s, i));
                        }
                    foreach (var k in open.Keys.ToList()) if (!runs.Contains(k)) { Close(k, open[k], j - 1); open.Remove(k); }
                    foreach (var r in runs) if (!open.ContainsKey(r)) open[r] = j;
                }
                if (rects.Count > 0) result.Add((groups[g].Average(s => s.Z), rects));
            }
            return result;
        }

        // ------------------------------------------------------------------ stairs

        /// <summary>
        /// Flights on a plan's stair layer: line work grouped where it touches (within 150 mm), then in each group the
        /// longest run of evenly spaced parallel treads (200-450 mm apart). Width is the treads' common length.
        /// </summary>
        public static List<StairGroup> Stairs(IList<Seg> segs, int minTreads = 3)
        {
            var groups = Groups(segs, 150);
            var result = new List<StairGroup>();
            foreach (var g in groups)
            {
                var lines = g.Where(s => s.Length2D >= 500 && s.Length2D <= 4000).ToList();
                if (lines.Count < minTreads) continue;
                // the commonest direction (5 degree bins, folded to 0-180)
                var dirs = lines.GroupBy(s => (int)Math.Round(Angle(s) / 5) % 36).OrderByDescending(d => d.Count()).First();
                var par = dirs.ToList();
                double ang = par.Average(s => Angle(s)) * Math.PI / 180;
                double nx = -Math.Sin(ang), ny = Math.Cos(ang); // across the treads
                var offs = par.Select(s => ((s.A.X + s.B.X) / 2 * nx + (s.A.Y + s.B.Y) / 2 * ny, s.Length2D)).OrderBy(o => o.Item1).ToList();
                // merge lines at the same offset (a tread drawn in pieces)
                var treads = new List<(double off, double len)>();
                foreach (var o in offs)
                {
                    if (treads.Count > 0 && Math.Abs(o.Item1 - treads[^1].off) < 30) treads[^1] = (treads[^1].off, Math.Max(treads[^1].len, o.Item2));
                    else treads.Add(o);
                }
                // longest even run
                var best = new List<(double off, double len)>(); double bestGo = 0;
                for (int i = 0; i + 1 < treads.Count; i++)
                {
                    double go = treads[i + 1].off - treads[i].off;
                    if (go < 200 || go > 450) continue;
                    var run = new List<(double, double)> { treads[i], treads[i + 1] };
                    for (int j = i + 2; j < treads.Count; j++)
                    {
                        double d = treads[j].off - run[^1].Item1;
                        if (Math.Abs(d - go) <= 25) run.Add(treads[j]); else if (d > go + 25) break;
                    }
                    if (run.Count > best.Count) { best = run; bestGo = (run[^1].Item1 - run[0].Item1) / (run.Count - 1); }
                }
                if (best.Count < minTreads) continue;
                GeoMath.BoundingBox(g.SelectMany(s => new[] { s.A, s.B }), out var mn, out var mx);
                var lens = best.Select(b => b.len).OrderBy(v => v).ToList();
                result.Add(new StairGroup
                {
                    Min = mn, Max = mx, Treads = best.Count, GoingMm = Math.Round(bestGo), WidthMm = Math.Round(lens[lens.Count / 2]),
                    Dx = nx, Dy = ny,
                });
            }
            return result;
        }

        private static double Angle(Seg s)
        {
            double a = Math.Atan2(s.B.Y - s.A.Y, s.B.X - s.A.X) * 180 / Math.PI;
            if (a < 0) a += 180;
            if (a >= 180) a -= 180;
            return a;
        }

        /// <summary>Segments grouped where any two come within gap of each other (end to segment).</summary>
        public static List<List<Seg>> Groups(IList<Seg> segs, double gap)
        {
            int n = segs.Count;
            var parent = Enumerable.Range(0, n).ToArray();
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            var grid = new Dictionary<GridKey, List<int>>();
            double cell = Math.Max(gap * 4, 1000);
            for (int i = 0; i < n; i++)
                foreach (var k in Cells(segs[i], cell)) { if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>(); l.Add(i); }
            foreach (var l in grid.Values)
                for (int a = 0; a < l.Count; a++)
                    for (int b = a + 1; b < l.Count; b++)
                    {
                        int i = l[a], j = l[b];
                        if (Find(i) == Find(j)) continue;
                        if (SegDist(segs[i], segs[j]) <= gap) parent[Find(i)] = Find(j);
                    }
            return Enumerable.Range(0, n).GroupBy(Find).Select(g => g.Select(i => segs[i]).ToList()).ToList();
        }

        private static double SegDist(Seg p, Seg q)
        {
            if (Cross(p, q, 0, out _, out _)) return 0;
            return new[] { PtSeg(p.A, q), PtSeg(p.B, q), PtSeg(q.A, p), PtSeg(q.B, p) }.Min();
        }

        public static double PtSeg(Pt p, Seg s)
        {
            double dx = s.B.X - s.A.X, dy = s.B.Y - s.A.Y, l2 = dx * dx + dy * dy;
            double t = l2 < 1e-9 ? 0 : Math.Clamp(((p.X - s.A.X) * dx + (p.Y - s.A.Y) * dy) / l2, 0, 1);
            return p.Dist2D(new Pt(s.A.X + dx * t, s.A.Y + dy * t));
        }

        // ------------------------------------------------------------------ columns

        /// <summary>
        /// Free-standing small closed outlines (piers, columns, chimney stacks): rings that touch no other line work
        /// (a nib of a wall is part of the wall's outline, not a ring of its own), within the size limits.
        /// </summary>
        public static List<List<Pt>> Columns(IList<Seg> segs, double minAreaM2 = 0.02, double maxAreaM2 = 2.5, double maxSideMm = 1600)
        {
            return LoopBuilder.Build(segs, 5).Loops
                .Where(f =>
                {
                    double area = Math.Abs(GeoMath.SignedArea(f)) / 1e6;
                    if (area < minAreaM2) return false;
                    GeoMath.BoundingBox(f, out var mn, out var mx);
                    return area <= maxAreaM2 && Math.Max(mx.X - mn.X, mx.Y - mn.Y) <= maxSideMm;
                }).ToList();
        }

        // ------------------------------------------------------------------ merged openings

        /// <summary>
        /// One wide opening split at the piers the plan draws inside it: reveal lines across the wall at positions
        /// (mm along the wall, inside u0..u1) pair up into piers (between minPier and maxPier wide); the openings
        /// are what lies between. Returns one span (u0, u1) when there is nothing to split.
        /// </summary>
        public static List<(double a, double b)> SplitSpan(double u0, double u1, IEnumerable<double> reveals, double minPier = 80, double maxPier = 1500, double minOpening = 300)
        {
            var cuts = new List<double>();
            foreach (var r in reveals.Where(r => r > u0 + 50 && r < u1 - 50).OrderBy(r => r))
                if (cuts.Count == 0 || r - cuts[^1] > 25) cuts.Add(r);
            var piers = new List<(double a, double b)>();
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                double w = cuts[i + 1] - cuts[i];
                if (w >= minPier && w <= maxPier) { piers.Add((cuts[i], cuts[i + 1])); i++; }
            }
            var spans = new List<(double, double)>();
            double start = u0;
            foreach (var (a, b) in piers)
            {
                if (a - start >= minOpening) spans.Add((start, a));
                start = b;
            }
            if (u1 - start >= minOpening) spans.Add((start, u1));
            return spans.Count == 0 ? new List<(double, double)> { (u0, u1) } : spans;
        }

        // ------------------------------------------------------------------ sections on the plan

        /// <summary>
        /// Where a section drawing sits along its cut line: the long vertical lines of its outline against the
        /// walls the cut line crosses on the plan. Tries both ways round; the offset most crossings agree on (within
        /// tolMm) wins.
        /// </summary>
        public static SectionPlacement PlaceSection(IList<double> planCrossings, IList<double> sectionVerticals, double tolMm = 40)
        {
            SectionPlacement best = null;
            if (planCrossings.Count == 0 || sectionVerticals.Count == 0) return null;
            foreach (int dir in new[] { 1, -1 })
            {
                var votes = new Dictionary<long, int>();
                foreach (var x in sectionVerticals)
                    foreach (var t in planCrossings)
                    {
                        long bin = (long)Math.Round((x - dir * t) / tolMm);
                        votes[bin] = votes.TryGetValue(bin, out int c) ? c + 1 : 1;
                    }
                // a bin and its neighbours (an offset on a bin edge)
                foreach (var kv in votes.OrderByDescending(v => v.Value).Take(5))
                {
                    double guess = kv.Key * tolMm;
                    var hits = new List<double>();
                    foreach (var x in sectionVerticals)
                    {
                        double near = planCrossings.Select(t => x - dir * t).OrderBy(o => Math.Abs(o - guess)).First();
                        if (Math.Abs(near - guess) <= tolMm) hits.Add(near);
                    }
                    if (hits.Count == 0) continue;
                    double off = hits.OrderBy(h => h).ElementAt(hits.Count / 2);
                    int matched = sectionVerticals.Count(x => planCrossings.Any(t => Math.Abs(x - dir * t - off) <= tolMm));
                    if (best == null || matched > best.Matched)
                        best = new SectionPlacement { Dir = dir, Offset = off, Matched = matched, Of = sectionVerticals.Count };
                }
            }
            return best;
        }

        /// <summary>
        /// The ceiling at one place on a section: the lowest horizontal outline line spanning x between the floor
        /// plus minHeadroom and the next floor (heights in mm, as the section is read). Null where there is none.
        /// </summary>
        public static double? Soffit(IEnumerable<(double x0, double x1, double z)> horizontals, double x, double floorZ, double nextFloorZ, double minHeadroomMm = 1800)
        {
            var hit = horizontals.Where(h => Math.Min(h.x0, h.x1) <= x && Math.Max(h.x0, h.x1) >= x && h.z >= floorZ + minHeadroomMm && h.z <= nextFloorZ - 20)
                .Select(h => h.z).DefaultIfEmpty(double.NaN).Min();
            return double.IsNaN(hit) ? (double?)null : hit;
        }
    }
}
