// 1.12.0: speed tools (PLAN.md P1, P2, V2, V5). The org-side Claude was slow because it drove 10-15 tool calls per
// floor and read thousands of raw DWG coordinates. survey_run does a floor's whole score -> autofix -> score round
// in one call and answers with the remaining issues grouped by cause; dwg_image shows a piece of a DWG as a picture
// (a window on its elevation with the model's window drawn over it in red, a bay, a wall on the plan) so Claude
// looks before it reads coordinates.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        // ------------------------------------------------------------------ survey_run (P1)

        /// <summary>survey_run calls the hub's own tools (and Survey to Revit's through other) in order, on Revit's thread.</summary>
        public static RevitTool RunTool(IReadOnlyList<RevitTool> own, Func<object, string, JsonObject, string> other, JsonSerializerOptions json) => new RevitTool
        {
            Name = "survey_run",
            Write = true,
            Description = "The whole 1:1 round for a floor in ONE call, instead of driving each tool by hand: score it (survey_fidelity), autofix windows (and walls with walls: true; openings first so wall moves carry correct windows) with survey_autofix, score it again, and answer with what still fails GROUPED BY CAUSE (e.g. \"212 walls ~50 mm off: a single drawn line read as a wall's centre\") with sample ids and what to do about each cause. " +
                          "Optional: rebuild the floor first with Survey to Revit (rebuild: true, replaces what the build made there), save at the end (save: true). Appends the numbers to score-history.csv and writes the full detail to a run-*.json in the reports folder. " +
                          "Run facade_list + elevation_index once before it, or window heights are judged against the inside labels. Each floor's fixes are undo steps in Revit (one per autofix half). Long: it comes back as a job (job_status). dry_run: the fixes are tried and rolled back.",
            InputSchema = Schema(
                ("levels", Arr("string", "Floors to run, lowest first (default: every floor that fails survey_fidelity)"), false),
                ("autofix", S("boolean", "Run survey_autofix between the scores (default true; false = score and group causes only)"), false),
                ("walls", S("boolean", "Also autofix walls (default FALSE for now: the wall autofix crashed Revit on 2026-11-12, see COORDINATION.md; windows are always fixed)"), false),
                ("rebuild", S("boolean", "Rebuild each floor with Survey to Revit's survey_build first (default false: ask the user before)"), false),
                ("dry_run", S("boolean", "Autofix tried and rolled back (default false)"), false),
                ("save", S("boolean", "save_model at the end (default false; never with dry_run)"), false),
                ("facades", S("boolean", "Also run facade_check on every indexed facade at the end and include its scores (default false)"), false),
                ("max_causes", S("integer", "Causes to return per floor (default 8)"), false),
                ("source", S("string", "As survey_fidelity"), false),
                ("survey_folder", S("string", "As survey_fidelity"), false)),
            Run = (app, a) =>
            {
                var uiapp = app;
                var doc = Writable(uiapp);
                bool dry = Args.Bool(a, "dry_run") == true, fix = Args.Bool(a, "autofix") != false, rebuild = Args.Bool(a, "rebuild") == true, walls = Args.Bool(a, "walls") == true;
                int maxCauses = Math.Clamp(Args.Int(a, "max_causes", 8), 1, 30);
                JsonObject Pass(JsonObject extra)
                {
                    foreach (var k in new[] { "source", "survey_folder" }) if (a[k] != null) extra[k] = a[k].DeepClone();
                    return extra;
                }
                JsonNode Call(string tool, JsonObject args)
                {
                    var t = own.FirstOrDefault(x => x.Name == tool) ?? throw new InvalidOperationException($"{tool} is not loaded in this hub.");
                    object r = t.Run(uiapp, args);
                    string text = r is ToolContent tc ? tc.Text : r as string ?? JsonSerializer.Serialize(r, json);
                    return JsonNode.Parse(text);
                }

                var warnings = new List<string>();
                if (fix && !walls) warnings.Add("Walls were not autofixed (walls: false, the default while the wall-fix crash is open). Wall causes below are still listed.");
                string reports = ReportsFolder(doc);
                if (!File.Exists(Path.Combine(reports, "elevation-index.json")))
                    warnings.Add("No elevation-index.json: window sills/heads are judged against the plan's inside labels. Run facade_list, then elevation_index with the elevation sheets, then survey_run again.");

                var levels = Args.Strings(a, "levels");
                if (levels.Count == 0)
                {
                    var all = Call("survey_fidelity", Pass(new JsonObject { ["write_report"] = false, ["max_issues"] = 0 }));
                    levels = (all["floors"] as JsonArray)?.OfType<JsonObject>().Where(f => f["pass"]?.GetValue<bool>() != true).Select(f => f["level"]?.ToString()).Where(n => n != null).ToList() ?? new List<string>();
                    if (levels.Count == 0) return new { floors = Array.Empty<object>(), note = "Every floor with a plan already passes survey_fidelity.", warnings };
                }

                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string model = string.IsNullOrEmpty(doc.PathName) ? doc.Title : Path.GetFileName(doc.PathName);
                var floors = new List<object>();
                var detail = new JsonObject { ["model"] = model, ["when"] = stamp, ["version"] = Hub.Version, ["dryRun"] = dry };
                var detailFloors = new JsonArray();
                foreach (string level in levels)
                {
                    var one = new JsonArray(level);
                    var fd = new JsonObject { ["level"] = level };
                    try
                    {
                        if (rebuild)
                        {
                            string built = other(uiapp, "survey_build", new JsonObject { ["levels"] = one.DeepClone(), ["confirm"] = !dry })
                                           ?? throw new InvalidOperationException("rebuild needs Survey to Revit's survey_build, which is not loaded.");
                            fd["rebuild"] = Parse(built);
                        }
                        var before = FloorOf(Call("survey_fidelity", Pass(new JsonObject { ["levels"] = one.DeepClone(), ["write_report"] = false, ["max_issues"] = 0 })), level);
                        fd["before"] = before?.DeepClone();
                        JsonObject openingsFix = null, wallsFix = null;
                        if (fix)
                        {
                            openingsFix = FloorOf(Call("survey_autofix", Pass(new JsonObject { ["levels"] = one.DeepClone(), ["walls"] = false, ["dry_run"] = dry, ["max_issues"] = 0 })), level);
                            if (walls) wallsFix = FloorOf(Call("survey_autofix", Pass(new JsonObject { ["levels"] = one.DeepClone(), ["openings"] = false, ["dry_run"] = dry, ["max_issues"] = 0 })), level);
                            fd["autofixOpenings"] = openingsFix?.DeepClone(); fd["autofixWalls"] = wallsFix?.DeepClone();
                        }
                        var afterAll = Call("survey_fidelity", Pass(new JsonObject { ["levels"] = one.DeepClone(), ["write_report"] = true, ["max_issues"] = 0 }));
                        var after = FloorOf(afterAll, level);
                        fd["after"] = after?.DeepClone();
                        string reportJson = afterAll["report"]?["json"]?.ToString();
                        var issues = ReadIssues(reportJson).Where(i => i.Level == null || i.Level.Equals(level, StringComparison.OrdinalIgnoreCase)).ToList();
                        var causes = IssueCauses.Group(issues);
                        fd["causes"] = SurveyConfig.ToJson(causes);
                        detailFloors.Add(fd);

                        double? Num(JsonObject f, params string[] path) { JsonNode n = f; foreach (var p in path) n = (n as JsonObject)?[p]; return n is JsonValue v && v.TryGetValue(out double d) ? d : (double?)null; }
                        if (!dry && after != null)
                            AppendHistory(reports, model, level, new (string, double?)[]
                            {
                                ("score", Num(after, "score")), ("wallsWithinTolPct", Num(after, "walls", "withinTolPct")), ("wallsMissing", Num(after, "walls", "missing")),
                                ("openingsMissing", Num(after, "openings", "missing")), ("levelDevMm", Num(after, "levelCheck", "devMm")), ("issues", Num(after, "issues")),
                                ("pass", after["pass"]?.GetValue<bool>() == true ? 1 : 0),
                            });
                        floors.Add(new
                        {
                            level,
                            pass = after?["pass"]?.GetValue<bool>(), failedOn = after?["failedOn"]?.DeepClone(),
                            score = new { before = Num(before, "score"), after = Num(after, "score") },
                            wallsWithinTolPct = new { before = Num(before, "walls", "withinTolPct"), after = Num(after, "walls", "withinTolPct") },
                            autofix = !fix ? null : new
                            {
                                windowsReshaped = Num(openingsFix, "openings", "reshaped"), windowsPlaced = Num(openingsFix, "openings", "placed"), gapsFilled = Num(openingsFix, "openings", "gapsFilled"),
                                wallsFitted = Num(wallsFix, "walls", "fitted"), refused = (Num(openingsFix, "openings", "failed") ?? 0) + (Num(wallsFix, "walls", "failed") ?? 0),
                                forReview = (Num(openingsFix, "forReviewTotal") ?? 0) + (Num(wallsFix, "forReviewTotal") ?? 0),
                            },
                            issuesLeft = issues.Count,
                            causes = causes.Take(maxCauses).Select(c => new { c.Count, c.Cause, medianMm = c.MedianMm, systematic = c.Systematic ? true : (bool?)null, ids = c.Ids, at = c.At, todo = c.Action }).ToList(),
                            moreCauses = causes.Count > maxCauses ? causes.Count - maxCauses : (int?)null,
                        });
                    }
                    catch (Exception ex)
                    {
                        string why = McpServer.Unwrap(ex).Message;
                        fd["error"] = why; detailFloors.Add(fd);
                        floors.Add(new { level, error = why });
                    }
                }
                detail["floors"] = detailFloors;
                string runFile = Path.Combine(reports, $"run-{stamp}.json");
                File.WriteAllText(runFile, detail.ToJsonString(new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }));
                object facadeScores = null;
                if (Args.Bool(a, "facades") == true)
                    try
                    {
                        var fc = Call("facade_check", new JsonObject { ["facade"] = "all", ["issues"] = 5 });
                        facadeScores = fc;
                        detail["facades"] = fc?.DeepClone();
                        File.WriteAllText(runFile, detail.ToJsonString(new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }));
                    }
                    catch (Exception ex) { warnings.Add("facade_check: " + McpServer.Unwrap(ex).Message); }
                object saved = null;
                if (Args.Bool(a, "save") == true && !dry)
                    try { saved = Call("save_model", new JsonObject()); } catch (Exception ex) { warnings.Add("Not saved: " + McpServer.Unwrap(ex).Message); }
                return new
                {
                    dryRun = dry, floors, facades = facadeScores, warnings = warnings.Count > 0 ? warnings : null,
                    detail = runFile, history = Path.Combine(reports, "score-history.csv"), saved,
                    next = "Work the causes top down: a systematic cause is one fix (a reading/layer or build rule), not many hand edits. dwg_image with element_id or near=at to look at one. Then survey_run that floor again.",
                    undo = dry ? "dry run: nothing kept" : "Each autofix half is one undo step per floor in Revit.",
                };
            }
        };

        private static JsonObject FloorOf(JsonNode result, string level) =>
            (result?["floors"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(f => string.Equals(f["level"]?.ToString(), level, StringComparison.OrdinalIgnoreCase));

        private static JsonNode Parse(string s) { try { return JsonNode.Parse(s); } catch { return JsonValue.Create(s); } }

        /// <summary>Every issue of a fidelity report JSON (its keys in any case).</summary>
        private static List<FidIssue> ReadIssues(string reportJson)
        {
            if (reportJson == null || !File.Exists(reportJson)) return new List<FidIssue>();
            var root = JsonNode.Parse(File.ReadAllText(reportJson)) as JsonObject;
            var arr = (root?["issues"] ?? root?["Issues"]) as JsonArray;
            return arr == null ? new List<FidIssue>() : arr.Deserialize<List<FidIssue>>(ConfigJson.Read) ?? new List<FidIssue>();
        }

        private static void AppendHistory(string folder, string model, string level, IEnumerable<(string metric, double? value)> values)
        {
            try
            {
                string file = Path.Combine(folder, "score-history.csv");
                string rows = IssueCauses.HistoryRows(DateTime.Now.ToString("yyyy-MM-dd HH:mm"), model, Hub.Version, level,
                    values.Where(v => v.value != null).Select(v => (v.metric, v.value.Value)));
                File.AppendAllText(file, (File.Exists(file) ? "" : IssueCauses.HistoryHeader) + rows);
            }
            catch { /* history is an extra */ }
        }

        // ------------------------------------------------------------------ dwg_image (P2)

        public static IEnumerable<RevitTool> Speed()
        {
            yield return new RevitTool
            {
                Name = "dwg_image",
                Description = "LOOK at part of a survey DWG as a picture instead of reading its coordinates: a window on its elevation, a bay of a facade, a cornice on a section, a wall on a plan. Lines black (highlight_layers blue), texts blue, rulers and a light grid so sizes can be read off. " +
                              "Ways to say where: element_id = a window/door: its facade's elevation (from elevation-index.json) round it, with the model's window drawn over it in red; facade = an F id: that elevation (along_mm / z_mm narrow it to a bay or a band; every model window on it drawn in red, rulers in metres along the facade and AOD); path + box or near = any DWG sheet in its own mm (sections, details); level (or import_id) + box or near = a linked plan in model mm (an issue's `at`). " +
                              "Also returns the texts in the picture with their positions and the mm per pixel. Changes nothing.",
            InputSchema = Schema(
                ("element_id", S("integer", "A window or door: its elevation round it, model window in red"), false),
                ("facade", S("string", "A facade id (F1...) from facade_list: its indexed elevation"), false),
                ("along_mm", Arr("number", "With facade: [from, to] mm along the facade (left to right seen from outside)"), false),
                ("z_mm", Arr("number", "With facade: [bottom, top] model height mm"), false),
                ("path", S("string", "A DWG/DXF sheet (sections, details, an elevation not indexed)"), false),
                ("level", S("string", "A floor: its linked plan, in model mm"), false),
                ("import_id", S("integer", "Or a CAD link by id (list_cad)"), false),
                ("box", Arr("number", "[x1, y1, x2, y2] mm: the area (sheet mm with path, model mm with level/import)"), false),
                ("near", Arr("number", "Or [x, y]: the centre of the area"), false),
                ("size_mm", S("number", "With near: the width/height of the area (default 4000)"), false),
                ("margin_mm", S("number", "With element_id: space round the window (default 800)"), false),
                ("highlight_layers", Arr("string", "Layer globs drawn in blue (e.g. *Window*)"), false),
                ("layers", Arr("string", "Only these layer globs"), false),
                ("width_px", S("integer", "Picture width (default 1200, at most 2400)"), false),
                ("model_overlay", S("boolean", "With element_id/facade: draw the model's windows and doors in red (default true)"), false)),
            Run = (app, a) =>
            {
                int width = Math.Clamp(Args.Int(a, "width_px", 1200), 300, 2400);
                var layers = Args.Strings(a, "layers");
                var hl = Args.Strings(a, "highlight_layers");
                var spec = new RasterSpec { WidthPx = width, MaxHeightPx = 2000, Highlight = hl.Count > 0 ? (Func<string, bool>)(l => DwgFile.LayerMatches(l, hl)) : null };
                var marks = new List<RasterMark>();
                IEnumerable<DwgLine> lines; IEnumerable<DwgText> texts;
                string mode, file, coords; var notes = new List<string>();
                Document doc = null;

                if (Args.Long(a, "element_id") is long eid || Args.Str(a, "facade") != null)
                {
                    doc = Doc(app);
                    FamilyInstance target = null;
                    JsonObject el = null;
                    if (Args.Long(a, "element_id") is long id)
                    {
                        target = doc.GetElement(new ElementId(id)) as FamilyInstance ?? throw new ArgumentException($"{id} is not a window or door.");
                        el = FindElevationFor(doc, target) ?? throw new ArgumentException($"No indexed elevation draws {id}'s facade: run facade_list and elevation_index, or use path + box on the elevation sheet.");
                    }
                    else
                    {
                        string fid = Args.Str(a, "facade");
                        el = ElevationSpecs(doc, null).FirstOrDefault(s => string.Equals(Args.Str(s, "facade"), fid, StringComparison.OrdinalIgnoreCase))
                             ?? throw new ArgumentException($"No elevation for {fid} in elevation-index.json: run elevation_index, or use path + box on the sheet.");
                    }
                    var geo = ElevationGeometry(doc, el);
                    file = Args.Str(el, "path");
                    var content = DwgFile.Read(file);
                    double A = geo.heights.A, B = geo.heights.B, datum = geo.datumMm;
                    // along the facade u = Dir * (A * x) + Offset (the sheet's scale is the height fit's A); model z = A * y + B - datum
                    double SheetX(double u) => geo.along.SheetX(u) / A;
                    double SheetY(double z) => (z + datum - B) / A;
                    double U(double x) => geo.along.Along(x * A);
                    bool trusted = geo.along.Matched >= 4;
                    if (!trusted) notes.Add($"The elevation is not well matched to the facade ({geo.along.Note}): the red model windows may be misplaced.");
                    var xr = Args.Numbers(el, "sheet_x_range"); var yr = Args.Numbers(el, "sheet_y_range");
                    double sx0 = Math.Min(xr[0], xr[1]), sx1 = Math.Max(xr[0], xr[1]);
                    double sy0 = yr.Count == 2 ? Math.Min(yr[0], yr[1]) : geo.lines.Count > 0 ? geo.lines.Min(l => Math.Min(l.A.Y, l.B.Y)) : 0;
                    double sy1 = yr.Count == 2 ? Math.Max(yr[0], yr[1]) : geo.lines.Count > 0 ? geo.lines.Max(l => Math.Max(l.A.Y, l.B.Y)) : 1;

                    // the model's windows and doors on this facade, in sheet mm
                    double fl = geo.length, ux = (geo.fb.X - geo.fa.X) / fl, uy = (geo.fb.Y - geo.fa.Y) / fl;
                    var inserts = new List<(long id, double u0, double u1, double z0, double z1)>();
                    foreach (var cat in new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors })
                        foreach (var fi in new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().OfType<FamilyInstance>())
                        {
                            if (!(fi.Location is LocationPoint lp)) continue;
                            var p = CadLink.ToMm(lp.Point);
                            double u = (p.X - geo.fa.X) * ux + (p.Y - geo.fa.Y) * uy, o = Math.Abs((p.X - geo.fa.X) * -uy + (p.Y - geo.fa.Y) * ux);
                            double w = WidthOf(fi);
                            if (o > 800 || u < -500 || u > fl + 500 || w <= 0) continue;
                            double lz = ((doc.GetElement(fi.LevelId) as Level)?.Elevation ?? 0) * Mm;
                            double sill = LenOf(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM) ?? 0, head = LenOf(fi, BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM) ?? sill + 1000;
                            inserts.Add((fi.Id.Value, u - w / 2, u + w / 2, lz + sill, lz + head));
                        }
                    if (Args.Bool(a, "model_overlay") != false && trusted)
                        foreach (var m in inserts)
                            if (target == null || m.id == target.Id.Value)
                                marks.Add(new RasterMark { X0 = SheetX(m.u0), X1 = SheetX(m.u1), Y0 = SheetY(m.z0), Y1 = SheetY(m.z1), Label = target == null ? m.id.ToString() : "model" });

                    double bx0 = sx0, bx1 = sx1, by0 = sy0, by1 = sy1;
                    if (target != null)
                    {
                        var m = inserts.FirstOrDefault(x => x.id == target.Id.Value);
                        if (m.id == 0) throw new ArgumentException($"{target.Id.Value} is not on facade {Args.Str(el, "facade")}'s line.");
                        double margin = Args.Dbl(a, "margin_mm") ?? 800;
                        bx0 = SheetX(m.u0 - margin); bx1 = SheetX(m.u1 + margin); by0 = SheetY(m.z0 - margin); by1 = SheetY(m.z1 + margin);
                        mode = $"window {target.Id.Value} on the elevation of {Args.Str(el, "facade")}";
                    }
                    else
                    {
                        var al = Args.Numbers(a, "along_mm"); var zz = Args.Numbers(a, "z_mm");
                        if (al.Count == 2) { bx0 = SheetX(al[0]); bx1 = SheetX(al[1]); }
                        if (zz.Count == 2) { by0 = SheetY(zz[0]); by1 = SheetY(zz[1]); }
                        mode = $"elevation of {Args.Str(el, "facade")}" + (al.Count == 2 || zz.Count == 2 ? " (part)" : "");
                    }
                    spec.X0 = Math.Min(bx0, bx1); spec.X1 = Math.Max(bx0, bx1); spec.Y0 = Math.Min(by0, by1); spec.Y1 = Math.Max(by0, by1);
                    spec.XLabel = x => (U(x) / 1000).ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture);
                    spec.YLabel = y => ((A * y + B) / 1000).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    spec.XTitle = "m along facade"; spec.YTitle = "AOD m";
                    lines = content.Lines.Where(l => DwgFile.LayerMatches(l.Layer, layers));
                    texts = content.Texts.Where(t => DwgFile.LayerMatches(t.Layer, layers));
                    coords = "sheet mm of the elevation; rulers in m along the facade and m AOD (from its level marks)";
                }
                else if (Args.Str(a, "path") is string path)
                {
                    file = path;
                    var content = DwgFile.Read(path);
                    lines = content.Lines.Where(l => DwgFile.LayerMatches(l.Layer, layers));
                    texts = content.Texts.Where(t => DwgFile.LayerMatches(t.Layer, layers));
                    SetBox(spec, a, lines);
                    mode = "sheet " + Path.GetFileName(path); coords = "the drawing's own mm";
                }
                else if (Args.Str(a, "level") != null || Args.Long(a, "import_id") != null)
                {
                    doc = Doc(app);
                    ImportInstance imp;
                    if (Args.Str(a, "level") is string ln)
                        imp = CadLink.All(doc).FirstOrDefault(i => i.ViewSpecific && doc.GetElement(i.OwnerViewId) is ViewPlan vp && vp.GenLevel != null && vp.GenLevel.Name.Equals(ln, StringComparison.OrdinalIgnoreCase))
                              ?? throw new ArgumentException($"No DWG is linked into a plan of \"{ln}\". list_cad shows the links.");
                    else imp = CadLink.Pick(doc, a);
                    file = CadLink.FilePath(doc, imp) ?? throw new InvalidOperationException("Revit does not know that link's file: give its path instead.");
                    var content = DwgFile.Read(file);
                    var map = CadLink.FileToModel(doc, imp, content, out var note) ?? throw new InvalidOperationException("Can't place the DWG in the model: " + note);
                    lines = content.Lines.Where(l => DwgFile.LayerMatches(l.Layer, layers)).Select(l => new DwgLine { A = map(l.A), B = map(l.B), Layer = l.Layer }).ToList();
                    texts = content.Texts.Where(t => DwgFile.LayerMatches(t.Layer, layers)).Select(t => new DwgText { At = map(t.At), Value = t.Value, Layer = t.Layer, HeightMm = t.HeightMm, Tag = t.Tag }).ToList();
                    SetBox(spec, a, lines);
                    var near = Args.Numbers(a, "near");
                    if (near.Count >= 2) { double s = (spec.X1 - spec.X0) * 0.015; marks.Add(new RasterMark { X0 = near[0] - s, X1 = near[0] + s, Y0 = near[1] - s, Y1 = near[1] + s }); }
                    mode = "plan " + CadLink.NameOf(doc, imp); coords = "model mm (x east, y north as the model's internal origin)";
                }
                else throw new ArgumentException("Say where: element_id, facade, path + box/near, or level + box/near.");

                var r = DwgRaster.Render(lines, texts, spec, marks);
                double bxa = spec.X0, bxb = spec.X1, bya = spec.Y0, byb = spec.Y1;
                var inside = texts.Where(t => t.At.X >= bxa && t.At.X <= bxb && t.At.Y >= bya && t.At.Y <= byb && !string.IsNullOrWhiteSpace(t.Value)).Take(60)
                    .Select(t => { var (px, py) = r.ToPx(t.At.X, t.At.Y); return new { text = t.Value, at = new[] { Math.Round(t.At.X), Math.Round(t.At.Y) }, px = new[] { px, py }, layer = t.Layer }; }).ToList();
                var layerCounts = lines.Where(l => Math.Max(l.A.X, l.B.X) >= bxa && Math.Min(l.A.X, l.B.X) <= bxb && Math.Max(l.A.Y, l.B.Y) >= bya && Math.Min(l.A.Y, l.B.Y) <= byb)
                    .GroupBy(l => l.Layer ?? "").OrderByDescending(g => g.Count()).Take(15).ToDictionary(g => g.Key, g => g.Count());

                string saved = null;
                try
                {
                    string folder = doc != null ? Path.Combine(ReportsFolder(doc), "views") : Path.Combine(Path.GetTempPath(), "BGMCP-dwg-images");
                    Directory.CreateDirectory(folder);
                    saved = Path.Combine(folder, $"dwg-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
                    File.WriteAllBytes(saved, r.Png);
                }
                catch { saved = null; }
                var c = new ToolContent
                {
                    Text = new JsonObject
                    {
                        ["shows"] = mode, ["file"] = file, ["coordinates"] = coords,
                        ["box"] = new JsonArray(Math.Round(bxa), Math.Round(bya), Math.Round(bxb), Math.Round(byb)),
                        ["mmPerPx"] = Math.Round(r.MmPerPx, 2), ["widthPx"] = r.Width, ["heightPx"] = r.Height,
                        ["pxToMm"] = $"x = {Math.Round(bxa)} + (px - {r.Left}) * {Math.Round(r.MmPerPx, 3)}; y = {Math.Round(byb)} - (py - {r.Top}) * {Math.Round(r.MmPerPx, 3)}",
                        ["linesDrawn"] = r.LinesDrawn, ["textsDrawn"] = r.TextsDrawn, ["textsTooSmallToDraw"] = r.TextsTooSmall,
                        ["texts"] = SurveyConfig.ToJson(inside), ["layers"] = SurveyConfig.ToJson(layerCounts),
                        ["red"] = marks.Count > 0 ? "the model's windows/doors (or the point asked about)" : null,
                        ["notes"] = notes.Count > 0 ? SurveyConfig.ToJson(notes) : null,
                        ["image"] = saved,
                    }.ToJsonString(),
                };
                c.Images.Add(("image/png", r.Png));
                return c;
            }
            };
        }

        /// <summary>The box from box, or near + size_mm, or else everything drawn.</summary>
        private static void SetBox(RasterSpec spec, JsonObject a, IEnumerable<DwgLine> lines)
        {
            var box = Args.Numbers(a, "box"); var near = Args.Numbers(a, "near");
            if (box.Count >= 4) { spec.X0 = box[0]; spec.Y0 = box[1]; spec.X1 = box[2]; spec.Y1 = box[3]; }
            else if (near.Count >= 2)
            {
                double s = (Args.Dbl(a, "size_mm") ?? 4000) / 2;
                spec.X0 = near[0] - s; spec.X1 = near[0] + s; spec.Y0 = near[1] - s; spec.Y1 = near[1] + s;
            }
            else
            {
                var l = lines.ToList();
                if (l.Count == 0) throw new ArgumentException("Nothing drawn on those layers.");
                spec.X0 = l.Min(x => Math.Min(x.A.X, x.B.X)); spec.X1 = l.Max(x => Math.Max(x.A.X, x.B.X));
                spec.Y0 = l.Min(x => Math.Min(x.A.Y, x.B.Y)); spec.Y1 = l.Max(x => Math.Max(x.A.Y, x.B.Y));
            }
        }

        /// <summary>The indexed elevation whose facade line the window or door sits on.</summary>
        private static JsonObject FindElevationFor(Document doc, FamilyInstance fi)
        {
            if (!(fi.Location is LocationPoint lp)) return null;
            var p = CadLink.ToMm(lp.Point);
            JsonObject best = null; double bestOff = 800;
            foreach (var s in ElevationSpecs(doc, null))
            {
                try
                {
                    var (fa, fb) = FacadeLine(doc, s);
                    double fl = fa.Dist2D(fb), ux = (fb.X - fa.X) / fl, uy = (fb.Y - fa.Y) / fl;
                    double u = (p.X - fa.X) * ux + (p.Y - fa.Y) * uy, o = Math.Abs((p.X - fa.X) * -uy + (p.Y - fa.Y) * ux);
                    if (u >= -500 && u <= fl + 500 && o <= bestOff) { best = s; bestOff = o; }
                }
                catch { }
            }
            return best;
        }
    }
}
