// Revit-free. 1.14.4: round columns on a facade, built the way the reference model builds them (a tapered shaft,
// revolved base and capital) but from the survey: the plans give each column's axis and radius (an arc in front of
// the facade line), the elevation gives its silhouette (a round thing's outline in elevation is its profile).
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public class FacadeColumn
    {
        /// <summary>Axis: along the facade (U) and out from its line (V), mm; radius from the plan.</summary>
        public double U, V, Radius;
        public string Level;
        /// <summary>Radius at every step of height from Z0 (NaN where the elevation draws no edges).</summary>
        public double Z0, Step = 10;
        public double[] Profile;
        /// <summary>The shaft (where the radius stays near the plan's): from, to (mm, model height).</summary>
        public double ShaftZ0, ShaftZ1, ShaftR0, ShaftR1;
        public double Top => Z0 + (Profile?.Length ?? 0) * Step;
    }

    public static class FacadeColumns
    {
        /// <summary>
        /// Columns on one storey plan: chains of short lines (an arc drawn in pieces) in front of the facade line
        /// (fa, direction u, outward n), whose points fit a circle of radius minR..maxR within tol, turning through at
        /// least minTurnDeg. Returns (U, V, R) in facade terms.
        /// </summary>
        public static List<(double u, double v, double r)> FromPlan(IEnumerable<Seg> segs, Pt fa, double ux, double uy, double nx, double ny, double length,
                                                                    double minR = 150, double maxR = 600, double tol = 12, double minTurnDeg = 100)
        {
            (double u, double v) F(Pt p) => ((p.X - fa.X) * ux + (p.Y - fa.Y) * uy, (p.X - fa.X) * nx + (p.Y - fa.Y) * ny);
            var pieces = segs.Select(s => (a: F(s.A), b: F(s.B)))
                .Where(s => Math.Max(s.a.v, s.b.v) > 20 && Math.Min(s.a.v, s.b.v) > -maxR && Math.Max(s.a.v, s.b.v) < 2 * maxR + 200
                            && s.a.u > -500 && s.a.u < length + 500
                            && Math.Sqrt((s.a.u - s.b.u) * (s.a.u - s.b.u) + (s.a.v - s.b.v) * (s.a.v - s.b.v)) < maxR * 0.6)
                .ToList();
            // chains: pieces joined end to end (within 3 mm)
            var used = new bool[pieces.Count];
            var found = new List<(double u, double v, double r)>();
            for (int i = 0; i < pieces.Count; i++)
            {
                if (used[i]) continue;
                var chain = new List<int> { i }; used[i] = true;
                for (bool grew = true; grew;)
                {
                    grew = false;
                    for (int j = 0; j < pieces.Count; j++)
                    {
                        if (used[j]) continue;
                        if (chain.Any(k => Near(pieces[k].a, pieces[j].a) || Near(pieces[k].a, pieces[j].b) || Near(pieces[k].b, pieces[j].a) || Near(pieces[k].b, pieces[j].b)))
                        { chain.Add(j); used[j] = true; grew = true; }
                    }
                }
                if (chain.Count < 5) continue;
                var pts = chain.SelectMany(k => new[] { pieces[k].a, pieces[k].b }).Distinct().ToList();
                var c = FitCircle(pts);
                if (c == null || c.Value.r < minR || c.Value.r > maxR) continue;
                double worst = pts.Max(p => Math.Abs(Math.Sqrt((p.u - c.Value.u) * (p.u - c.Value.u) + (p.v - c.Value.v) * (p.v - c.Value.v)) - c.Value.r));
                if (worst > tol) continue;
                var ang = pts.Select(p => Math.Atan2(p.v - c.Value.v, p.u - c.Value.u)).OrderBy(x => x).ToList();
                double gap = 0; for (int k = 0; k < ang.Count; k++) gap = Math.Max(gap, (k + 1 < ang.Count ? ang[k + 1] : ang[0] + 2 * Math.PI) - ang[k]);
                double turn = (2 * Math.PI - gap) * 180 / Math.PI;
                if (turn < minTurnDeg) continue;
                if (c.Value.v + c.Value.r < 30) continue; // nothing of it in front of the facade
                found.Add(c.Value);
            }
            return found;
        }

        private static bool Near((double u, double v) a, (double u, double v) b) => Math.Abs(a.u - b.u) < 3 && Math.Abs(a.v - b.v) < 3;

        /// <summary>
        /// What a storey plan draws standing in front of the facade, as closed outlines in facade terms (U along, V out):
        /// chains of wall lines in front of it (a pilaster with its flutes, a pier, an engaged column) whose two ends come
        /// back to the facade line (within backTol of V = 0), closed along it. Kept when at least minDepth proud,
        /// minWidth..maxWidth wide. This is the shaft's exact section, flutes and all.
        /// </summary>
        public static List<List<Pt>> PlanPieces(IEnumerable<Seg> segs, Pt fa, double ux, double uy, double nx, double ny, double length,
                                                double backTol = 60, double minDepth = 40, double maxOut = 1500, double minWidth = 150, double maxWidth = 2500)
        {
            (double u, double v) F(Pt p) => ((p.X - fa.X) * ux + (p.Y - fa.Y) * uy, (p.X - fa.X) * nx + (p.Y - fa.Y) * ny);
            var pieces = segs.Select(s => (a: F(s.A), b: F(s.B)))
                .Where(s => Math.Min(s.a.v, s.b.v) > -backTol && Math.Max(s.a.v, s.b.v) < maxOut && Math.Max(s.a.u, s.b.u) > -200 && Math.Min(s.a.u, s.b.u) < length + 200)
                // not the facade's own face line
                .Where(s => !(Math.Abs(s.a.v) < backTol && Math.Abs(s.b.v) < backTol && Math.Abs(s.a.u - s.b.u) > 300))
                .ToList();
            var result = new List<List<Pt>>();
            var used = new bool[pieces.Count];
            for (int i = 0; i < pieces.Count; i++)
            {
                if (used[i]) continue;
                // a chain: walk from piece i both ways
                var chain = new LinkedList<(double u, double v)>(); chain.AddFirst(pieces[i].a); chain.AddLast(pieces[i].b); used[i] = true;
                for (bool grew = true; grew;)
                {
                    grew = false;
                    for (int j = 0; j < pieces.Count; j++)
                    {
                        if (used[j]) continue;
                        var (a, b) = pieces[j];
                        if (Near(chain.Last.Value, a)) { chain.AddLast(b); used[j] = grew = true; }
                        else if (Near(chain.Last.Value, b)) { chain.AddLast(a); used[j] = grew = true; }
                        else if (Near(chain.First.Value, b)) { chain.AddFirst(a); used[j] = grew = true; }
                        else if (Near(chain.First.Value, a)) { chain.AddFirst(b); used[j] = grew = true; }
                    }
                }
                var pts = chain.ToList();
                if (pts.Count < 3) continue;
                double depth = pts.Max(p => p.v), u0 = pts.Min(p => p.u), u1 = pts.Max(p => p.u);
                if (depth < minDepth || u1 - u0 < minWidth || u1 - u0 > maxWidth) continue;
                bool closed = Near(pts[0], pts[^1]);
                if (!closed && !(Math.Abs(pts[0].v) <= backTol && Math.Abs(pts[^1].v) <= backTol)) continue;
                // the part in front of the facade (from the first point standing proud to the last), closed straight along the
                // facade line: nibs behind the line would make the outline double back on itself
                var ring = pts.Select(p => new Pt(p.u, p.v)).ToList();
                if (!closed)
                {
                    int a = pts.FindIndex(p => p.v > 5), b = pts.FindLastIndex(p => p.v > 5);
                    if (a < 0 || b <= a) continue;
                    ring = pts.Skip(a).Take(b - a + 1).Select(p => new Pt(p.u, Math.Max(0, p.v))).ToList();
                    ring.Add(new Pt(ring[^1].X, 0)); ring.Insert(0, new Pt(ring[0].X, 0));
                    var clean = new List<Pt>();
                    foreach (var q in ring) if (clean.Count == 0 || q.Dist2D(clean[^1]) > 1) clean.Add(q);
                    ring = clean;
                }
                if (Math.Abs(GeoMath.SignedArea(ring)) < 10000) continue;
                result.Add(ring);
            }
            return result;
        }

        /// <summary>
        /// Heights where an elevation (U along, Y height) draws a piece's two edges (vertical lines within tol of u0 and
        /// u1): the run containing nearZ, as (from, to); null when the elevation doesn't draw both edges there.
        /// </summary>
        public static (double z0, double z1)? EdgeRun(IList<Seg> elev, double u0, double u1, double nearZ, double tol = 40, double gap = 150)
        {
            List<(double lo, double hi)> Cover(double u)
            {
                var iv = elev.Where(s => Math.Abs(s.A.X - s.B.X) < 5 && Math.Abs((s.A.X + s.B.X) / 2 - u) <= tol)
                    .Select(s => (lo: Math.Min(s.A.Y, s.B.Y), hi: Math.Max(s.A.Y, s.B.Y))).OrderBy(x => x.lo).ToList();
                var runs = new List<(double lo, double hi)>();
                foreach (var x in iv) { if (runs.Count > 0 && x.lo <= runs[^1].hi + gap) runs[^1] = (runs[^1].lo, Math.Max(runs[^1].hi, x.hi)); else runs.Add(x); }
                return runs;
            }
            var l = Cover(u0).FirstOrDefault(r => r.lo <= nearZ && r.hi >= nearZ);
            var r2 = Cover(u1).FirstOrDefault(r => r.lo <= nearZ && r.hi >= nearZ);
            if (l.hi <= l.lo || r2.hi <= r2.lo) return null;
            double z0 = Math.Max(l.lo, r2.lo), z1 = Math.Min(l.hi, r2.hi);
            return z1 - z0 > 300 ? (z0, z1) : null;
        }

        /// <summary>Least-squares circle (Kasa).</summary>
        public static (double u, double v, double r)? FitCircle(IList<(double u, double v)> p)
        {
            if (p.Count < 3) return null;
            double mu = p.Average(q => q.u), mv = p.Average(q => q.v);
            double suu = 0, svv = 0, suv = 0, suuu = 0, svvv = 0, suvv = 0, svuu = 0;
            foreach (var q in p)
            {
                double u = q.u - mu, v = q.v - mv;
                suu += u * u; svv += v * v; suv += u * v; suuu += u * u * u; svvv += v * v * v; suvv += u * v * v; svuu += v * u * u;
            }
            double det = suu * svv - suv * suv;
            if (Math.Abs(det) < 1e-9) return null;
            double b1 = 0.5 * (suuu + suvv), b2 = 0.5 * (svvv + svuu);
            double uc = (b1 * svv - b2 * suv) / det, vc = (suu * b2 - suv * b1) / det;
            double r = Math.Sqrt(uc * uc + vc * vc + (suu + svv) / p.Count);
            return (uc + mu, vc + mv, r);
        }

        /// <summary>
        /// The column's silhouette from the elevation's lines (U along, Y height): at every step of height, the outermost
        /// lines either side of the axis within maxR of it, kept when they are roughly symmetric (centre within 25% of
        /// the radius). Radius = half their distance. NaN where nothing symmetric is drawn.
        /// </summary>
        public static double[] Silhouette(IList<Seg> elev, double axisU, double planR, double z0, double z1, double step = 10)
        {
            int n = Math.Max(1, (int)Math.Ceiling((z1 - z0) / step));
            var r = Enumerable.Repeat(double.NaN, n).ToArray();
            double maxR = planR * 1.7;
            var near = elev.Where(s => Math.Max(s.A.X, s.B.X) >= axisU - maxR && Math.Min(s.A.X, s.B.X) <= axisU + maxR && Math.Abs(s.A.Y - s.B.Y) > 1).ToList();
            for (int i = 0; i < n; i++)
            {
                double z = z0 + (i + 0.5) * step, left = double.NaN, right = double.NaN;
                foreach (var s in near)
                {
                    double lo = Math.Min(s.A.Y, s.B.Y), hi = Math.Max(s.A.Y, s.B.Y);
                    if (z < lo || z > hi) continue;
                    double u = s.A.X + (z - s.A.Y) / (s.B.Y - s.A.Y) * (s.B.X - s.A.X);
                    double d = u - axisU;
                    if (Math.Abs(d) > maxR) continue;
                    if (d < 0 && (double.IsNaN(left) || u < left)) left = u;
                    if (d > 0 && (double.IsNaN(right) || u > right)) right = u;
                }
                if (double.IsNaN(left) || double.IsNaN(right)) continue;
                if (Math.Abs((left + right) / 2 - axisU) > 0.25 * planR) continue;
                r[i] = (right - left) / 2;
            }
            return r;
        }

        /// <summary>Fills the column's shaft from its silhouette: the longest run where the radius stays within 15% of the plan's.</summary>
        public static void Shaft(FacadeColumn c)
        {
            int best0 = -1, best1 = -1, s0 = -1;
            for (int i = 0; i <= c.Profile.Length; i++)
            {
                bool ok = i < c.Profile.Length && !double.IsNaN(c.Profile[i]) && Math.Abs(c.Profile[i] - c.Radius) <= 0.15 * c.Radius;
                if (ok && s0 < 0) s0 = i;
                if (!ok && s0 >= 0) { if (i - s0 > best1 - best0) { best0 = s0; best1 = i; } s0 = -1; }
            }
            if (best0 < 0) { c.ShaftZ0 = c.ShaftZ1 = c.Z0; return; }
            c.ShaftZ0 = c.Z0 + best0 * c.Step; c.ShaftZ1 = c.Z0 + best1 * c.Step;
            double Med(int a, int b) { var v = c.Profile.Skip(a).Take(Math.Max(1, b - a)).Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList(); return v.Count == 0 ? c.Radius : v[v.Count / 2]; }
            int k = Math.Max(1, (best1 - best0) / 10);
            c.ShaftR0 = Med(best0, best0 + k); c.ShaftR1 = Med(best1 - k, best1);
        }
    }
}
