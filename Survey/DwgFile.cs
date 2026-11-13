// Revit-free. Reads a DWG file directly (ACadSharp, MIT, the same reader Survey to Revit uses), for what Revit's
// CAD link cannot give: TEXT and MTEXT (spot heights, "S 0.94" / "H 3.27" window labels, room names), block
// attributes, and drawings that are not linked at all (elevations, sections).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Types.Units;

namespace BarrGazetas.RevitMcp.Survey
{
    public class DwgText
    {
        public Pt At;
        public string Value;
        public string Layer;
        public double HeightMm;
        /// <summary>Set for a block attribute: the attribute tag.</summary>
        public string Tag;
    }

    public class DwgLine
    {
        public Pt A, B;
        public string Layer;
    }

    /// <summary>
    /// 1.21.0: a true curve in the drawing (ARC, CIRCLE, a bulged polyline piece), kept as a curve so it can be built as
    /// one: centre, radius, start angle and sweep (radians, anticlockwise, sweep 2pi for a circle). Its chords are still in
    /// Lines for everything that reads straight lines.
    /// </summary>
    public class DwgArc
    {
        public Pt C;
        public double R, Start, Sweep;
        public string Layer;
        public Pt PointAt(double t) => new Pt(C.X + R * Math.Cos(Start + Sweep * t), C.Y + R * Math.Sin(Start + Sweep * t), C.Z);
        public double Length => R * Sweep;
    }

    /// <summary>A POINT, or where a block is inserted (survey spot-height crosses are usually one or the other).</summary>
    public class DwgMarker
    {
        public Pt At;
        public string Layer;
        /// <summary>The block's name; null for a POINT.</summary>
        public string Block;
    }

