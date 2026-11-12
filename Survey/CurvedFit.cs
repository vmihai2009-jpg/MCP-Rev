// Revit-free. Curved walls in the 1:1 score: each face of an arc wall sampled along its curve and measured to the
// nearest DWG wall line running the same way (DWG arcs come in as short straight pieces), and the face's own arc
// fitted to the DWG points along it, so a wrong radius or centre gives a fix with numbers, not just a deviation.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    /// <summary>A curved wall: its location arc (centre line), model mm, angles in radians, sweep signed (positive = anticlockwise).</summary>
    public class ArcWall
    {
        public long Id;
        public double Cx, Cy, Radius, Start, Sweep, ThicknessMm;
        public string Type;
        public double LengthMm => Math.Abs(Sweep) * Radius;
        public Pt PointAt(double ang, double r) => new Pt(Cx + r * Math.Cos(ang), Cy + r * Math.Sin(ang));
    }

    /// <summary>A circle fitted to one face: centre, radius, rms of the points about it, how many points.</summary>
    public class ArcFaceFit
    {
        public double Cx, Cy, R, RmsMm;
        public int Points;
        /// <summary>-1 inner face (radius minus half the thickness), +1 outer.</summary>
        public int Side;
    }

    public class ArcWallResult
    {
        public long Id;
        public bool Verified;
        public double MedianMm, WorstFaceMm, FaceLengthMm, InTolLengthMm, In25LengthMm;
        public List<ArcFaceFit> Fits = new List<ArcFaceFit>();
        /// <summary>The centre line the DWG faces give (centre and radius), when a face fitted well.</summary>
        public double? NewCx, NewCy, NewRadius, NewThicknessMm;
    }

    public static class CurvedFit
    {
        public const double StepMm = 200, EndSkipMm = 100, SearchMm = 300, TangentDeg = 15;

        /// <summary>Each face of a curved wall against the DWG's wall lines.</summary>
        public static ArcWallResult Measure(ArcWall w, IList<Seg> dwg, double tolMm)
        {
            var res = new ArcWallResult { Id = w.Id };
            double cosTol = Math.Cos(TangentDeg * Math.PI / 180);
            // only DWG lines near the wall's ring
            double rIn = w.Radius - w.ThicknessMm / 2 - SearchMm, rOut = w.Radius + w.ThicknessMm / 2 + SearchMm;
            var near = dwg.Where(d =>
            {
                if (d.Length2D < 20) return false;
                double ra = Math.Sqrt((d.A.X - w.Cx) * (d.A.X - w.Cx) + (d.A.Y - w.Cy) * (d.A.Y - w.Cy));
                double rb = Math.Sqrt((d.B.X - w.Cx) * (d.B.X - w.Cx) + (d.B.Y - w.Cy) * (d.B.Y - w.Cy));
                return Math.Max(ra, rb) >= rIn && Math.Min(ra, rb) <= rOut;
            }).ToList();
            var allDevs = new List<double>();
            foreach (int side in new[] { -1, 1 })
            {
                double r = w.Radius + side * w.ThicknessMm / 2;
                if (r <= 10) continue;
                double L = Math.Abs(w.Sweep) * r, usable = L - 2 * EndSkipMm;
                int n = usable <= 0 ? 1 : (int)Math.Floor(usable / StepMm) + 1;
                var devs = new List<double>();
                var hits = new List<Pt>();
                for (int i = 0; i < n; i++)
                {
                    double t = n == 1 ? L / 2 : EndSkipMm + usable * i / (n - 1);
                    double ang = w.Start + Math.Sign(w.Sweep) * t / r;
                    double px = w.Cx + r * Math.Cos(ang), py = w.Cy + r * Math.Sin(ang);
                    double tx = -Math.Sin(ang), ty = Math.Cos(ang);
                    double best = double.MaxValue; Pt foot = default;
                    foreach (var d in near)
                    {
                        double dl = d.Length2D, dux = (d.B.X - d.A.X) / dl, duy = (d.B.Y - d.A.Y) / dl;
                        if (Math.Abs(dux * tx + duy * ty) < cosTol) continue;
                        double along = (px - d.A.X) * dux + (py - d.A.Y) * duy;
                        if (along < -20 || along > dl + 20) continue;
                        double perp = Math.Abs((px - d.A.X) * -duy + (py - d.A.Y) * dux);
                        if (perp < best) { best = perp; double a2 = Math.Clamp(along, 0, dl); foot = new Pt(d.A.X + dux * a2, d.A.Y + duy * a2); }
                    }
                    if (best <= SearchMm) { devs.Add(best); hits.Add(foot); }
                }
                if (devs.Count < Math.Max(1, n * 0.3)) continue;
                double seg = L * devs.Count / n;
                res.FaceLengthMm += seg;
                res.InTolLengthMm += seg * devs.Count(d => d <= tolMm) / devs.Count;
                res.In25LengthMm += seg * devs.Count(d => d <= 25) / devs.Count;
                allDevs.AddRange(devs);
                var sorted = devs.OrderBy(d => d).ToList();
                res.WorstFaceMm = Math.Max(res.WorstFaceMm, sorted[sorted.Count / 2]);
                // the face's own circle, from where the DWG lines are (a straight run fits badly and is left out)
                if (hits.Count >= 6 && Circle(hits) is ArcFaceFit fit && fit.RmsMm <= 8 && fit.R < 200000)
                { fit.Side = side; res.Fits.Add(fit); }
            }
            res.Verified = allDevs.Count > 0;
            if (res.Verified) { allDevs.Sort(); res.MedianMm = allDevs[allDevs.Count / 2]; }
            // the centre line: both faces fitted -> their mean radius about their mean centre, thickness their gap;
            // one face -> that face moved by half the wall's thickness
            var inner = res.Fits.FirstOrDefault(f => f.Side < 0); var outer = res.Fits.FirstOrDefault(f => f.Side > 0);
            if (inner != null && outer != null && Math.Sqrt(Sq(inner.Cx - outer.Cx) + Sq(inner.Cy - outer.Cy)) < 100 && outer.R > inner.R)
            {
                res.NewCx = (inner.Cx + outer.Cx) / 2; res.NewCy = (inner.Cy + outer.Cy) / 2;
                res.NewRadius = (inner.R + outer.R) / 2; res.NewThicknessMm = outer.R - inner.R;
            }
            else if ((inner ?? outer) is ArcFaceFit one)
            {
                res.NewCx = one.Cx; res.NewCy = one.Cy; res.NewRadius = one.R - one.Side * w.ThicknessMm / 2;
            }
            return res;
        }

        private static double Sq(double v) => v * v;

        /// <summary>Least-squares circle through points (Kasa), or null when they are (nearly) in a line.</summary>
        public static ArcFaceFit Circle(IList<Pt> pts)
        {
            if (pts.Count < 3) return null;
            double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
            double suu = 0, svv = 0, suv = 0, suuu = 0, svvv = 0, suvv = 0, svuu = 0;
            foreach (var p in pts)
            {
                double u = p.X - mx, v = p.Y - my;
                suu += u * u; svv += v * v; suv += u * v;
                suuu += u * u * u; svvv += v * v * v; suvv += u * v * v; svuu += v * u * u;
            }
            double det = suu * svv - suv * suv;
            if (Math.Abs(det) < 1e-6 * (suu * svv + 1)) return null;
            double bu = 0.5 * (suuu + suvv), bv = 0.5 * (svvv + svuu);
            double uc = (bu * svv - bv * suv) / det, vc = (suu * bv - suv * bu) / det;
            double cx = uc + mx, cy = vc + my;
            double R = pts.Average(p => Math.Sqrt(Sq(p.X - cx) + Sq(p.Y - cy)));
            double rms = Math.Sqrt(pts.Average(p => Sq(Math.Sqrt(Sq(p.X - cx) + Sq(p.Y - cy)) - R)));
            return new ArcFaceFit { Cx = cx, Cy = cy, R = R, RmsMm = rms, Points = pts.Count };
        }

        /// <summary>Adds the curved walls of a floor to its wall score (called from Fidelity.Measure).</summary>
        internal static void Score(FidelityInput f, FidelityGate g, WallScore ws, List<FidIssue> issues, ref double total, ref double inTol, ref double in25, ref double max)
        {
            foreach (var w in f.ArcWalls)
            {
                if (w.LengthMm < 300 || w.ThicknessMm < 30) continue;
                var r = Measure(w, f.WallFaces, g.WallTolMm);
                ws.CurvedChecked++;
                if (!r.Verified) { ws.Unverified++; continue; }
                ws.Checked++;
                total += r.FaceLengthMm; inTol += r.InTolLengthMm; in25 += r.In25LengthMm;
                max = Math.Max(max, r.WorstFaceMm);
                if (r.WorstFaceMm <= g.WallTolMm) continue;
                string fix = r.NewRadius is double nr
                    ? $"survey_autofix (walls) re-curves it: centre [{r.NewCx:0}, {r.NewCy:0}], radius {nr:0} mm (model [{w.Cx:0}, {w.Cy:0}], {w.Radius:0})" + (r.NewThicknessMm is double nt ? $", thickness {nt:0} mm" : "")
                    : "cad_lines round it, then set_wall_line with an arc (start, end, and a point on the curve)";
                var mid = w.PointAt(w.Start + w.Sweep / 2, w.Radius);
                issues.Add(new FidIssue
                {
                    Kind = "wall_face_curved", Level = f.Level, Id = w.Id, DevMm = Math.Round(r.WorstFaceMm),
                    Against = "plan DWG wall face lines (curved wall)", At = new[] { Math.Round(mid.X), Math.Round(mid.Y) },
                    Fix = fix, Severity = r.WorstFaceMm / g.WallTolMm,
                });
            }
        }
    }
}
