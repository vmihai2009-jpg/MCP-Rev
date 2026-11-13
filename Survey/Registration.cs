// Revit-free. 1.28.0: do the survey's drawings share one frame? Two drawings of the same building (two floor plans, a plan
// and the floor below it) must line up: the long straight lines of one, offset across themselves, mostly land on lines of
// the other. Walls that really step between floors only add scattered offsets; a sheet placed a few mm off moves all of
// them by the same amount. So the shift is the most common offset across each of the two main directions, and what is
// left after it (the residual) says how well the two agree. A few mm here use up the 10 mm budget before any modelling.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public class RegistrationResult
    {
        /// <summary>Move the moving drawing by this (mm) to sit on the fixed one.</summary>
        public double Dx, Dy, RotDeg;
        /// <summary>RMS of the matched offsets after the shift (mm), and the matched length (m) each direction rests on.</summary>
        public double ResidualMm, MatchedAM, MatchedBM;
        public string Note;
        public double ShiftMm => Math.Sqrt(Dx * Dx + Dy * Dy);
    }

    public static class Registration
    {
        public static RegistrationResult Align(IList<Seg> moving, IList<Seg> fix, double maxShiftMm = 300, double minLenMm = 1000, double binMm = 2)
        {
            var r = new RegistrationResult();
            var lf = fix.Where(s => s.Length2D >= minLenMm).ToList(); var lm = moving.Where(s => s.Length2D >= minLenMm).ToList();
            if (lf.Count < 4 || lm.Count < 4) { r.Note = "too few long lines to compare"; return r; }
            double theta = Dominant(lf), thetaM = Dominant(lm);
            var turns = new List<(double a, double w)>();
            var devs = new List<double>();
            double[] shift = new double[2]; double[] matched = new double[2];
            for (int k = 0; k < 2; k++)
            {
                double ang = theta + k * Math.PI / 2, ux = Math.Cos(ang), uy = Math.Sin(ang), nx = -uy, ny = ux;
                bool Along(Seg s) { double a = Math.Atan2(s.B.Y - s.A.Y, s.B.X - s.A.X); return Math.Abs(Wrap180(a - ang)) <= 2 * Math.PI / 180; }
                var F = lf.Where(Along).Select(s => (a: Math.Atan2(s.B.Y - s.A.Y, s.B.X - s.A.X), d: nx * (s.A.X + s.B.X) / 2 + ny * (s.A.Y + s.B.Y) / 2, t0: Math.Min(ux * s.A.X + uy * s.A.Y, ux * s.B.X + uy * s.B.Y), t1: Math.Max(ux * s.A.X + uy * s.A.Y, ux * s.B.X + uy * s.B.Y))).ToList();
                var M = lm.Where(Along).Select(s => (a: Math.Atan2(s.B.Y - s.A.Y, s.B.X - s.A.X), d: nx * (s.A.X + s.B.X) / 2 + ny * (s.A.Y + s.B.Y) / 2, t0: Math.Min(ux * s.A.X + uy * s.A.Y, ux * s.B.X + uy * s.B.Y), t1: Math.Max(ux * s.A.X + uy * s.A.Y, ux * s.B.X + uy * s.B.Y))).ToList();
                var votes = new List<(double off, double w, double turn)>();
                foreach (var m in M)
                    foreach (var f in F)
                    {
                        double ov = Math.Min(m.t1, f.t1) - Math.Max(m.t0, f.t0);
                        if (ov < 0.3 * Math.Min(m.t1 - m.t0, f.t1 - f.t0)) continue;
                        double off = f.d - m.d;
                        if (Math.Abs(off) <= maxShiftMm) votes.Add((off, ov, Wrap180(f.a - m.a)));
                    }
                if (votes.Count == 0) continue;
                // the most common offset (weighted by overlapping length), then refined: the weighted median near it
                var bins = votes.GroupBy(v => (long)Math.Round(v.off / binMm)).Select(g => (k: g.Key, w: g.Sum(x => x.w))).ToList();
                long best = bins.OrderByDescending(b => bins.Where(o => Math.Abs(o.k - b.k) <= 1).Sum(o => o.w)).First().k;
                var near = votes.Where(v => Math.Abs(v.off - best * binMm) <= 3 * binMm).OrderBy(v => v.off).ToList();
                double half = near.Sum(v => v.w) / 2, acc = 0, med = best * binMm;
                foreach (var v in near) { acc += v.w; if (acc >= half) { med = v.off; break; } }
                shift[k] = med; matched[k] = near.Sum(v => v.w) / 1000;
                turns.AddRange(near.Select(v => (v.turn, v.w)));
                devs.AddRange(votes.Where(v => Math.Abs(v.off - med) <= 25).Select(v => v.off - med));
                // the shift across this direction, as x/y
                r.Dx += med * nx; r.Dy += med * ny;
            }
            if (turns.Count > 0)
            {
                var ts = turns.OrderBy(x => x.a).ToList(); double h = ts.Sum(x => x.w) / 2, ac = 0;
                foreach (var x in ts) { ac += x.w; if (ac >= h) { r.RotDeg = Math.Round(x.a * 180 / Math.PI, 3); break; } }
            }
            r.Dx = Math.Round(r.Dx, 1); r.Dy = Math.Round(r.Dy, 1);
            r.MatchedAM = Math.Round(matched[0], 1); r.MatchedBM = Math.Round(matched[1], 1);
            r.ResidualMm = devs.Count == 0 ? 0 : Math.Round(Math.Sqrt(devs.Average(d => d * d)), 1);
            if (matched[0] < 2 || matched[1] < 2) r.Note = "little shared length in one direction: the shift across it is not certain";
            return r;
        }

        /// <summary>The main direction of the lines (0..90 degrees, length weighted, mod 90).</summary>
        public static double Dominant(IList<Seg> segs)
        {
            double sx = 0, sy = 0;
            foreach (var s in segs)
            {
                double a = Math.Atan2(s.B.Y - s.A.Y, s.B.X - s.A.X), l = s.Length2D;
                sx += l * Math.Cos(4 * a); sy += l * Math.Sin(4 * a);
            }
            double t = Math.Atan2(sy, sx) / 4;
            // again with only the lines within 2 degrees of it: a few slanted lines don't turn the drawing
            double rx = 0, ry = 0;
            foreach (var s in segs)
            {
                double a = Math.Atan2(s.B.Y - s.A.Y, s.B.X - s.A.X), l = s.Length2D;
                if (Math.Abs(Wrap90(a - t)) > 2 * Math.PI / 180) continue;
                rx += l * Math.Cos(4 * a); ry += l * Math.Sin(4 * a);
            }
            if (rx != 0 || ry != 0) t = Math.Atan2(ry, rx) / 4;
            return t < 0 ? t + Math.PI / 2 : t;
        }

        private static double Wrap180(double a) { while (a > Math.PI / 2) a -= Math.PI; while (a < -Math.PI / 2) a += Math.PI; return a; }
        private static double Wrap90(double a) { while (a > Math.PI / 4) a -= Math.PI / 2; while (a < -Math.PI / 4) a += Math.PI / 2; return a; }
    }
}
