// Ported from the mcp-servers-for-revit survey pipeline (survey-pipeline.patch, base 86cf705). Revit-free.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public enum Severity { Info, Warning, Error }

    public class ValidationIssue
    {
        public string Severity { get; set; }
        public string Code { get; set; }
        public string Layer { get; set; }
        public int Count { get; set; }
        public string Message { get; set; }
        public string Fix { get; set; }
    }

    public class ValidationReport
    {
        public List<ValidationIssue> Issues { get; } = new List<ValidationIssue>();
        public bool HasErrors { get { return Issues.Any(i => i.Severity == "error"); } }

        public void Add(Severity sev, string code, string layer, int count, string message, string fix = null)
        {
            Issues.Add(new ValidationIssue { Severity = sev.ToString().ToLowerInvariant(), Code = code, Layer = layer, Count = count, Message = message, Fix = fix });
        }
    }

    public static class CadValidator
    {
        // Autodesk guidance: keep geometry within ~3 miles of the internal origin; Revit hard-warns beyond 20 miles (33 km).
        public const double FarFromOriginMm = 4828032;   // 3 miles
        public const double TooLargeMm = 32186880;       // 20 miles

        /// <summary>Checks that can be made on the raw (un-cleaned) entities.</summary>
        public static ValidationReport ValidateRaw(IList<CadEntity> raw, CleanStats stats, LayerRuleSet rules, BuildConfig cfg)
        {
            var rep = new ValidationReport();
            foreach (var p in rules.ConfigProblems) rep.Add(Severity.Error, "CONFIG", null, 1, p);

            if (raw.Count == 0)
            {
                rep.Add(Severity.Error, "EMPTY", null, 0, "No CAD entities were read from the import/link.",
                    "Check the DWG is imported/linked in the active view's document and that its layers are visible; try 'dwg_extract' to inspect.");
                return rep;
            }

            Pt min, max; GeoMath.BoundingBox(raw.SelectMany(e => e.Points), out min, out max);
            double cx = (min.X + max.X) / 2, cy = (min.Y + max.Y) / 2;
            double extent = Math.Max(max.X - min.X, max.Y - min.Y);
            double fromOrigin = Math.Sqrt(cx * cx + cy * cy);
            if (extent > TooLargeMm)
                rep.Add(Severity.Error, "EXTENTS_TOO_LARGE", null, 1, string.Format("Geometry spans {0:0} km (Revit limit ~33 km).", extent / 1e6),
                    "In AutoCAD: PURGE, delete stray far-away entities (ZOOM EXTENTS shows them), then relink.");
            else if (fromOrigin > FarFromOriginMm)
                rep.Add(Severity.Warning, "FAR_FROM_ORIGIN", null, 1, string.Format("Geometry centre is {0:0.0} km from the internal origin; precision and graphics degrade beyond ~4.8 km (3 mi).", fromOrigin / 1e6),
                    "Move the drawing near 0,0 in CAD and use shared coordinates (survey_set_coordinates) instead of modelling at survey coordinates.");

            if (stats.NonFiniteRemoved > 0) rep.Add(Severity.Warning, "NON_FINITE", null, stats.NonFiniteRemoved, "Entities with NaN/infinite coordinates were dropped.");
            if (stats.TinySegmentsMerged > 0) rep.Add(Severity.Info, "TINY_SEGMENTS", null, stats.TinySegmentsMerged, "Vertices closer than the minimum segment length were merged.");
            if (stats.DuplicatesRemoved > 0) rep.Add(Severity.Warning, "DUPLICATES", null, stats.DuplicatesRemoved, "Duplicate entities (same layer, same geometry) were removed – typical of 'line drawn twice' CAD errors.");
            if (stats.AutoClosed > 0) rep.Add(Severity.Info, "AUTO_CLOSED", null, stats.AutoClosed, "Polylines with a small end gap were closed (gap <= closeGapMm).");
            if (stats.Dropped > 0) rep.Add(Severity.Info, "DEGENERATE", null, stats.Dropped, "Zero-length / degenerate entities were dropped.");

            if (cfg.ReportUnmappedLayers)
            {
                foreach (var g in raw.GroupBy(e => e.Layer ?? "").OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var rule = rules.Resolve(g.Key);
                    if (rule == null)
                        rep.Add(Severity.Info, "UNMAPPED_LAYER", g.Key, g.Count(), "Layer matches no rule and will not be modelled.", "Add a rule with this layer in 'match', or an explicit 'ignore' rule.");
                }
            }
            return rep;
        }
    }
}
