// Revit-free. 1.14.3: a facade's profile from the survey section that crosses it: cornices, string courses,
// balustrade, parapet and the mansard, as the section draws them, swept along the facade. Together with the
// elevation relief (FacadeRelief) this is how a hand model of a classical front is made: profiles from the
// sections run along the facade, vertical pieces from the elevation.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public static class SectionProfile
    {
        /// <summary>
        /// The outermost line of a section at each height: lines given as (out, z) mm, out = distance in front of the
        /// facade line (negative behind it); for each z every step mm, the largest out among lines there within
        /// [minOut, maxOut]. NaN where nothing is drawn.
        /// </summary>
        public static double[] Envelope(IList<(Pt a, Pt b)> lines, double z0, double z1, double step = 10, double minOut = -3000, double maxOut = 2500)
        {
            int n = Math.Max(1, (int)Math.Ceiling((z1 - z0) / step) + 1);
            var o = Enumerable.Repeat(double.NaN, n).ToArray();
            foreach (var (a, b) in lines)
            {
                double lo = Math.Min(a.Y, b.Y), hi = Math.Max(a.Y, b.Y);
                int i0 = Math.Max(0, (int)Math.Ceiling((lo - z0) / step)), i1 = Math.Min(n - 1, (int)Math.Floor((hi - z0) / step));
                for (int i = i0; i <= i1; i++)
                {
                    double z = z0 + i * step;
                    double x = hi - lo < 1e-6 ? Math.Max(a.X, b.X) : a.X + (z - a.Y) / (b.Y - a.Y) * (b.X - a.X);
                    if (x < minOut || x > maxOut) continue;
                    if (double.IsNaN(o[i]) || x > o[i]) o[i] = x;
                }
            }
            return o;
        }

        /// <summary>
        /// The solid parts of the profile as closed outlines (out, z): below wallTop, what stands proud of the wall face
        /// (out > 0: cornices, bands, balustrade, sills) down to the face; from wallTop up, the outline with a skin of
        /// roofThick behind it (the mansard and parapets). Runs thinner than minProud are left out. Each outline is
        /// simplified to tol mm.
        /// </summary>
        public static List<List<Pt>> Regions(double[] env, double z0, double step, double wallTop, double roofThick = 250, double minProud = 15, double tol = 5, List<string> why = null)
        {
            var regions = new List<List<Pt>>();
            var run = new List<(double z, double outer, double inner)>();
            void Close()
            {
                if (run.Count >= 2)
                {
                    var ring = run.Select(r => new Pt(r.outer, r.z)).ToList();
                    ring.AddRange(Enumerable.Reverse(run).Select(r => new Pt(r.inner, r.z)));
                    var s = Simplify(ring, tol);
                    double area = s.Count >= 3 ? Math.Abs(GeoMath.SignedArea(s)) : 0;
                    if (s.Count >= 3 && area > 2000) regions.Add(s);
                    else why?.Add($"run {run[0].z:0}-{run[^1].z:0}: {s.Count} points after simplifying, {area:0} mm2");
                }
                run.Clear();
            }
            for (int i = 0; i < env.Length; i++)
            {
                double z = z0 + i * step, outer = env[i];
                if (double.IsNaN(outer)) { Close(); continue; }
                bool roof = z >= wallTop;
                double inner = roof ? outer - roofThick : 0;
                if (outer - inner < minProud) { Close(); continue; }
                // a break between the wall part and the roof part
                if (run.Count > 0 && (run[^1].z >= wallTop) != roof) Close();
                run.Add((z, outer, inner));
            }
            Close();
            return regions;
        }

        /// <summary>
        /// The facade's wall face in a section: of the vertical lines within search mm of out = 0, the position with the most
        /// length drawn (20 mm bins), counting only those between zLo and zHi (the storeys, not the roof). Returns the out
        /// value to subtract (0 when nothing is found).
        /// </summary>
        public static double WallFace(IList<(Pt a, Pt b)> lines, double zLo, double zHi, double search = 1500)
        {
            var bins = new Dictionary<long, double>();
            foreach (var (a, b) in lines)
            {
                if (Math.Abs(a.X - b.X) > 2 || Math.Abs(a.X) > search) continue;
                double lo = Math.Max(Math.Min(a.Y, b.Y), zLo), hi = Math.Min(Math.Max(a.Y, b.Y), zHi);
                if (hi <= lo) continue;
                long k = (long)Math.Round((a.X + b.X) / 2 / 20);
                bins[k] = bins.TryGetValue(k, out var v) ? v + (hi - lo) : hi - lo;
            }
            if (bins.Count == 0) return 0;
            // the outermost bin with at least half the best length: the face, not a reveal or the back of a window
            double best = bins.Values.Max();
            return bins.Where(kv => kv.Value >= best * 0.5).Max(kv => kv.Key) * 20.0;
        }

        /// <summary>Douglas-Peucker on a closed ring.</summary>
        public static List<Pt> Simplify(List<Pt> ring, double tol)
        {
            if (ring.Count < 4) return ring;
            var keep = new bool[ring.Count];
            keep[0] = true; keep[ring.Count / 2] = true;
            void DP(int a, int b)
            {
                if (b <= a + 1) return;
                double best = -1; int idx = -1;
                for (int i = a + 1; i < b; i++)
                {
                    double d = SurveyDetail.PtSeg(ring[i], new Seg(ring[a], ring[b], 0));
                    if (d > best) { best = d; idx = i; }
                }
                if (best > tol) { keep[idx] = true; DP(a, idx); DP(idx, b); }
            }
            DP(0, ring.Count / 2); DP(ring.Count / 2, ring.Count - 1);
            keep[ring.Count - 1] = true;
            var r = ring.Where((p, i) => keep[i]).ToList();
            // no repeated points
            var outp = new List<Pt>();
            foreach (var p in r) if (outp.Count == 0 || p.Dist2D(outp[^1]) > 1) outp.Add(p);
            if (outp.Count > 1 && outp[0].Dist2D(outp[^1]) <= 1) outp.RemoveAt(outp.Count - 1);
            return outp;
        }
    }
}
