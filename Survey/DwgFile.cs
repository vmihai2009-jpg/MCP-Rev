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

    public class DwgContent
    {
        public string Path;
        public double MmPerUnit;
        public string UnitsNote;
        public List<DwgText> Texts = new List<DwgText>();
        public List<DwgLine> Lines = new List<DwgLine>();
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
                            if (a.X != b.X || a.Y != b.Y) lines.Add((layer, a.X, a.Y, pl.Elevation, b.X, b.Y, pl.Elevation));
                        }
                        break;
                    }
                    case Arc arc:
                    {
                        double sweep = arc.EndAngle - arc.StartAngle;
                        if (sweep <= 0) sweep += 2 * Math.PI;
                        int pieces = Math.Max(2, Math.Min(48, (int)Math.Ceiling(sweep / (Math.PI / 24))));
                        for (int i = 0; i < pieces; i++)
                        {
                            double t0 = arc.StartAngle + sweep * i / pieces, t1 = arc.StartAngle + sweep * (i + 1) / pieces;
                            lines.Add((layer, arc.Center.X + arc.Radius * Math.Cos(t0), arc.Center.Y + arc.Radius * Math.Sin(t0), arc.Center.Z,
                                              arc.Center.X + arc.Radius * Math.Cos(t1), arc.Center.Y + arc.Radius * Math.Sin(t1), arc.Center.Z));
                        }
                        break;
                    }
                    case TextEntity t:
                        texts.Add((t.InsertPoint.X, t.InsertPoint.Y, t.InsertPoint.Z, t.Value ?? "", layer, t.Height, null));
                        break;
                    case MText mt:
                        texts.Add((mt.InsertPoint.X, mt.InsertPoint.Y, mt.InsertPoint.Z, mt.PlainText ?? "", layer, mt.Height, null));
                        break;
                    case Insert ins:
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

            lock (Gate)
            {
                if (Cache.Count >= 6) Cache.Remove(Cache.OrderBy(c => c.Value.Stamp).First().Key);
                Cache[path] = (stamp, raw);
            }
            return raw;
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
