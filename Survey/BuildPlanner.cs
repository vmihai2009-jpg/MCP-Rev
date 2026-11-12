// Ported from the mcp-servers-for-revit survey pipeline (survey-pipeline.patch, base 86cf705). Revit-free.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BarrGazetas.RevitMcp.Survey
{
    public class PlanOp
    {
        /// <summary>Deterministic id derived from rule + geometry. Written to the Revit element's Comments for traceability and QA.</summary>
        public string Id { get; set; }
        public string Action { get; set; }
        public string Rule { get; set; }
        public string Layer { get; set; }
        public List<int> SourceIndexes { get; set; } = new List<int>();
        public Pt Start { get; set; }
        public Pt End { get; set; }
        public List<Pt> Points { get; set; } = new List<Pt>();
        public double ThicknessMm { get; set; }
        public double HeightMm { get; set; }
        public double BaseOffsetMm { get; set; }
        public string TypeName { get; set; }
        public string FamilyName { get; set; }
        public string Level { get; set; }
        public double RotationRad { get; set; }
    }

    public class BuildPlan
    {
        public List<PlanOp> Ops { get; } = new List<PlanOp>();
        public ValidationReport Report { get; set; } = new ValidationReport();
        public CleanStats Stats { get; set; } = new CleanStats();
        public List<string> Notes { get; } = new List<string>();
        public Dictionary<string, int> CountsByAction()
        {
            return Ops.GroupBy(o => o.Action).ToDictionary(g => g.Key, g => g.Count());
        }
    }

    /// <summary>
    /// Pure (Revit-free) pipeline: raw CAD entities + rules -> validated, cleaned, de-duplicated list of modelling operations.
    /// The Revit side only executes the plan, so all the intelligence can be unit-tested.
    /// </summary>
    public static class BuildPlanner
    {
        public static BuildPlan Plan(IList<CadEntity> raw, BuildConfig cfg)
        {
            cfg = cfg ?? new BuildConfig();
            var tol = cfg.Tolerances ?? new Tolerances();
            var rules = new LayerRuleSet(cfg.Rules);
            var plan = new BuildPlan();

            var cleaned = GeometryCleaner.CleanAll(raw, tol, plan.Stats);
            plan.Report = CadValidator.ValidateRaw(raw, plan.Stats, rules, cfg);
            if (plan.Report.HasErrors && raw.Count == 0) return plan;

            // group entities per rule (rules are processed in a fixed order => deterministic output)
            var byRule = new Dictionary<LayerRule, List<CadEntity>>();
            foreach (var e in cleaned)
            {
                var r = rules.Resolve(e.Layer);
                if (r == null || string.Equals(r.Action, Actions.Ignore, StringComparison.OrdinalIgnoreCase)) continue;
                List<CadEntity> l; if (!byRule.TryGetValue(r, out l)) { l = new List<CadEntity>(); byRule[r] = l; }
                l.Add(e);
            }

            var ids = new HashSet<string>();
            foreach (var rule in rules.Rules)
            {
                List<CadEntity> ents;
                if (!byRule.TryGetValue(rule, out ents)) continue;
                var ops = new List<PlanOp>();
                switch ((rule.Action ?? "").ToLowerInvariant())
                {
                    case Actions.WallCentreline: PlanWallsCentreline(rule, cfg, ents, ops); break;
                    case Actions.WallDoubleLine: PlanWallsDoubleLine(rule, cfg, ents, ops, plan); break;
                    case Actions.Floor: PlanFloors(rule, cfg, tol, ents, ops, plan); break;
                    case Actions.TopoPoints:
                    case Actions.TopoContours: PlanTopo(rule, cfg, ents, ops, plan); break;
                    case Actions.Grid: PlanGrids(rule, cfg, ents, ops); break;
                    case Actions.PointFamily: PlanPoints(rule, cfg, ents, ops, plan); break;
                }
                foreach (var op in ops) if (ids.Add(op.Id)) plan.Ops.Add(op);
            }

            foreach (var lvl in cfg.Levels ?? new List<LevelSpec>())
                if (string.IsNullOrWhiteSpace(lvl.Name)) plan.Report.Add(Severity.Error, "CONFIG", null, 1, "A level in 'levels' has no name.");
            return plan;
        }

        // ---------------------------------------------------------------- walls
        private static PlanOp NewOp(LayerRule rule, BuildConfig cfg, string layer, string geomKey)
        {
            return new PlanOp
            {
                Action = rule.Action.ToLowerInvariant(), Rule = rule.Name, Layer = layer,
                Id = (rule.Action.ToLowerInvariant()) + ":" + Slug(rule.Name) + ":" + Fnv(geomKey),
                HeightMm = rule.HeightMm, BaseOffsetMm = rule.BaseOffsetMm, ThicknessMm = rule.ThicknessMm,
                TypeName = rule.TypeName, FamilyName = rule.FamilyName,
                Level = string.IsNullOrWhiteSpace(rule.Level) ? cfg.DefaultLevel : rule.Level
            };
        }

        private static IEnumerable<Seg> SegmentsOf(CadEntity e)
        {
            int n = e.Points.Count; int count = e.Closed ? n : n - 1;
            for (int i = 0; i < count; i++) yield return new Seg(e.Points[i], e.Points[(i + 1) % n], e.Index);
        }

        private static void PlanWallsCentreline(LayerRule rule, BuildConfig cfg, List<CadEntity> ents, List<PlanOp> ops)
        {
            foreach (var e in ents.Where(x => x.Kind == CadKind.Line || x.Kind == CadKind.Polyline))
                foreach (var s in SegmentsOf(e))
                {
                    if (s.Length2D < 1) continue;
                    var op = NewOp(rule, cfg, e.Layer, SegKey(s.A, s.B));
                    op.Start = s.A; op.End = s.B; op.SourceIndexes.Add(e.Index);
                    ops.Add(op);
                }
        }

        private static void PlanWallsDoubleLine(LayerRule rule, BuildConfig cfg, List<CadEntity> ents, List<PlanOp> ops, BuildPlan plan)
        {
            var segs = ents.Where(x => x.Kind == CadKind.Line || x.Kind == CadKind.Polyline).SelectMany(SegmentsOf).ToList();
            var found = WallDetector.FromParallelPairs(segs, rule.MinThicknessMm, rule.MaxThicknessMm, rule.MinOverlapMm, rule.AngleTolDeg);
            var paired = new HashSet<int>();
            foreach (var c in found)
            {
                paired.Add(c.SegA); paired.Add(c.SegB);
                double t = Math.Round(c.ThicknessMm); // wall types are per whole mm
                var op = NewOp(rule, cfg, ents[0].Layer, SegKey(c.Start, c.End));
                op.Start = c.Start; op.End = c.End; op.ThicknessMm = t;
                op.SourceIndexes.Add(segs[c.SegA].Source); if (segs[c.SegB].Source != segs[c.SegA].Source) op.SourceIndexes.Add(segs[c.SegB].Source);
                ops.Add(op);
            }
            int unpaired = segs.Count - paired.Count;
            if (unpaired > 0)
                plan.Report.Add(Severity.Info, "UNPAIRED_WALL_LINES", rule.Name, unpaired,
                    "Wall-layer segments that could not be paired into a wall (door/window jambs, hatch, short stubs, or thickness outside min/max).",
                    "Widen minThicknessMm/maxThicknessMm, lower minOverlapMm, or review the layer in CAD.");
            if (found.Count == 0 && segs.Count > 0)
                plan.Report.Add(Severity.Warning, "NO_WALLS_DETECTED", rule.Name, 0, "No parallel line pairs were found on the wall layer(s).", "If walls are single lines use action 'wall_centerline'.");
        }

        // ---------------------------------------------------------------- floors
        private static void PlanFloors(LayerRule rule, BuildConfig cfg, Tolerances tol, List<CadEntity> ents, List<PlanOp> ops, BuildPlan plan)
        {
            var rings = new List<KeyValuePair<List<Pt>, CadEntity>>();
            foreach (var e in ents.Where(x => x.Kind == CadKind.Polyline && x.Closed)) rings.Add(new KeyValuePair<List<Pt>, CadEntity>(e.Points, e));

            var loose = ents.Where(x => (x.Kind == CadKind.Line || x.Kind == CadKind.Polyline) && !x.Closed).ToList();
            if (loose.Count > 0)
            {
                if (rule.JoinLooseLines)
                {
                    var lr = LoopBuilder.Build(loose.SelectMany(SegmentsOf).ToList(), Math.Max(tol.SnapMm, tol.CloseGapMm));
                    foreach (var loop in lr.Loops) rings.Add(new KeyValuePair<List<Pt>, CadEntity>(loop, loose[0]));
                    if (lr.DanglingSegments > 0) plan.Report.Add(Severity.Warning, "DANGLING_LINES", rule.Name, lr.DanglingSegments, "Line segments on a floor layer that do not close a loop were ignored.", "Close the outline in CAD or raise closeGapMm.");
                    if (lr.ComplexComponents > 0) plan.Report.Add(Severity.Warning, "COMPLEX_JUNCTIONS", rule.Name, lr.ComplexComponents, "Line work with T/X junctions cannot be auto-joined into a single outline.", "Draw the outline as one closed polyline (e.g. BOUNDARY/PEDIT Join).");
                }
                else plan.Report.Add(Severity.Warning, "OPEN_RING_FOR_FLOOR", rule.Name, loose.Count, "Open polylines on a floor layer were skipped (joinLooseLines=false).");
            }

            foreach (var kv in rings)
            {
                var ring = kv.Key;
                if (GeoMath.RingSelfIntersects(ring)) { plan.Report.Add(Severity.Warning, "SELF_INTERSECTING", rule.Name, 1, "A floor outline crosses itself and was skipped (bow-tie)."); continue; }
                double area = GeoMath.SignedArea(ring);
                if (Math.Abs(area) < rule.MinAreaMm2) { plan.Report.Add(Severity.Info, "TINY_AREA", rule.Name, 1, "A closed outline smaller than minAreaMm2 was skipped."); continue; }
                var pts = new List<Pt>(ring);
                if (area < 0) pts.Reverse(); // normalise to counter-clockwise
                // rotate so the smallest (x,y) vertex is first => stable id
                int start = 0; for (int i = 1; i < pts.Count; i++) if (pts[i].X < pts[start].X || (pts[i].X == pts[start].X && pts[i].Y < pts[start].Y)) start = i;
                pts = pts.Skip(start).Concat(pts.Take(start)).ToList();
                var op = NewOp(rule, cfg, kv.Value.Layer, string.Join("|", pts.Select(p => GeoMath.Key(p, 1))));
                op.Points = pts; op.SourceIndexes.Add(kv.Value.Index);
                ops.Add(op);
            }
        }

        // ---------------------------------------------------------------- topography
        private static void PlanTopo(LayerRule rule, BuildConfig cfg, List<CadEntity> ents, List<PlanOp> ops, BuildPlan plan)
        {
            var pts = new List<Pt>();
            bool contours = string.Equals(rule.Action, Actions.TopoContours, StringComparison.OrdinalIgnoreCase);
            foreach (var e in ents)
            {
                if (e.Kind == CadKind.Point || e.Kind == CadKind.Block) pts.AddRange(e.Points.Take(1));
                else pts.AddRange(contours ? TopoSampler.Densify(e.Points, e.Closed, rule.ContourSpacingMm) : e.Points);
            }
            if (rule.ZOffsetMm.HasValue) pts = pts.Select(p => new Pt(p.X, p.Y, p.Z + rule.ZOffsetMm.Value)).ToList();

            // outliers must be judged on the RAW points: merging/averaging duplicates would dilute a typo into its neighbour
            var outliers = TopoSampler.ZOutliers(pts, 8);
            int dup;
            pts = TopoSampler.UniqueXY(pts, 1.0, out dup);
            if (dup > 0) plan.Report.Add(Severity.Info, "TOPO_DUPLICATE_XY", rule.Name, dup, "Points sharing an XY position were merged (average Z); Revit rejects topography with duplicate XY.");
            int before = pts.Count;
            DecimateMode mode; if (!Enum.TryParse(rule.DecimateMode ?? "average", true, out mode)) mode = DecimateMode.Average;
            double cell = rule.TopoCellMm;
            pts = TopoSampler.Decimate(pts, cell, mode);
            double cellUsed; pts = TopoSampler.ThinToMax(pts, rule.MaxPoints, mode, out cellUsed);
            if (cellUsed > 0) plan.Report.Add(Severity.Info, "TOPO_THINNED", rule.Name, before - pts.Count, string.Format(CultureInfo.InvariantCulture, "Topography points thinned from {0} to {1} (cell {2:0} mm) to stay under maxPoints={3}.", before, pts.Count, cellUsed, rule.MaxPoints));

            if (pts.Count < 3) { plan.Report.Add(Severity.Error, "TOPO_TOO_FEW_POINTS", rule.Name, pts.Count, "Fewer than 3 usable points for a topography surface."); return; }
            if (TopoSampler.IsCollinear2D(pts, 1.0)) { plan.Report.Add(Severity.Error, "TOPO_COLLINEAR", rule.Name, pts.Count, "All topography points lie on one line; a surface cannot be built."); return; }
            double zmin = pts.Min(p => p.Z), zmax = pts.Max(p => p.Z);
            if (zmax - zmin < 1.0) plan.Report.Add(Severity.Warning, "TOPO_FLAT", rule.Name, pts.Count, "All topography points have the same Z. The DWG may be 2D (elevations in text/attributes).", "Use a 3D DWG, or set zOffsetMm for a constant level.");
            if (outliers.Count > 0) plan.Report.Add(Severity.Warning, "TOPO_Z_OUTLIERS", rule.Name, outliers.Count, "Points with extreme Z (likely survey typos, e.g. 0 or 1000x) – they would spike the surface.", "Review them in CAD or filter before building.");

            var op = NewOp(rule, cfg, ents[0].Layer, "topo|" + pts.Count + "|" + GeoMath.Key(pts[0], 1) + "|" + GeoMath.Key(pts[pts.Count - 1], 1) + "|" + pts.Sum(p => p.Z).ToString("0.###", CultureInfo.InvariantCulture));
            op.Points = pts; op.SourceIndexes.AddRange(ents.Select(e => e.Index).Take(1000));
            ops.Add(op);
        }

        // ---------------------------------------------------------------- grids & points
        private static void PlanGrids(LayerRule rule, BuildConfig cfg, List<CadEntity> ents, List<PlanOp> ops)
        {
            foreach (var e in ents.Where(x => x.Kind == CadKind.Line || x.Kind == CadKind.Polyline))
                foreach (var s in SegmentsOf(e))
                {
                    if (s.Length2D < 500) continue; // a grid line shorter than 0.5 m is noise
                    var op = NewOp(rule, cfg, e.Layer, SegKey(s.A, s.B));
                    op.Start = s.A; op.End = s.B; op.SourceIndexes.Add(e.Index);
                    ops.Add(op);
                }
        }

        private static void PlanPoints(LayerRule rule, BuildConfig cfg, List<CadEntity> ents, List<PlanOp> ops, BuildPlan plan)
        {
            int unmapped = 0;
            foreach (var e in ents.Where(x => x.Kind == CadKind.Point || x.Kind == CadKind.Block))
            {
                string family = rule.FamilyName, type = rule.TypeName;
                if (e.Kind == CadKind.Block && rule.FamilyMap != null && rule.FamilyMap.Count > 0)
                {
                    var m = rule.FamilyMap.FirstOrDefault(x => LayerRuleSet.Glob(x.Block ?? "").IsMatch(e.BlockName ?? ""));
                    if (m != null) { family = m.FamilyName; type = m.TypeName; } else if (string.IsNullOrEmpty(family)) { unmapped++; continue; }
                }
                if (string.IsNullOrEmpty(family)) { unmapped++; continue; }
                var p = e.Points[0];
                var op = NewOp(rule, cfg, e.Layer, GeoMath.Key(p, 1) + "|" + family + "|" + type + "|" + (e.BlockName ?? ""));
                op.Start = p; op.End = p; op.FamilyName = family; op.TypeName = type; op.RotationRad = e.RotationRad; op.SourceIndexes.Add(e.Index);
                ops.Add(op);
            }
            if (unmapped > 0) plan.Report.Add(Severity.Warning, "NO_FAMILY_FOR_POINTS", rule.Name, unmapped, "Points/blocks matched the rule but no familyName (or familyMap entry) is configured.", "Set familyName/typeName on the rule or add a familyMap entry per block name.");
        }

        // ---------------------------------------------------------------- helpers
        private static string SegKey(Pt a, Pt b)
        {
            string ka = GeoMath.Key(a, 1), kb = GeoMath.Key(b, 1);
            return string.CompareOrdinal(ka, kb) <= 0 ? ka + ">" + kb : kb + ">" + ka;
        }

        private static string Slug(string s) { return new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant(); }

        /// <summary>FNV-1a 64 bit => short stable hex id (net48 compatible, culture independent).</summary>
        public static string Fnv(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
            return h.ToString("x16");
        }
    }
}