    public class DwgContent
    {
        public string Path;
        public double MmPerUnit;
        public string UnitsNote;
        public List<DwgText> Texts = new List<DwgText>();
        public List<DwgLine> Lines = new List<DwgLine>();
        public List<DwgMarker> Markers = new List<DwgMarker>();
        public List<DwgArc> Arcs = new List<DwgArc>();
        public Dictionary<string, int> LayerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    public static class DwgFile
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, (DateTime Stamp, DwgContent Content)> Cache = new Dictionary<string, (DateTime, DwgContent)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Every text and straight line (polylines split into segments, arcs in short pieces, blocks exploded up to
        /// three deep) in model space, in millimetres in the drawing's own coordinates. The last few files read are
        /// kept, keyed by their modified time.
        /// </summary>
        public static DwgContent Read(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("There is no file " + path);
            var stamp = File.GetLastWriteTimeUtc(path);
            lock (Gate)
                if (Cache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Content;

            CadDocument doc;
            if (path.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
                using (var r = new ACadSharp.IO.DxfReader(path)) doc = r.Read();
            else doc = ACadSharp.IO.DwgReader.Read(path, (_, __) => { });

            var raw = new DwgContent { Path = path };
            var lines = new List<(string layer, double x1, double y1, double z1, double x2, double y2, double z2)>();
            var texts = new List<(double x, double y, double z, string value, string layer, double h, string tag)>();
            var markers = new List<(double x, double y, double z, string layer, string block)>();

            var arcs = new List<(string layer, double cx, double cy, double cz, double r, double start, double sweep)>();
            void AddArc(string layer, double cx, double cy, double cz, double r, double start, double sweep)
            {
                if (!(r > 0) || !(sweep > 0)) return;
                arcs.Add((layer, cx, cy, cz, r, start, sweep));
                int pieces = Math.Max(2, Math.Min(96, (int)Math.Ceiling(sweep / (Math.PI / 24))));
                for (int i = 0; i < pieces; i++)
                {
                    double t0 = start + sweep * i / pieces, t1 = start + sweep * (i + 1) / pieces;
                    lines.Add((layer, cx + r * Math.Cos(t0), cy + r * Math.Sin(t0), cz, cx + r * Math.Cos(t1), cy + r * Math.Sin(t1), cz));
                }
            }
            // a polyline piece: straight, or (bulge = tan(sweep/4), positive anticlockwise) an arc from a to b
            void Piece(string layer, double ax, double ay, double bx, double by, double z, double bulge)
            {
                if (ax == bx && ay == by) return;
                if (Math.Abs(bulge) < 1e-9) { lines.Add((layer, ax, ay, z, bx, by, z)); return; }
                var g = BulgeArc(ax, ay, bx, by, bulge);
                AddArc(layer, g.cx, g.cy, z, g.r, g.start, g.sweep);
            }
            void Polygon(string layer, IEnumerable<CSMath.XYZ> pts)
            {
                var p = pts.ToList();
                for (int i = 0; i + 1 < p.Count; i++)
                    if (p[i].X != p[i + 1].X || p[i].Y != p[i + 1].Y) lines.Add((layer, p[i].X, p[i].Y, p[i].Z, p[i + 1].X, p[i + 1].Y, p[i + 1].Z));
            }

            void Count(string layer) { raw.LayerCounts.TryGetValue(layer, out int n); raw.LayerCounts[layer] = n + 1; }

            void Visit(Entity e, string inherited, int depth)
            {
                string layer = e.Layer?.Name ?? "";
                if (layer == "0" && inherited != null) layer = inherited;
                Count(layer);
                switch (e)
                {
                    case Line l:
                        lines.Add((layer, l.StartPoint.X, l.StartPoint.Y, l.StartPoint.Z, l.EndPoint.X, l.EndPoint.Y, l.EndPoint.Z));
                        break;
                    case LwPolyline pl:
                    {
                        var v = pl.Vertices;
                        int n = pl.IsClosed ? v.Count : v.Count - 1;
                        for (int i = 0; i < n; i++)
                        {
                            var a = v[i].Location; var b = v[(i + 1) % v.Count].Location;
                            Piece(layer, a.X, a.Y, b.X, b.Y, pl.Elevation, v[i].Bulge);
                        }
                        break;
                    }
                    case Polyline2D p2:
                    {
                        var v = p2.Vertices.ToList();
                        int n = p2.IsClosed ? v.Count : v.Count - 1;
                        for (int i = 0; i < n; i++)
                        {
                            var a = v[i].Location; var b = v[(i + 1) % v.Count].Location;
                            Piece(layer, a.X, a.Y, b.X, b.Y, p2.Elevation, v[i].Bulge);
                        }
                        break;
                    }
                    case Polyline3D p3:
                    {
                        var v = p3.Vertices.ToList();
                        int n = p3.IsClosed ? v.Count : v.Count - 1;
                        for (int i = 0; i < n; i++)
                        {
                            var a = v[i].Location; var b = v[(i + 1) % v.Count].Location;
                            if (a.X != b.X || a.Y != b.Y || a.Z != b.Z) lines.Add((layer, a.X, a.Y, a.Z, b.X, b.Y, b.Z));
                        }
                        break;
                    }
                    case Arc arc:
                    {
                        double sweep = arc.EndAngle - arc.StartAngle;
                        if (sweep <= 0) sweep += 2 * Math.PI;
                        AddArc(layer, arc.Center.X, arc.Center.Y, arc.Center.Z, arc.Radius, arc.StartAngle, sweep);
                        break;
                    }
                    case Circle c:
                        AddArc(layer, c.Center.X, c.Center.Y, c.Center.Z, c.Radius, 0, 2 * Math.PI);
                        break;
                    case Ellipse el:
                        Polygon(layer, el.PolygonalVertexes(64));
                        break;
                    case Spline sp:
                        if (sp.TryPolygonalVertexes(64, out var spv)) Polygon(layer, spv);
                        break;
                    case TextEntity t:
                        texts.Add((t.InsertPoint.X, t.InsertPoint.Y, t.InsertPoint.Z, t.Value ?? "", layer, t.Height, null));
                        break;
                    case MText mt:
                        texts.Add((mt.InsertPoint.X, mt.InsertPoint.Y, mt.InsertPoint.Z, mt.PlainText ?? "", layer, mt.Height, null));
                        break;
                    case Point pt:
                        markers.Add((pt.Location.X, pt.Location.Y, pt.Location.Z, layer, null));
                        break;
                    case Insert ins:
                        markers.Add((ins.InsertPoint.X, ins.InsertPoint.Y, ins.InsertPoint.Z, layer, ins.Block?.Name ?? ""));
                        foreach (var att in ins.Attributes)
                            if (!string.IsNullOrWhiteSpace(att.Value))
                                texts.Add((att.InsertPoint.X, att.InsertPoint.Y, att.InsertPoint.Z, att.Value, att.Layer?.Name ?? layer, att.Height, att.Tag));
                        if (depth < 3)
                        {
                            try { foreach (var sub in ins.Explode()) if (!(sub is AttributeDefinition)) Visit(sub, layer, depth + 1); }
                            catch { /* an unusual block: its lines are left out */ }
                        }
                        break;
                }
            }
            foreach (var e in doc.Entities) Visit(e, null, 0);

            var (mmPerUnit, note) = Units(doc.Header.InsUnits, lines.Select(l => (l.x1, l.y1, l.x2, l.y2)).ToList());
            raw.MmPerUnit = mmPerUnit;
            raw.UnitsNote = note;
            double k = mmPerUnit;
            raw.Lines = lines.Select(l => new DwgLine { Layer = l.layer, A = new Pt(l.x1 * k, l.y1 * k, l.z1 * k), B = new Pt(l.x2 * k, l.y2 * k, l.z2 * k) }).ToList();
            raw.Texts = texts.Select(t => new DwgText { At = new Pt(t.x * k, t.y * k, t.z * k), Value = Clean(t.value), Layer = t.layer, HeightMm = Math.Round(t.h * k, 1), Tag = t.tag })
                .Where(t => t.Value.Length > 0).ToList();
            raw.Arcs = arcs.Select(a => new DwgArc { Layer = a.layer, C = new Pt(a.cx * k, a.cy * k, a.cz * k), R = a.r * k, Start = a.start, Sweep = a.sweep }).ToList();
            raw.Markers = markers.Select(m => new DwgMarker { At = new Pt(m.x * k, m.y * k, m.z * k), Layer = m.layer, Block = m.block }).ToList();

            lock (Gate)
            {
                if (Cache.Count >= 6) Cache.Remove(Cache.OrderBy(c => c.Value.Stamp).First().Key);
                Cache[path] = (stamp, raw);
            }
            return raw;
        }

        /// <summary>The arc of a bulged polyline piece from a to b: centre, radius, start angle, sweep (anticlockwise, positive).</summary>
        public static (double cx, double cy, double r, double start, double sweep) BulgeArc(double ax, double ay, double bx, double by, double bulge)
        {
            double sweep = 4 * Math.Atan(Math.Abs(bulge));
            double dx = bx - ax, dy = by - ay, chord = Math.Sqrt(dx * dx + dy * dy);
            double r = chord / (2 * Math.Sin(sweep / 2));
            // the centre sits off the chord's middle, to the left for an anticlockwise piece
            double h = r * Math.Cos(sweep / 2), mx = (ax + bx) / 2, my = (ay + by) / 2, lx = -dy / chord, ly = dx / chord;
            double side = bulge > 0 ? 1 : -1;
            double cx = mx + side * lx * h, cy = my + side * ly * h;
            // anticlockwise from a (bulge > 0) or from b (bulge < 0)
            var (fx, fy) = bulge > 0 ? (ax, ay) : (bx, by);
            return (cx, cy, r, Math.Atan2(fy - cy, fx - cx), sweep);
        }

        private static string Clean(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim();

        /// <summary>
        /// Drawing units from the file header; when the file says "unitless" (common in surveys) they are inferred
        /// from the drawing's size: under 2000 units across is metres, otherwise millimetres.
        /// </summary>
        public static (double mmPerUnit, string note) Units(UnitsType header, IReadOnlyList<(double x1, double y1, double x2, double y2)> lines)
        {
            switch (header)
            {
                case UnitsType.Millimeters: return (1, "millimetres (from file)");
                case UnitsType.Centimeters: return (10, "centimetres (from file)");
                case UnitsType.Meters: return (1000, "metres (from file)");
                case UnitsType.Inches: return (25.4, "inches (from file)");
                case UnitsType.Feet: return (304.8, "feet (from file)");
            }
            if (lines.Count == 0) return (1000, "unknown: no lines, assumed metres");
            // the middle 90% of the drawing, so a stray far-off entity does not decide the units
            var xs = lines.SelectMany(l => new[] { l.x1, l.x2 }).OrderBy(v => v).ToList();
            var ys = lines.SelectMany(l => new[] { l.y1, l.y2 }).OrderBy(v => v).ToList();
            double P(List<double> v, double q) => v[(int)Math.Clamp(q * (v.Count - 1), 0, v.Count - 1)];
            double span = Math.Max(P(xs, 0.95) - P(xs, 0.05), P(ys, 0.95) - P(ys, 0.05));
            return span < 2000
                ? (1000, $"metres (file says unitless; the drawing spans {span:0.#} units)")
                : (1, $"millimetres (file says unitless; the drawing spans {span:0} units)");
        }

        /// <summary>Glob match (* and ?) of a layer name, case-insensitive; null or empty patterns match everything.</summary>
        public static bool LayerMatches(string layer, IList<string> globs) =>
            globs == null || globs.Count == 0 || globs.Any(g => LayerRuleSet.Glob(g).IsMatch(layer ?? "") || LayerRuleSet.Glob(g).IsMatch(LayerRuleSet.StripXref(layer ?? "")));
    }
}
