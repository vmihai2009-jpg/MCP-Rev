// 1.6.0: the 1:1 score (survey_fidelity) and the facade key (facade_list). The measuring is in Survey/Fidelity.cs
// and Survey/Facades.cs (Revit-free, tested); this file gathers the model and the DWGs for them.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        private static readonly string[] OpeningLayers = { "*Window*", "*Door*" };

        public static IEnumerable<RevitTool> Phase1()
        {
            yield return new RevitTool
            {
                Name = "survey_fidelity",
                Description = "The 1:1 score: each floor of the model measured against its survey DWGs, pass or fail against the gate (settings.json fidelityGate; defaults: wall faces 95% within 10 mm and none over 25 mm, thickness within 10 mm, openings within 30 mm, levels within 10 mm, no missing walls). The plans are read by Survey to Revit (survey_reading: its layer roles, the survey's layers.txt and every label style it knows), so the score measures against the same reading as the build; without Survey to Revit, the hub reads the linked plan DWGs itself. Wall faces against the plan's face lines; thickness against the DWG face pair; windows and doors against the plan jambs, the sill/head labels and, where an elevation is indexed (elevation-index.json in the reports folder, or elevations), the elevation; windows and doors Survey to Revit read on the plan that the model lacks; room names and ceiling heights written on the plan against the model's rooms and ceilings; the level against the plan's spot heights. With against_reference, also Survey to Revit's count against a linked hand-made model (survey_score). Returns the numbers per floor and the worst issues with element ids; writes the full result as JSON and an HTML page to the reports folder. Changes nothing.",
                InputSchema = Schema(
                    ("levels", Arr("string", "Floors to measure (default: every floor with a plan DWG linked into one of its plans)"), false),
                    ("max_issues", S("integer", "Worst issues to return (default 20; the report has them all)"), false),
                    ("write_report", S("boolean", "Write JSON + HTML to the reports folder (default true)"), false),
                    ("elevations", Arr("object", "Elevations to measure openings against, as dwg_elevation takes them: {path, sheet_x_range, sheet_y_range?, facade (F id) | facade_line | wall_ids, along_direction?}. Default: the reports folder's elevation-index.json"), false),
                    ("source", S("string", "Who reads the plans: auto (default: Survey to Revit if loaded, else the hub), survey_to_revit, or cad_link (the hub's own reading of the linked plans)"), false),
                    ("survey_folder", S("string", "Folder of the survey DWGs for Survey to Revit (default: the folder of the linked plans, else the survey it used last)"), false),
                    ("against_reference", S("string", "Also count walls, windows and doors against a linked hand-made model (Survey to Revit's survey_score): its name, or \"true\" when only one is linked"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var source = ReadingFor(app, doc, a);
                    var gate = McpSettings.Load().FidelityGate ?? new FidelityGate();
                    var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                    var wanted = Args.Strings(a, "levels");
                    var plans = CadLink.All(doc).Where(i => i.ViewSpecific && doc.GetElement(i.OwnerViewId) is ViewPlan vp && vp.GenLevel != null)
                        .GroupBy(i => ((ViewPlan)doc.GetElement(i.OwnerViewId)).GenLevel.Id.Value).ToDictionary(g => g.Key, g => g.First());
                    var floors = levels.Where(l => source.Has(l, plans) && (wanted.Count == 0 || wanted.Any(w => w.Equals(l.Name, StringComparison.OrdinalIgnoreCase)))).ToList();
                    if (floors.Count == 0) throw new ArgumentException("No floor has a plan to measure against (" + source.Note + ")" + (wanted.Count > 0 ? " among: " + string.Join(", ", wanted) : "") + ". list_cad shows the links; survey_reading what Survey to Revit read.");
                    double datumMm = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).Elevation * Mm;

                    // elevations once for the whole building, then split by floor
                    var elevSpecs = ElevationSpecs(doc, a);
                    var elevMatches = new List<ElevMatch>();
                    var elevMissing = new List<ElevMissing>();
                    var elevNotes = new List<string>();
                    foreach (var spec in elevSpecs)
                    {
                        try
                        {
                            var r = ElevationOnFacade(doc, spec, source.Roles);
                            elevMatches.AddRange(r.matched); elevMissing.AddRange(r.missing);
                            elevNotes.Add(r.note);
                        }
                        catch (Exception ex) { elevNotes.Add($"{Args.Str(spec, "facade") ?? Path.GetFileName(Args.Str(spec, "path") ?? "?")}: {McpServer.Unwrap(ex).Message}"); }
                    }

                    var result = new FidelityResult
                    {
                        Model = string.IsNullOrEmpty(doc.PathName) ? doc.Title : Path.GetFileName(doc.PathName),
                        When = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), Gate = gate,
                    };
                    var problems = new List<string>();
                    foreach (var level in floors)
                    {
                        try
                        {
                            var input = FidelityInputFor(doc, level, plans.TryGetValue(level.Id.Value, out var imp) ? imp : null, datumMm, source);
                            input.Elevation = elevMatches.Where(e => e.Insert.Level == level.Name && input.Inserts.Any(i => i.Id == e.Insert.Id)).ToList();
                            input.ElevationMissing = elevMissing.Where(m => m.Level == level.Name).ToList();
                            if (input.Elevation.Count > 0 || input.ElevationMissing.Count > 0) input.Sources.Add($"elevations: {elevSpecs.Count}");
                            var (score, issues) = Fidelity.Measure(input, gate);
                            result.Floors.Add(score);
                            result.Issues.AddRange(issues);
                        }
                        catch (Exception ex) { problems.Add($"{level.Name}: {McpServer.Unwrap(ex).Message}"); }
                    }
                    result.Issues = result.Issues.OrderByDescending(i => i.Severity).ToList();

                    string json = null, html = null;
                    if (Args.Bool(a, "write_report") != false)
                    {
                        string folder = ReportsFolder(doc);
                        string stem = Path.Combine(folder, $"fidelity-{Safe(Path.GetFileNameWithoutExtension(result.Model))}-{DateTime.Now:yyyyMMdd-HHmmss}");
                        json = stem + ".json"; html = stem + ".html";
                        File.WriteAllText(json, SurveyConfig.ToJson(result).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        File.WriteAllText(html, Fidelity.Html(result));
                    }
                    int max = Math.Clamp(Args.Int(a, "max_issues", 20), 0, 200);
                    object reference = Args.Str(a, "against_reference") is string refName && refName != "false" ? AgainstReference(app, refName) : null;
                    return new
                    {
                        plans = source.Note,
                        gate,
                        passed = result.Floors.Count(f => f.Pass), floorsMeasured = result.Floors.Count,
                        floors = result.Floors.Select(f => new
                        {
                            level = f.Level, pass = f.Pass, failedOn = f.FailedOn, unverified = f.Unverified.Count > 0 ? f.Unverified : null, score = f.Score,
                            walls = new { f.Walls.Checked, f.Walls.Unverified, f.Walls.FaceLengthM, withinTolPct = f.Walls.WithinTolPct, f.Walls.Within25Pct, f.Walls.MaxMm, thicknessOver = $"{f.Walls.ThicknessOver} of {f.Walls.ThicknessChecked}", f.Walls.Missing },
                            openings = new { f.Openings.Checked, plan = $"{f.Openings.PlanOver} out of {f.Openings.PlanChecked}", labels = $"{f.Openings.LabelOver} out of {f.Openings.LabelChecked}", elevation = $"{f.Openings.ElevationOver} out of {f.Openings.ElevationChecked}", f.Openings.Missing, f.Openings.MissingOnPlan, f.Openings.NotChecked },
                            levelCheck = new { f.LevelCheck.ModelAodM, f.LevelCheck.DevMm, f.LevelCheck.Note },
                            rooms = f.Rooms.Checked == 0 ? null : new { f.Rooms.Checked, f.Rooms.Missing, f.Rooms.Misnamed },
                            ceilings = f.Ceilings.Checked == 0 ? null : new { f.Ceilings.Checked, f.Ceilings.Over, f.Ceilings.Missing, f.Ceilings.MaxMm },
                            issues = f.IssueCount,
                        }).ToList(),
                        elevations = elevNotes.Count > 0 ? elevNotes : null,
                        issues = SurveyConfig.ToJson(result.Issues.Take(max).ToList()), issuesTotal = result.Issues.Count,
                        problems = problems.Count > 0 ? problems : null,
                        againstReference = reference,
                        report = json == null ? null : new { json, html },
                        next = result.Floors.All(f => f.Pass) ? "Every floor passes." : "Fix the worst first (each issue says how), or survey_autofix per floor when it exists; then survey_fidelity again.",
                    };
                }
            };

            yield return new RevitTool
            {
                Name = "facade_list",
                Description = "Every external facade run of the model: the walls with the outside on one side, grouped where they share a line and face the same way, from the ground up. For each: a stable id (F1... clockwise from grid north), a name guessed from its bearing and the street text on the plan DWGs, the walls in order, the outer face line left to right as seen from outside in model mm and in British National Grid (via the survey point), the outward bearing from grid north, length, base and top (model mm and above datum), floors and window count. Saves facades.json in the reports folder: dwg_elevation and survey_fidelity take a facade id from it. Changes nothing.",
                InputSchema = Schema(
                    ("min_length_mm", S("number", "Leave out runs shorter than this (default 1500)"), false),
                    ("levels", Arr("string", "Only walls on these floors (default all)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var pp = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
                    var wanted = Args.Strings(a, "levels");
                    var walls = new List<FacadeWall>();
                    var levelName = new Dictionary<long, string>();
                    foreach (var w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
                    {
                        if (w.WallType.Kind != WallKind.Basic || !(w.Location is LocationCurve lc) || !(lc.Curve is Line)) continue;
                        var lv = doc.GetElement(w.LevelId) as Level;
                        if (lv == null || (wanted.Count > 0 && !wanted.Any(x => x.Equals(lv.Name, StringComparison.OrdinalIgnoreCase)))) continue;
                        var bb = w.get_BoundingBox(null);
                        walls.Add(new FacadeWall
                        {
                            Id = w.Id.Value, Level = lv.Name, A = CadLink.ToMm(lc.Curve.GetEndPoint(0)), B = CadLink.ToMm(lc.Curve.GetEndPoint(1)),
                            ThicknessMm = w.Width * Mm, BaseZ = bb != null ? bb.Min.Z * Mm : lv.Elevation * Mm, TopZ = bb != null ? bb.Max.Z * Mm : lv.Elevation * Mm,
                        });
                    }
                    var runs = Facades.Runs(walls, pp.Angle, Args.Dbl(a, "min_length_mm") ?? 1500);

                    // street names: text on the plan DWGs, put in the model
                    var streets = new List<DwgText>();
                    foreach (var imp in CadLink.All(doc).Where(i => i.ViewSpecific))
                    {
                        try
                        {
                            string path = CadLink.FilePath(doc, imp);
                            if (path == null || !File.Exists(path)) continue;
                            var file = DwgFile.Read(path);
                            var cand = file.Texts.Where(t => Facades.LooksLikeStreet(t.Value)).ToList();
                            if (cand.Count == 0) continue;
                            var map = CadLink.FileToModel(doc, imp, file, out _);
                            if (map == null) continue;
                            streets.AddRange(cand.Select(t => new DwgText { Value = t.Value, At = map(t.At), Layer = t.Layer }));
                            if (streets.Count > 200) break;
                        }
                        catch { /* a drawing that cannot be read gives no names */ }
                    }

                    // windows and doors per wall
                    var hosted = new Dictionary<long, int>();
                    foreach (var cat in new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors })
                        foreach (var fi in new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().OfType<FamilyInstance>())
                            if (fi.Host is Wall hw) { hosted.TryGetValue(hw.Id.Value, out int c); hosted[hw.Id.Value] = c + 1; }

                    double datumMm = pp.Elevation * Mm;
                    var outRuns = new List<JsonObject>();
                    foreach (var r in runs)
                    {
                        double bearing = Math.Round(Facades.Bearing(r.OutX, r.OutY, pp.Angle), 1);
                        string street = Facades.StreetNear(r, streets);
                        r.Name = Facades.Compass(bearing) + (street != null ? ", " + street : "");
                        var ga = ToGrid(pp, r.A); var gb = ToGrid(pp, r.B);
                        // walls in order along the facade, bottom floor first
                        double dx = r.B.X - r.A.X, dy = r.B.Y - r.A.Y, dl = Math.Sqrt(dx * dx + dy * dy);
                        var ordered = r.WallIds.Select(id => walls.First(w => w.Id == id))
                            .OrderBy(w => w.BaseZ).ThenBy(w => Math.Min(((w.A.X - r.A.X) * dx + (w.A.Y - r.A.Y) * dy) / dl, ((w.B.X - r.A.X) * dx + (w.B.Y - r.A.Y) * dy) / dl))
                            .Select(w => w.Id).ToList();
                        outRuns.Add(new JsonObject
                        {
                            ["id"] = r.Id, ["name"] = r.Name,
                            ["line"] = new JsonArray(Math.Round(r.A.X), Math.Round(r.A.Y), Math.Round(r.B.X), Math.Round(r.B.Y)),
                            ["bng"] = new JsonArray(new JsonArray(Math.Round(ga.e, 3), Math.Round(ga.n, 3)), new JsonArray(Math.Round(gb.e, 3), Math.Round(gb.n, 3))),
                            ["outwardBearingDeg"] = bearing, ["lengthMm"] = r.LengthMm,
                            ["baseMm"] = Math.Round(r.BaseZ), ["topMm"] = Math.Round(r.TopZ),
                            ["baseAodM"] = Math.Round((r.BaseZ + datumMm) / 1000, 3), ["topAodM"] = Math.Round((r.TopZ + datumMm) / 1000, 3),
                            ["levels"] = new JsonArray(r.Levels.Select(x => (JsonNode)x).ToArray()),
                            ["windows"] = r.WallIds.Sum(id => hosted.TryGetValue(id, out int c) ? c : 0),
                            ["walls"] = ordered.Count,
                            ["wallIds"] = new JsonArray(ordered.Select(x => (JsonNode)x).ToArray()),
                        });
                        // a main facade: a street front from the ground up, not a setback, a roof parapet or a basement wall
                        int win = outRuns[outRuns.Count - 1]["windows"].GetValue<int>();
                        outRuns[outRuns.Count - 1]["main"] = r.Levels.Count >= 3 || win >= 8;
                    }
                    var saved = new JsonObject
                    {
                        ["model"] = doc.Title, ["when"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                        ["note"] = "line = the outer face, left to right as seen from outside (model mm); bng = the same ends in British National Grid, metres",
                        ["facades"] = new JsonArray(outRuns.Select(o => (JsonNode)o.DeepClone()).ToArray()),
                    };
                    string file2 = Path.Combine(ReportsFolder(doc), "facades.json");
                    File.WriteAllText(file2, saved.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    // the answer: the walls list cut short (the file has them all)
                    foreach (var o in outRuns)
                    {
                        var ids = (JsonArray)o["wallIds"];
                        if (ids.Count > 12) o["wallIds"] = new JsonArray(ids.Take(12).Select(x => (JsonNode)x.GetValue<long>()).ToArray());
                    }
                    var main = outRuns.Where(o => o["main"].GetValue<bool>()).ToList();
                    var minor = outRuns.Where(o => !o["main"].GetValue<bool>()).Select(o => (JsonNode)new JsonObject
                    {
                        ["id"] = o["id"].GetValue<string>(), ["name"] = o["name"].GetValue<string>(), ["lengthMm"] = o["lengthMm"].GetValue<double>(),
                        ["levels"] = o["levels"].DeepClone(), ["windows"] = o["windows"].GetValue<int>(),
                    }).ToArray();
                    return new JsonObject
                    {
                        ["facades"] = new JsonArray(main.Select(o => (JsonNode)o).ToArray()), ["count"] = outRuns.Count, ["mainCount"] = main.Count,
                        ["others"] = new JsonArray(minor.Take(80).ToArray()),
                        ["othersNote"] = "setbacks, roof-level walls, lightwells and basement walls: their full rows are in the saved file",
                        ["externalWalls"] = runs.Sum(r => r.WallIds.Count), ["wallsLookedAt"] = walls.Count,
                        ["saved"] = file2,
                        ["note"] = "line runs left to right as seen from outside, as an elevation is drawn: give the id to dwg_elevation (facade) and along_direction 1.",
                    };
                }
            };
        }

        /// <summary>Model mm -> British National Grid metres, through the project position (verified against the survey DWGs).</summary>
        private static (double e, double n) ToGrid(ProjectPosition pp, Pt p)
        {
            double c = Math.Cos(pp.Angle), s = Math.Sin(pp.Angle);
            double x = p.X / Mm, y = p.Y / Mm;
            double e = x * c - y * s + pp.EastWest, n = x * s + y * c + pp.NorthSouth;
            return (e * Mm / 1000, n * Mm / 1000);
        }

        /// <summary>Where reports go: "BGMCP reports" beside the model, else %LocalAppData%\BarrGazetas\MCP\reports.</summary>
        internal static string ReportsFolder(Document doc)
        {
            var tries = new List<string>();
            if (!string.IsNullOrEmpty(doc.PathName) && !doc.IsWorkshared || (doc.IsWorkshared && !string.IsNullOrEmpty(doc.PathName)))
                try { tries.Add(Path.Combine(Path.GetDirectoryName(doc.PathName), "BGMCP reports", Safe(doc.Title))); } catch { }
            tries.Add(Path.Combine(CallLog.Folder, "reports", Safe(doc.Title)));
            foreach (var t in tries)
            {
                try
                {
                    Directory.CreateDirectory(t);
                    string probe = Path.Combine(t, ".write-test");
                    File.WriteAllText(probe, ""); File.Delete(probe);
                    return t;
                }
                catch { }
            }
            throw new IOException("No folder for reports could be written: not beside the model, nor in " + tries.Last());
        }

        private static string Safe(string s) => string.Concat((s ?? "model").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

        private static (Pt a, Pt b) SavedFacade(Document doc, string id)
        {
            string file = Path.Combine(ReportsFolder(doc), "facades.json");
            if (!File.Exists(file)) throw new ArgumentException($"No facades.json yet: run facade_list first, then use its ids (\"{id}\").");
            var root = JsonNode.Parse(File.ReadAllText(file));
            foreach (var f in root["facades"].AsArray())
                if (string.Equals(f["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))
                {
                    var l = f["line"].AsArray().Select(v => v.GetValue<double>()).ToList();
                    return (new Pt(l[0], l[1]), new Pt(l[2], l[3]));
                }
            throw new ArgumentException($"No facade \"{id}\" in facades.json: run facade_list again.");
        }

        private static List<JsonObject> ElevationSpecs(Document doc, JsonObject a)
        {
            if (a?["elevations"] is JsonArray given) return given.OfType<JsonObject>().ToList();
            string file = Path.Combine(ReportsFolder(doc), "elevation-index.json");
            if (!File.Exists(file)) return new List<JsonObject>();
            var root = JsonNode.Parse(File.ReadAllText(file));
            var arr = root is JsonArray ja ? ja : root?["elevations"] as JsonArray;
            return arr?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        }

        /// <summary>One elevation on its facade: the same measuring as dwg_elevation, for the score.</summary>
        private static (List<ElevMatch> matched, List<ElevMissing> missing, string note) ElevationOnFacade(Document doc, JsonObject spec, Func<IEnumerable<string>, Dictionary<string, string>> roles = null)
        {
            string path = Args.Str(spec, "path") ?? throw new ArgumentException("an elevation needs path");
            var xr = Args.Numbers(spec, "sheet_x_range");
            if (xr.Count != 2) throw new ArgumentException("an elevation needs sheet_x_range");
            double xMin = Math.Min(xr[0], xr[1]), xMax = Math.Max(xr[0], xr[1]);
            var yr = Args.Numbers(spec, "sheet_y_range");
            double yMin = yr.Count == 2 ? Math.Min(yr[0], yr[1]) : double.MinValue, yMax = yr.Count == 2 ? Math.Max(yr[0], yr[1]) : double.MaxValue;
            bool InSheet(Pt p) => p.X >= xMin && p.X <= xMax && p.Y >= yMin && p.Y <= yMax;
            var (fa, fb) = FacadeLine(doc, spec);
            double fl = fa.Dist2D(fb), ux = (fb.X - fa.X) / fl, uy = (fb.Y - fa.Y) / fl;

            var file = DwgFile.Read(path);
            var lines = file.Lines.Where(l => InSheet(l.A) && InSheet(l.B)).ToList();
            // layers by Survey to Revit's roles (its rules plus the survey's layers.txt), with the hub's names as well
            var role = roles?.Invoke(file.Lines.Select(l => l.Layer).Concat(file.Texts.Select(t => t.Layer)));
            bool Is(string layer, string[] names, string[] globs) => role != null && layer != null && role.TryGetValue(layer, out var r) && names.Contains(r) || DwgFile.LayerMatches(layer, globs);
            string[] winRole = { "Window" }, wallRole = { "Wall", "CurtainWall" }, levelRole = { "LevelText", "HeightText" };
            var winLines = lines.Where(l => Is(l.Layer, winRole, new[] { "*Window*" })).ToList();
            var reveal = lines.Where(l => Is(l.Layer, wallRole, new[] { "*Wall*" })).ToList();
            var heights = ElevationFit.Heights(file.Texts.Where(t => Is(t.Layer, levelRole, new[] { "*Level*" }) && t.At.Y >= yMin && t.At.Y <= yMax),
                                               lines.Where(l => Is(l.Layer, levelRole, new[] { "*Level*" })), xMin, xMax);
            double datumMm = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).Elevation * Mm;
            double ModelZ(double y) => heights.Height(y) - datumMm;

            double band = Args.Dbl(spec, "band_mm") ?? 800;
            var inserts = new List<FacadeInsert>();
            foreach (var cat in new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors })
                foreach (var fi in new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().OfType<FamilyInstance>())
                {
                    if (!(fi.Location is LocationPoint lp)) continue;
                    var p = CadLink.ToMm(lp.Point);
                    double u = (p.X - fa.X) * ux + (p.Y - fa.Y) * uy, off = Math.Abs((p.X - fa.X) * -uy + (p.Y - fa.Y) * ux);
                    if (off > band || u < -500 || u > fl + 500) continue;
                    double w = WidthOf(fi);
                    if (w <= 0) continue;
                    var lv = doc.GetElement(fi.LevelId) as Level;
                    inserts.Add(new FacadeInsert
                    {
                        Id = fi.Id.Value, Category = fi.Category?.Name, Level = lv?.Name, U = u, W = w, LevelZ = (lv?.Elevation ?? 0) * Mm,
                        Sill = LenOf(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM), Head = LenOf(fi, BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM),
                    });
                }
            int? dir = Args.Dbl(spec, "along_direction") is double dg ? (dg < 0 ? -1 : 1) : (Args.Str(spec, "facade") != null ? 1 : (int?)null);
            var along = ElevationFit.Along(ElevationFit.Jambs(winLines), inserts.SelectMany(m => new[] { m.U - m.W / 2, m.U + m.W / 2 }).ToList(), heights.A, 60, dir);
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var wallLevels = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(Wall)).Select(e => e.LevelId.Value));
            var storeys = levels.Where(lv => wallLevels.Contains(lv.Id.Value)).Select(lv => (lv.Name, lv.Elevation * Mm)).ToList();
            var (matched, _, missing) = ElevationFit.MatchFacade(winLines, reveal, inserts, heights, along, ModelZ, fl, storeys);
            foreach (var m in missing) { m.X = fa.X + ux * m.U; m.Y = fa.Y + uy * m.U; }
            // only trust the matches when the elevation sits on the facade well enough
            bool trusted = along.Matched >= 4 && along.Matched >= along.ElevationJambs * 0.3;
            string name = Args.Str(spec, "facade") ?? $"{Path.GetFileName(path)} x {xMin:0}-{xMax:0}";
            if (!trusted) return (new List<ElevMatch>(), new List<ElevMissing>(), $"{name}: not used, the elevation does not sit on the facade ({along.Note})");
            return (matched, missing, $"{name}: {matched.Count} openings measured, {missing.Count} drawn but not modelled; {heights.Note} {along.Note}");
        }

        /// <summary>One floor's model and survey content for the score: Survey to Revit's reading of its plan when there is one, else the linked plan DWG.</summary>
        private static FidelityInput FidelityInputFor(Document doc, Level level, ImportInstance imp, double datumMm, SurveySource source = null)
        {
            var reading = source?.Reading?.Level(level.Name);
            if (reading == null && imp == null) throw new ArgumentException($"{level.Name}: no plan DWG is linked into its plan, and Survey to Revit's reading has no floor of that name.");
            var input = new FidelityInput { Level = level.Name, LevelZ = level.Elevation * Mm, DatumMm = datumMm };
            input.Walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
                .Where(w => w.LevelId == level.Id && w.WallType.Kind == WallKind.Basic && w.Location is LocationCurve lc && lc.Curve is Line)
                .Select(w =>
                {
                    var c = ((LocationCurve)w.Location).Curve;
                    return new FitWall { Id = w.Id.Value, A = CadLink.ToMm(c.GetEndPoint(0)), B = CadLink.ToMm(c.GetEndPoint(1)), ThicknessMm = w.Width * Mm, Type = w.WallType.Name };
                }).ToList();
            input.ArcWalls = ArcWallsOn(doc, level);
            var wallIds = new HashSet<long>(input.Walls.Select(w => w.Id));
            foreach (var cat in new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors })
                foreach (var fi in new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().OfType<FamilyInstance>())
                {
                    if (!(fi.Host is Wall host) || !wallIds.Contains(host.Id.Value) || !(fi.Location is LocationPoint lp)) continue;
                    input.Inserts.Add(new FidInsert
                    {
                        Id = fi.Id.Value, Category = fi.Category?.Name, HostWallId = host.Id.Value, At = CadLink.ToMm(lp.Point), W = WidthOf(fi),
                        Sill = LenOf(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM), Head = LenOf(fi, BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM),
                    });
                }

            var spots = new List<(Pt at, double aodMm)>();
            if (reading != null)
            {
                input.Sources.Add($"plan: Survey to Revit's reading of {Path.GetFileName(reading.Plan ?? "?")} ({source.Reading.FrameNote}" + (source.Reading.LayerFile != null ? ", layers.txt " + source.Reading.LayerFile : "") + ")");
                input.WallFaces = reading.WallFaces;
                input.WallArcs = reading.WallFaceArcs;
                input.OpeningLines = reading.WindowLines.Concat(reading.DoorLines).ToList();
                input.Heights = reading.Heights;
                input.PlanOpenings = reading.Openings;
                spots = reading.SpotHeights;
                if (reading.Rooms.Count > 0)
                {
                    var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>()
                        .Where(r => r.LevelId == level.Id && r.Area > 0).ToList();
                    foreach (var (at, name) in reading.Rooms)
                    {
                        var p = new XYZ(at.X / Mm, at.Y / Mm, level.Elevation + 1000 / Mm);
                        var room = rooms.FirstOrDefault(r => { try { return r.IsPointInRoom(p); } catch { return false; } });
                        input.Rooms.Add(new RoomCheck { At = at, Survey = name, RoomId = room?.Id.Value, ModelName = room?.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() });
                    }
                }
                var notes = reading.CeilingNotes.Count > 0 ? reading.CeilingNotes
                    : reading.Heights.Where(h => h.Kind == 'C' && h.Mm >= 1800).Select(h => (h.At, h.Mm)).ToList();
                if (notes.Count > 0)
                {
                    var ceilings = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Ceilings).WhereElementIsNotElementType()
                        .Where(c => c.LevelId == level.Id).Select(c => (c, bb: c.get_BoundingBox(null))).Where(x => x.bb != null).ToList();
                    foreach (var (at, mm) in notes)
                    {
                        double x = at.X / Mm, y = at.Y / Mm;
                        var over = ceilings.Where(c => x >= c.bb.Min.X && x <= c.bb.Max.X && y >= c.bb.Min.Y && y <= c.bb.Max.Y)
                            .OrderBy(c => (c.bb.Max.X - c.bb.Min.X) * (c.bb.Max.Y - c.bb.Min.Y)).Select(c => c.c).FirstOrDefault();
                        double? h = over?.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.AsDouble() * Mm;
                        input.Ceilings.Add(new CeilingCheck { At = at, SurveyMm = mm, Id = over?.Id.Value, ModelMm = h });
                    }
                }
            }
            else
            {
                var entities = CadLink.Read(doc, imp, new ReadStats());
                input.WallFaces = CadLink.Segments(entities, DefaultWallLayers);
                input.OpeningLines = CadLink.Segments(entities, OpeningLayers);
                input.Sources.Add("plan: " + CadLink.NameOf(doc, imp) + " (the hub's own reading" + (source?.Note != null ? "; " + source.Note : "") + ")");
                // labels and spot heights: read from the DWG file itself (Revit's link drops text), placed in the model
                string path = CadLink.FilePath(doc, imp);
                if (path != null && File.Exists(path))
                {
                    var file = DwgFile.Read(path);
                    var map = CadLink.FileToModel(doc, imp, file, out string note);
                    if (map != null)
                    {
                        input.Labels = file.Texts.Where(t => Fidelity.Label(t.Value) != null).Select(t => new DwgText { Value = t.Value, At = map(t.At), Layer = t.Layer }).ToList();
                        foreach (var t in file.Texts.Where(t => DwgFile.LayerMatches(t.Layer, new[] { "*Level*" })))
                            if (ElevationFit.LevelValueMm(t.Value) is double v) spots.Add((map(t.At), v));
                    }
                    else input.Sources.Add("labels and spot heights not read: " + note);
                }
            }
            // spot heights inside the floor (not the street outside): within the walls' extent, 500 mm in
            if (input.Walls.Count > 0)
            {
                double x0 = input.Walls.Min(w => Math.Min(w.A.X, w.B.X)) + 500, x1 = input.Walls.Max(w => Math.Max(w.A.X, w.B.X)) - 500;
                double y0 = input.Walls.Min(w => Math.Min(w.A.Y, w.B.Y)) + 500, y1 = input.Walls.Max(w => Math.Max(w.A.Y, w.B.Y)) - 500;
                input.SpotHeights.AddRange(spots.Where(x => x.at.X >= x0 && x.at.X <= x1 && x.at.Y >= y0 && x.at.Y <= y1));
            }
            return input;
        }

        /// <summary>
        /// Who reads the survey for one call of the score or autofix. Survey to Revit's reading (survey_reading) when it
        /// is loaded and source is not cad_link; the hub's own reading of the linked plan DWGs otherwise.
        /// </summary>
        internal sealed class SurveySource
        {
            public SurveyReading Reading;
            public string Note;
            /// <summary>Layer roles for drawings the hub reads itself (elevations), from Survey to Revit; null without it.</summary>
            public Func<IEnumerable<string>, Dictionary<string, string>> Roles;

            /// <summary>Is there a plan to measure this floor against?</summary>
            public bool Has(Level level, Dictionary<long, ImportInstance> plans) => plans.ContainsKey(level.Id.Value) || Reading?.Level(level.Name) != null;
        }

        internal static SurveySource ReadingFor(UIApplication app, Document doc, JsonObject a)
        {
            string mode = (Args.Str(a, "source") ?? "auto").ToLowerInvariant();
            if (mode == "cad_link") return new SurveySource { Note = "the hub's own reading of the linked plans (source cad_link)" };
            var hub = App.Instance?.Hub;
            // the survey folder: given, else the folder the linked plans came from, else the one Survey to Revit used last
            string folder = Args.Str(a, "survey_folder");
            if (folder == null)
                foreach (var imp in CadLink.All(doc).Where(i => i.ViewSpecific))
                {
                    try { if (CadLink.FilePath(doc, imp) is string f && File.Exists(f)) { folder = Path.GetDirectoryName(f); break; } } catch { }
                }
            var args = new JsonObject();
            if (folder != null) args["folder"] = folder;
            string answer;
            try { answer = hub?.Providers.CallHere(app, "survey_reading", args); }
            catch (Exception ex)
            {
                if (mode == "survey_to_revit") throw;
                return new SurveySource { Note = "Survey to Revit could not read the survey (" + McpServer.Unwrap(ex).Message + "), so the hub read the linked plans itself" };
            }
            if (answer == null)
            {
                if (mode == "survey_to_revit") throw new InvalidOperationException("Survey to Revit is not loaded, or is a release without survey_reading (update it).");
                return new SurveySource { Note = "Survey to Revit is not loaded (or has no survey_reading), so the hub read the linked plans itself with its own layer names" };
            }
            string path = JsonNode.Parse(answer)?["path"]?.GetValue<string>() ?? throw new InvalidOperationException("survey_reading gave no file.");
            var pp = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
            var reading = SurveyReading.Load(path, pp.EastWest * Mm, pp.NorthSouth * Mm, pp.Angle);
            string readFolder = reading.Folder ?? folder;
            var cache = new Dictionary<string, string>(StringComparer.Ordinal);
            return new SurveySource
            {
                Reading = reading,
                Note = $"Survey to Revit's reading of {readFolder} ({reading.Levels.Count} floors; {reading.FrameNote})",
                Roles = layers =>
                {
                    var ask = layers.Where(l => l != null && !cache.ContainsKey(l)).Distinct().ToList();
                    if (ask.Count > 0)
                    {
                        try
                        {
                            var q = new JsonObject { ["layers"] = new JsonArray(ask.Select(l => (JsonNode)l).ToArray()) };
                            if (readFolder != null) q["folder"] = readFolder;
                            var roles = JsonNode.Parse(hub.Providers.CallHere(app, "survey_layer_roles", q) ?? "{}")?["roles"] as JsonObject;
                            foreach (var kv in roles ?? new JsonObject()) cache[kv.Key] = kv.Value?.GetValue<string>();
                        }
                        catch { foreach (var l in ask) cache[l] = null; } // the hub's layer names still apply
                    }
                    return cache;
                },
            };
        }

        /// <summary>Survey to Revit's count against a linked hand-made model (survey_score), for the same answer.</summary>
        private static object AgainstReference(UIApplication app, string link)
        {
            try
            {
                var args = new JsonObject();
                if (!string.Equals(link, "true", StringComparison.OrdinalIgnoreCase)) args["link"] = link;
                string answer = App.Instance?.Hub?.Providers.CallHere(app, "survey_score", args);
                if (answer == null) return new { problem = "Survey to Revit is not loaded: no reference count." };
                var o = JsonNode.Parse(answer) as JsonObject;
                return new JsonObject
                {
                    ["reference"] = o?["reference"]?.DeepClone(), ["meaning"] = o?["meaning"]?.DeepClone(),
                    ["walls"] = o?["walls"]?.DeepClone(), ["windows"] = o?["windows"]?.DeepClone(), ["doors"] = o?["doors"]?.DeepClone(),
                    ["reportPath"] = o?["reportPath"]?.DeepClone(),
                };
            }
            catch (Exception ex) { return new { problem = McpServer.Unwrap(ex).Message }; }
        }
    }
}
