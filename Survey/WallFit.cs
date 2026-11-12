// Revit-free. Fits the model's walls to the survey DWG's wall faces: the logic the Swallow Place session ran as
// fit.ps1 / audit.ps1 against AutoCAD DXF exports, made a tool that reads the linked DWG directly.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    /// <summary>A wall in the model: id, centre line (model mm) and thickness.</summary>
    public class FitWall
    {
        public long Id;
        public Pt A, B;
        public double ThicknessMm;
        public string Type;
    }

    public class FitOptions
    {
        /// <summary>Walls thinner or shorter than these are left alone.</summary>
        public double MinThicknessMm = 60, MinLengthMm = 600;
        /// <summary>How far from the wall's centre line a DWG face may be to count as one of its faces.</summary>
        public double SearchMm = 700;
        /// <summary>Share of the wall's length a face must cover.</summary>
        public double MinCover = 0.6;
        /// <summary>Changes smaller than this are not proposed.</summary>
        public double MinChangeMm = 30;
        /// <summary>New thicknesses are rounded to this (Concept Wall types come in 10 mm steps or more).</summary>
        public double RoundThicknessMm = 10;
        public double MaxThicknessMm = 1300;
    }

    public class FitProposal
    {
        public long Id { get; set; }
        public string Type { get; set; }
        public double LengthMm { get; set; }
        public double ThicknessMm { get; set; }
        public double NewThicknessMm { get; set; }
        /// <summary>How far the centre line moves at the wall's start and end (mm, sideways).</summary>
        public double ShiftStartMm { get; set; }
        public double ShiftEndMm { get; set; }
        /// <summary>The corrected centre line [x1, y1, x2, y2], model mm.</summary>
        public double[] NewLine { get; set; }
        public double CoverA { get; set; }
        public double CoverB { get; set; }
        /// <summary>moderate = both faces well covered and the change modest; review = large change or weak evidence.</summary>
        public string Confidence { get; set; }
    }

    public class MissingWall
    {
        public double[] Line { get; set; }
        public double ThicknessMm { get; set; }
        public double LengthMm { get; set; }
        public string Layer { get; set; }
    }

    public static class WallFit
    {
        private class Face { public double O0, OL, Om, Cover, Best; }

        /// <summary>
        /// For each model wall: the pair of DWG face lines that best fits it, and the centre line and thickness that
        /// would put the wall on them. Only walls that would change by more than MinChangeMm are returned.
        /// </summary>
        public static List<FitProposal> Fit(IList<FitWall> walls, IList<Seg> dwg, FitOptions o)
        {
            o = o ?? new FitOptions();
            var lines = dwg.Where(s => s.Length2D >= 150).ToList();
            var result = new List<FitProposal>();
            foreach (var w in walls)
            {
                double L = w.A.Dist2D(w.B);
                if (w.ThicknessMm < o.MinThicknessMm || L < o.MinLengthMm) continue;
                double ux = (w.B.X - w.A.X) / L, uy = (w.B.Y - w.A.Y) / L, nx = -uy, ny = ux;

                var cands = new List<Face>();
                foreach (var d in lines)
                {
                    double dl = d.Length2D, dux = (d.B.X - d.A.X) / dl, duy = (d.B.Y - d.A.Y) / dl;
                    if (Math.Abs(dux * ux + duy * uy) < 0.998) continue; // within ~3.6 degrees
                    double a = (d.A.X - w.A.X) * ux + (d.A.Y - w.A.Y) * uy, b = (d.B.X - w.A.X) * ux + (d.B.Y - w.A.Y) * uy;
                    double lo = Math.Max(0, Math.Min(a, b)), hi = Math.Min(L, Math.Max(a, b));
                    if (hi - lo < 100 || Math.Abs(b - a) < 1) continue;
                    double oa = (d.A.X - w.A.X) * nx + (d.A.Y - w.A.Y) * ny, ob = (d.B.X - w.A.X) * nx + (d.B.Y - w.A.Y) * ny;
                    double o0 = oa + (ob - oa) * (0 - a) / (b - a), oL = oa + (ob - oa) * (L - a) / (b - a); // offsets at the wall's ends
                    double om = (o0 + oL) / 2;
                    if (Math.Abs(om) > o.SearchMm) continue;
                    cands.Add(new Face { O0 = o0, OL = oL, Om = om, Cover = hi - lo, Best = hi - lo });
                }
                if (cands.Count < 2) continue;

                // faces: candidates within 25 mm of each other are one face; their coverage adds up
                var faces = new List<Face>();
                foreach (var c in cands.OrderBy(c => c.Om))
                {
                    var f = faces.FirstOrDefault(x => Math.Abs(x.Om - c.Om) <= 25);
                    if (f == null) { faces.Add(new Face { O0 = c.O0, OL = c.OL, Om = c.Om, Cover = c.Cover, Best = c.Best }); continue; }
                    f.Cover += c.Cover;
                    if (c.Cover > f.Best) { f.Best = c.Cover; f.O0 = c.O0; f.OL = c.OL; f.Om = c.Om; }
                }
                var good = faces.Where(f => Math.Min(1, f.Cover / L) >= o.MinCover).ToList();
                if (good.Count < 2) continue;

                // best pair: a face each side, a plausible thickness, most coverage, close to the current thickness and place
                Face fa = null, fb = null; double best = double.MinValue;
                foreach (var a in good)
                    foreach (var b in good)
                    {
                        if (b.Om <= a.Om) continue;
                        double th = b.Om - a.Om;
                        if (th < 60 || th > o.MaxThicknessMm) continue;
                        double score = Math.Min(Math.Min(1, a.Cover / L), Math.Min(1, b.Cover / L)) - Math.Abs(th - w.ThicknessMm) / 2000 - Math.Abs((a.Om + b.Om) / 2) / 3000;
                        if (score > best) { best = score; fa = a; fb = b; }
                    }
                if (fa == null) continue;

                double c0 = (fa.O0 + fb.O0) / 2, cL = (fa.OL + fb.OL) / 2;
                double step = Math.Max(1, o.RoundThicknessMm);
                double newT = Math.Round(((fb.O0 - fa.O0) + (fb.OL - fa.OL)) / 2 / step) * step;
                double shift = Math.Max(Math.Abs(c0), Math.Abs(cL)), dT = newT - w.ThicknessMm;
                if (shift <= o.MinChangeMm && Math.Abs(dT) <= o.MinChangeMm) continue;

                double coverA = Math.Round(Math.Min(1, fa.Cover / L), 2), coverB = Math.Round(Math.Min(1, fb.Cover / L), 2);
                result.Add(new FitProposal
                {
                    Id = w.Id, Type = w.Type, LengthMm = Math.Round(L), ThicknessMm = Math.Round(w.ThicknessMm), NewThicknessMm = newT,
                    ShiftStartMm = Math.Round(c0), ShiftEndMm = Math.Round(cL), CoverA = coverA, CoverB = coverB,
                    NewLine = new[] { Math.Round(w.A.X + nx * c0), Math.Round(w.A.Y + ny * c0), Math.Round(w.B.X + nx * cL), Math.Round(w.B.Y + ny * cL) },
                    Confidence = coverA >= 0.8 && coverB >= 0.8 && shift <= 400 && Math.Abs(dT) <= 300 ? "moderate" : "review",
                });
            }
            return result.OrderByDescending(p => Math.Max(Math.Abs(p.ShiftStartMm), Math.Abs(p.ShiftEndMm)) + Math.Abs(p.NewThicknessMm - p.ThicknessMm)).ToList();
        }

        /// <summary>
        /// Walls the DWG draws (pairs of parallel face lines) with no model wall along them: less than a fifth of the
        /// DWG wall's length has a parallel model wall within 400 mm.
        /// A pair is not a wall (and not reported) when:
        /// - either line is already the face of a model wall (the pair is the gap between two walls: a corridor, a chimney breast's side),
        /// - another drawn face line runs between the two (two walls, or a wall and a step or plinth),
        /// - a room or spot height sits between them (it is a space),
        /// - it is wider than <paramref name="maxThickMm"/>.
        /// Each line pairs with its nearest partner first, so a wall's two faces are not split across wide pairs.
        /// </summary>
        public static List<MissingWall> Missing(IList<FitWall> walls, IList<Seg> dwg, double minThickMm, double maxThickMm, double minLengthMm,
            IList<Pt> spaceMarkers = null) => Missing(walls, dwg, minThickMm, maxThickMm, minLengthMm, spaceMarkers, out _);

        /// <param name="notWalls">Face pairs left out because they are not walls (see the rules above).</param>
        public static List<MissingWall> Missing(IList<FitWall> walls, IList<Seg> dwg, double minThickMm, double maxThickMm, double minLengthMm,
            IList<Pt> spaceMarkers, out int notWalls)
        {
            var pairs = WallDetector.FromParallelPairs(dwg, minThickMm, maxThickMm, Math.Max(300, minLengthMm), 1.0, nearestFirst: true);
            var result = new List<MissingWall>();
            notWalls = 0;
            foreach (var p in pairs)
            {
                double L = p.Start.Dist2D(p.End);
                if (L < minLengthMm) continue;
                if (Covered(p.Start, p.End, walls, 400) >= 0.2) continue;
                if (!IsWall(p, dwg, walls, spaceMarkers)) { notWalls++; continue; }
                result.Add(new MissingWall
                {
                    Line = new[] { Math.Round(p.Start.X), Math.Round(p.Start.Y), Math.Round(p.End.X), Math.Round(p.End.Y) },
                    ThicknessMm = Math.Round(p.ThicknessMm), LengthMm = Math.Round(L),
                });
            }
            return result.OrderByDescending(m => m.LengthMm).ToList();
        }

        /// <summary>Within this of a model wall's face, a drawn line is that wall's face (wider than the 50 mm of a wall centred on its face line).</summary>
        public const double OnFaceMm = 60;

        /// <summary>Could this face pair be one wall? See <see cref="Missing(IList{FitWall}, IList{Seg}, double, double, double, IList{Pt})"/>.</summary>
        public static bool IsWall(WallCandidate p, IList<Seg> dwg, IList<FitWall> walls, IList<Pt> spaceMarkers = null)
        {
            double L = p.Start.Dist2D(p.End);
            if (L < 1e-6) return false;
            double ux = (p.End.X - p.Start.X) / L, uy = (p.End.Y - p.Start.Y) / L, nx = -uy, ny = ux;
            double half = p.ThicknessMm / 2;
            // the two face lines, along the pair's overlap
            Pt Face(double side, bool end) => new Pt((end ? p.End.X : p.Start.X) + nx * side * half, (end ? p.End.Y : p.Start.Y) + ny * side * half);
            foreach (var side in new[] { -1.0, 1.0 })
                if (OnModelFace(Face(side, false), Face(side, true), walls) >= 0.5) return false;

            // another parallel face line between the two, along at least half the pair
            var inner = new List<(double lo, double hi)>();
            foreach (var d in dwg)
            {
                double dl = d.Length2D; if (dl < 1) continue;
                double dux = (d.B.X - d.A.X) / dl, duy = (d.B.Y - d.A.Y) / dl;
                if (Math.Abs(dux * ux + duy * uy) < 0.996) continue;
                double oa = (d.A.X - p.Start.X) * nx + (d.A.Y - p.Start.Y) * ny, ob = (d.B.X - p.Start.X) * nx + (d.B.Y - p.Start.Y) * ny;
                double gap = Math.Min(30, half / 3);
                if (Math.Abs(oa) > half - gap || Math.Abs(ob) > half - gap) continue;
                double s = GeoMath.Clamp((d.A.X - p.Start.X) * ux + (d.A.Y - p.Start.Y) * uy, 0, L), t = GeoMath.Clamp((d.B.X - p.Start.X) * ux + (d.B.Y - p.Start.Y) * uy, 0, L);
                if (Math.Abs(t - s) > 1) inner.Add((Math.Min(s, t), Math.Max(s, t)));
            }
            if (Union(inner) >= 0.5 * L) return false;

            // a room name or spot height between the faces
            if (spaceMarkers != null)
                foreach (var m in spaceMarkers)
                {
                    double t = (m.X - p.Start.X) * ux + (m.Y - p.Start.Y) * uy, o = (m.X - p.Start.X) * nx + (m.Y - p.Start.Y) * ny;
                    if (t > 0 && t < L && Math.Abs(o) < half - 20) return false;
                }
            return true;
        }

        /// <summary>Share of a-b's length that lies on a face of a model wall (parallel, within <see cref="OnFaceMm"/>).</summary>
        public static double OnModelFace(Pt a, Pt b, IList<FitWall> walls)
        {
            double L = a.Dist2D(b);
            if (L < 1e-6) return 0;
            double ux = (b.X - a.X) / L, uy = (b.Y - a.Y) / L, nx = -uy, ny = ux;
            var spans = new List<(double lo, double hi)>();
            foreach (var w in walls)
            {
                double wl = w.A.Dist2D(w.B);
                if (wl < 1) continue;
                double wux = (w.B.X - w.A.X) / wl, wuy = (w.B.Y - w.A.Y) / wl;
                if (Math.Abs(wux * ux + wuy * uy) < 0.996) continue;
                double oa = (w.A.X - a.X) * nx + (w.A.Y - a.Y) * ny, ob = (w.B.X - a.X) * nx + (w.B.Y - a.Y) * ny;
                double h = w.ThicknessMm / 2;
                // one of the wall's faces runs along the line
                if (!(Math.Abs(Math.Abs(oa) - h) <= OnFaceMm && Math.Abs(Math.Abs(ob) - h) <= OnFaceMm)) continue;
                double s = GeoMath.Clamp((w.A.X - a.X) * ux + (w.A.Y - a.Y) * uy, 0, L), t = GeoMath.Clamp((w.B.X - a.X) * ux + (w.B.Y - a.Y) * uy, 0, L);
                if (Math.Abs(t - s) > 1) spans.Add((Math.Min(s, t), Math.Max(s, t)));
            }
            return Union(spans) / L;
        }

        static double Union(List<(double lo, double hi)> spans)
        {
            double total = 0, end = double.NegativeInfinity;
            foreach (var (lo, hi) in spans.OrderBy(x => x.lo))
            {
                if (lo > end) { total += hi - lo; end = hi; }
                else if (hi > end) { total += hi - end; end = hi; }
            }
            return total;
        }

        /// <summary>Share of a-b's length that model walls run along (parallel within 5 degrees, centre within <paramref name="within"/> mm).</summary>
        public static double Covered(Pt a, Pt b, IList<FitWall> walls, double within)
        {
            double L = a.Dist2D(b);
            if (L < 1e-6) return 0;
            double ux = (b.X - a.X) / L, uy = (b.Y - a.Y) / L, nx = -uy, ny = ux;
            var spans = new List<(double lo, double hi)>();
            foreach (var w in walls)
            {
                double wl = w.A.Dist2D(w.B);
                if (wl < 1) continue;
                double wux = (w.B.X - w.A.X) / wl, wuy = (w.B.Y - w.A.Y) / wl;
                if (Math.Abs(wux * ux + wuy * uy) < 0.996) continue;
                if (Math.Abs((w.A.X - a.X) * nx + (w.A.Y - a.Y) * ny) > within || Math.Abs((w.B.X - a.X) * nx + (w.B.Y - a.Y) * ny) > within) continue;
                double s = GeoMath.Clamp((w.A.X - a.X) * ux + (w.A.Y - a.Y) * uy, 0, L), t = GeoMath.Clamp((w.B.X - a.X) * ux + (w.B.Y - a.Y) * uy, 0, L);
                if (Math.Abs(t - s) > 1) spans.Add((Math.Min(s, t), Math.Max(s, t)));
            }
            double total = 0, end = double.NegativeInfinity;
            foreach (var (lo, hi) in spans.OrderBy(x => x.lo))
            {
                if (lo > end) { total += hi - lo; end = hi; }
                else if (hi > end) { total += hi - end; end = hi; }
            }
            return total / L;
        }
    }
}
