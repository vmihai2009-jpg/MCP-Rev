// Revit-free. 1.8.0: ground points for a Toposolid from a survey site plan: spot-height labels (paired with the
// cross or point they belong to), 3D points, and contours drawn at their height.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace BarrGazetas.RevitMcp.Survey
{
    public class TerrainOptions
    {
        /// <summary>Layers of the spot-height labels and their crosses/points. Empty: every layer (the label pattern decides).</summary>
        public List<string> SpotLayers = new List<string>();
        /// <summary>Layers of the contours. Empty: layers named like contours.</summary>
        public List<string> ContourLayers = new List<string>();
        /// <summary>Layers never used (building levels inside the house, drainage inverts...).</summary>
        public List<string> ExcludeLayers = new List<string>();
        /// <summary>Label prefixes taken as ground levels. A label with any other prefix (S, H, FFL, IL...) is not ground.</summary>
        public List<string> Prefixes = new List<string>(DefaultPrefixes);
        public static readonly string[] DefaultPrefixes = { "", "+", "SL", "GL", "EL", "RL", "LVL", "L", "CL", "TK", "BK" };
        public static readonly string[] DefaultContourLayers = { "*contour*", "*cont*", "*kontur*" };
        /// <summary>Labels are in metres (survey standard). False: millimetres.</summary>
        public bool LabelsInMetres = true;
        /// <summary>How far from its label a cross or point may be, mm (0: from the text height, at least 1000).</summary>
        public double PairRadiusMm;
        public bool UseContours = true;
        public bool Use3DPoints = true;
        /// <summary>Contour points no further apart than this, mm.</summary>
        public double ContourSpacingMm = 2000;
        public bool DropOutliers = true;
        public double OutlierFactor = 6;
        /// <summary>A point is a typo only this far off the median at least, mm.</summary>
        public double MinOutlierMm = 2000;
        public int MaxPoints = 5000;
        /// <summary>Only points inside this outline (drawing mm), when given.</summary>
        public List<Pt> Boundary;
        /// <summary>Plausible ground, mm above datum: labels outside are not ground (a 0.94 sill, a 1995 date).</summary>
        public double MinMm = -500_000, MaxMm = 3_000_000;
    }

    public class TerrainPoint
    {
        public Pt P;
        /// <summary>spot (label at its cross), spot_at_text (no cross found), point3d, contour.</summary>
        public string Source;
        public string Layer;
        public string Label;
    }

    public class TerrainResult
    {
        public List<Pt> Points = new List<Pt>();
        public List<TerrainPoint> Spots = new List<TerrainPoint>();
        public int SpotLabels, SpotsAtCross, SpotsAtText, Points3D, ContourSegments, FlatContourSegments, ContourPoints, Duplicates, OutsideBoundary;
        public double ThinnedToCellMm;
        public List<TerrainPoint> Outliers = new List<TerrainPoint>();
        public List<string> Warnings = new List<string>();
        public Dictionary<string, int> SpotLayersUsed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ContourLayersUsed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public double MinZ => Points.Count == 0 ? 0 : Points.Min(p => p.Z);
        public double MaxZ => Points.Count == 0 ? 0 : Points.Max(p => p.Z);
    }

    public static class Terrain
    {
        private static readonly Regex Label = new Regex(@"^\s*(?<pre>[A-Za-z]{0,4})\s*[:=]?\s*(?<sign>[+\-])?\s*(?<num>\d{1,5}(?:[.,]\d{1,3})?)\s*(?:m)?\s*$", RegexOptions.CultureInvariant);

        /// <summary>
        /// A ground level from a label: "12.345", "+12.35", "SL 12.34", "GL12.30", "CL 11.95". Null for anything else,
        /// including window labels ("S 0.94", "H 3.27"), floor levels ("FFL 27.05") and inverts ("IL 10.20"), unless
        /// their prefix is in the list. Metres need a decimal point (a bare "12" is a room number or a count).
        /// </summary>
        public static double? ParseLevel(string text, IList<string> prefixes, bool metres = true)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = Label.Match(text);
            if (!m.Success) return null;
            string pre = m.Groups["pre"].Value.ToUpperInvariant();
            string sign = m.Groups["sign"].Value;
            string num = m.Groups["num"].Value.Replace(',', '.');
            var allowed = prefixes ?? TerrainOptions.DefaultPrefixes;
            string key = pre.Length == 0 && sign == "+" ? "+" : pre;
            if (!allowed.Any(a => string.Equals(a ?? "", key, StringComparison.OrdinalIgnoreCase))
                && !(pre.Length == 0 && allowed.Any(a => (a ?? "") == ""))) return null;
            if (metres && num.IndexOf('.') < 0) return null;
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
            if (sign == "-") v = -v;
            return metres ? v * 1000 : v;
        }

        public static TerrainResult Collect(DwgContent d, TerrainOptions o)
        {
            o = o ?? new TerrainOptions();
            var r = new TerrainResult();
            bool Excluded(string layer) => o.ExcludeLayers.Count > 0 && DwgFile.LayerMatches(layer, o.ExcludeLayers);
            bool SpotLayer(string layer) => !Excluded(layer) && (o.SpotLayers.Count == 0 || DwgFile.LayerMatches(layer, o.SpotLayers));
            var contourLayers = o.ContourLayers.Count > 0 ? o.ContourLayers : TerrainOptions.DefaultContourLayers.ToList();

            // ---- spot heights: each label at the nearest cross or point, else at the label itself
            var markers = d.Markers.Where(m => SpotLayer(m.Layer)).ToList();
            var usedMarkers = new HashSet<int>();
            var all = new List<TerrainPoint>();
            foreach (var t in d.Texts)
            {
                if (!SpotLayer(t.Layer)) continue;
                var z = ParseLevel(t.Value, o.Prefixes, o.LabelsInMetres);
                if (z == null || z < o.MinMm || z > o.MaxMm) continue;
                r.SpotLabels++;
                double radius = o.PairRadiusMm > 0 ? o.PairRadiusMm : Math.Max(1000, t.HeightMm * 5);
                int best = -1; double bestD = double.MaxValue;
                for (int i = 0; i < markers.Count; i++)
                {
                    if (usedMarkers.Contains(i)) continue;
                    double dd = markers[i].At.Dist2D(t.At);
                    if (dd <= radius && dd < bestD) { bestD = dd; best = i; }
                }
                TerrainPoint tp;
                if (best >= 0)
                {
                    usedMarkers.Add(best);
                    tp = new TerrainPoint { P = new Pt(markers[best].At.X, markers[best].At.Y, z.Value), Source = "spot", Layer = t.Layer, Label = t.Value };
                    r.SpotsAtCross++;
                }
                else
                {
                    tp = new TerrainPoint { P = new Pt(t.At.X, t.At.Y, z.Value), Source = "spot_at_text", Layer = t.Layer, Label = t.Value };
                    r.SpotsAtText++;
                }
                all.Add(tp);
                r.SpotLayersUsed.TryGetValue(t.Layer, out int n); r.SpotLayersUsed[t.Layer] = n + 1;
            }
            if (o.Use3DPoints)
                for (int i = 0; i < markers.Count; i++)
                {
                    if (usedMarkers.Contains(i)) continue;
                    var m = markers[i];
                    if (Math.Abs(m.At.Z) < 1 || m.At.Z < o.MinMm || m.At.Z > o.MaxMm) continue; // at 0: a 2D cross, not a height
                    all.Add(new TerrainPoint { P = m.At, Source = "point3d", Layer = m.Layer });
                    r.Points3D++;
                }
            r.Spots = all.ToList();

            // ---- contours: lines on contour layers that carry their height
            if (o.UseContours)
                foreach (var l in d.Lines)
                {
                    if (Excluded(l.Layer) || !DwgFile.LayerMatches(l.Layer, contourLayers)) continue;
                    if (Math.Abs(l.A.Z) < 1 && Math.Abs(l.B.Z) < 1) { r.FlatContourSegments++; continue; }
                    if (l.A.Z < o.MinMm || l.A.Z > o.MaxMm) continue;
                    r.ContourSegments++;
                    r.ContourLayersUsed.TryGetValue(l.Layer, out int n); r.ContourLayersUsed[l.Layer] = n + 1;
                    var pts = TopoSampler.Densify(new List<Pt> { l.A, l.B }, false, o.ContourSpacingMm);
                    // a segment's end is the next one's start: the repeats go in UniqueXY below
                    foreach (var p in pts) all.Add(new TerrainPoint { P = p, Source = "contour", Layer = l.Layer });
                    r.ContourPoints += pts.Count;
                }
            if (r.FlatContourSegments > 0 && r.ContourSegments == 0)
                r.Warnings.Add($"{r.FlatContourSegments} contour segments are drawn at height 0, so they give no ground level (the surveyor drew them flat; only the spot heights are used).");

            // ---- inside the boundary
            if (o.Boundary != null && o.Boundary.Count >= 3)
            {
                int before = all.Count;
                all = all.Where(p => GeoMath.PointInPolygon(p.P, o.Boundary)).ToList();
                r.OutsideBoundary = before - all.Count;
            }

            // ---- gross typos ("1.234" for "11.234")
            if (o.DropOutliers)
            {
                // a typo is metres off; a real hump or a kerb on a site of nearly equal levels is not
                double median = all.Count == 0 ? 0 : all.Select(p => p.P.Z).OrderBy(z => z).ElementAt(all.Count / 2);
                var idx = new HashSet<int>(TopoSampler.ZOutliers(all.Select(p => p.P).ToList(), o.OutlierFactor).Where(i => Math.Abs(all[i].P.Z - median) > o.MinOutlierMm));
                if (idx.Count > 0 && idx.Count < all.Count / 4)
                {
                    r.Outliers = idx.Select(i => all[i]).ToList();
                    all = all.Where((_, i) => !idx.Contains(i)).ToList();
                }
                else if (idx.Count > 0) r.Warnings.Add($"{idx.Count} points look far off the rest but are too many to be typos: kept. The site may be steep, or the label layers mix two datums.");
            }

            var unique = TopoSampler.UniqueXY(all.Select(p => p.P), 10, out int dups);
            r.Duplicates = dups;
            var thin = TopoSampler.ThinToMax(unique, o.MaxPoints, DecimateMode.Average, out double cell);
            r.ThinnedToCellMm = Math.Round(cell);
            r.Points = thin;

            if (r.SpotsAtText > 0 && r.SpotsAtCross == 0 && r.SpotLabels > 0)
                r.Warnings.Add("No crosses or points were found next to the labels, so each height sits where its text is (usually 200-500 mm off). Give spot_layers that hold the crosses too, or a larger pair_radius_mm.");
            if (r.Points.Count < 3) r.Warnings.Add("Fewer than 3 ground points: no Toposolid can be made. Check the layers (dwg_read with what: text shows the labels and their layers).");
            else if (TopoSampler.IsCollinear2D(r.Points, 50)) r.Warnings.Add("The ground points lie on one line: Revit cannot make a surface from them.");
            if (r.Points.Count >= 3 && Math.Abs(r.MaxZ - r.MinZ) < 1) r.Warnings.Add("Every point is at the same height: a flat ground. If the survey has levels, they were not read.");
            return r;
        }
    }
}
