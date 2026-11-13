// 1.14.7: the mansard as the reference model builds it (one native footprint roof round the building, slope per edge,
// cut off at the kerb; it hips round corners and follows curves by itself), with the slopes and heights the survey's
// sections give (facade_profile writes them to mansard.json). Editable like any roof: sketch, slopes, cutoff, type.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        /// <summary>A closed outline without the small jogs: line-line corners whose distance from the line joining the
        /// neighbouring corners is under tol are dropped (repeatedly); arcs and their ends stay.</summary>
        internal static CurveLoop Simplified(CurveLoop loop, double tol)
        {
            var cs = loop.ToList();
            // as a ring of pieces: a line is (start, null), an arc is kept whole
            var pts = cs.Select(c => c.GetEndPoint(0)).ToList();
            var arcAt = cs.Select(c => c is Line ? null : c).ToList();
            bool changed = true;
            while (changed && pts.Count > 4)
            {
                changed = false;
                for (int i = 0; i < pts.Count && pts.Count > 4; i++)
                {
                    int p = (i - 1 + pts.Count) % pts.Count, n = (i + 1) % pts.Count;
                    if (arcAt[p] != null || arcAt[i] != null) continue; // corner i joins piece p (p->i) and piece i (i->n): both lines
                    var a = pts[p]; var b = pts[n]; var v = b - a; double len = v.GetLength();
                    double d = len < 1e-6 ? pts[i].DistanceTo(a) : (pts[i] - a).CrossProduct(v).GetLength() / len;
                    // also a corner that hardly turns (< 12°): two sloped edges nearly in line meet in a hip of almost no
                    // length, which Revit can't make
                    var u1 = (pts[i] - a); var u2 = (b - pts[i]);
                    double turn = u1.GetLength() < 1e-6 || u2.GetLength() < 1e-6 ? 0 : u1.Normalize().AngleTo(u2.Normalize()) * 180 / Math.PI;
                    // and a spike (the outline runs out and straight back: > 160°), which Revit refuses as doubling back
                    if ((d < tol || turn < 12 || turn > 160) && len > 1e-3) { pts.RemoveAt(i); arcAt.RemoveAt(i); changed = true; i--; }
                }
            }
            var outLoop = new CurveLoop();
            for (int i = 0; i < pts.Count; i++)
            {
                var arc = arcAt[i]; var end = pts[(i + 1) % pts.Count];
                if (arc != null) outLoop.Append(arc);
                else if (pts[i].DistanceTo(end) > 2 / Mm) outLoop.Append(Line.CreateBound(pts[i], end));
            }
            return outLoop;
        }

        /// <summary>Total solid volume of an element's geometry (0 when Revit could not make it).</summary>
        internal static double Volume(Element e)
        {
            double v = 0;
            void Walk(GeometryElement ge) { if (ge == null) return; foreach (var o in ge) { if (o is Solid s) v += s.Volume; else if (o is GeometryInstance gi) Walk(gi.GetInstanceGeometry()); } }
            try { Walk(e.get_Geometry(new Options())); } catch { }
            return v;
        }

        /// <summary>A loop whose curves were drawn as runs of short chords (a slab sketched from a chorded survey) with each run
        /// that stays on a circle put back as one true arc (CurveChains, as curve_walls does): the hand model's mansard has its
        /// curved corner as ONE arc edge, and a footprint roof slopes an arc edge as a cone.</summary>
        internal static CurveLoop ArcsFitted(CurveLoop loop, double tolMm, out int fitted)
        {
            fitted = 0;
            var cs = loop.ToList(); int n = cs.Count;
            var pieces = cs.Select(c => (a: new Pt(c.GetEndPoint(0).X * Mm, c.GetEndPoint(0).Y * Mm), b: new Pt(c.GetEndPoint(1).X * Mm, c.GetEndPoint(1).Y * Mm))).ToList();
            List<CurveChain> chains;
            try { chains = CurveChains.Find(Enumerable.Range(0, n).Select(i => cs[i] is Line ? pieces[i] : (pieces[i].a, pieces[i].a)).ToList(), new CurveChainOptions { TolMm = tolMm, MaxPieceMm = 6000, MaxTurnDeg = 35 }); }
            catch { return loop; }
            if (chains.Count == 0) return loop;
            var chainOf = new int[n]; for (int i = 0; i < n; i++) chainOf[i] = -1;
            for (int c = 0; c < chains.Count; c++) foreach (int m in chains[c].Members) chainOf[m] = c;
            // start where a run doesn't straddle the loop's start
            int s0 = Enumerable.Range(0, n).FirstOrDefault(i => chainOf[i] == -1 || chainOf[(i - 1 + n) % n] != chainOf[i]);
            var outLoop = new CurveLoop();
            try
            {
                for (int k = 0; k < n;)
                {
                    int i = (s0 + k) % n; int c = chainOf[i];
                    if (c < 0) { outLoop.Append(cs[i]); k++; continue; }
                    var run = new List<int> { i }; k++;
                    while (k < n && chainOf[(s0 + k) % n] == c) { run.Add((s0 + k) % n); k++; }
                    if (run.Count < 3) { foreach (int r in run) outLoop.Append(cs[r]); continue; }
                    var p0 = cs[run[0]].GetEndPoint(0); var p1 = cs[run[^1]].GetEndPoint(1);
                    var mid = cs[run[run.Count / 2]].GetEndPoint(0);
                    outLoop.Append(Arc.Create(p0, p1, mid)); fitted++;
                }
                return outLoop;
            }
            catch { fitted = 0; return loop; }
        }

        /// <summary>Is the point (plan) inside the closed loop (tessellated, even-odd).</summary>
        internal static bool InsideLoop(CurveLoop loop, XYZ p)
        {
            var pts = loop.SelectMany(c => { var t = c.Tessellate(); return t.Take(t.Count - 1); }).ToList();
            bool inside = false;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
                if ((pts[i].Y > p.Y) != (pts[j].Y > p.Y) && p.X < (pts[j].X - pts[i].X) * (p.Y - pts[i].Y) / (pts[j].Y - pts[i].Y) + pts[i].X) inside = !inside;
            return inside;
        }

        public static IEnumerable<RevitTool> RoofTools()
        {
            // read-only: what clips through what (Mihai: "clipping through walls and stuff, make sure everything is correct")
            yield return new RevitTool
            {
                Name = "clash_check",
                Description = "What the given elements intersect (solid against solid, Revit's own test): per element hit, its category, " +
                              "type, level, Comments tag, and for walls the length, base/top and how many windows/doors it hosts. Default: " +
                              "the mansard roofs (Comments MCP|mansard_roof). Run after every roof/facade build: a mansard through the " +
                              "storey's walls, relief through windows. Changes nothing.",
                InputSchema = Schema(("element_ids", S("array", "Elements to test (default the MCP mansard roofs)"), false),
                                     ("categories", S("array", "Only hits in these categories (default all model categories)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var src = (a["element_ids"] as JsonArray)?.Select(v => doc.GetElement(new ElementId(v.GetValue<long>()))).Where(e => e != null).ToList()
                              ?? new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Roofs).WhereElementIsNotElementType()
                                   .Where(e => (e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "") == "MCP|mansard_roof").ToList();
                    if (src.Count == 0) throw new ArgumentException("Nothing to test: give element_ids.");
                    var cats = (a["categories"] as JsonArray)?.Select(v => v.ToString()).ToList();
                    var result = new List<object>();
                    foreach (var s in src)
                    {
                        var hits = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new ElementIntersectsElementFilter(s))
                            .Where(e => e.Id != s.Id && e.Category != null && e.Category.CategoryType == CategoryType.Model)
                            .Where(e => cats == null || cats.Any(c => string.Equals(c, e.Category.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                        var byCat = hits.GroupBy(e => e.Category.Name).Select(g => new { category = g.Key, count = g.Count() }).OrderByDescending(x => x.count).ToList();
                        var list = hits.Take(150).Select(e =>
                        {
                            var w = e as Wall; var lc = (e.Location as LocationCurve)?.Curve;
                            int inserts = w == null ? 0 : w.FindInserts(true, false, false, false).Count;
                            return new
                            {
                                id = e.Id.Value, category = e.Category.Name, type = doc.GetElement(e.GetTypeId())?.Name, level = (doc.GetElement(e.LevelId) as Level)?.Name,
                                tag = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString(),
                                lengthMm = lc == null ? (double?)null : Math.Round(lc.Length * Mm),
                                topMm = w == null ? (double?)null : Math.Round((e.get_BoundingBox(null)?.Max.Z ?? 0) * Mm),
                                hosts = w == null ? (int?)null : inserts,
                            };
                        }).ToList();
                        result.Add(new { element = s.Id.Value, hits = hits.Count, byCategory = byCat, list });
                    }
                    return new { tested = src.Count, results = result, next = "Walls of the mansard storey hit by the roof: attach their tops to the roof (wall_attach) or move them to the kerb line; detail families hit: move/trim them; windows hit: they need dormers." };
                }
            };

            // read-only: how a roof is made (ours or a reference model's), so a person or the next agent can copy the technique
            yield return new RevitTool
            {
                Name = "roof_detail",
                Description = "How a roof is built: footprint or extrusion, base/cutoff levels and offsets, type, thickness, rafter cut, and for a " +
                              "footprint roof every sketch edge (line/arc, length, ends in mm, whether it defines a slope, the angle, overhang, " +
                              "offset); for an extrusion roof its profile and extents. Use it on the reference model (document) to see how a " +
                              "person modelled a mansard or dormer before building ours. Changes nothing.",
                InputSchema = Schema(("element_id", S("integer", "The roof"), true), ("max_edges", S("integer", "Most edges to list (default 200)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var el = doc.GetElement(new ElementId((long)(Args.Dbl(a, "element_id") ?? 0))) ?? throw new ArgumentException("No such element.");
                    int max = (int)(Args.Dbl(a, "max_edges") ?? 200);
                    string P(BuiltInParameter bp) { var p = el.get_Parameter(bp); return p == null ? null : (p.StorageType == StorageType.ElementId ? doc.GetElement(p.AsElementId())?.Name : p.AsValueString()); }
                    // overhang/offset exist only on roofs made by picking walls (a sketched roof throws)
                    double? Try(Func<double> f) { try { return Math.Round(f() * Mm); } catch { return null; } }
                    double[] Xy(XYZ q) => new[] { Math.Round(q.X * Mm), Math.Round(q.Y * Mm), Math.Round(q.Z * Mm) };
                    object common = new
                    {
                        id = el.Id.Value, kind = el.GetType().Name, type = doc.GetElement(el.GetTypeId())?.Name,
                        baseLevel = P(BuiltInParameter.ROOF_BASE_LEVEL_PARAM), baseOffset = P(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM),
                        cutoffLevel = P(BuiltInParameter.ROOF_UPTO_LEVEL_PARAM), cutoffOffset = P(BuiltInParameter.ROOF_UPTO_LEVEL_OFFSET_PARAM),
                        thickness = P(BuiltInParameter.ROOF_ATTR_THICKNESS_PARAM), rafterCut = P(BuiltInParameter.ROOF_EAVE_CUT_PARAM),
                        fasciaDepth = P(BuiltInParameter.FASCIA_DEPTH_PARAM), maxRidge = P(BuiltInParameter.ACTUAL_MAX_RIDGE_HEIGHT_PARAM),
                        phase = P(BuiltInParameter.PHASE_CREATED), volumeM3 = Math.Round(Volume(el) * Math.Pow(Mm / 1000, 3), 2),
                    };
                    if (el is FootPrintRoof fr)
                    {
                        var edges = new List<object>(); int n = 0, sloped = 0; var angles = new List<double>();
                        foreach (ModelCurveArray loop in fr.GetProfiles())
                            foreach (ModelCurve mc in loop)
                            {
                                n++; bool s = fr.get_DefinesSlope(mc); double ang = s ? Math.Atan(fr.get_SlopeAngle(mc)) * 180 / Math.PI : 0;
                                if (s) { sloped++; angles.Add(Math.Round(ang, 1)); }
                                if (edges.Count < max)
                                {
                                    var c = mc.GeometryCurve;
                                    edges.Add(new { kind = c is Line ? "line" : c.GetType().Name.ToLowerInvariant(), length = Math.Round(c.Length * Mm), from = Xy(c.GetEndPoint(0)), to = Xy(c.GetEndPoint(1)),
                                                    slope = s, slopeDeg = s ? Math.Round(ang, 1) : (double?)null, overhang = Try(() => fr.get_Overhang(mc)), offset = Try(() => fr.get_Offset(mc)) });
                                }
                            }
                        return new { roof = common, footprint = new { edges = n, sloped, flat = n - sloped, distinctAngles = angles.Distinct().OrderBy(x => x).ToList(), list = edges } };
                    }
                    if (el is ExtrusionRoof er)
                    {
                        var prof = er.GetProfile().Cast<ModelCurve>().Select(mc => new { kind = mc.GeometryCurve is Line ? "line" : "arc", from = Xy(mc.GeometryCurve.GetEndPoint(0)), to = Xy(mc.GeometryCurve.GetEndPoint(1)) }).ToList();
                        return new { roof = common, extrusion = new { start = er.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM)?.AsValueString(), end = er.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM)?.AsValueString(), profile = prof } };
                    }
                    return new { roof = common, note = "Not a footprint or extrusion roof (in-place or other): use family_detail / element_geometry." };
                }
            };

            yield return new RevitTool
            {
                Name = "mansard_roof",
                Write = true,
                Description = "The mansard as one native footprint roof round the building (as the hand model does it, see roof_detail: Basic Roof, EVERY edge sloped incl. short jogs, the curved corner as ONE arc edge, cut off at a level; a flat edge next to sloped ones gives \"Invalid footprint\"). From the survey: a slope " +
                              "on each facade edge, cut off at the kerb), from the survey: the outline is the slab at the mansard's base, set " +
                              "in by the eaves' setback; each edge on a facade (facades.json) slopes at the angle its section gives " +
                              "(mansard.json, written by facade_profile; others take the median), party-wall edges don't slope; base and " +
                              "cutoff heights from the sections. Corners hip and curves turn by themselves. Replaces the facade_profile " +
                              "extrusion mansards and this tool's earlier roof. dry_run (default true) reports the edges. One undo step.",
                InputSchema = Schema(
                    ("slope_deg", S("number", "Use this slope for every facade edge instead of the sections'"), false),
                    ("thickness_mm", S("number", "Roof thickness (default 250)"), false),
                    ("arc_tol_mm", S("number", "Chord runs within this of a circle become one arc edge (default 25)"), false),
                    ("hole", S("boolean", "Footprint with a hole at the kerb line so only the sloped band is solved (default true; the flat roof on top fills it)"), false),
                    ("hole_margin_mm", S("number", "How far past the kerb line the hole starts (default 30)"), false),
                    ("diagnose", S("boolean", "When Revit can't slope an outline, find the first edge (longest first) that breaks it and say which"), false),
                    ("street_only", S("boolean", "Default true: a sloped band only along the street facades (eaves to kerb), as site photos show for a mansard on the street fronts; false = the whole outline"), false),
                    ("facades", S("array", "Street facade ids the mansard runs along (default: those with a section in mansard.json)"), false),
                    ("street_tol_mm", S("number", "How far an outline edge may be from a street facade's line (default 2500)"), false),
                    ("show_outlines", S("boolean", "Also list each outline's corner points"), false),
                    ("simplify_mm", S("number", "Drop outline corners straying less than this from their neighbours' line (default 350)"), false),
                    ("min_slope_edge_mm", S("number", "With per_facade_slopes: straight edges shorter than this don't slope (default 900)"), false),
                    ("cutoff", S("boolean", "Cut the roof off at the kerb (default true)"), false),
                    ("per_facade_slopes", S("boolean", "Slope only facade edges, each at its own section's angle (default false: every edge at one angle, which Revit accepts far more often)"), false),
                    ("dry_run", S("boolean", "Only report (default true)"), false)),
                Run = (app, a) =>
                {
                    bool dry = Args.Bool(a, "dry_run") != false;
                    var doc = dry ? Doc(app) : Writable(app);
                    string mf = Path.Combine(ReportsFolder(doc), "mansard.json");
                    if (!File.Exists(mf)) throw new ArgumentException("No mansard.json: run facade_profile (dry_run false) on the facades a section crosses first.");
                    var ms = (JsonNode.Parse(File.ReadAllText(mf)) as JsonObject).Select(kv => (fid: kv.Key, e: kv.Value["eaves"].AsArray().Select(v => v.GetValue<double>()).ToArray(), k: kv.Value["kerb"].AsArray().Select(v => v.GetValue<double>()).ToArray())).ToList();
                    if (ms.Count == 0) throw new ArgumentException("mansard.json is empty.");
                    double Med(IEnumerable<double> v) { var s = v.OrderBy(x => x).ToList(); return s[(s.Count - 1) / 2]; }
                    // per facade: angle (from the eaves up and in to the kerb), setback of the eaves behind the facade line, heights
                    var per = ms.ToDictionary(m => m.fid, m => (deg: Math.Atan2(m.k[1] - m.e[1], Math.Max(1, m.e[0] - m.k[0])) * 180 / Math.PI, setback: -m.e[0], eavesZ: m.e[1], kerbZ: m.k[1]), StringComparer.OrdinalIgnoreCase);
                    double medDeg = Args.Dbl(a, "slope_deg") ?? Med(per.Values.Select(p => p.deg)), medSet = Med(per.Values.Select(p => p.setback));
                    double eavesZ = Med(per.Values.Select(p => p.eavesZ)), kerbZ = Med(per.Values.Select(p => p.kerbZ));
                    double thick = Args.Dbl(a, "thickness_mm") ?? 250;

                    // the outline: the biggest slab whose top is at the mansard's base storey
                    var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                    var baseLevel = levels.Where(l => l.Elevation * Mm <= eavesZ + 300).OrderByDescending(l => l.Elevation).First();
                    var slab = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().Where(f => f.LevelId == baseLevel.Id && f.SketchId != ElementId.InvalidElementId)
                        .OrderByDescending(f => f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0).FirstOrDefault()
                        ?? throw new InvalidOperationException($"No slab on {baseLevel.Name} to take the building's outline from.");
                    var sketch = (Sketch)doc.GetElement(slab.SketchId);
                    var outer = sketch.Profile.Cast<CurveArray>().Select(ca => { var cl = new CurveLoop(); foreach (Curve c in ca) cl.Append(c); return cl; })
                        .OrderByDescending(cl => cl.GetExactLength()).First();
                    // The slab's edge has every jog of the plan (138 edges here) and its curved corner as long chords; the hand model's
                    // sketch is the clean outline with ONE arc. So: fit the arcs, drop the jogs, THEN set in by the eaves' setback
                    // (setting in the raw edge first made a spike that doubled back: "Invalid footprint"), then clean again.
                    // Tried in this order until Revit takes one with its slopes.
                    double simp = (Args.Dbl(a, "simplify_mm") ?? 350) / Mm;
                    CurveLoop SetIn(CurveLoop lp)
                    {
                        try
                        {
                            var c1 = CurveLoop.CreateViaOffset(lp, -medSet / Mm, XYZ.BasisZ); var c2 = CurveLoop.CreateViaOffset(lp, medSet / Mm, XYZ.BasisZ);
                            return c1.GetExactLength() < c2.GetExactLength() ? c1 : c2;
                        }
                        catch { return null; }
                    }
                    var outlines = new List<(string name, CurveLoop loop)>(); int arcs = 0;
                    CurveLoop clean = null;
                    try { clean = Simplified(ArcsFitted(outer, Args.Dbl(a, "arc_tol_mm") ?? 60, out arcs), simp); } catch { }
                    if (clean != null)
                    {
                        var ci = SetIn(clean);
                        if (ci != null) try { outlines.Add(($"clean ({arcs} arc(s)), set in", Simplified(ci, simp))); } catch { }
                        outlines.Add(($"clean ({arcs} arc(s)), at the slab edge", clean));
                    }
                    var rawIn = SetIn(outer);
                    if (rawIn != null) try { outlines.Add(("set in, simplified", Simplified(rawIn, simp))); } catch { }
                    try { outlines.Add(("slab edge, simplified", Simplified(outer, simp))); } catch { }
                    var inset = outlines.Count > 0 ? outlines[0].loop : outer;

                    // facades: lines from facades.json
                    var froot = JsonNode.Parse(File.ReadAllText(Path.Combine(ReportsFolder(doc), "facades.json")));
                    var facades = froot["facades"].AsArray().OfType<JsonObject>().Select(f => { var l = f["line"].AsArray().Select(v => v.GetValue<double>()).ToList(); return (id: f["id"].GetValue<string>(), a: new Pt(l[0], l[1]), b: new Pt(l[2], l[3])); }).ToList();
                    (string fid, double dist) Facing(Curve c)
                    {
                        var mid = c.Evaluate(0.5, true); var p = new Pt(mid.X * Mm, mid.Y * Mm);
                        var dir = c is Line ln ? ln.Direction : (c.GetEndPoint(1) - c.GetEndPoint(0)).Normalize();
                        string best = null; double bd = double.MaxValue;
                        foreach (var f in facades)
                        {
                            double fl = f.a.Dist2D(f.b); if (fl < 1) continue;
                            double fx = (f.b.X - f.a.X) / fl, fy = (f.b.Y - f.a.Y) / fl;
                            if (c is Line && Math.Abs(dir.X * fx + dir.Y * fy) < 0.95) continue;
                            double t = Math.Clamp((p.X - f.a.X) * fx + (p.Y - f.a.Y) * fy, 0, fl);
                            double d = Math.Sqrt(Math.Pow(p.X - (f.a.X + fx * t), 2) + Math.Pow(p.Y - (f.a.Y + fy * t), 2));
                            if (d < bd) { bd = d; best = f.id; }
                        }
                        return (best, bd);
                    }
                    double minEdge = (Args.Dbl(a, "min_slope_edge_mm") ?? 900) / Mm;
                    bool uniform = Args.Bool(a, "per_facade_slopes") != true;
                    List<(Curve c, string fid, double deg, bool arc)> EdgesOf(CurveLoop lp) => lp.Select(c => { var (fid, d) = Facing(c); bool on = fid != null && d < medSet + 1500 && (c.Length >= minEdge || !(c is Line)); double deg = on && per.TryGetValue(fid, out var pf) ? pf.deg : medDeg; return (c, fid: on ? fid : null, deg: on ? (Args.Dbl(a, "slope_deg") ?? deg) : 0.0, arc: !(c is Line)); }).ToList();
                    var edges = EdgesOf(inset); string used = null; var refused = new List<string>();
                    var report = edges.Select(e => new { length = Math.Round(e.c.Length * Mm), kind = e.arc ? "arc" : "line", facade = e.fid, slopeDeg = e.fid == null ? (double?)null : Math.Round(e.deg, 1) }).ToList();

                    // STREET BANDS (default). Site photos of 7 Swallow Place: the slate mansard runs only along the street fronts; the
                    // rear block has a flat set-back attic. Sloping the whole building's outline is wrong AND Revit can't solve it
                    // (diagnose: every breaking edge was on the rear). So: the clean outline's edges along the street facades
                    // (default: those with a surveyed mansard section; arcs between them included) as one chain per run, closed by
                    // the same chain set in by the slope's run (the kerb line). Chain edges slope, the kerb and the two ends don't;
                    // the flat roof on top (roof_solver's top face / an existing roof) fills behind.
                    var streetIds = (a["facades"] as JsonArray)?.Select(v => v.ToString()).ToList() ?? per.Keys.ToList();
                    var streetLines = facades.Where(f => streetIds.Contains(f.id, StringComparer.OrdinalIgnoreCase)).ToList();
                    double bandRun = (kerbZ - eavesZ) / Math.Tan(medDeg * Math.PI / 180) + (Args.Dbl(a, "hole_margin_mm") ?? 30);
                    bool streetMode = Args.Bool(a, "street_only") != false && streetLines.Count > 0;
                    var bands = new List<(List<Curve> chain, CurveLoop loop, string why)>();
                    if (streetMode)
                    {
                        var src = outlines.FirstOrDefault(o => o.name.StartsWith("clean")).loop ?? outer;
                        var cs = src.ToList(); int n = cs.Count;
                        bool OnStreet(Curve c)
                        {
                            if (!(c is Line ln)) return false;
                            var m = c.Evaluate(0.5, true); var p = new Pt(m.X * Mm, m.Y * Mm);
                            foreach (var f in streetLines)
                            {
                                double fl = f.a.Dist2D(f.b); if (fl < 1) continue;
                                double fx = (f.b.X - f.a.X) / fl, fy = (f.b.Y - f.a.Y) / fl;
                                if (Math.Abs(ln.Direction.X * fx + ln.Direction.Y * fy) < 0.95) continue;
                                double t = (p.X - f.a.X) * fx + (p.Y - f.a.Y) * fy; if (t < -500 || t > fl + 500) continue;
                                double d = Math.Abs((p.X - f.a.X) * fy - (p.Y - f.a.Y) * fx);
                                if (d < (Args.Dbl(a, "street_tol_mm") ?? 2500)) return true;
                            }
                            return false;
                        }
                        var on = cs.Select(OnStreet).ToArray();
                        // arcs and short jogs between street edges belong to the run (the curved corner turns between two streets)
                        for (int pass = 0; pass < 3; pass++)
                            for (int i = 0; i < n; i++)
                                if (!on[i] && (!(cs[i] is Line) || cs[i].Length * Mm < 1500) && on[(i - 1 + n) % n] && (on[(i + 1) % n] || !(cs[i] is Line))) on[i] = true;
                        if (on.Any(x => x) && !on.All(x => x))
                        {
                            int s0 = Enumerable.Range(0, n).First(i => on[i] && !on[(i - 1 + n) % n]);
                            var run = new List<Curve>();
                            void Flush()
                            {
                                // a run can't end on a short jog or start on one
                                while (run.Count > 0 && run[^1] is Line && run[^1].Length * Mm < 1500) run.RemoveAt(run.Count - 1);
                                while (run.Count > 0 && run[0] is Line && run[0].Length * Mm < 1500) run.RemoveAt(0);
                                if (run.Sum(c => c.Length) * Mm < 3000) { run = new List<Curve>(); return; }
                                var chain = new CurveLoop(); foreach (var c in run) chain.Append(c);
                                string why = null; CurveLoop band = null;
                                foreach (double sgn in new[] { 1.0, -1.0 })
                                    try
                                    {
                                        var off = CurveLoop.CreateViaOffset(chain, sgn * bandRun / Mm, XYZ.BasisZ);
                                        var om = off.First().Evaluate(0.5, true);
                                        if (!InsideLoop(src, om)) continue;
                                        var offs = off.ToList();
                                        var lp = new CurveLoop();
                                        foreach (var c in run) lp.Append(c);
                                        lp.Append(Line.CreateBound(run[^1].GetEndPoint(1), offs[^1].GetEndPoint(1)));
                                        for (int j = offs.Count - 1; j >= 0; j--) lp.Append(offs[j].CreateReversed());
                                        lp.Append(Line.CreateBound(offs[0].GetEndPoint(0), run[0].GetEndPoint(0)));
                                        band = lp; break;
                                    }
                                    catch (Exception ex) { why = McpServer.Unwrap(ex).Message; }
                                bands.Add((run, band, band == null ? (why ?? "the set-in kerb line falls outside the building") : null));
                                run = new List<Curve>();
                            }
                            for (int k = 0; k < n; k++) { int i = (s0 + k) % n; if (on[i]) run.Add(cs[i]); else if (run.Count > 0) Flush(); }
                            if (run.Count > 0) Flush();
                        }
                    }

                    var ids = new List<long>(); List<string> revit = null; int removed = 0;
                    if (!dry)
                    {
                        string tag = "MCP|mansard_roof";
                        string step = "start";
                        try {
                        revit = BuildTools.Tx.Run(doc, "MCP: mansard roof", () =>
                        {
                            step = "old roofs";
                            var old = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Roofs).WhereElementIsNotElementType()
                                .Where(e => { var c = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? ""; return c == tag || (c.StartsWith("MCP|facade_profile|") && c.EndsWith("|mansard")); })
                                .Select(e => e.Id).ToList();
                            if (old.Count > 0) { doc.Delete(old); removed = old.Count; }
                            step = "roof type";
                            var baseType = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().FirstOrDefault(x => x.GetCompoundStructure() != null && x.FamilyName.IndexOf("Glazing", StringComparison.OrdinalIgnoreCase) < 0)
                                ?? throw new InvalidOperationException("No basic roof type to start from.");
                            string tn = $"BG Mansard Roof {thick:0}";
                            var rt = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().FirstOrDefault(x => x.Name == tn) ?? (RoofType)baseType.Duplicate(tn);
                            try { var cs = rt.GetCompoundStructure(); if (cs != null && cs.LayerCount > 0) { for (int i = 1; i < cs.LayerCount; i++) cs.SetLayerWidth(i, 1 / Mm); cs.SetLayerWidth(0, (thick - (cs.LayerCount - 1)) / Mm); rt.SetCompoundStructure(cs); } } catch { }
                            step = "footprint";
                            // Revit's "Invalid footprint" mostly comes from the slopes, not the lines: a flat edge between sloped ones,
                            // or neighbours a degree or two apart, leave planes that can't meet (Mihai, 2026-11-13). So by default every
                            // edge slopes at ONE angle (the sections' median) and each outline is tried flat first, then sloped, in a
                            // sub-transaction; the first outline that takes the slopes is kept.
                            ModelCurveArray map = null; FootPrintRoof roof = null;
                            // A whole-building hip roof makes Revit solve the full skeleton up to the ridge, which fails on an L-shaped
                            // plan with narrow limbs (here: flat yes, sloped no, at any angle). The usual mansard: the same footprint
                            // with a HOLE at the kerb line (set in by the slope's run, flat edges), so only the sloped band is solved;
                            // the flat roof on top fills the hole. Tried first; then without.
                            double run = (kerbZ - eavesZ) / Math.Tan(medDeg * Math.PI / 180) + (Args.Dbl(a, "hole_margin_mm") ?? 30);
                            CurveLoop HoleOf(CurveLoop lp)
                            {
                                try
                                {
                                    var c1 = CurveLoop.CreateViaOffset(lp, -run / Mm, XYZ.BasisZ); var c2 = CurveLoop.CreateViaOffset(lp, run / Mm, XYZ.BasisZ);
                                    return Simplified(c1.GetExactLength() < c2.GetExactLength() ? c1 : c2, simp);
                                }
                                catch { return null; }
                            }
                            var tries = new List<(string name, CurveLoop lp, CurveLoop hole)>();
                            if (streetMode)
                            {
                                step = "street bands";
                                int bi = 0;
                                foreach (var (chain, band, why) in bands)
                                {
                                    bi++;
                                    if (band == null) { refused.Add($"band {bi}: {why}"); continue; }
                                    var fp = new CurveArray(); foreach (var c in band) fp.Append(c);
                                    using (var st = new SubTransaction(doc))
                                    {
                                        st.Start();
                                        try
                                        {
                                            var bm = new ModelCurveArray();
                                            var r = doc.Create.NewFootPrintRoof(fp, baseLevel, rt, out bm);
                                            foreach (ModelCurve mc in bm)
                                            {
                                                var mid = mc.GeometryCurve.Evaluate(0.5, true);
                                                bool isChain = chain.Any(c => c.Distance(mid) < 5 / Mm);
                                                r.set_DefinesSlope(mc, isChain);
                                                if (isChain) r.set_SlopeAngle(mc, Math.Tan(medDeg * Math.PI / 180));
                                            }
                                            r.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)?.Set((eavesZ - baseLevel.Elevation * Mm) / Mm);
                                            doc.Regenerate();
                                            if (Volume(r) <= 0) throw new InvalidOperationException("no solid");
                                            r.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(tag);
                                            st.Commit(); ids.Add(r.Id.Value); roof ??= r;
                                            used = $"street bands ({streetIds.Count} facades' lines)";
                                        }
                                        catch (Exception ex) { refused.Add($"band {bi} ({chain.Count} edges, {chain.Sum(c => c.Length) * Mm:0} mm): {McpServer.Unwrap(ex).Message}"); if (st.HasStarted() && !st.HasEnded()) st.RollBack(); }
                                    }
                                }
                                if (ids.Count == 0) throw new InvalidOperationException("No street band could be built: " + string.Join("; ", refused));
                            }
                            else
                            {
                                if (Args.Bool(a, "hole") != false) foreach (var (name, lp) in outlines) { var h = HoleOf(lp); if (h != null) tries.Add(($"{name} + kerb hole", lp, h)); }
                                foreach (var (name, lp) in outlines) tries.Add((name, lp, null));
                            }
                            foreach (var (name, lp, hole) in tries)
                            {
                                var es = EdgesOf(lp);
                                var fp = new CurveArray(); foreach (var e in es) fp.Append(e.c);
                                var holeCurves = hole?.ToList() ?? new List<Curve>();
                                foreach (var hc in holeCurves) fp.Append(hc);
                                using (var st = new SubTransaction(doc))
                                {
                                    st.Start();
                                    try
                                    {
                                        // the mapping must be an existing array (Revit fills it; a null one throws "Value cannot be null")
                                        map = new ModelCurveArray();
                                        var r = doc.Create.NewFootPrintRoof(fp, baseLevel, rt, out map);
                                        doc.Regenerate();
                                        double flatVol = Volume(r);
                                        if (flatVol <= 0) throw new InvalidOperationException($"no solid even flat (map {map.Size} of {es.Count} edges)");
                                        foreach (ModelCurve mc in map)
                                        {
                                            var mid = mc.GeometryCurve.Evaluate(0.5, true);
                                            var e = es.OrderBy(x => x.c.Evaluate(0.5, true).DistanceTo(mid)).First();
                                            // a hole edge (the kerb line) stays flat
                                            bool inHole = holeCurves.Count > 0 && holeCurves.Min(h => h.Evaluate(0.5, true).DistanceTo(mid)) < e.c.Evaluate(0.5, true).DistanceTo(mid);
                                            bool slope = !inHole && (uniform || e.fid != null);
                                            r.set_DefinesSlope(mc, slope);
                                            if (slope) r.set_SlopeAngle(mc, Math.Tan((uniform ? medDeg : e.deg) * Math.PI / 180));
                                        }
                                        doc.Regenerate();
                                        if (Volume(r) <= 0 && Args.Bool(a, "diagnose") == true)
                                        {
                                            // which edge breaks it: slope the edges longest first, halving until the first one Revit can't take
                                            var mcs = map.Cast<ModelCurve>().OrderByDescending(m => m.GeometryCurve.Length).ToList();
                                            bool Works(int k)
                                            {
                                                for (int j = 0; j < mcs.Count; j++) { r.set_DefinesSlope(mcs[j], j < k); if (j < k) r.set_SlopeAngle(mcs[j], Math.Tan(medDeg * Math.PI / 180)); }
                                                doc.Regenerate(); return Volume(r) > 0;
                                            }
                                            int lo = 0, hi = mcs.Count; // Works(lo) true, Works(hi) false
                                            while (hi - lo > 1) { int m = (lo + hi) / 2; if (Works(m)) lo = m; else hi = m; }
                                            var bad = mcs[hi - 1].GeometryCurve; var p0 = bad.GetEndPoint(0); var p1 = bad.GetEndPoint(1);
                                            var bm = bad.Evaluate(0.5, true);
                                            var neigh = mcs.Select(m => m.GeometryCurve).Where(c => c.GetEndPoint(0).DistanceTo(p0) < 1e-3 || c.GetEndPoint(1).DistanceTo(p0) < 1e-3 || c.GetEndPoint(0).DistanceTo(p1) < 1e-3 || c.GetEndPoint(1).DistanceTo(p1) < 1e-3)
                                                .Where(c => !ReferenceEquals(c, bad)).Select(c => $"{(c is Line ? "line" : "arc")} {c.Length * Mm:0} mm").ToList();
                                            throw new InvalidOperationException($"no solid with these slopes; the {lo} longest edges slope fine, edge #{hi} breaks it: {(bad is Line ? "line" : "arc")} {bad.Length * Mm:0} mm from ({p0.X * Mm:0}, {p0.Y * Mm:0}) to ({p1.X * Mm:0}, {p1.Y * Mm:0}), next to {string.Join(", ", neigh)}");
                                        }
                                        if (Volume(r) <= 0) throw new InvalidOperationException("no solid with these slopes");
                                        // eaves height and kerb cutoff here too: "Can't make footprint roof" shows up only once they are set
                                        r.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)?.Set((eavesZ - baseLevel.Elevation * Mm) / Mm);
                                        var cutLevel = levels.Where(l => l.Elevation * Mm >= kerbZ - 300).OrderBy(l => l.Elevation).FirstOrDefault();
                                        if (cutLevel != null && Args.Bool(a, "cutoff") != false)
                                        {
                                            r.get_Parameter(BuiltInParameter.ROOF_UPTO_LEVEL_PARAM)?.Set(cutLevel.Id);
                                            r.get_Parameter(BuiltInParameter.ROOF_UPTO_LEVEL_OFFSET_PARAM)?.Set((kerbZ - cutLevel.Elevation * Mm) / Mm);
                                        }
                                        doc.Regenerate();
                                        if (Volume(r) <= 0) throw new InvalidOperationException("no solid once cut off at the kerb");
                                        st.Commit(); roof = r; edges = es; used = name; break;
                                    }
                                    catch (Exception ex) { refused.Add($"{name} ({es.Count} edges): {McpServer.Unwrap(ex).Message}"); if (st.HasStarted() && !st.HasEnded()) st.RollBack(); }
                                }
                            }
                            if (roof == null) throw new InvalidOperationException("Revit refused every outline: " + string.Join("; ", refused));
                            step = "commit";
                            roof.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(tag);
                            if (!ids.Contains(roof.Id.Value)) ids.Add(roof.Id.Value);
                        });
                        } catch (Exception ex) { throw new InvalidOperationException($"mansard_roof at {step}: {McpServer.Unwrap(ex).Message}", ex); }
                        report = edges.Select(e => new { length = Math.Round(e.c.Length * Mm), kind = e.arc ? "arc" : "line", facade = e.fid, slopeDeg = e.fid == null ? (double?)null : Math.Round(e.deg, 1) }).ToList();
                    }
                    return new
                    {
                        slab = slab.Id.Value, baseLevel = baseLevel.Name, eavesMm = Math.Round(eavesZ), kerbMm = Math.Round(kerbZ), setbackMm = Math.Round(medSet), medianSlopeDeg = Math.Round(medDeg, 1),
                        fromSections = per.Select(p => new { facade = p.Key, slopeDeg = Math.Round(p.Value.deg, 1), setbackMm = Math.Round(p.Value.setback), eaves = Math.Round(p.Value.eavesZ), kerb = Math.Round(p.Value.kerbZ) }).ToList(),
                        arcsFitted = arcs, streetMode, streetFacades = streetIds, bandRunMm = Math.Round(bandRun), bands = bands.Select(b => new { edges = b.chain.Count, lengthMm = Math.Round(b.chain.Sum(c => c.Length) * Mm), arcs = b.chain.Count(c => !(c is Line)), from = new[] { Math.Round(b.chain[0].GetEndPoint(0).X * Mm), Math.Round(b.chain[0].GetEndPoint(0).Y * Mm) }, to = new[] { Math.Round(b.chain[^1].GetEndPoint(1).X * Mm), Math.Round(b.chain[^1].GetEndPoint(1).Y * Mm) }, ok = b.loop != null, b.why }).ToList(), outlineUsed = used, outlines = outlines.Select(o => new { o.name, points = o.loop.Select(c => new[] { Math.Round(c.GetEndPoint(0).X * Mm), Math.Round(c.GetEndPoint(0).Y * Mm) }).ToList(), kinds = string.Concat(o.loop.Select(c => c is Line ? "l" : "a")) }).Take(Args.Bool(a, "show_outlines") == true ? 9 : 0).ToList(), outlinesRefused = refused, edges = report.Count, sloped = report.Count(r => r.facade != null), edgeList = report.Take(60).ToList(), removed, roofs = ids, revit,
                        next = dry ? "dry_run: false to build it." : "check_view + view_image; dormers next (dormer_families).",
                    };
                }
            };
        }
    }
}
