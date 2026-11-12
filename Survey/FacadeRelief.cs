// Revit-free. 1.14.2: a facade that looks like its survey elevation. The elevation draws the stonework (columns and
// their flutes, capitals and bases, pilasters, cornices, string courses, window surrounds, consoles, balustrades) as
// loose lines; split where they cross, they give faces exactly as the drawing reads. Each face is pushed out from the
// wall face by its projection: what the storey's plan draws in front of the facade there (columns, pilasters, piers,
// bays), else a modest relief by the face's shape. In an elevation view the model then draws the survey's lines, and
// in 3D it stands out from the wall as the plan says.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    /// <summary>One face of the elevation, in facade coordinates: U along the facade, Y = model height (mm).</summary>
    public class ReliefFace
    {
        public List<Pt> Ring;
        public double U0, U1, Z0, Z1, AreaM2;
        public double Depth;
        /// <summary>plan | band | strip | small | panel</summary>
        public string Source;
    }

    public class ReliefOptions
    {
        public double TolMm = 3, MinFaceMm2 = 1500, MaxFaceM2 = 4;
        public double BandDepth = 120, StripDepth = 80, SmallDepth = 50, PanelDepth = 30;
        public double MinPlanDepth = 20, MaxPlanDepth = 1500;
    }

    /// <summary>How far the plan's outline stands in front of the facade, sampled along it.</summary>
    public class ProjectionProfile
    {
        public double Start, Step;
        public double[] Depth;
        public double At(double u)
        {
            if (Depth == null || Depth.Length == 0) return 0;
            int i = (int)Math.Round((u - Start) / Step);
            return i < 0 || i >= Depth.Length ? 0 : Depth[i];
        }
        /// <summary>Median over u0..u1 (a face's width).</summary>
        public double Over(double u0, double u1)
        {
            var v = new List<double>();
            for (double u = Math.Min(u0, u1); u <= Math.Max(u0, u1) + 1e-6; u += Step) v.Add(At(u));
            if (v.Count == 0) return At((u0 + u1) / 2);
            v.Sort(); return v[v.Count / 2];
        }
    }

    public static class FacadeRelief
    {
        /// <summary>
        /// The plan's projection in front of the facade line (origin fa, direction (ux, uy), outward normal (nx, ny)),
        /// every step mm from 0 to length: the furthest point of any plan line within maxOut in front of it there,
        /// counting lines from minOut out (the wall's own face lines sit at 0).
        /// </summary>
        public static ProjectionProfile Profile(IEnumerable<Seg> planSegs, Pt fa, double ux, double uy, double nx, double ny, double length,
                                                double step = 25, double minOut = 15, double maxOut = 1500)
        {
            int n = Math.Max(1, (int)Math.Ceiling(length / step) + 1);
            var d = new double[n];
            (double u, double v) F(Pt p) => ((p.X - fa.X) * ux + (p.Y - fa.Y) * uy, (p.X - fa.X) * nx + (p.Y - fa.Y) * ny);
            foreach (var s in planSegs)
            {
                var (ua, va) = F(s.A); var (ub, vb) = F(s.B);
                if (Math.Max(va, vb) < minOut || Math.Min(va, vb) > maxOut) continue;
                double lo = Math.Min(ua, ub), hi = Math.Max(ua, ub);
                int i0 = Math.Max(0, (int)Math.Ceiling(lo / step)), i1 = Math.Min(n - 1, (int)Math.Floor(hi / step));
                if (hi - lo < 1e-6)
                {
                    // a line straight out from the facade: the furthest point of it, at that u
                    int i = (int)Math.Round(lo / step);
                    if (i >= 0 && i < n) d[i] = Math.Max(d[i], Math.Min(maxOut, Math.Max(va, vb)));
                    continue;
                }
                for (int i = i0; i <= i1; i++)
                {
                    double u = i * step, t = (u - ua) / (ub - ua), v = va + t * (vb - va);
                    if (v >= minOut && v <= maxOut) d[i] = Math.Max(d[i], v);
                }
            }
            // the furthest line at each u is the front of what stands out there: a column's front arc, a pilaster's face
            return new ProjectionProfile { Start = 0, Step = step, Depth = d };
        }

        /// <summary>
        /// The elevation's lines (U along the facade, Y = model height) split into faces; each face's depth from the
        /// plan's projection (planAt: the profile of a storey whose plan cut height the face crosses, or null), else by shape. Faces inside a window
        /// (the glass) or bigger than MaxFaceM2 (the plain wall) are left out.
        /// </summary>
        public static List<ReliefFace> Build(IList<Seg> segs, IList<(double u0, double u1, double z0, double z1)> windows,
                                             Func<double, double, ProjectionProfile> planAt, ReliefOptions o = null)
        {
            o ??= new ReliefOptions();
            var faces = SurveyDetail.Faces(segs, o.TolMm, o.MinFaceMm2);
            var result = new List<ReliefFace>();
            foreach (var ring in faces)
            {
                GeoMath.BoundingBox(ring, out var mn, out var mx);
                double area = Math.Abs(GeoMath.SignedArea(ring)) / 1e6;
                if (area > o.MaxFaceM2) continue;
                double cu = (mn.X + mx.X) / 2, cz = (mn.Y + mx.Y) / 2;
                if (windows.Any(w => cu > w.u0 + 20 && cu < w.u1 - 20 && cz > w.z0 + 20 && cz < w.z1 - 20)) continue;
                var f = new ReliefFace { Ring = ring, U0 = mn.X, U1 = mx.X, Z0 = mn.Y, Z1 = mx.Y, AreaM2 = area };
                double w = mx.X - mn.X, h = mx.Y - mn.Y;
                // the plan only says what stands out at its cut height: a face that crosses it (a column, a pilaster, a pier)
                var prof = planAt?.Invoke(mn.Y, mx.Y);
                double pd = prof == null ? 0 : prof.Over(mn.X, mx.X);
                if (pd >= o.MinPlanDepth) { f.Depth = Math.Min(pd, o.MaxPlanDepth); f.Source = "plan"; }
                else if (w > 4 * h && h < 800) { f.Depth = o.BandDepth; f.Source = "band"; }
                else if (h > 4 * w && w < 800) { f.Depth = o.StripDepth; f.Source = "strip"; }
                else if (area < 0.5) { f.Depth = o.SmallDepth; f.Source = "small"; }
                else { f.Depth = o.PanelDepth; f.Source = "panel"; }
                result.Add(f);
            }
            return result;
        }
    }
}
