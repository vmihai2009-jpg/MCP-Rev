using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    /// <summary>
    /// 1.5.0: tools for taking a Survey to Revit base model to a 1:1 match with the survey DWGs. Ported from the
    /// mcp-servers-for-revit survey pipeline (config-driven DWG build, QA against the DWG, coordinates), plus the DWG
    /// wall fit the Swallow Place session ran by hand, and reading DWG text (heights, labels) straight from the file.
    /// </summary>
    internal static partial class SurveyTools
    {
        private const double Mm = CadLink.MmPerFoot;

        private static readonly string[] DefaultWallLayers = { "*wall*", "*partition*", "*PartGlass*", "EF_25*", "Ss_25*", "*plinth*" };

        private static JsonObject Schema(params (string name, JsonObject schema, bool required)[] props)
        {
            var p = new JsonObject();
            var req = new JsonArray();
            foreach (var (name, schema, required) in props) { p[name] = schema; if (required) req.Add(name); }
            return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = req };
        }
        private static JsonObject S(string type, string d) => new JsonObject { ["type"] = type, ["description"] = d };
        private static JsonObject Arr(string item, string d) => new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = item }, ["description"] = d };
        private static JsonObject Obj(string d) => new JsonObject { ["type"] = "object", ["description"] = d };

        private static (string, JsonObject, bool)[] Import => new[]
        {
            ("import_id", S("integer", "The CAD link or import (list_cad shows them)"), false),
            ("import_name", S("string", "Or part of its name"), false),
        };
        private static (string, JsonObject, bool)[] Config => new[]
        {
            ("config", Obj("Layer rules: { levels, defaultLevel, tolerances, rules: [ { name, match: [globs], exclude, action, ... } ] }. dwg_analyze suggests one; the dwg_survey_build prompt explains it"), false),
            ("config_path", S("string", "Or a JSON file holding the config"), false),
        };
        private static (string, JsonObject, bool)[] With(this (string, JsonObject, bool)[] a, params (string, JsonObject, bool)[] b) => a.Concat(b).ToArray();

        public static IEnumerable<RevitTool> All()
        {
            yield return new RevitTool
            {
                Name = "dwg_analyze",
                Description = "Read a linked survey DWG by layer: what is on each layer, problems (far from origin, duplicates, unmapped layers), and what a build would make. With no config it suggests one from the layer names. Changes nothing.",
                InputSchema = Schema(Import.With(Config).With(("sample_ops", S("integer", "Planned operations to show as a sample (default 15)"), false))),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var imports = CadLink.All(doc).Select(i => CadLink.Describe(doc, i)).ToList();
                    var imp = CadLink.Pick(doc, a);
                    var stats = new ReadStats();
                    var entities = CadLink.Read(doc, imp, stats);
                    var inventory = CadLink.Inventory(entities);
                    var cfg = SurveyConfig.Load(a);
                    SuggestedConfig suggestion = null;
                    if (cfg == null)
                    {
                        suggestion = ConfigSuggester.Suggest(inventory); cfg = suggestion.Config;
                        // a plan linked into one view belongs to that view's level, not to a made-up "Level 1"
                        if (imp.ViewSpecific && doc.GetElement(imp.OwnerViewId) is ViewPlan vp && vp.GenLevel != null)
                        {
                            cfg.Levels = new List<LevelSpec> { new LevelSpec { Name = vp.GenLevel.Name, ElevationMm = Math.Round(vp.GenLevel.ProjectElevation * 304.8, 1) } };
                            cfg.DefaultLevel = vp.GenLevel.Name;
                            suggestion.Notes.Insert(0, $"Level set to '{vp.GenLevel.Name}', the level of the view the plan is linked into.");
                        }
                    }
                    var plan = BuildPlanner.Plan(entities, cfg);
                    var rules = new LayerRuleSet(cfg.Rules);
                    GeoMath.BoundingBox(entities.SelectMany(e => e.Points), out var min, out var max);
                    int sample = Args.Int(a, "sample_ops", 15);
                    return new
                    {
                        imports,
                        selected = CadLink.Describe(doc, imp),
                        entities = entities.Count,
                        read = stats,
                        extentsMm = new { min = new[] { Math.Round(min.X), Math.Round(min.Y), Math.Round(min.Z) }, max = new[] { Math.Round(max.X), Math.Round(max.Y), Math.Round(max.Z) } },
                        layers = inventory.Select(l => { var r = rules.Resolve(l.Name); return new { name = l.Name, lines = l.Lines, polylines = l.Polylines, closed = l.ClosedPolylines, points = l.Points, blocks = l.Blocks, zMinMm = Math.Round(l.ZMinMm), zMaxMm = Math.Round(l.ZMaxMm), rule = r?.Name, action = r?.Action }; }).ToList(),
                        validation = SurveyConfig.ToJson(plan.Report.Issues),
                        cleaning = SurveyConfig.ToJson(plan.Stats),
                        plan = new
                        {
                            operations = plan.Ops.Count, byAction = plan.CountsByAction(),
                            sample = plan.Ops.Take(Math.Max(0, sample)).Select(o => new { o.Id, o.Action, o.Rule, o.Layer, o.Level, o.TypeName, o.FamilyName, o.ThicknessMm, vertices = o.Points.Count, start = o.Start.ToString(), end = o.End.ToString() }).ToList(),
                        },
                        configIsSuggested = suggestion != null,
                        config = suggestion != null ? SurveyConfig.ToJson(cfg) : null,
                        notes = suggestion?.Notes,
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "dwg_extract",
                Description = "The raw geometry of a linked DWG (lines, polylines, points, blocks), in model mm, filtered by layer, kind and area, a page at a time. Changes nothing. (cad_lines gives just straight lines near a place.)",
                InputSchema = Schema(Import.With(
                    ("layers", Arr("string", "Layer globs, e.g. A-WALL* (all when left out)"), false),
                    ("kinds", Arr("string", "line, polyline, point, block (all when left out)"), false),
                    ("box", Arr("number", "[x1, y1, x2, y2] model mm: only entities with a point inside"), false),
                    ("cleaned", S("boolean", "Snap, de-duplicate and simplify first, as a build does"), false),
                    ("offset", S("integer", "Skip this many"), false), ("limit", S("integer", "At most this many (default 200)"), false))),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var imp = CadLink.Pick(doc, a);
                    var entities = CadLink.Read(doc, imp, new ReadStats());
                    if (Args.Bool(a, "cleaned") == true) entities = GeometryCleaner.CleanAll(entities, new Tolerances(), new CleanStats());
                    var layers = Args.Strings(a, "layers");
                    if (layers.Count > 0) entities = entities.Where(e => DwgFile.LayerMatches(e.Layer, layers)).ToList();
                    var kinds = new HashSet<string>(Args.Strings(a, "kinds").Select(k => k.ToLowerInvariant()));
                    if (kinds.Count > 0) entities = entities.Where(e => kinds.Contains(e.Kind.ToString().ToLowerInvariant())).ToList();
                    var box = Args.Numbers(a, "box");
                    if (box.Count == 4) entities = entities.Where(e => e.Points.Any(p => p.X >= Math.Min(box[0], box[2]) && p.X <= Math.Max(box[0], box[2]) && p.Y >= Math.Min(box[1], box[3]) && p.Y <= Math.Max(box[1], box[3]))).ToList();
                    int limit = Math.Clamp(Args.Int(a, "limit", 200), 1, 2000), offset = Math.Max(0, Args.Int(a, "offset", 0));
                    return new
                    {
                        total = entities.Count, offset,
                        entities = entities.Skip(offset).Take(limit).Select(e => new
                        {
                            index = e.Index, layer = e.Layer, kind = e.Kind.ToString().ToLowerInvariant(), closed = e.Closed ? true : (bool?)null, block = e.BlockName,
                            points = e.Points.Take(200).Select(p => new[] { Math.Round(p.X, 1), Math.Round(p.Y, 1), Math.Round(p.Z, 1) }).ToList(),
                        }).ToList(),
                        notShown = Math.Max(0, entities.Count - offset - limit),
                        units = "mm, model (internal) coordinates",
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "dwg_build_model",
                Write = true,
                Description = "Build from a linked DWG by layer rules: walls from pairs of face lines or centre lines, floors from closed outlines, Toposolid from spot points or contours, grids, and families on blocks. DRY RUN by default: the build runs for real, is rolled back, and reports what would be made and what Revit refuses. Safe to re-run: only what is missing is added (mode rebuild replaces this link's earlier output). Every element is tagged in Comments for survey_qa_check. One undo step.",
                InputSchema = Schema(Import.With(Config).With(
                    ("dry_run", S("boolean", "Default true: run, roll back, report. false to build"), false),
                    ("mode", S("string", "add_missing (default) or rebuild"), false),
                    ("only_actions", Arr("string", "Only these actions"), false), ("only_rules", Arr("string", "Only these rules"), false),
                    ("max_ops", S("integer", "At most this many operations"), false),
                    ("create_levels", S("boolean", "Create config levels that do not exist (default true)"), false),
                    ("create_types", S("boolean", "Create wall types of the measured thickness (default true)"), false),
                    ("round_thickness_mm", S("number", "Measured wall thicknesses are rounded to this, so types are shared (default 10; 0 keeps every mm)"), false),
                    ("force", S("boolean", "Build even if the checks report errors (not recommended)"), false))),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var imp = CadLink.Pick(doc, a);
                    var cfg = SurveyConfig.Load(a) ?? throw new ArgumentException("No config. Run dwg_analyze first (it suggests one), review it, then pass it as config or config_path.");
                    var entities = CadLink.Read(doc, imp, new ReadStats());
                    var plan = BuildPlanner.Plan(entities, cfg);
                    if (plan.Report.HasErrors && Args.Bool(a, "force") != true)
                        return new { built = false, reason = "The checks found errors, so nothing was built. Fix them, or pass force: true.", validation = SurveyConfig.ToJson(plan.Report.Issues) };
                    if (plan.Ops.Count == 0)
                        return new { built = false, reason = "The config makes nothing. Check its layer rules with dwg_analyze.", validation = SurveyConfig.ToJson(plan.Report.Issues) };
                    bool dry = Args.Bool(a, "dry_run") != false;
                    var run = new BuildRun
                    {
                        DryRun = dry, Mode = Args.Str(a, "mode") ?? "add_missing",
                        OnlyActions = new HashSet<string>(Args.Strings(a, "only_actions").Select(x => x.ToLowerInvariant())),
                        OnlyRules = new HashSet<string>(Args.Strings(a, "only_rules")),
                        MaxOps = Args.Int(a, "max_ops", 0),
                        CreateLevels = Args.Bool(a, "create_levels") != false, CreateTypes = Args.Bool(a, "create_types") != false,
                        RoundThicknessMm = Args.Dbl(a, "round_thickness_mm") ?? 10,
                        ImportName = CadLink.NameOf(doc, imp),
                    };
                    var outcome = new SurveyBuildExecutor(doc, cfg, run).Execute(plan);
                    return new
                    {
                        summary = dry
                            ? $"DRY RUN, model unchanged: {outcome.Created} would be made, {outcome.SkippedExisting} already there, {outcome.Failed} would fail."
                            : $"Made {outcome.Created}, skipped {outcome.SkippedExisting} already there, {outcome.Failed} failed, in {outcome.Seconds} s.",
                        import = CadLink.Describe(doc, imp),
                        outcome = SurveyConfig.ToJson(outcome),
                        validation = SurveyConfig.ToJson(plan.Report.Issues),
                        planByAction = plan.CountsByAction(),
                        next = dry ? "Look at the failures and warnings, then call again with dry_run: false." : "survey_qa_check to check the result against the DWG.",
                        undo = dry ? null : "One undo step in Revit.",
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "survey_qa_check",
                Description = "Check what dwg_build_model made against the DWG as it is now: missing, moved, wrong size, duplicated, or stale (no longer in the DWG). PASS means nothing is missing, moved or duplicated. Changes nothing. For walls from any source (Survey to Revit's too), use dwg_wall_fit.",
                InputSchema = Schema(Import.With(Config).With(
                    ("tolerance_mm", S("number", "How far an element may be from the DWG (default 10)"), false),
                    ("max_issues", S("integer", "At most this many issues listed (default 100)"), false))),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var imp = CadLink.Pick(doc, a);
                    var cfg = SurveyConfig.Load(a) ?? throw new ArgumentException("Pass the same config the build used (config or config_path).");
                    var plan = BuildPlanner.Plan(CadLink.Read(doc, imp, new ReadStats()), cfg);
                    var rep = SurveyQa.Run(doc, plan, CadLink.NameOf(doc, imp), Args.Dbl(a, "tolerance_mm") ?? 10, Math.Clamp(Args.Int(a, "max_issues", 100), 1, 1000));
                    return new
                    {
                        verdict = rep.Passed ? "PASS" : "FAIL",
                        summary = $"{rep.Matched} match, {rep.Missing} missing, {rep.Moved} moved, {rep.WrongSize} wrong size, {rep.Duplicated} duplicated, {rep.Stale} stale; worst {rep.MaxDeviationMm:0.#} mm.",
                        report = SurveyConfig.ToJson(rep),
                        next = rep.Missing > 0 ? "dwg_build_model (mode add_missing) adds only what is missing." : null,
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "survey_coordinates",
                Description = "The model's coordinates: survey point and project base point (E/W, N/S, elevation, angle to true north), the shared position at the internal origin, and the site's latitude and longitude. Changes nothing.",
                InputSchema = Schema(),
                Run = (app, a) => Coordinates(Doc(app), new List<string>(), "report"),
            };

            yield return new RevitTool
            {
                Name = "set_survey_coordinates",
                Write = true,
                Description = "Set the model's coordinates. mode acquire_from_import: take shared coordinates from a linked DWG (this OVERWRITES the shared coordinates, true north and geolocation, and clears them if the DWG has none; needs confirm: true). mode set_base_points: set the survey point and/or project base point. One undo step. Run survey_coordinates first.",
                InputSchema = Schema(Import.With(
                    ("mode", S("string", "acquire_from_import or set_base_points"), true),
                    ("survey_point", Obj("{ east_west_mm, north_south_mm, elevation_mm }"), false),
                    ("project_base_point", Obj("{ east_west_mm, north_south_mm, elevation_mm, angle_to_true_north_deg }"), false),
                    ("confirm", S("boolean", "Needed for acquire_from_import"), false))),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var mode = (Args.Str(a, "mode") ?? "").ToLowerInvariant();
                    var warnings = new List<string>();
                    if (mode == "acquire_from_import")
                    {
                        if (Args.Bool(a, "confirm") != true)
                            throw new ArgumentException("acquire_from_import OVERWRITES the shared coordinates, true north and geolocation (and clears them if the DWG has none). Check with survey_coordinates, ask the user, then call again with confirm: true.");
                        var imp = CadLink.Pick(doc, a);
                        using (var t = new Transaction(doc, "MCP: acquire coordinates from " + CadLink.NameOf(doc, imp)))
                        {
                            t.Start();
                            try { doc.AcquireCoordinates(imp.Id); }
                            catch (Exception ex) { t.RollBack(); throw new InvalidOperationException("Revit could not take coordinates from that link: " + ex.Message + " (it must be linked, not imported; otherwise use set_base_points)."); }
                            t.Commit();
                        }
                    }
                    else if (mode == "set_base_points")
                    {
                        var sp = a["survey_point"] as JsonObject;
                        var pbp = a["project_base_point"] as JsonObject;
                        if (sp == null && pbp == null) throw new ArgumentException("Give survey_point and/or project_base_point.");
                        using (var t = new Transaction(doc, "MCP: set base points"))
                        {
                            t.Start();
                            foreach (var bp in new FilteredElementCollector(doc).OfClass(typeof(BasePoint)).Cast<BasePoint>())
                            {
                                var v = bp.IsShared ? sp : pbp;
                                if (v == null) continue;
                                string what = bp.IsShared ? "survey point" : "project base point";
                                SetLength(bp, BuiltInParameter.BASEPOINT_EASTWEST_PARAM, Args.Dbl(v, "east_west_mm"), warnings, what + " E/W");
                                SetLength(bp, BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM, Args.Dbl(v, "north_south_mm"), warnings, what + " N/S");
                                SetLength(bp, BuiltInParameter.BASEPOINT_ELEVATION_PARAM, Args.Dbl(v, "elevation_mm"), warnings, what + " elevation");
                                if (!bp.IsShared && Args.Dbl(v, "angle_to_true_north_deg") is double deg)
                                {
                                    var ang = bp.get_Parameter(BuiltInParameter.BASEPOINT_ANGLETON_PARAM);
                                    if (ang != null && !ang.IsReadOnly) ang.Set(deg * Math.PI / 180); else warnings.Add("Angle to true north is read-only.");
                                }
                            }
                            t.Commit();
                        }
                    }
                    else throw new ArgumentException("mode is acquire_from_import or set_base_points.");
                    return Coordinates(doc, warnings, mode);
                }
            };

            yield return new RevitTool
            {
                Name = "dwg_wall_fit",
                Description = "Fit the model's walls on a floor to the survey DWG's wall faces (any wall: Survey to Revit's, or added since). For each wall that is off, the DWG face pair that fits it and the corrected centre line and thickness, marked moderate (both faces well covered, change modest) or review. Also the walls the DWG draws that the model lacks. Reads the floor's linked plan DWG directly. Changes nothing: apply with dwg_wall_fit_apply.",
                InputSchema = FitSchema(false),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var (level, imp, walls, segs) = FitInputs(doc, a);
                    var opts = FitOptionsFrom(a);
                    var proposals = WallFit.Fit(walls, segs, opts);
                    var missing = Args.Bool(a, "include_missing") == false ? new List<MissingWall>()
                        : WallFit.Missing(walls, segs, Args.Dbl(a, "min_thickness_mm") ?? 60, Args.Dbl(a, "max_thickness_mm") ?? Fidelity.MaxWallMm, Args.Dbl(a, "min_length_mm") ?? 600);
                    int limit = Math.Clamp(Args.Int(a, "limit", 60), 1, 500);
                    return new
                    {
                        level = level.Name, import = CadLink.Describe(doc, imp), wallsChecked = walls.Count, dwgLines = segs.Count,
                        toFix = proposals.Count, moderate = proposals.Count(p => p.Confidence == "moderate"), review = proposals.Count(p => p.Confidence == "review"),
                        proposals = SurveyConfig.ToJson(proposals.Take(limit).ToList()),
                        missingWalls = SurveyConfig.ToJson(missing.Take(limit).ToList()), missingCount = missing.Count,
                        notShown = Math.Max(0, proposals.Count - limit),
                        next = "dwg_wall_fit_apply with confidence moderate (or chosen ids). Missing walls: create_walls with the line and thickness, after checking with cad_lines.",
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "dwg_wall_fit_apply",
                Write = true,
                Description = "Apply dwg_wall_fit's corrections: each chosen wall gets the thickness (a wall type of that thickness, made from its own type if needed) and the centre line the DWG gives, keeping its windows and doors. By default only the moderate ones; or give ids. Each wall is done on its own, so one Revit refuses does not stop the rest. One undo step.",
                InputSchema = FitSchema(true),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var (level, imp, walls, segs) = FitInputs(doc, a);
                    var proposals = WallFit.Fit(walls, segs, FitOptionsFrom(a));
                    var ids = new HashSet<long>(Args.Numbers(a, "ids").Select(d => (long)d));
                    string confidence = (Args.Str(a, "confidence") ?? "moderate").ToLowerInvariant();
                    var chosen = proposals.Where(p => ids.Count > 0 ? ids.Contains(p.Id) : confidence == "all" || p.Confidence == "moderate").ToList();
                    int max = Math.Clamp(Args.Int(a, "max_changes", 200), 1, 2000);
                    chosen = chosen.Take(max).ToList();
                    var done = new List<object>();
                    var failed = new List<object>();
                    var notes = new List<string>();
                    int wallsBefore = CountWalls(doc, level);
                    // One undo step for the lot; each wall its own transaction inside it, under BuildTools' failure rules:
                    // joins Revit cannot keep are unjoined, and a fix Revit would make by DELETING elements undoes that
                    // wall's change instead (1.5.0 let Revit delete three neighbouring walls here).
                    using (var g = new TransactionGroup(doc, $"MCP: fit {chosen.Count} walls to the DWG on {level.Name}"))
                    {
                        g.Start();
                        foreach (var p in chosen)
                        {
                            var wall = doc.GetElement(new ElementId(p.Id)) as Wall;
                            if (wall == null) { failed.Add(new { id = p.Id, problem = "no longer a wall" }); continue; }
                            string typeChange = null;
                            try
                            {
                                void changeType()
                                {
                                    if (Math.Abs(p.NewThicknessMm - p.ThicknessMm) > 1)
                                    {
                                        var type = BuildTools.WallTypeOf(doc, p.NewThicknessMm);
                                        if (type.Id != wall.WallType.Id) { wall.ChangeTypeId(type.Id); typeChange = $"{p.ThicknessMm:0} -> {p.NewThicknessMm:0} mm ({type.Name})"; }
                                    }
                                }
                                var lc = (LocationCurve)wall.Location;
                                double z = lc.Curve.GetEndPoint(0).Z;
                                var line = Line.CreateBound(new XYZ(p.NewLine[0] / Mm, p.NewLine[1] / Mm, z), new XYZ(p.NewLine[2] / Mm, p.NewLine[3] / Mm, z));
                                if (Math.Max(Math.Abs(p.ShiftStartMm), Math.Abs(p.ShiftEndMm)) > 1)
                                    notes.AddRange(BuildTools.SetWallLineSafely(doc, wall, line, "MCP: fit wall " + p.Id, changeType));
                                else notes.AddRange(BuildTools.Tx.Run(doc, "MCP: fit wall " + p.Id, changeType));
                                if (!wall.IsValidObject) failed.Add(new { id = p.Id, problem = "Revit removed the wall" });
                                else done.Add(new { id = p.Id, type = typeChange, shiftMm = new[] { p.ShiftStartMm, p.ShiftEndMm } });
                            }
                            catch (Exception ex) { failed.Add(new { id = p.Id, problem = McpServer.Unwrap(ex).Message }); }
                        }
                        if (done.Count == 0) g.RollBack(); else g.Assimilate();
                    }
                    int wallsAfter = CountWalls(doc, level);
                    return new
                    {
                        level = level.Name, fixedCount = done.Count, failedCount = failed.Count,
                        notChosen = proposals.Count - chosen.Count,
                        wallsOnFloor = new { before = wallsBefore, after = wallsAfter, warning = wallsAfter < wallsBefore ? "Walls were lost: undo this step in Revit (Ctrl+Z) and report it." : null },
                        @fixed = done.Take(100).ToList(), failed = failed.Take(100).ToList(),
                        revitMessages = notes.Distinct().Take(30).ToList(),
                        next = "dwg_wall_fit again: the count should drop. Review the rest one by one.",
                        undo = done.Count > 0 ? "One undo step in Revit." : null,
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "dwg_read",
                Description = "Read a DWG file directly (not through Revit): its TEXT, MTEXT and block attributes (spot heights, window sill and head labels such as \"S 0.94\" / \"H 3.27\", room names), and/or its lines, near a place, by layer or by words. Works on drawings that are not linked (elevations, sections: their own coordinates, in mm, so elevation heights read as drawn). For a linked plan (import_id/import_name) results are in model mm. Changes nothing.",
                InputSchema = Schema(Import.With(
                    ("path", S("string", "A DWG or DXF file (instead of a link)"), false),
                    ("what", S("string", "text (default), lines or both"), false),
                    ("layers", Arr("string", "Layer globs"), false),
                    ("contains", S("string", "Only text containing this (case-insensitive)"), false),
                    ("near", Arr("number", "[x, y]: only near this point (mm)"), false),
                    ("radius_mm", S("number", "How near (default 2000)"), false),
                    ("box", Arr("number", "Or [x1, y1, x2, y2] (mm)"), false),
                    ("offset", S("integer", "Skip this many"), false), ("limit", S("integer", "At most this many (default 150)"), false))),
                Run = (app, a) =>
                {
                    string path = Args.Str(a, "path");
                    Func<Pt, Pt> map = p => p;
                    string coords = "the drawing's own coordinates, mm", note = null;
                    object link = null;
                    if (path == null)
                    {
                        var doc = Doc(app);
                        var imp = CadLink.Pick(doc, a);
                        link = CadLink.Describe(doc, imp);
                        path = CadLink.FilePath(doc, imp) ?? throw new InvalidOperationException("Revit does not know that link's file (an import, or the file has moved). Give its path instead.");
                        var file = DwgFile.Read(path);
                        var m = CadLink.FileToModel(doc, imp, file, out note);
                        if (m != null) { map = m; coords = "model (internal) coordinates, mm"; }
                    }
                    var content = DwgFile.Read(path);
                    string what = (Args.Str(a, "what") ?? "text").ToLowerInvariant();
                    var layers = Args.Strings(a, "layers");
                    var near = Args.Numbers(a, "near");
                    double radius = Args.Dbl(a, "radius_mm") ?? 2000;
                    var box = Args.Numbers(a, "box");
                    bool Keep(Pt p) =>
                        (near.Count < 2 || Math.Sqrt((p.X - near[0]) * (p.X - near[0]) + (p.Y - near[1]) * (p.Y - near[1])) <= radius) &&
                        (box.Count < 4 || (p.X >= Math.Min(box[0], box[2]) && p.X <= Math.Max(box[0], box[2]) && p.Y >= Math.Min(box[1], box[3]) && p.Y <= Math.Max(box[1], box[3])));
                    double Dist(Pt p) => near.Count < 2 ? 0 : Math.Sqrt((p.X - near[0]) * (p.X - near[0]) + (p.Y - near[1]) * (p.Y - near[1]));
                    int limit = Math.Clamp(Args.Int(a, "limit", 150), 1, 2000), offset = Math.Max(0, Args.Int(a, "offset", 0));
                    string contains = Args.Str(a, "contains");

                    object texts = null, lines = null; int textTotal = 0, lineTotal = 0;
                    if (what == "text" || what == "both")
                    {
                        var list = content.Texts.Where(t => DwgFile.LayerMatches(t.Layer, layers) && (contains == null || t.Value.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0))
                            .Select(t => (t, at: map(t.At))).Where(x => Keep(x.at)).OrderBy(x => Dist(x.at)).ToList();
                        textTotal = list.Count;
                        texts = list.Skip(offset).Take(limit).Select(x => new { text = x.t.Value, at = new[] { Math.Round(x.at.X), Math.Round(x.at.Y), Math.Round(x.at.Z) }, layer = x.t.Layer, heightMm = x.t.HeightMm, tag = x.t.Tag }).ToList();
                    }
                    if (what == "lines" || what == "both")
                    {
                        var list = content.Lines.Where(l => DwgFile.LayerMatches(l.Layer, layers))
                            .Select(l => (l, a: map(l.A), b: map(l.B))).Where(x => Keep(x.a) || Keep(x.b)).OrderBy(x => Math.Min(Dist(x.a), Dist(x.b))).ToList();
                        lineTotal = list.Count;
                        lines = list.Skip(offset).Take(limit).GroupBy(x => x.l.Layer).ToDictionary(g => g.Key, g => g.Select(x => new[] { Math.Round(x.a.X), Math.Round(x.a.Y), Math.Round(x.b.X), Math.Round(x.b.Y) }).ToList());
                    }
                    return new
                    {
                        file = path, link, units = content.UnitsNote, coordinates = coords, match = note,
                        texts, textTotal, lines, lineTotal,
                        layers = layers.Count == 0 ? content.LayerCounts.OrderByDescending(k => k.Value).Take(40).ToDictionary(k => k.Key, k => k.Value) : null,
                    };
                }
            };
        }

        /// <summary>1.5.1: elevations onto facades, from a survey sheet that is not linked.</summary>
        public static IEnumerable<RevitTool> Facade()
        {
            yield return new RevitTool
            {
                Name = "dwg_elevation",
                Description = "Put one elevation from a survey sheet onto its facade in the model, and read the facade from it. Heights come from the sheet's level marks; the position along the facade from matching the elevation's window jambs to the model's windows on that facade. Returns each model window on the facade with its width, sill and head as the elevation draws them against the model's (mm above its level), openings the elevation draws that the model lacks, and optionally the elevation's lines as 3D model points on the facade plane (for model_sweep / model_extrusion: cornices, string courses, parapets, surrounds). Changes nothing.",
                InputSchema = Schema(
                    ("path", S("string", "The elevation sheet (DWG or DXF)"), true),
                    ("sheet_x_range", Arr("number", "[x1, x2]: the part of the sheet this elevation occupies, sheet mm (dwg_read with contains \"Elevation\" finds the titles)"), true),
                    ("sheet_y_range", Arr("number", "Optional [y1, y2] to leave out other elevations stacked above or below"), false),
                    ("facade_line", Arr("number", "[x1, y1, x2, y2] the facade in plan, model mm"), false),
                    ("wall_ids", Arr("integer", "Or the facade's walls: their line is taken"), false),
                    ("band_mm", S("number", "How far from the facade line a window may be to belong to it (default 800)"), false),
                    ("along_direction", S("integer", "1 if the elevation's left-to-right runs from the facade line's start to its end, -1 if the other way; found from the windows when left out"), false),
                    ("window_layers", Arr("string", "Window layer globs (default *Window*)"), false),
                    ("level_layers", Arr("string", "Level mark layer globs (default *Level*)"), false),
                    ("include_lines", S("boolean", "Also return the elevation's lines on the facade plane"), false),
                    ("line_layers", Arr("string", "Which layers' lines (default all but text, level and frame layers)"), false),
                    ("z_range_mm", Arr("number", "Only lines between these model heights [z1, z2]"), false),
                    ("limit", S("integer", "At most this many lines and windows (default 200)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    string path = Args.Str(a, "path") ?? throw new ArgumentException("path is needed.");
                    var xr = Args.Numbers(a, "sheet_x_range");
                    if (xr.Count != 2) throw new ArgumentException("sheet_x_range is [x1, x2] in sheet mm.");
                    double xMin = Math.Min(xr[0], xr[1]), xMax = Math.Max(xr[0], xr[1]);
                    var yr = Args.Numbers(a, "sheet_y_range");
                    double yMin = yr.Count == 2 ? Math.Min(yr[0], yr[1]) : double.MinValue, yMax = yr.Count == 2 ? Math.Max(yr[0], yr[1]) : double.MaxValue;
                    bool InSheet(Pt p) => p.X >= xMin && p.X <= xMax && p.Y >= yMin && p.Y <= yMax;

                    var (fa, fb) = FacadeLine(doc, a);
                    double fl = fa.Dist2D(fb);
                    double ux = (fb.X - fa.X) / fl, uy = (fb.Y - fa.Y) / fl;

                    var file = DwgFile.Read(path);
                    var winLayers = Args.Strings(a, "window_layers"); if (winLayers.Count == 0) winLayers = new List<string> { "*Window*" };
                    var lvlLayers = Args.Strings(a, "level_layers"); if (lvlLayers.Count == 0) lvlLayers = new List<string> { "*Level*" };
                    var lines = file.Lines.Where(l => InSheet(l.A) && InSheet(l.B)).ToList();
                    var winLines = lines.Where(l => DwgFile.LayerMatches(l.Layer, winLayers)).ToList();
                    var heights = ElevationFit.Heights(file.Texts.Where(t => ElevationFit.IsLevelText(t, DwgFile.LayerMatches(t.Layer, lvlLayers)) && t.At.Y >= yMin && t.At.Y <= yMax),
                                                       lines.Where(l => ElevationFit.IsLevelLine(l, DwgFile.LayerMatches(l.Layer, lvlLayers), file.Texts)), xMin, xMax);
                    // heights above datum -> model heights: the shared elevation at the internal origin is the datum's offset
                    double datumMm = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).Elevation * Mm;
                    double ModelZ(double sheetY) => heights.Height(sheetY) - datumMm;

                    // the model's windows and doors on this facade, as distances along it
                    double band = Args.Dbl(a, "band_mm") ?? 800;
                    var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                    var model = new List<(FamilyInstance fi, double u, double w, double? sill, double? head, Level level)>();
                    foreach (var cat in new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors })
                        foreach (var fi in new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().OfType<FamilyInstance>())
                        {
                            if (!(fi.Location is LocationPoint lp)) continue;
                            var p = CadLink.ToMm(lp.Point);
                            double u = (p.X - fa.X) * ux + (p.Y - fa.Y) * uy, off = Math.Abs((p.X - fa.X) * -uy + (p.Y - fa.Y) * ux);
                            if (off > band || u < -500 || u > fl + 500) continue;
                            double w = WidthOf(fi);
                            if (w <= 0) continue;
                            model.Add((fi, u, w, LenOf(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM), LenOf(fi, BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM), doc.GetElement(fi.LevelId) as Level));
                        }
                    var inserts = model.Select(m => new FacadeInsert
                    {
                        Id = m.fi.Id.Value, Category = m.fi.Category?.Name, Level = m.level?.Name, U = m.u, W = m.w,
                        LevelZ = (m.level?.Elevation ?? 0) * Mm, Sill = m.sill, Head = m.head,
                    }).ToList();
                    var modelJambs = inserts.SelectMany(m => new[] { m.U - m.W / 2, m.U + m.W / 2 }).ToList();
                    int? dirGiven = Args.Dbl(a, "along_direction") is double dg ? (dg < 0 ? -1 : 1) : (int?)null;
                    var along = ElevationFit.Along(ElevationFit.Jambs(winLines), modelJambs, heights.A, 60, dirGiven);

                    int limit = Math.Clamp(Args.Int(a, "limit", 200), 1, 2000);
                    // storeys: the levels that hold walls (leaves out spare levels such as a template's "Level 1")
                    var wallLevels = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(Wall)).Select(e => e.LevelId.Value));
                    var storeys = levels.Where(lv => wallLevels.Contains(lv.Id.Value)).ToList();
                    if (storeys.Count == 0) storeys = levels;
                    var revealLines = lines.Where(l => DwgFile.LayerMatches(l.Layer, new[] { "*Wall*" })).ToList();
                    var (matched, notDrawn, missingOps) = ElevationFit.MatchFacade(winLines, revealLines, inserts, heights, along, ModelZ, fl,
                        storeys.Select(s => (s.Name, s.Elevation * Mm)).ToList());
                    var windows = new List<object>();
                    foreach (var e in matched)
                    {
                        var m = e.Insert;
                        windows.Add(new
                        {
                            id = m.Id, category = m.Category, level = m.Level, alongMm = Math.Round(m.U),
                            widthMm = Math.Round(m.W), elevationWidthMm = Math.Round(e.Width), widthFrom = e.FromReveal ? "masonry opening" : "frame",
                            jambOffsetMm = Math.Round(Math.Max(Math.Abs(e.U0 - (m.U - m.W / 2)), Math.Abs(e.U1 - (m.U + m.W / 2)))),
                            sillMm = Rd(m.Sill), elevationSillMm = Math.Round(e.Sill),
                            headMm = Rd(m.Head), elevationHeadMm = Math.Round(e.Head),
                            differs = Math.Abs(e.Width - m.W) > 30 || (m.Sill is double s && Math.Abs(e.Sill - s) > 30) || (m.Head is double h && Math.Abs(e.Head - h) > 30),
                        });
                    }
                    foreach (var m in notDrawn)
                        windows.Add(new { id = m.Id, category = m.Category, level = m.Level, alongMm = Math.Round(m.U), widthMm = Math.Round(m.W), sillMm = Rd(m.Sill), headMm = Rd(m.Head), elevation = "nothing drawn there" });
                    var missing = missingOps.Take(limit).Select(x => new
                    {
                        alongMm = Math.Round(x.U), widthMm = Math.Round(x.W), level = x.Level, sillMm = Rd(x.Sill), headMm = Rd(x.Head),
                        at = new[] { Math.Round(fa.X + ux * x.U), Math.Round(fa.Y + uy * x.U) },
                    }).ToList();

                    object outLines = null; int lineTotal = 0;
                    if (Args.Bool(a, "include_lines") == true)
                    {
                        var layers = Args.Strings(a, "line_layers");
                        var zr = Args.Numbers(a, "z_range_mm");
                        var chosen = lines.Where(l => layers.Count > 0 ? DwgFile.LayerMatches(l.Layer, layers)
                                                                       : !DwgFile.LayerMatches(l.Layer, new[] { "Zz_*", "*Text*", "*Level*", "*Frame*", "*Border*", "Defpoints" }))
                            .Select(l => (l, z0: ModelZ(l.A.Y), z1: ModelZ(l.B.Y), u0: along.Along(l.A.X * heights.A), u1: along.Along(l.B.X * heights.A)))
                            .Where(x => zr.Count != 2 || (Math.Max(x.z0, x.z1) >= Math.Min(zr[0], zr[1]) && Math.Min(x.z0, x.z1) <= Math.Max(zr[0], zr[1])))
                            .ToList();
                        lineTotal = chosen.Count;
                        outLines = chosen.Take(limit).GroupBy(x => x.l.Layer).ToDictionary(g => g.Key, g => g.Select(x => new[]
                        {
                            Math.Round(fa.X + ux * x.u0), Math.Round(fa.Y + uy * x.u0), Math.Round(x.z0),
                            Math.Round(fa.X + ux * x.u1), Math.Round(fa.Y + uy * x.u1), Math.Round(x.z1),
                        }).ToList());
                    }

                    return new
                    {
                        file = path, units = file.UnitsNote,
                        facade = new { line = new[] { Math.Round(fa.X), Math.Round(fa.Y), Math.Round(fb.X), Math.Round(fb.Y) }, lengthMm = Math.Round(fl) },
                        heights = new { heights.Note, scale = Math.Round(heights.A, 5), datumAtModelZeroMm = Math.Round(datumMm) },
                        along = new { along.Note, direction = along.Dir, offsetMm = Math.Round(along.Offset) },
                        trust = along.Ambiguous ? "direction unknown (symmetrical facade): heights are good; give along_direction for positions"
                              : along.Matched >= Math.Max(4, model.Count) ? "good" : along.Matched >= 4 ? "partial: check a few against the drawing" : "poor: check sheet_x_range and the facade line",
                        windows = windows.Take(limit).ToList(), windowsOnFacade = model.Count,
                        differing = windows.Count(w => (bool)(w.GetType().GetProperty("differs")?.GetValue(w) ?? false)),
                        inElevationNotInModel = missing,
                        lines = outLines, lineTotal,
                        units2 = "Heights (sill, head) are mm above each window's level. Lines are [x1, y1, z1, x2, y2, z2] in model mm on the facade line.",
                    };
                }
            };
        }

        private static int CountWalls(Document doc, Level level) =>
            new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Count(w => w.LevelId == level.Id);

        private static double? Rd(double? v) => v == null ? (double?)null : Math.Round(v.Value);

        private static double WidthOf(FamilyInstance fi)
        {
            foreach (var e in new Element[] { fi, fi.Symbol })
                foreach (var p in new[] { BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM })
                    if (e?.get_Parameter(p) is Parameter par && par.HasValue && par.AsDouble() > 0) return par.AsDouble() * Mm;
            return 0;
        }

        private static double? LenOf(Element e, BuiltInParameter p) => e.get_Parameter(p) is Parameter par && par.HasValue ? par.AsDouble() * Mm : (double?)null;

        private static (Pt a, Pt b) FacadeLine(Document doc, JsonObject a)
        {
            if (Args.Str(a, "facade") is string fid) return SavedFacade(doc, fid);
            var line = Args.Numbers(a, "facade_line");
            if (line.Count == 4) return (new Pt(line[0], line[1]), new Pt(line[2], line[3]));
            var ids = Args.Numbers(a, "wall_ids");
            var walls = ids.Select(id => doc.GetElement(new ElementId((long)id)) as Wall).Where(w => w?.Location is LocationCurve lc && lc.Curve is Line).ToList();
            if (walls.Count == 0) throw new ArgumentException("Give facade_line [x1, y1, x2, y2] (model mm) or wall_ids of the facade's walls.");
            var first = ((LocationCurve)walls[0].Location).Curve;
            Pt p0 = CadLink.ToMm(first.GetEndPoint(0)), p1 = CadLink.ToMm(first.GetEndPoint(1));
            double l = p0.Dist2D(p1), ux = (p1.X - p0.X) / l, uy = (p1.Y - p0.Y) / l;
            var ends = walls.SelectMany(w => { var c = ((LocationCurve)w.Location).Curve; return new[] { CadLink.ToMm(c.GetEndPoint(0)), CadLink.ToMm(c.GetEndPoint(1)) }; }).ToList();
            double lo = ends.Min(e => (e.X - p0.X) * ux + (e.Y - p0.Y) * uy), hi = ends.Max(e => (e.X - p0.X) * ux + (e.Y - p0.Y) * uy);
            return (new Pt(p0.X + ux * lo, p0.Y + uy * lo), new Pt(p0.X + ux * hi, p0.Y + uy * hi));
        }

        // ---------- wall fit inputs ----------

        private static JsonObject FitSchema(bool apply)
        {
            var props = new List<(string, JsonObject, bool)>
            {
                ("level", S("string", "The floor (level name)"), true),
                ("import_id", S("integer", "The floor's plan DWG link; found from the floor's plan view when left out"), false),
                ("import_name", S("string", "Or part of the link's name"), false),
                ("layers", Arr("string", "Wall-face layer globs (default: *wall*, *partition*, *PartGlass*, EF_25*, Ss_25*, *plinth*)"), false),
                ("min_thickness_mm", S("number", "Walls thinner than this are left alone (default 60)"), false),
                ("max_thickness_mm", S("number", "Thickest wall the DWG may give (default 1300 when fitting, 1000 for missing walls: wider pairs are spaces)"), false),
                ("min_length_mm", S("number", "Walls shorter than this are left alone (default 600)"), false),
                ("search_mm", S("number", "How far a DWG face may be from the wall's centre line (default 700)"), false),
                ("min_cover", S("number", "Share of the wall's length each face must cover (default 0.6)"), false),
                ("min_change_mm", S("number", "Smaller changes are ignored (default 30)"), false),
            };
            if (apply)
            {
                props.Add(("ids", Arr("integer", "Only these walls"), false));
                props.Add(("confidence", S("string", "moderate (default) or all"), false));
                props.Add(("max_changes", S("integer", "At most this many walls (default 200)"), false));
            }
            else
            {
                props.Add(("include_missing", S("boolean", "Also list walls the DWG has and the model lacks (default true)"), false));
                props.Add(("limit", S("integer", "At most this many of each (default 60)"), false));
            }
            return Schema(props.ToArray());
        }

        private static FitOptions FitOptionsFrom(JsonObject a) => new FitOptions
        {
            MinThicknessMm = Args.Dbl(a, "min_thickness_mm") ?? 60,
            MaxThicknessMm = Args.Dbl(a, "max_thickness_mm") ?? 1300,
            MinLengthMm = Args.Dbl(a, "min_length_mm") ?? 600,
            SearchMm = Args.Dbl(a, "search_mm") ?? 700,
            MinCover = Args.Dbl(a, "min_cover") ?? 0.6,
            MinChangeMm = Args.Dbl(a, "min_change_mm") ?? 30,
        };

        private static (Level level, ImportInstance imp, List<FitWall> walls, List<Seg> segs) FitInputs(Document doc, JsonObject a)
        {
            string name = Args.Str(a, "level") ?? throw new ArgumentException("level is needed.");
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var level = levels.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"There is no level \"{name}\". Levels: {string.Join(", ", levels.OrderBy(l => l.Elevation).Select(l => l.Name))}.");

            ImportInstance imp;
            if (Args.Long(a, "import_id") != null || Args.Str(a, "import_name") != null) imp = CadLink.Pick(doc, a);
            else
            {
                // the plan DWG linked into this floor's plan view (Survey to Revit links each plan that way)
                var onLevel = CadLink.All(doc).Where(i => i.ViewSpecific && doc.GetElement(i.OwnerViewId) is ViewPlan vp && vp.GenLevel?.Id == level.Id).ToList();
                if (onLevel.Count == 1) imp = onLevel[0];
                else if (onLevel.Count > 1) throw new ArgumentException("Several DWGs are linked into that floor's plans: give import_id. They are: " + string.Join("; ", onLevel.Select(i => i.Id.Value + " = " + CadLink.NameOf(doc, i))));
                else throw new ArgumentException("No DWG is linked into a plan of that floor: give import_id (list_cad shows the links).");
            }

            var layers = Args.Strings(a, "layers");
            if (layers.Count == 0) layers = DefaultWallLayers.ToList();
            var segs = CadLink.Segments(CadLink.Read(doc, imp, new ReadStats()), layers);
            if (segs.Count == 0) throw new InvalidOperationException("The link has no lines on those layers. Check the layer names with dwg_analyze, and pass layers.");

            var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
                .Where(w => w.LevelId == level.Id && w.WallType.Kind == WallKind.Basic && w.Location is LocationCurve lc && lc.Curve is Line)
                .Select(w =>
                {
                    var c = ((LocationCurve)w.Location).Curve;
                    return new FitWall { Id = w.Id.Value, A = CadLink.ToMm(c.GetEndPoint(0)), B = CadLink.ToMm(c.GetEndPoint(1)), ThicknessMm = w.Width * Mm, Type = w.WallType.Name };
                }).ToList();
            return (level, imp, walls, segs);
        }

        // ---------- coordinates ----------

        private static object Coordinates(Document doc, List<string> warnings, string mode)
        {
            var points = new FilteredElementCollector(doc).OfClass(typeof(BasePoint)).Cast<BasePoint>().Select(bp => new
            {
                kind = bp.IsShared ? "survey point" : "project base point",
                eastWestMm = Read(bp, BuiltInParameter.BASEPOINT_EASTWEST_PARAM),
                northSouthMm = Read(bp, BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM),
                elevationMm = Read(bp, BuiltInParameter.BASEPOINT_ELEVATION_PARAM),
                angleToTrueNorthDeg = bp.IsShared ? null : bp.get_Parameter(BuiltInParameter.BASEPOINT_ANGLETON_PARAM) is Parameter p ? Math.Round(p.AsDouble() * 180 / Math.PI, 4) : (double?)null,
                clipped = bp.Clipped,
            }).ToList();
            var loc = doc.ActiveProjectLocation;
            var pos = loc.GetProjectPosition(XYZ.Zero);
            return new
            {
                mode, activeLocation = loc.Name, basePoints = points,
                sharedPositionAtInternalOrigin = new { eastWestMm = Math.Round(pos.EastWest * Mm, 1), northSouthMm = Math.Round(pos.NorthSouth * Mm, 1), elevationMm = Math.Round(pos.Elevation * Mm, 1), angleDeg = Math.Round(pos.Angle * 180 / Math.PI, 4) },
                siteLatitudeDeg = Math.Round(doc.SiteLocation.Latitude * 180 / Math.PI, 6),
                siteLongitudeDeg = Math.Round(doc.SiteLocation.Longitude * 180 / Math.PI, 6),
                warnings,
                guidance = "Keep the model near the internal origin and let shared coordinates carry the survey position; precision suffers beyond about 4.8 km from the internal origin.",
                undo = mode == "report" ? null : "One undo step in Revit.",
            };
        }

        private static double? Read(Element e, BuiltInParameter bip) => e.get_Parameter(bip) is Parameter p ? Math.Round(p.AsDouble() * Mm, 1) : (double?)null;

        private static void SetLength(Element e, BuiltInParameter bip, double? mm, List<string> warnings, string label)
        {
            if (mm == null) return;
            var p = e.get_Parameter(bip);
            if (p == null || p.IsReadOnly) { warnings.Add(label + " is read-only (is the point clipped or pinned? Unclip it first)."); return; }
            p.Set(mm.Value / Mm);
        }

        // ---------- documents ----------

        private static Document Doc(UIApplication app) =>
            Reference.Current ?? app?.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No Revit project is open. Ask the user to open one.");

        private static Document Writable(UIApplication app)
        {
            var doc = Doc(app);
            if (doc.IsReadOnly) throw new InvalidOperationException("This project is read-only.");
            if (doc.IsModifiable) throw new InvalidOperationException("Another command is still changing the model. Ask the user to finish it in Revit, then try again.");
            return doc;
        }
    }
}
