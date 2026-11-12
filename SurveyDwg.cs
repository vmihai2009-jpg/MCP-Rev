using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    // The Revit side of the DWG survey pipeline (ported from the mcp-servers-for-revit survey-pipeline patch and
    // made to work on the Revit 2025 API): reading a linked or imported DWG, building from it by layer rules,
    // checking the model against it, and its coordinates. The planning itself is Revit-free (Survey\*.cs).

    internal class ReadStats
    {
        public int Lines { get; set; }
        public int PolyLines { get; set; }
        public int Arcs { get; set; }
        public int OtherCurves { get; set; }
        public int Points { get; set; }
        public int Blocks { get; set; }
        public int Solids { get; set; }
        public int Meshes { get; set; }
        public int Unknown { get; set; }
    }

    /// <summary>Reads CAD geometry from a DWG link or import as Revit-free entities, in mm, model (internal) coordinates.</summary>
    internal static class CadLink
    {
        public const double MmPerFoot = 304.8;

        public static List<ImportInstance> All(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();

        public static string NameOf(Document doc, ImportInstance imp)
        {
            string name = null;
            try { name = doc.GetElement(imp.GetTypeId())?.Name; } catch { }
            if (string.IsNullOrEmpty(name)) name = imp.Category?.Name;
            return string.IsNullOrEmpty(name) ? "Import " + imp.Id.Value : name;
        }

        public static object Describe(Document doc, ImportInstance imp) => new
        {
            id = imp.Id.Value, name = NameOf(doc, imp), linked = imp.IsLinked,
            view = imp.ViewSpecific ? (doc.GetElement(imp.OwnerViewId) as View)?.Name : null,
            file = FilePath(doc, imp),
        };

        /// <summary>The DWG file behind a link, when Revit knows it; null for an import or a missing file.</summary>
        public static string FilePath(Document doc, ImportInstance imp)
        {
            try
            {
                var type = doc.GetElement(imp.GetTypeId());
                if (type == null || !type.IsExternalFileReference()) return null;
                var path = ModelPathUtils.ConvertModelPathToUserVisiblePath(type.GetExternalFileReference().GetAbsolutePath());
                return File.Exists(path) ? path : null;
            }
            catch { return null; }
        }

        /// <summary>By id, by part of its name, or the only one in the model.</summary>
        public static ImportInstance Pick(Document doc, JsonObject a)
        {
            var all = All(doc);
            if (all.Count == 0) throw new InvalidOperationException("No CAD links or imports in the model. Link the survey DWG first (Insert > Link CAD), or give a file path where the tool takes one.");
            string list = string.Join("; ", all.Select(i => i.Id.Value + " = " + NameOf(doc, i)));
            if (Args.Long(a, "import_id") is long id)
                return all.FirstOrDefault(i => i.Id.Value == id) ?? throw new ArgumentException($"No CAD link or import {id}. There are: {list}");
            if (Args.Str(a, "import_name") is string name)
            {
                var hits = all.Where(i => NameOf(doc, i).IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (hits.Count == 1) return hits[0];
                throw new ArgumentException((hits.Count == 0 ? $"No CAD link matches \"{name}\". " : $"\"{name}\" matches {hits.Count} links. ") + "There are: " + list);
            }
            if (all.Count == 1) return all[0];
            throw new ArgumentException("There are several CAD links: give import_id or import_name. There are: " + list);
        }

        public static List<CadEntity> Read(Document doc, ImportInstance imp, ReadStats stats)
        {
            var result = new List<CadEntity>();
            var ge = imp.get_Geometry(new Options { ComputeReferences = false, IncludeNonVisibleObjects = false, DetailLevel = ViewDetailLevel.Fine });
            if (ge != null) Walk(doc, ge, 0, result, new Dictionary<long, string>(), stats);
            return result;
        }

        private static void Walk(Document doc, GeometryElement ge, int depth, List<CadEntity> sink, Dictionary<long, string> layers, ReadStats stats)
        {
            foreach (GeometryObject go in ge)
            {
                if (go is GeometryInstance gi)
                {
                    int blockIndex = -1;
                    // depth 0 is the link itself; deeper instances are CAD blocks (INSERTs)
                    if (depth >= 1)
                    {
                        var block = new CadEntity { Index = sink.Count, Kind = CadKind.Block, Layer = LayerOf(doc, gi, layers) };
                        try
                        {
                            var t = gi.Transform;
                            block.Points.Add(ToMm(t.Origin));
                            block.RotationRad = Math.Atan2(t.BasisX.Y, t.BasisX.X);
                        }
                        catch { block.Points.Add(new Pt(0, 0, 0)); }
                        // GeometryInstance.Symbol is gone from the 2025 API: the block's symbol comes by id
                        try { block.BlockName = doc.GetElement(gi.GetSymbolGeometryId().SymbolId)?.Name; } catch { }
                        sink.Add(block); blockIndex = sink.Count - 1; stats.Blocks++;
                    }
                    GeometryElement inner = null;
                    try { inner = gi.GetInstanceGeometry(); } catch { }
                    if (inner != null) Walk(doc, inner, depth + 1, sink, layers, stats);
                    if (blockIndex >= 0 && string.IsNullOrEmpty(sink[blockIndex].Layer))
                        for (int i = blockIndex + 1; i < sink.Count; i++)
                            if (!string.IsNullOrEmpty(sink[i].Layer)) { sink[blockIndex].Layer = sink[i].Layer; break; }
                    continue;
                }
                string layer = LayerOf(doc, go, layers);
                switch (go)
                {
                    case Line line:
                    {
                        var e = New(sink, layer, CadKind.Line);
                        e.Points.Add(ToMm(line.GetEndPoint(0))); e.Points.Add(ToMm(line.GetEndPoint(1)));
                        stats.Lines++;
                        break;
                    }
                    case PolyLine pl:
                    {
                        var e = New(sink, layer, CadKind.Polyline);
                        foreach (XYZ p in pl.GetCoordinates()) e.Points.Add(ToMm(p));
                        if (e.Points.Count > 2 && e.Points[0].Dist2D(e.Points[e.Points.Count - 1]) < 1.0) { e.Points.RemoveAt(e.Points.Count - 1); e.Closed = true; }
                        stats.PolyLines++;
                        break;
                    }
                    case Curve curve:
                    {
                        // arcs, ellipses, splines in short straight pieces (curved walls are approximated)
                        var e = New(sink, layer, CadKind.Polyline);
                        foreach (XYZ p in curve.Tessellate()) e.Points.Add(ToMm(p));
                        if (curve is Arc) stats.Arcs++; else stats.OtherCurves++;
                        break;
                    }
                    case Point pt:
                    {
                        var e = New(sink, layer, CadKind.Point);
                        e.Points.Add(ToMm(pt.Coord)); stats.Points++;
                        break;
                    }
                    case Solid _: stats.Solids++; break;
                    case Mesh _: stats.Meshes++; break;
                    default: stats.Unknown++; break;
                }
            }
        }

        private static CadEntity New(List<CadEntity> sink, string layer, CadKind kind)
        {
            var e = new CadEntity { Index = sink.Count, Layer = layer, Kind = kind };
            sink.Add(e);
            return e;
        }

        public static Pt ToMm(XYZ p) => new Pt(p.X * MmPerFoot, p.Y * MmPerFoot, p.Z * MmPerFoot);

        private static string LayerOf(Document doc, GeometryObject go, Dictionary<long, string> cache)
        {
            var sid = go.GraphicsStyleId;
            if (sid == null || sid.Value < 0) return "";
            if (cache.TryGetValue(sid.Value, out var name)) return name;
            name = (doc.GetElement(sid) as GraphicsStyle)?.GraphicsStyleCategory?.Name ?? "";
            cache[sid.Value] = name;
            return name;
        }

        public static List<LayerInventory> Inventory(IEnumerable<CadEntity> entities) =>
            entities.GroupBy(e => e.Layer ?? "").Select(g => new LayerInventory
            {
                Name = g.Key,
                Lines = g.Count(e => e.Kind == CadKind.Line),
                Polylines = g.Count(e => e.Kind == CadKind.Polyline),
                ClosedPolylines = g.Count(e => e.Kind == CadKind.Polyline && e.Closed),
                Points = g.Count(e => e.Kind == CadKind.Point),
                Blocks = g.Count(e => e.Kind == CadKind.Block),
                ZMinMm = g.SelectMany(e => e.Points).Select(p => p.Z).DefaultIfEmpty(0).Min(),
                ZMaxMm = g.SelectMany(e => e.Points).Select(p => p.Z).DefaultIfEmpty(0).Max(),
            }).OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>The link's straight wall-face lines (polylines split), in model mm, on layers matching the globs.</summary>
        public static List<Seg> Segments(IEnumerable<CadEntity> entities, IList<string> layers)
        {
            var segs = new List<Seg>();
            foreach (var e in entities.Where(x => (x.Kind == CadKind.Line || x.Kind == CadKind.Polyline) && DwgFile.LayerMatches(x.Layer, layers)))
            {
                int n = e.Points.Count, count = e.Closed ? n : n - 1;
                for (int i = 0; i < count; i++) segs.Add(new Seg(e.Points[i], e.Points[(i + 1) % n], e.Index));
            }
            return segs;
        }

        /// <summary>
        /// How the DWG file's own coordinates (mm) map onto the model for this link: the link's placement, at the unit
        /// scale Revit used. The scale is found by checking which candidate puts the file's line ends on the link's.
        /// Null when the file cannot be matched to the link.
        /// </summary>
        public static Func<Pt, Pt> FileToModel(Document doc, ImportInstance imp, DwgContent file, out string note)
        {
            note = null;
            var t = imp.GetTotalTransform();
            var linkEnds = new HashSet<(long, long)>();
            foreach (var e in Read(doc, imp, new ReadStats()).Where(x => x.Kind == CadKind.Line || x.Kind == CadKind.Polyline))
                foreach (var p in e.Points) linkEnds.Add(((long)Math.Round(p.X / 5), (long)Math.Round(p.Y / 5)));
            if (linkEnds.Count == 0 || file.Lines.Count == 0) { note = "The link or the file has no lines to match."; return null; }

            var sample = file.Lines.Where((_, i) => i % Math.Max(1, file.Lines.Count / 300) == 0).Take(300).ToList();
            // Two ways the file can sit in the model: by the link's own placement, or (survey drawings, far from the
            // origin, which Revit moves closer when linking) by the project's shared coordinates. At each, Revit may
            // have read the file at another unit. The one that puts the file's line ends on the link's wins.
            var sharedToInternal = doc.ActiveProjectLocation.GetTotalTransform().Inverse;
            var ways = new List<(string name, Transform tf)> { ("the link's placement", t), ("the project's shared coordinates", sharedToInternal) };
            // The project position read directly, both ways round (the sign of the angle is not the same in every API
            // path): shared -> internal = rotate about the origin after taking off the survey offset.
            var pp = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
            var off = Transform.CreateTranslation(new XYZ(-pp.EastWest, -pp.NorthSouth, 0));
            foreach (int sign in new[] { -1, 1 })
                ways.Add(($"the project position (angle {(sign < 0 ? "-" : "+")}{pp.Angle * 180 / Math.PI:0.###} deg)",
                          Transform.CreateRotation(XYZ.BasisZ, sign * pp.Angle).Multiply(off)));
            var tried = new List<string>();
            double bestScore = 0, bestK = 0;
            Transform bestTf = null; string bestWay = null;
            foreach (var (way, tf) in ways)
                foreach (double unitMm in new[] { 1.0, 10, 1000, 25.4, 304.8 })
                {
                    double k = unitMm / file.MmPerUnit / MmPerFoot; // file mm -> drawing units -> feet at that unit
                    int hits = 0;
                    foreach (var l in sample)
                    {
                        var q = tf.OfPoint(new XYZ(l.A.X * k, l.A.Y * k, 0));
                        long qx = (long)Math.Round(q.X * MmPerFoot / 5), qy = (long)Math.Round(q.Y * MmPerFoot / 5);
                        bool hit = false;
                        for (long dx = -1; dx <= 1 && !hit; dx++) for (long dy = -1; dy <= 1 && !hit; dy++) hit = linkEnds.Contains((qx + dx, qy + dy));
                        if (hit) hits++;
                    }
                    double score = (double)hits / sample.Count;
                    if (unitMm == 1000 || score > 0.05) tried.Add($"{way} at {unitMm} mm/unit: {score:P0}");
                    if (score > bestScore) { bestScore = score; bestK = k; bestTf = tf; bestWay = way; }
                }
            if (bestScore < 0.3) { note = $"The file's lines do not land on the link's (best match {bestScore:P0}): is it the same drawing? Tried: {string.Join("; ", tried)}"; return null; }
            note = $"Placed by {bestWay}: {bestScore:P0} of sampled line ends land on the link.";
            double kk = bestK;
            var tt = bestTf;
            return p => { var q = tt.OfPoint(new XYZ(p.X * kk, p.Y * kk, p.Z * kk)); return new Pt(q.X * MmPerFoot, q.Y * MmPerFoot, q.Z * MmPerFoot); };
        }
    }

    /// <summary>Traceability tag in each generated element's Comments: "DWG-MCP|op id|import|layer".</summary>
    internal static class SurveyTags
    {
        public const string Prefix = "DWG-MCP|";

        public class Tagged { public Element Element; public string OpId, Import, Layer; }

        public static string Make(string opId, string importName, string layer) => Prefix + opId + "|" + Clean(importName) + "|" + Clean(layer);

        private static string Clean(string s) => (s ?? "").Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');

        public static bool TryParse(string comment, out string opId, out string import, out string layer)
        {
            opId = import = layer = null;
            if (string.IsNullOrEmpty(comment) || !comment.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            var parts = comment.Substring(Prefix.Length).Split(new[] { '|' }, 3);
            if (parts[0].Length == 0) return false;
            opId = parts[0]; import = parts.Length > 1 ? parts[1] : ""; layer = parts.Length > 2 ? parts[2] : "";
            return true;
        }

        public static bool Apply(Element el, string tag)
        {
            var p = el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            return p != null && !p.IsReadOnly && p.Set(tag);
        }

        public static List<Tagged> Collect(Document doc, string importName)
        {
            var rule = ParameterFilterRuleFactory.CreateBeginsWithRule(new ElementId(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS), Prefix);
            var result = new List<Tagged>();
            foreach (Element el in new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new ElementParameterFilter(rule)))
            {
                if (!TryParse(el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString(), out var opId, out var imp, out var layer)) continue;
                if (!string.IsNullOrEmpty(importName) && !string.Equals(imp, importName.Replace('|', '/'), StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new Tagged { Element = el, OpId = opId, Import = imp, Layer = layer });
            }
            return result;
        }
    }

    /// <summary>Dismisses warnings and resolves errors without a dialog; an error with no fix rolls the step back.</summary>
    internal class SurveyFailures : IFailuresPreprocessor
    {
        private readonly HashSet<string> _seen = new HashSet<string>();
        public List<string> Messages { get; } = new List<string>();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var msgs = accessor.GetFailureMessages();
            if (msgs.Count == 0) return FailureProcessingResult.Continue;
            bool unresolved = false;
            foreach (FailureMessageAccessor m in msgs)
            {
                var text = m.GetDescriptionText();
                switch (m.GetSeverity())
                {
                    case FailureSeverity.Warning: Add("warning: " + text); accessor.DeleteWarning(m); break;
                    case FailureSeverity.Error:
                        // unjoining loses nothing; any other fix Revit offers may delete elements, so the step is undone instead
                        if (m.HasResolutions() && m.HasResolutionOfType(FailureResolutionType.DetachElements))
                        {
                            m.SetCurrentResolutionType(FailureResolutionType.DetachElements);
                            accessor.ResolveFailure(m);
                            Add("unjoined: " + text);
                        }
                        else { Add("refused: " + text); unresolved = true; }
                        break;
                    default: unresolved = true; break;
                }
            }
            return unresolved ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.ProceedWithCommit;
        }

        private void Add(string text) { if (Messages.Count < 60 && _seen.Add(text)) Messages.Add(text); }
    }

    internal static class SurveyConfig
    {
        /// <summary>The build config from "config" (an object) or "config_path" (a JSON file); null when neither is given.</summary>
        public static BuildConfig Load(JsonObject a)
        {
            string json = null;
            if (a["config"] is JsonObject inline) json = inline.ToJsonString();
            else if (Args.Str(a, "config_path") is string path)
            {
                if (!File.Exists(path)) throw new ArgumentException("config_path not found: " + path);
                json = File.ReadAllText(path);
            }
            return json == null ? null : ConfigJson.Parse(json);
        }

        public static JsonNode ToJson(object value) => JsonSerializer.SerializeToNode(value, ConfigJson.Write);
    }

    internal class BuildRun
    {
        public bool DryRun = true;
        public string Mode = "add_missing";
        public HashSet<string> OnlyActions, OnlyRules;
        public int MaxOps, BatchSize = 200;
        public bool CreateLevels = true, CreateTypes = true;
        public double RoundThicknessMm = 10;
        public string ImportName = "";
    }

    internal class OpOutcome
    {
        public string OpId { get; set; }
        public string Action { get; set; }
        public string Rule { get; set; }
        public string Layer { get; set; }
        public string Status { get; set; }
        public long ElementId { get; set; }
        public string Message { get; set; }
    }

    internal class BuildOutcome
    {
        public bool DryRun { get; set; }
        public string Mode { get; set; }
        public int Planned { get; set; }
        public int Created { get; set; }
        public int SkippedExisting { get; set; }
        public int Failed { get; set; }
        public int Deleted { get; set; }
        public double Seconds { get; set; }
        public Dictionary<string, int> CreatedByAction { get; set; } = new Dictionary<string, int>();
        public List<OpOutcome> Failures { get; set; } = new List<OpOutcome>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> RevitMessages { get; set; } = new List<string>();
        public List<string> LevelsCreated { get; set; } = new List<string>();
        public List<string> TypesCreated { get; set; } = new List<string>();
        public List<long> SampleElementIds { get; set; } = new List<long>();
    }

    /// <summary>Carries out a build plan. All the decisions were made by the planner; this only talks to Revit.</summary>
    internal class SurveyBuildExecutor
    {
        private const double Mm = CadLink.MmPerFoot;
        private readonly Document _doc;
        private readonly BuildConfig _cfg;
        private readonly BuildRun _opt;
        private readonly BuildOutcome _out;
        private readonly SurveyFailures _failures = new SurveyFailures();
        private readonly Dictionary<string, ElementId> _levelIds = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _levelElev = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ElementId> _wallTypes = new Dictionary<string, ElementId>();
        private readonly Dictionary<string, ElementId> _floorTypes = new Dictionary<string, ElementId>();
        private readonly Dictionary<string, ElementId> _symbols = new Dictionary<string, ElementId>();
        private readonly Dictionary<string, string> _problems = new Dictionary<string, string>();
        private readonly HashSet<string> _existing = new HashSet<string>();
        private int _untagged;

        public SurveyBuildExecutor(Document doc, BuildConfig cfg, BuildRun opt)
        {
            _doc = doc; _cfg = cfg; _opt = opt;
            _out = new BuildOutcome { DryRun = opt.DryRun, Mode = opt.Mode };
        }

        public BuildOutcome Execute(BuildPlan plan)
        {
            var sw = Stopwatch.StartNew();
            var ops = plan.Ops.Where(o =>
                (_opt.OnlyActions == null || _opt.OnlyActions.Count == 0 || _opt.OnlyActions.Contains(o.Action)) &&
                (_opt.OnlyRules == null || _opt.OnlyRules.Count == 0 || _opt.OnlyRules.Contains(o.Rule))).ToList();
            if (_opt.MaxOps > 0) ops = ops.Take(_opt.MaxOps).ToList();
            _out.Planned = ops.Count;

            using (var group = new TransactionGroup(_doc, _opt.DryRun ? "MCP: DWG build (dry run)" : "MCP: build from DWG"))
            {
                group.Start();
                try
                {
                    if (string.Equals(_opt.Mode, "rebuild", StringComparison.OrdinalIgnoreCase)) DeletePrevious();
                    foreach (var t in SurveyTags.Collect(_doc, _opt.ImportName)) _existing.Add(t.OpId);
                    Prepare(ops);
                    int batch = Math.Max(1, _opt.BatchSize);
                    for (int i = 0; i < ops.Count; i += batch)
                    {
                        var slice = ops.Skip(i).Take(batch).ToList();
                        // Revit rolled the batch back: try one by one to find the element it refused
                        if (!RunBatch("DWG build", slice, false))
                            foreach (var op in slice) RunBatch("DWG build (one)", new List<PlanOp> { op }, true);
                    }
                    if (_opt.DryRun) group.RollBack(); else group.Assimilate();
                }
                catch
                {
                    if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                    throw;
                }
            }
            if (_untagged > 0) _out.Warnings.Add(_untagged + " element(s) have no writable Comments, so they carry no tag: re-runs and QA cannot recognise them.");
            _out.RevitMessages = _failures.Messages;
            if (_opt.DryRun) _out.SampleElementIds.Clear();
            _out.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
            return _out;
        }

        private void Configure(Transaction t)
        {
            var fho = t.GetFailureHandlingOptions();
            fho.SetFailuresPreprocessor(_failures);
            fho.SetClearAfterRollback(true);
            t.SetFailureHandlingOptions(fho);
        }

        private void DeletePrevious()
        {
            var prev = SurveyTags.Collect(_doc, _opt.ImportName);
            if (prev.Count == 0) return;
            using (var t = new Transaction(_doc, "Remove the earlier DWG build"))
            {
                t.Start(); Configure(t);
                _out.Deleted = _doc.Delete(prev.Select(p => p.Element.Id).ToList())?.Count ?? 0;
                t.Commit();
            }
        }

        private void Prepare(List<PlanOp> ops)
        {
            using (var t = new Transaction(_doc, "DWG build: levels and types"))
            {
                t.Start(); Configure(t);
                var levels = new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                foreach (var l in levels) { _levelIds[l.Name] = l.Id; _levelElev[l.Name] = l.Elevation; }
                foreach (var name in ops.Select(o => o.Level ?? "").Distinct()) ResolveLevel(name, levels);
                foreach (var op in ops)
                    switch (op.Action)
                    {
                        case Actions.WallCentreline:
                        case Actions.WallDoubleLine: ResolveWallType(op); break;
                        case Actions.Floor: ResolveFloorType(op); break;
                        case Actions.PointFamily: ResolveSymbol(op); break;
                    }
                t.Commit();
            }
        }

        private void ResolveLevel(string name, List<Level> existing)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                if (_levelIds.ContainsKey("")) return;
                if (existing.Count > 0)
                {
                    _levelIds[""] = existing[0].Id; _levelElev[""] = existing[0].Elevation;
                    _out.Warnings.Add("No level given in the config: using the lowest level, '" + existing[0].Name + "'.");
                    return;
                }
                name = _cfg.Levels.Count > 0 ? _cfg.Levels[0].Name : null;
                if (name == null) { _problems["level:"] = "The model has no levels and the config lists none."; return; }
            }
            if (_levelIds.ContainsKey(name)) return;
            var spec = _cfg.Levels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (spec != null && _opt.CreateLevels)
            {
                var lvl = Level.Create(_doc, spec.ElevationMm / Mm);
                lvl.Name = spec.Name;
                _levelIds[spec.Name] = lvl.Id; _levelElev[spec.Name] = lvl.Elevation;
                _out.LevelsCreated.Add(spec.Name + " at " + spec.ElevationMm.ToString("0") + " mm");
            }
            else _problems["level:" + name] = "Level '" + name + "' does not exist" + (spec == null ? " and is not in the config's levels." : " (create_levels is off).");
        }

        private ElementId LevelFor(PlanOp op, out double elevFt, out string problem)
        {
            problem = null; elevFt = 0;
            string name = op.Level ?? "";
            if (string.IsNullOrWhiteSpace(name) && !_levelIds.ContainsKey("") && _cfg.Levels.Count > 0) name = _cfg.Levels[0].Name;
            if (string.IsNullOrWhiteSpace(name)) name = "";
            if (_levelIds.TryGetValue(name, out var id)) { elevFt = _levelElev[name]; return id; }
            problem = _problems.TryGetValue("level:" + name, out var p) ? p : "Level '" + name + "' is not available.";
            return null;
        }

        /// <summary>Measured thicknesses rounded (10 mm by default), so a drawing does not make a wall type per millimetre.</summary>
        private double Rounded(double mm) => _opt.RoundThicknessMm > 0 ? Math.Round(mm / _opt.RoundThicknessMm) * _opt.RoundThicknessMm : Math.Round(mm);

        private string WallKey(PlanOp op) => (op.TypeName ?? "") + "|" + Rounded(op.ThicknessMm);

        private void ResolveWallType(PlanOp op)
        {
            string key = WallKey(op);
            if (_wallTypes.ContainsKey(key) || _problems.ContainsKey("wall:" + key)) return;
            if (string.IsNullOrWhiteSpace(op.TypeName) && _opt.CreateTypes && op.ThicknessMm > 0)
            {
                // the same "Concept Wall <n>mm" types Survey to Revit and the other wall tools use, found or made
                bool existed = new FilteredElementCollector(_doc).OfClass(typeof(WallType)).Cast<WallType>().Any(w => w.Name == $"Concept Wall {Rounded(op.ThicknessMm):0}mm");
                var t = BuildTools.WallTypeOf(_doc, Rounded(op.ThicknessMm));
                if (!existed) _out.TypesCreated.Add("Wall type '" + t.Name + "'");
                _wallTypes[key] = t.Id;
                return;
            }
            var basics = new FilteredElementCollector(_doc).OfClass(typeof(WallType)).Cast<WallType>().Where(w => w.Kind == WallKind.Basic).ToList();
            WallType hit = !string.IsNullOrWhiteSpace(op.TypeName)
                ? basics.FirstOrDefault(w => string.Equals(w.Name, op.TypeName, StringComparison.OrdinalIgnoreCase))
                : basics.FirstOrDefault(w => Math.Abs(w.Width * Mm - op.ThicknessMm) < 0.5);
            if (hit == null)
            {
                string newName = !string.IsNullOrWhiteSpace(op.TypeName) ? op.TypeName : "DWG Wall " + Math.Round(op.ThicknessMm) + "mm";
                hit = basics.FirstOrDefault(w => string.Equals(w.Name, newName, StringComparison.OrdinalIgnoreCase));
                if (hit == null)
                {
                    if (!_opt.CreateTypes || basics.Count == 0 || op.ThicknessMm <= 0)
                    {
                        _problems["wall:" + key] = "Wall type '" + (op.TypeName ?? "of " + op.ThicknessMm + " mm") + "' not found" + (_opt.CreateTypes ? "." : " (create_types is off).");
                        return;
                    }
                    var nt = (WallType)basics[0].Duplicate(newName);
                    var cs = nt.GetCompoundStructure();
                    if (cs != null)
                    {
                        var mat = cs.GetLayers().First().MaterialId;
                        cs.SetLayers(new List<CompoundStructureLayer> { new CompoundStructureLayer(op.ThicknessMm / Mm, MaterialFunctionAssignment.Structure, mat) });
                        nt.SetCompoundStructure(cs);
                    }
                    hit = nt;
                    _out.TypesCreated.Add("Wall type '" + newName + "' (" + Math.Round(op.ThicknessMm) + " mm)");
                }
            }
            _wallTypes[key] = hit.Id;
        }

        private void ResolveFloorType(PlanOp op)
        {
            string key = op.TypeName ?? "";
            if (_floorTypes.ContainsKey(key) || _problems.ContainsKey("floor:" + key)) return;
            var types = new FilteredElementCollector(_doc).OfClass(typeof(FloorType)).Cast<FloorType>().Where(f => !f.IsFoundationSlab).ToList();
            var hit = string.IsNullOrWhiteSpace(key) ? types.FirstOrDefault() : types.FirstOrDefault(f => string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase));
            if (hit == null) _problems["floor:" + key] = "Floor type '" + key + "' not found. There are: " + string.Join(", ", types.Select(f => f.Name).Take(12));
            else _floorTypes[key] = hit.Id;
        }

        private void ResolveSymbol(PlanOp op)
        {
            string key = (op.FamilyName ?? "") + "|" + (op.TypeName ?? "");
            if (_symbols.ContainsKey(key) || _problems.ContainsKey("symbol:" + key)) return;
            var syms = new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => string.Equals(s.FamilyName, op.FamilyName, StringComparison.OrdinalIgnoreCase)).ToList();
            var hit = string.IsNullOrWhiteSpace(op.TypeName) ? syms.FirstOrDefault() : syms.FirstOrDefault(s => string.Equals(s.Name, op.TypeName, StringComparison.OrdinalIgnoreCase));
            if (hit == null)
                _problems["symbol:" + key] = "Family '" + op.FamilyName + "'" + (string.IsNullOrEmpty(op.TypeName) ? "" : " type '" + op.TypeName + "'") + " is not loaded" +
                    (syms.Count > 0 ? " (it has: " + string.Join(", ", syms.Select(s => s.Name).Take(10)) + ")." : ".");
            else { if (!hit.IsActive) hit.Activate(); _symbols[key] = hit.Id; }
        }

        private bool RunBatch(string name, List<PlanOp> slice, bool final)
        {
            var results = new List<OpOutcome>();
            using (var t = new Transaction(_doc, name))
            {
                t.Start(); Configure(t);
                foreach (var op in slice)
                {
                    if (_existing.Contains(op.Id)) { results.Add(Outcome(op, "skipped_existing", 0, null)); continue; }
                    using (var st = new SubTransaction(_doc))
                    {
                        st.Start();
                        try
                        {
                            var el = CreateOne(op) ?? throw new InvalidOperationException("Revit returned no element.");
                            if (!SurveyTags.Apply(el, SurveyTags.Make(op.Id, _opt.ImportName, op.Layer))) _untagged++;
                            st.Commit();
                            results.Add(Outcome(op, "created", el.Id.Value, null));
                        }
                        catch (Exception ex)
                        {
                            if (st.GetStatus() == TransactionStatus.Started) st.RollBack();
                            results.Add(Outcome(op, "failed", 0, ex.Message));
                        }
                    }
                }
                var status = t.Commit();
                if (status != TransactionStatus.Committed && !final) return false;
                if (status != TransactionStatus.Committed)
                    foreach (var r in results.Where(r => r.Status == "created"))
                    { r.Status = "failed"; r.Message = "Revit rolled it back (" + string.Join("; ", _failures.Messages.Take(2)) + ")."; r.ElementId = 0; }
            }
            // made, then removed by Revit while committing: not made
            if (!_opt.DryRun)
                foreach (var r in results.Where(r => r.Status == "created" && _doc.GetElement(new ElementId(r.ElementId)) == null))
                { r.Status = "failed"; r.Message = "Revit removed it when committing."; r.ElementId = 0; }
            foreach (var r in results) Record(r);
            return true;
        }

        private static OpOutcome Outcome(PlanOp op, string status, long id, string msg) =>
            new OpOutcome { OpId = op.Id, Action = op.Action, Rule = op.Rule, Layer = op.Layer, Status = status, ElementId = id, Message = msg };

        private void Record(OpOutcome r)
        {
            switch (r.Status)
            {
                case "created":
                    _out.Created++; _existing.Add(r.OpId);
                    _out.CreatedByAction.TryGetValue(r.Action, out int c); _out.CreatedByAction[r.Action] = c + 1;
                    if (_out.SampleElementIds.Count < 25) _out.SampleElementIds.Add(r.ElementId);
                    break;
                case "skipped_existing": _out.SkippedExisting++; break;
                default: _out.Failed++; if (_out.Failures.Count < 200) _out.Failures.Add(r); break;
            }
        }

        private Element CreateOne(PlanOp op)
        {
            bool needsLevel = op.Action != Actions.Grid;
            ElementId levelId = null; double elev = 0;
            if (needsLevel)
            {
                levelId = LevelFor(op, out elev, out string problem);
                if (levelId == null)
                {
                    if (op.Action != Actions.TopoPoints && op.Action != Actions.TopoContours) throw new InvalidOperationException(problem);
                    var lowest = new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault()
                        ?? throw new InvalidOperationException("Topography needs a level, and the model has none.");
                    levelId = lowest.Id;
                }
            }
            switch (op.Action)
            {
                case Actions.WallCentreline:
                case Actions.WallDoubleLine: return CreateWall(op, levelId, elev);
                case Actions.Floor: return CreateFloor(op, levelId, elev);
                case Actions.TopoPoints:
                case Actions.TopoContours: return CreateTopo(op, levelId);
                case Actions.Grid: return Grid.Create(_doc, Line.CreateBound(new XYZ(op.Start.X / Mm, op.Start.Y / Mm, 0), new XYZ(op.End.X / Mm, op.End.Y / Mm, 0)));
                case Actions.PointFamily: return CreatePoint(op, levelId, elev);
                default: throw new InvalidOperationException("Unsupported action '" + op.Action + "'.");
            }
        }

        private Element CreateWall(PlanOp op, ElementId levelId, double elevFt)
        {
            string key = WallKey(op);
            if (!_wallTypes.TryGetValue(key, out var typeId)) throw new InvalidOperationException(_problems.TryGetValue("wall:" + key, out var p) ? p : "Wall type not available.");
            var a = new XYZ(op.Start.X / Mm, op.Start.Y / Mm, elevFt);
            var b = new XYZ(op.End.X / Mm, op.End.Y / Mm, elevFt);
            if (a.DistanceTo(b) < _doc.Application.ShortCurveTolerance) throw new InvalidOperationException("The wall is shorter than Revit allows.");
            return Wall.Create(_doc, Line.CreateBound(a, b), typeId, levelId, op.HeightMm / Mm, op.BaseOffsetMm / Mm, false, false);
        }

        private Element CreateFloor(PlanOp op, ElementId levelId, double elevFt)
        {
            string key = op.TypeName ?? "";
            if (!_floorTypes.TryGetValue(key, out var typeId)) throw new InvalidOperationException(_problems.TryGetValue("floor:" + key, out var p) ? p : "Floor type not available.");
            var pts = op.Points.Select(p => new XYZ(p.X / Mm, p.Y / Mm, elevFt)).ToList();
            var loop = new CurveLoop();
            for (int i = 0; i < pts.Count; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
            var floor = Floor.Create(_doc, new List<CurveLoop> { loop }, typeId, levelId);
            if (floor != null && Math.Abs(op.BaseOffsetMm) > 1e-6)
            {
                var off = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                if (off != null && !off.IsReadOnly) off.Set(op.BaseOffsetMm / Mm);
            }
            return floor;
        }

        /// <summary>Toposolid (TopographySurface.Create is obsolete from Revit 2024 on).</summary>
        private Element CreateTopo(PlanOp op, ElementId levelId)
        {
            var typeId = new FilteredElementCollector(_doc).OfClass(typeof(ToposolidType)).FirstElementId();
            if (typeId == null || typeId == ElementId.InvalidElementId) throw new InvalidOperationException("The project has no Toposolid type.");
            var pts = op.Points.Select(p => new XYZ(p.X / Mm, p.Y / Mm, p.Z / Mm)).ToList();
            return Toposolid.Create(_doc, pts, typeId, levelId);
        }

        private Element CreatePoint(PlanOp op, ElementId levelId, double elevFt)
        {
            string key = (op.FamilyName ?? "") + "|" + (op.TypeName ?? "");
            if (!_symbols.TryGetValue(key, out var symId)) throw new InvalidOperationException(_problems.TryGetValue("symbol:" + key, out var p) ? p : "Family type not available.");
            var symbol = (FamilySymbol)_doc.GetElement(symId);
            var level = (Level)_doc.GetElement(levelId);
            var loc = new XYZ(op.Start.X / Mm, op.Start.Y / Mm, elevFt + op.BaseOffsetMm / Mm);
            var inst = _doc.Create.NewFamilyInstance(loc, symbol, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural)
                ?? throw new InvalidOperationException("Revit placed nothing (a face- or work-plane-based family?).");
            if (Math.Abs(op.RotationRad) > 1e-6) ElementTransformUtils.RotateElement(_doc, inst.Id, Line.CreateBound(loc, loc + XYZ.BasisZ), op.RotationRad);
            return inst;
        }
    }

    internal class QaIssue
    {
        public string Severity { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public string OpId { get; set; }
        public string Action { get; set; }
        public string Layer { get; set; }
        public long ElementId { get; set; }
        public double DeviationMm { get; set; }
    }

    internal class QaReport
    {
        public int PlannedOps { get; set; }
        public int TaggedElements { get; set; }
        public int Matched { get; set; }
        public int Missing { get; set; }
        public int Moved { get; set; }
        public int WrongSize { get; set; }
        public int Duplicated { get; set; }
        public int Stale { get; set; }
        public bool Passed { get; set; }
        public double MaxDeviationMm { get; set; }
        public Dictionary<string, int> MissingByAction { get; set; } = new Dictionary<string, int>();
        public List<QaIssue> Issues { get; set; } = new List<QaIssue>();
        public int IssuesNotShown { get; set; }
    }

    /// <summary>What the DWG says should exist (a fresh plan) against what the model holds (tagged elements).</summary>
    internal static class SurveyQa
    {
        private const double Mm = CadLink.MmPerFoot;

        public static QaReport Run(Document doc, BuildPlan plan, string importName, double toleranceMm, int maxIssues)
        {
            var rep = new QaReport { PlannedOps = plan.Ops.Count };
            var tagged = SurveyTags.Collect(doc, importName);
            rep.TaggedElements = tagged.Count;
            var byOp = tagged.GroupBy(t => t.OpId).ToDictionary(g => g.Key, g => g.ToList());
            var planIds = new HashSet<string>(plan.Ops.Select(o => o.Id));

            void Add(string sev, string code, string msg, PlanOp op, long id, double dev, string opId = null, string layer = null)
            {
                if (rep.Issues.Count >= maxIssues) { rep.IssuesNotShown++; return; }
                rep.Issues.Add(new QaIssue { Severity = sev, Code = code, Message = msg, OpId = op?.Id ?? opId, Action = op?.Action, Layer = op?.Layer ?? layer, ElementId = id, DeviationMm = Math.Round(dev, 1) });
            }

            foreach (var op in plan.Ops)
            {
                if (!byOp.TryGetValue(op.Id, out var els))
                {
                    rep.Missing++;
                    rep.MissingByAction.TryGetValue(op.Action, out int c); rep.MissingByAction[op.Action] = c + 1;
                    Add("error", "MISSING", "The DWG has this " + op.Action + "; the model has no element for it.", op, 0, 0);
                    continue;
                }
                if (els.Count > 1) { rep.Duplicated++; Add("warning", "DUPLICATED", els.Count + " elements carry the same DWG tag (built twice, or copied).", op, els[1].Element.Id.Value, 0); }
                var el = els[0].Element;
                bool ok = Compare(el, op, out double dev, out string size);
                if (dev > rep.MaxDeviationMm) rep.MaxDeviationMm = dev;
                if (dev > toleranceMm) { rep.Moved++; Add("error", "MOVED", $"{dev:0.#} mm from where the DWG puts it (tolerance {toleranceMm:0.#} mm).", op, el.Id.Value, dev); }
                else if (size != null) { rep.WrongSize++; Add("warning", "WRONG_SIZE", size, op, el.Id.Value, dev); }
                else if (ok) rep.Matched++;
            }
            foreach (var kv in byOp.Where(k => !planIds.Contains(k.Key)))
            {
                rep.Stale++;
                Add("warning", "STALE", "Built earlier, but the DWG or config no longer produces it (layer '" + kv.Value[0].Layer + "').", null, kv.Value[0].Element.Id.Value, 0, kv.Key, kv.Value[0].Layer);
            }
            rep.Passed = rep.Missing == 0 && rep.Moved == 0 && rep.Duplicated == 0;
            return rep;
        }

        private static bool Compare(Element el, PlanOp op, out double dev, out string size)
        {
            dev = 0; size = null;
            switch (op.Action)
            {
                case Actions.WallCentreline:
                case Actions.WallDoubleLine:
                {
                    if (!(el is Wall wall) || !(el.Location is LocationCurve lc)) return false;
                    XYZ a = lc.Curve.GetEndPoint(0), b = lc.Curve.GetEndPoint(1);
                    dev = Math.Min(Math.Max(Xy(a, op.Start), Xy(b, op.End)), Math.Max(Xy(a, op.End), Xy(b, op.Start)));
                    // types are rounded to 10 mm when built: within 6 mm is the right thickness
                    if (op.ThicknessMm > 0 && Math.Abs(wall.Width * Mm - op.ThicknessMm) > 6.0)
                        size = $"The wall is {wall.Width * Mm:0} mm thick; the DWG gives {op.ThicknessMm:0} mm.";
                    return true;
                }
                case Actions.Grid:
                {
                    if (!(el is Grid g) || g.Curve == null) return false;
                    XYZ a = g.Curve.GetEndPoint(0), b = g.Curve.GetEndPoint(1);
                    dev = Math.Min(Math.Max(Xy(a, op.Start), Xy(b, op.End)), Math.Max(Xy(a, op.End), Xy(b, op.Start)));
                    return true;
                }
                case Actions.PointFamily:
                    if (!(el.Location is LocationPoint lp)) return false;
                    dev = Xy(lp.Point, op.Start);
                    return true;
                case Actions.Floor:
                case Actions.TopoPoints:
                case Actions.TopoContours:
                {
                    var bb = el.get_BoundingBox(null);
                    if (bb == null) return false;
                    double minX = op.Points.Min(p => p.X), maxX = op.Points.Max(p => p.X), minY = op.Points.Min(p => p.Y), maxY = op.Points.Max(p => p.Y);
                    dev = new[] { Math.Abs(bb.Min.X * Mm - minX), Math.Abs(bb.Max.X * Mm - maxX), Math.Abs(bb.Min.Y * Mm - minY), Math.Abs(bb.Max.Y * Mm - maxY) }.Max();
                    return true;
                }
                default: return false;
            }
        }

        private static double Xy(XYZ p, Pt q) { double dx = p.X * Mm - q.X, dy = p.Y * Mm - q.Y; return Math.Sqrt(dx * dx + dy * dy); }
    }

    /// <summary>Loosely typed argument readers (a number may arrive as text and the other way round).</summary>
    internal static class Args
    {
        public static string Str(JsonObject a, string k) => a?[k] is JsonValue v && v.ToString() is string s && s.Length > 0 ? s : null;
        public static bool? Bool(JsonObject a, string k) => a?[k] is JsonValue v && bool.TryParse(v.ToString(), out bool b) ? b : (bool?)null;
        public static double? Dbl(JsonObject a, string k) =>
            a?[k] is JsonValue v && double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : (double?)null;
        public static long? Long(JsonObject a, string k) => Dbl(a, k) is double d ? (long)d : (long?)null;
        public static int Int(JsonObject a, string k, int fallback) => Dbl(a, k) is double d ? (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, d)) : fallback;
        public static List<string> Strings(JsonObject a, string k) =>
            (a?[k] as JsonArray)?.OfType<JsonValue>().Select(v => v.ToString()).Where(s => s.Length > 0).ToList() ?? new List<string>();
        public static List<double> Numbers(JsonObject a, string k) =>
            (a?[k] as JsonArray)?.OfType<JsonValue>()
                .Select(v => double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : double.NaN)
                .Where(d => !double.IsNaN(d)).ToList() ?? new List<double>();
    }
}
