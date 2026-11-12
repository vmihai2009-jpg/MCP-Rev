// Revit-free. What a window looks like on a survey elevation: the glass panes the window layer draws inside its
// opening (two for a sliding sash, four for a pair of them, an arched top pane for a round-headed window), and the
// opening's outline round them, so a family can be built that looks like the drawing.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BarrGazetas.RevitMcp.Survey
{
    public class WindowPattern
    {
        /// <summary>The opening's size (mm): the outline's extents.</summary>
        public double Width, Height;
        /// <summary>The opening's outline, mm from its bottom-left corner as seen from outside (x right, y up).</summary>
        public List<Pt> Outline = new List<Pt>();
        /// <summary>The glass panes, same frame as the outline.</summary>
        public List<List<Pt>> Panes = new List<List<Pt>>();
        public bool Arched;
        /// <summary>False when the drawing gave no panes: the window is then one pane inside a frame of default width.</summary>
        public bool FromDrawing;

        /// <summary>Same look, to 5 mm (the model is to be 1:1, and the score checks to 10 mm): windows with the same key share a family.</summary>
        public string Key
        {
            get
            {
                var sb = new StringBuilder();
                string N(double v) => Math.Round(v / 5).ToString(CultureInfo.InvariantCulture);
                sb.Append(N(Width)).Append('x').Append(N(Height));
                // each ring as its set of points, so where it starts and which way round it goes do not matter
                string Ring(IEnumerable<Pt> r) => string.Join(";", r.Select(q => N(q.X) + "," + N(q.Y)).Distinct().OrderBy(s => s, StringComparer.Ordinal));
                foreach (var p in Panes.Select(Ring).OrderBy(s => s, StringComparer.Ordinal)) sb.Append('|').Append(p);
                if (Arched) sb.Append("|o").Append(Ring(Outline));
                return sb.ToString();
            }
        }

        /// <summary>A short readable name for the look: "2x1 sash", "2x2", "arched 2x2"...</summary>
        public string Describe()
        {
            if (!FromDrawing) return "plain";
            var cols = Cluster(Panes.Select(p => (p.Min(q => q.X) + p.Max(q => q.X)) / 2), 60).Count;
            var rows = Cluster(Panes.Select(p => (p.Min(q => q.Y) + p.Max(q => q.Y)) / 2), 60).Count;
            return (Arched ? "arched " : "") + $"{cols} wide x {rows} high";
        }

        /// <summary>The same window seen from the other side (a mirrored instance).</summary>
        public WindowPattern Mirrored()
        {
            Pt M(Pt p) => new Pt(Width - p.X, p.Y);
            return new WindowPattern
            {
                Width = Width, Height = Height, Arched = Arched, FromDrawing = FromDrawing,
                Outline = Enumerable.Reverse(Outline).Select(M).ToList(),
                Panes = Panes.Select(p => Enumerable.Reverse(p).Select(M).ToList()).ToList(),
            };
        }

        /// <summary>
        /// The pattern of one opening on an elevation sheet: window-layer loops inside the opening's box are the panes
        /// (the innermost ones, when a frame is drawn round them on the same layer); the outline is the opening box,
        /// with an arched top where the top panes are arched. scale: model mm per sheet unit (mm).
        /// </summary>
        public static WindowPattern Read(IEnumerable<DwgLine> windowLines, ElevOpening op, double scale = 1, double defaultFrameMm = 60)
        {
            // the same box the model's opening is sized to (MatchFacade: reveal width where drawn, sill and head as drawn)
            double L = op.OpeningLeft, R = op.OpeningRight, B = op.Bottom, T = op.Top;
            double pad = 15;
            var segs = windowLines.Where(l => Math.Min(l.A.X, l.B.X) >= L - pad && Math.Max(l.A.X, l.B.X) <= R + pad
                                              && Math.Min(l.A.Y, l.B.Y) >= B - pad && Math.Max(l.A.Y, l.B.Y) <= T + pad)
                                  .Select(l => new Seg(l.A, l.B, 0)).ToList();
            var loops = LoopBuilder.Build(segs, 3).Loops
                .Where(lp => Math.Abs(GeoMath.SignedArea(lp)) >= 0.01e6 / (scale * scale)).ToList();
            // innermost: no other loop inside it
            var panes = loops.Where(lp => !loops.Any(o => !ReferenceEquals(o, lp) && Inside(o, lp))).ToList();
            var w = new WindowPattern { Width = (R - L) * scale, Height = (T - B) * scale };
            Pt Rel(Pt p) => new Pt((p.X - L) * scale, (p.Y - B) * scale);
            if (panes.Count == 0)
            {
                double f = defaultFrameMm;
                w.Panes.Add(Rect(f, f, w.Width - f, w.Height - f));
                w.Outline = Rect(0, 0, w.Width, w.Height);
                return w;
            }
            w.FromDrawing = true;
            // a pane drawn to the opening's edge (no reveal drawn) keeps a frame of at least minFrame inside it
            const double minFrame = 25;
            Pt Keep(Pt q) => new Pt(Math.Clamp(q.X, minFrame, w.Width - minFrame), Math.Max(q.Y, minFrame));
            w.Panes = panes.Select(p => Ccw(Simplify(p.Select(Rel).Select(Keep).ToList()))).ToList();

            // arched: the top of the panes rises above where their sides stop
            var all = w.Panes.SelectMany(p => p).ToList();
            double minX = all.Min(p => p.X), maxX = all.Max(p => p.X), maxY = all.Max(p => p.Y);
            double spring = all.Where(p => p.X <= minX + 5 || p.X >= maxX - 5).Max(p => p.Y);
            if (maxY - spring < 50)
            {
                w.Outline = Rect(0, 0, w.Width, w.Height);
                w.Panes = w.Panes.Select(p => Simplify(p.Select(q => new Pt(q.X, Math.Min(q.Y, w.Height - minFrame))).ToList())).ToList();
                return w;
            }
            w.Arched = true;
            // the arch of the outline: the panes' upper hull, widened to the opening and lifted to its top
            var hull = UpperHull(all.Where(p => p.Y >= spring - 1).ToList());
            double cx = (minX + maxX) / 2, sx = maxX > minX ? w.Width / (maxX - minX) : 1;
            double sy = maxY > spring ? (w.Height - spring) / (maxY - spring) : 1;
            var arch = hull.Select(p => new Pt(Math.Clamp(w.Width / 2 + (p.X - cx) * sx, 0, w.Width), spring + (p.Y - spring) * sy)).ToList();
            w.Outline = new List<Pt> { new Pt(0, 0), new Pt(w.Width, 0), new Pt(w.Width, spring) };
            w.Outline.AddRange(Enumerable.Reverse(arch).Where(p => p.X < w.Width - 1 && p.X > 1));
            w.Outline.Add(new Pt(0, spring));
            return w;
        }

        public static List<Pt> Rect(double x0, double y0, double x1, double y1) =>
            new List<Pt> { new Pt(x0, y0), new Pt(x1, y0), new Pt(x1, y1), new Pt(x0, y1) };

        private static bool Inside(List<Pt> inner, List<Pt> outer)
        {
            if (Math.Abs(GeoMath.SignedArea(inner)) >= Math.Abs(GeoMath.SignedArea(outer))) return false;
            return inner.All(p => PointIn(p, outer, 1));
        }

        private static bool PointIn(Pt p, List<Pt> poly, double tol)
        {
            GeoMath.BoundingBox(poly, out var mn, out var mx);
            if (p.X < mn.X - tol || p.X > mx.X + tol || p.Y < mn.Y - tol || p.Y > mx.Y + tol) return false;
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i]; var b = poly[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            // on the edge counts as inside
            return inside || poly.Select((a, i) => (a, b: poly[(i + 1) % poly.Count])).Any(e => SurveyDetail.PtSeg(p, new Seg(e.a, e.b, 0)) <= tol);
        }

        /// <summary>Drops points on a straight run (within 1 mm).</summary>
        private static List<Pt> Simplify(List<Pt> ring)
        {
            var r = new List<Pt>(ring);
            bool changed = true;
            while (changed && r.Count > 3)
            {
                changed = false;
                for (int i = 0; i < r.Count && r.Count > 3; i++)
                {
                    var a = r[(i + r.Count - 1) % r.Count]; var b = r[i]; var c = r[(i + 1) % r.Count];
                    if (b.Dist2D(a) < 1 || SurveyDetail.PtSeg(b, new Seg(a, c, 0)) < 1) { r.RemoveAt(i); changed = true; i--; }
                }
            }
            return r;
        }

        private static List<Pt> Ccw(List<Pt> ring) => GeoMath.SignedArea(ring) < 0 ? Enumerable.Reverse(ring).ToList() : ring;

        /// <summary>Upper convex hull, left to right.</summary>
        private static List<Pt> UpperHull(List<Pt> pts)
        {
            var s = pts.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
            var h = new List<Pt>();
            foreach (var p in s)
            {
                while (h.Count >= 2 && Cross(h[h.Count - 2], h[h.Count - 1], p) >= 0) h.RemoveAt(h.Count - 1);
                h.Add(p);
            }
            return h;
        }

        private static double Cross(Pt o, Pt a, Pt b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        private static List<double> Cluster(IEnumerable<double> vs, double tol)
        {
            var c = new List<double>();
            foreach (var v in vs.OrderBy(v => v)) if (c.Count == 0 || v - c[c.Count - 1] > tol) c.Add(v);
            return c;
        }
    }
}
