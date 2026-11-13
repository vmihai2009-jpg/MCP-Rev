// Revit-free. 1.21.0: does the model build the survey's curves as curves? For each curved wall face on a floor's plan
// (a true arc, or a chain of short lines CurveChains fits to one), the model walls along it: one curved wall (right), a
// run of short straight walls (chorded: a staircase nobody can edit as a curve), or nothing. For a chorded run it
// works out the one curved wall to put there instead: centre, radius of the wall's centre line, angles, thickness.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public class CurveWallFinding
    {
        public string Source;          // arc | chain
        public double Cx, Cy, FaceR, StartDeg, SweepDeg;
        public int DrawnPieces;
        public string State;           // curved | chorded | partial | missing
        public List<long> StraightWalls = new List<long>(), CurvedWalls = new List<long>();
        public double Covered;         // share of the curve's sweep the model's walls run along
        /// <summary>The curved wall to build instead of StraightWalls: centre line radius, angles (radians, anticlockwise), thickness.</summary>
        public double NewR, NewStart, NewSweep, ThicknessMm;
        public string Type;
    }

    public static class CurveWalls
    {
        /// <param name="curves">Curved faces in the plan (model mm): centre, radius, start, sweep (radians, anticlockwise), how many drawn pieces.</param>
        public static List<CurveWallFinding> Check(IEnumerable<(string source, double cx, double cy, double r, double start, double sweep, int pieces)> curves,
            IList<FitWall> straight, IList<ArcWall> arcs, double tolMm = 30)
        {
            var res = new List<CurveWallFinding>();
            foreach (var c in curves)
            {
                var f = new CurveWallFinding
                {
                    Source = c.source, Cx = Math.Round(c.cx), Cy = Math.Round(c.cy), FaceR = Math.Round(c.r), StartDeg = Math.Round(c.start * 180 / Math.PI, 1),
                    SweepDeg = Math.Round(c.sweep * 180 / Math.PI, 1), DrawnPieces = c.pieces,
                };
                double Ang(Pt p) => Math.Atan2(p.Y - c.cy, p.X - c.cx);
                double Rel(double a) { double d = (a - c.start) % (2 * Math.PI); return d < 0 ? d + 2 * Math.PI : d; }   // 0..2pi from the curve's start
                double slack = Math.Min(0.05, 0.1 * c.sweep);
                bool Within(double a) { double d = Rel(a); return d <= c.sweep + slack || d >= 2 * Math.PI - slack; }
                double Clamp(double a) { double d = Rel(a); return d > c.sweep + slack ? 0 : Math.Min(d, c.sweep); }
                var spans = new List<(double a0, double a1)>();
                // straight walls with both ends on the curve's band and running along it
                var chords = new List<FitWall>();
                foreach (var w in straight)
                {
                    double t = w.ThicknessMm, dA = w.A.Dist2D(new Pt(c.cx, c.cy)), dB = w.B.Dist2D(new Pt(c.cx, c.cy));
                    if (Math.Abs(dA - c.r) > t + tolMm || Math.Abs(dB - c.r) > t + tolMm) continue;
                    if (!Within(Ang(w.A)) || !Within(Ang(w.B))) continue;
                    var mid = new Pt((w.A.X + w.B.X) / 2, (w.A.Y + w.B.Y) / 2);
                    double len = w.A.Dist2D(w.B); if (len < 1) continue;
                    // along the curve: square to the radius at its middle
                    double rx = mid.X - c.cx, ry = mid.Y - c.cy, rl = Math.Sqrt(rx * rx + ry * ry);
                    if (rl < 1 || Math.Abs(((w.B.X - w.A.X) * rx + (w.B.Y - w.A.Y) * ry) / (len * rl)) > 0.5) continue;
                    // and not cutting across: its sagitta is within the band
                    if (Math.Abs(rl - c.r) > t + tolMm + len * len / (8 * c.r)) continue;
                    chords.Add(w);
                    double a0 = Clamp(Ang(w.A)), a1 = Clamp(Ang(w.B));
                    spans.Add((Math.Min(a0, a1), Math.Max(a0, a1)));
                }
                foreach (var w in arcs)
                {
                    if (Math.Sqrt((w.Cx - c.cx) * (w.Cx - c.cx) + (w.Cy - c.cy) * (w.Cy - c.cy)) > 300 || Math.Abs(w.Radius - c.r) > w.ThicknessMm + tolMm + 100) continue;
                    double a0 = w.Sweep > 0 ? w.Start : w.Start + w.Sweep, sw = Math.Abs(w.Sweep);
                    double b0 = Clamp(a0), b1 = Clamp(a0 + sw);
                    if (!Within(a0) && !Within(a0 + sw) && !(Rel(a0) > c.sweep && Rel(a0) + sw > 2 * Math.PI)) continue;
                    f.CurvedWalls.Add(w.Id);
                    spans.Add((Math.Min(b0, b1), Math.Max(b0, b1) <= Math.Min(b0, b1) ? c.sweep : Math.Max(b0, b1)));
                }
                f.StraightWalls = chords.Select(w => w.Id).ToList();
                f.Covered = Math.Round(Union(spans) / Math.Max(1e-9, c.sweep), 2);
                f.State = f.CurvedWalls.Count > 0 && f.StraightWalls.Count <= 1 && f.Covered >= 0.5 ? "curved"
                        : f.StraightWalls.Count >= 2 && f.Covered >= 0.5 ? "chorded"
                        : f.Covered > 0.05 ? "partial" : "missing";
                if (chords.Count > 0)
                {
                    // the thickness and type most of the run has; the wall's centre line half a thickness off the face, on the walls' side
                    var main = chords.GroupBy(w => (Math.Round(w.ThicknessMm), w.Type)).OrderByDescending(g => g.Sum(w => w.A.Dist2D(w.B))).First();
                    f.ThicknessMm = main.Key.Item1; f.Type = main.Key.Type;
                    double mean = chords.Average(w => (w.A.Dist2D(new Pt(c.cx, c.cy)) + w.B.Dist2D(new Pt(c.cx, c.cy))) / 2);
                    f.NewR = Math.Round(mean >= c.r ? c.r + f.ThicknessMm / 2 : c.r - f.ThicknessMm / 2, 1);
                    // the run's own ends (it meets the straight walls there)
                    double lo = spans.Where((_, i) => i < chords.Count).Min(s => s.a0), hi = spans.Where((_, i) => i < chords.Count).Max(s => s.a1);
                    f.NewStart = c.start + lo; f.NewSweep = hi - lo;
                }
                res.Add(f);
            }
            return res;
        }

        private static double Union(List<(double a0, double a1)> spans)
        {
            double tot = 0, end = double.MinValue;
            foreach (var s in spans.OrderBy(s => s.a0))
            {
                if (s.a1 <= end) continue;
                tot += s.a1 - Math.Max(s.a0, end == double.MinValue ? s.a0 : end);
                end = s.a1;
            }
            return tot;
        }
    }
}
