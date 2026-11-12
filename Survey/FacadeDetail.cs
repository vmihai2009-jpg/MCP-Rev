// Revit-free. Facade bands (cornices, string courses, fascias, parapet copings) from the survey: their profile from
// a section that cuts the facade (the outline's projection past the wall face, height by height), their extent
// along the facade from the elevation (where it draws the band's horizontal lines). Sections and elevations are
// read 1:1 above datum, as Plowman Craven draws them.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    /// <summary>One side of a section drawing: its outline's outermost x at each height (sheet mm).</summary>
    public class SectionSide
    {
        public string Name;
        /// <summary>-1: the drawing's left side (outer = smallest x); +1: its right side.</summary>
        public int Side;
        public double Y0, Y1, Step = 10;
        /// <summary>Outer x per sample (NaN where nothing is cut at that height).</summary>
        public double[] Outer;
        /// <summary>The wall face: the commonest outer x.</summary>
        public double Face;
        /// <summary>Projection past the face at height y (mm, outward positive), or NaN.</summary>
        public double Projection(double y)
        {
            int i = (int)Math.Round((y - Y0) / Step);
            if (i < 0 || i >= Outer.Length || double.IsNaN(Outer[i])) return double.NaN;
            return Side < 0 ? Face - Outer[i] : Outer[i] - Face;
        }
    }

    public class FacadeBand
    {
        /// <summary>Heights (above datum, mm) and the profile: (projection out of the wall face, height above datum).</summary>
        public double Z0, Z1, MaxProjection;
        public List<(double p, double z)> Profile = new List<(double, double)>();
        /// <summary>Where along the facade the elevation draws it (mm from the facade line's start).</summary>
        public List<(double u0, double u1)> Runs = new List<(double, double)>();
        public double Cover;
    }

    public static class FacadeDetail
    {
        /// <summary>
        /// The two outer sides of one section drawing: for each height, the outermost profile line crossing it on
        /// each side, and each side's wall face (the commonest outer x, in 10 mm bins).
        /// </summary>
        public static List<SectionSide> Sides(IList<DwgLine> profile, string name, double step = 10)
        {
            var result = new List<SectionSide>();
            if (profile.Count == 0) return result;
            double y0 = profile.Min(l => Math.Min(l.A.Y, l.B.Y)), y1 = profile.Max(l => Math.Max(l.A.Y, l.B.Y));
            int n = (int)Math.Ceiling((y1 - y0) / step) + 1;
            foreach (int side in new[] { -1, 1 })
            {
                var outer = Enumerable.Repeat(double.NaN, n).ToArray();
                foreach (var l in profile)
                {
                    double ya = Math.Min(l.A.Y, l.B.Y), yb = Math.Max(l.A.Y, l.B.Y);
                    int i0 = (int)Math.Ceiling((ya - y0) / step), i1 = (int)Math.Floor((yb - y0) / step);
                    for (int i = Math.Max(0, i0); i <= Math.Min(n - 1, i1); i++)
                    {
                        double y = y0 + i * step, x;
                        if (yb - ya < 1e-6) x = side < 0 ? Math.Min(l.A.X, l.B.X) : Math.Max(l.A.X, l.B.X);
                        else { double t = (y - l.A.Y) / (l.B.Y - l.A.Y); x = l.A.X + (l.B.X - l.A.X) * t; }
                        if (double.IsNaN(outer[i]) || (side < 0 ? x < outer[i] : x > outer[i])) outer[i] = x;
                    }
                }
                var bins = outer.Where(v => !double.IsNaN(v)).GroupBy(v => Math.Round(v / 10)).OrderByDescending(g => g.Count()).FirstOrDefault();
                if (bins == null) continue;
                result.Add(new SectionSide { Name = name + (side < 0 ? " left" : " right"), Side = side, Y0 = y0, Y1 = y1, Step = step, Outer = outer, Face = bins.Average() });
            }
            return result;
        }

        /// <summary>
        /// Bands on a section side: height ranges where the outline stands out past the wall face by at least
        /// minProjMm, gaps under 30 mm closed. Each band's profile is the outline there, simplified to 5 mm.
        /// </summary>
        public static List<FacadeBand> Bands(SectionSide s, double minProjMm = 15, double minHeightMm = 20, double maxProjMm = 1500)
        {
            var bands = new List<FacadeBand>();
            int n = s.Outer.Length, i = 0;
            while (i < n)
            {
                double p = s.Projection(s.Y0 + i * s.Step);
                if (double.IsNaN(p) || p < minProjMm || p > maxProjMm) { i++; continue; }
                int j = i, lastIn = i;
                while (j < n)
                {
                    double q = s.Projection(s.Y0 + j * s.Step);
                    if (!double.IsNaN(q) && q >= minProjMm && q <= maxProjMm) lastIn = j;
                    else if ((j - lastIn) * s.Step > 30) break;
                    j++;
                }
                double z0 = s.Y0 + i * s.Step, z1 = s.Y0 + lastIn * s.Step;
                if (z1 - z0 >= minHeightMm)
                {
                    var b = new FacadeBand { Z0 = z0, Z1 = z1 };
                    var pts = new List<(double p, double z)> { (0, z0) };
                    for (int k = i; k <= lastIn; k++)
                    {
                        double q = s.Projection(s.Y0 + k * s.Step);
                        pts.Add((double.IsNaN(q) || q < 0 ? 0 : Math.Round(q), s.Y0 + k * s.Step));
                    }
                    pts.Add((0, z1));
                    b.Profile = Simplify(pts, 5);
                    b.MaxProjection = b.Profile.Max(x => x.p);
                    bands.Add(b);
                }
                i = lastIn + 1;
            }
            return bands;
        }

        /// <summary>Douglas-Peucker on the (projection, height) outline.</summary>
        public static List<(double p, double z)> Simplify(List<(double p, double z)> pts, double tol)
        {
            if (pts.Count < 3) return pts.ToList();
            var keep = new bool[pts.Count]; keep[0] = keep[pts.Count - 1] = true;
            var stack = new Stack<(int, int)>(); stack.Push((0, pts.Count - 1));
            while (stack.Count > 0)
            {
                var (a, b) = stack.Pop();
                double dx = pts[b].p - pts[a].p, dz = pts[b].z - pts[a].z, len = Math.Sqrt(dx * dx + dz * dz);
                int far = -1; double fd = 0;
                for (int k = a + 1; k < b; k++)
                {
                    double d = len < 1e-9 ? Math.Sqrt(Math.Pow(pts[k].p - pts[a].p, 2) + Math.Pow(pts[k].z - pts[a].z, 2))
                                          : Math.Abs(dz * (pts[k].p - pts[a].p) - dx * (pts[k].z - pts[a].z)) / len;
                    if (d > fd) { fd = d; far = k; }
                }
                if (far >= 0 && fd > tol) { keep[far] = true; stack.Push((a, far)); stack.Push((far, b)); }
            }
            return pts.Where((_, k) => keep[k]).ToList();
        }

        /// <summary>
        /// Where along the facade a band is drawn: the elevation's horizontal lines within the band's heights
        /// (given as along-the-facade spans at heights above datum), merged across gaps under 400 mm, runs
        /// shorter than 300 mm dropped.
        /// </summary>
        public static void Extent(FacadeBand b, IEnumerable<(double z, double u0, double u1)> horizontals, double facadeLength)
        {
            var spans = horizontals.Where(h => h.z >= b.Z0 - 30 && h.z <= b.Z1 + 30)
                .Select(h => (lo: Math.Max(0, Math.Min(h.u0, h.u1)), hi: Math.Min(facadeLength, Math.Max(h.u0, h.u1))))
                .Where(s => s.hi - s.lo > 50).OrderBy(s => s.lo).ToList();
            var runs = new List<(double, double)>();
            foreach (var (lo, hi) in spans)
            {
                if (runs.Count > 0 && lo <= runs[runs.Count - 1].Item2 + 400) runs[runs.Count - 1] = (runs[runs.Count - 1].Item1, Math.Max(runs[runs.Count - 1].Item2, hi));
                else runs.Add((lo, hi));
            }
            b.Runs = runs.Where(r => r.Item2 - r.Item1 >= 300).ToList();
            b.Cover = facadeLength > 0 ? b.Runs.Sum(r => r.Item2 - r.Item1) / facadeLength : 0;
        }

        /// <summary>
        /// How well a section side fits a facade's elevation: the share of its bands (weighted by height) that
        /// the elevation draws along at least a fifth of the facade.
        /// </summary>
        public static double Agreement(List<FacadeBand> bands, IList<(double z, double u0, double u1)> horizontals, double facadeLength)
        {
            if (bands.Count == 0) return 0;
            double total = 0, ok = 0;
            foreach (var b in bands)
            {
                var copy = new FacadeBand { Z0 = b.Z0, Z1 = b.Z1 };
                Extent(copy, horizontals, facadeLength);
                double w = Math.Max(20, b.Z1 - b.Z0);
                total += w;
                if (copy.Cover >= 0.2) ok += w;
            }
            return ok / total;
        }
    }
}
