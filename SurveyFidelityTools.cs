// 1.6.0: the 1:1 score (survey_fidelity) and the facade key (facade_list). The measuring is in Survey/Fidelity.cs
// and Survey/Facades.cs (Revit-free, tested); this file gathers the model and the DWGs for them.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
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
                Description = "The 1:1 score: each floor of the model measured against its survey DWGs, pass or fail against the gate (settings.json fidelityGate; defaults: wall faces 95% within 10 mm and none over 25 mm, thickness within 10 mm, openings within 30 mm, levels within 10 mm, no missing walls). Wall faces against the plan's face lines; thickness against the DWG face pair; windows and doors against the plan jambs, the S/H labels and, where an elevation is indexed (elevation-index.json in the reports folder, or elevations), the elevation; the level against the plan's spot heights. Returns the numbers per floor and the worst issues with element ids; writes the full result as JSON and an HTML page to the reports folder. Changes nothing.",
                InputSchema = Schema(
                    ("levels", Arr("string", "Floors to measure (default: every floor with a plan DWG linked into one of its plans)"), false),
                    ("max_issues", S("integer", "Worst issues to return (default 20; the report has them all)"), false),
                    ("write_report", S("boolean", "Write JSON + HTML to the reports folder (default true)"), false),
                    ("elevations", Arr("object", "Elevations to measure openings against, as dwg_elevation takes them: {path, sheet_x_range, sheet_y_range?, facade (F id) | facade_line | wall_ids, along_direction?}. Default: the reports folder's elevation-index.json"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var gate = McpSettings.Load().FidelityGate ?? new FidelityGate();
                    var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                    var wanted = Args.Strings(a, "levels");
                    var plans = CadLink.All(doc).Where(i => i.ViewSpecific && doc.GetElement(i.OwnerViewId) is ViewPlan vp && vp.GenLevel != null)
                        .GroupBy(i => ((ViewPlan)doc.GetElement(i.OwnerViewId)).GenLevel.Id.Value).ToDictionary(g => g.Key, g => g.First());
                    var floors = levels.Where(l => plans.ContainsKey(l.Id.Value) && (wanted.Count == 0 || wanted.Any(w => w.Equals(l.Name, StringComparison.OrdinalIgnoreCase)))).ToList();
                    if (floors.Count == 0) throw new ArgumentException("No floor has a plan DWG linked into its plan view" + (wanted.Count > 0 ? " among: " + string.Join(", ", wanted) : "") + ". list_cad shows the links.");
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
                            var r = ElevationOnFacade(doc, spec);
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
                            var input = FidelityInputFor(doc, level, plans[level.Id.Value], datumMm);
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
                    return new
                    {
                        gate,
                        passed = result.Floors.Count(f => f.Pass), floorsMeasured = result.Floors.Count,
                        floors = result.Floors.Select(f => new
                        {
                            level = f.Level, pass = f.Pass, failedOn = f.FailedOn, unverified = f.Unverified.Count > 0 ? f.Unverified : null, score = f.Score,
                            walls = new { f.Walls.Checked, f.Walls.Unverified, f.Walls.FaceLengthM, withinTolPct = f.Walls.WithinTolPct, f.Walls.Within25Pct, f.Walls.MaxMm, thicknessOver = $"{f.Walls.ThicknessOver} of {f.Walls.ThicknessChecked}", f.Walls.Missing },
                            openings = new { f.Openings.Checked, plan = $"{f.Openings.PlanOver} out of {f.Openings.PlanChecked}", labels = $"{f.Openings.LabelOver} out of {f.Openings.LabelChecked}", elevation = $"{f.Openings.ElevationOver} out of {f.Openings.ElevationChecked}", f.Openings.Missing, f.Openings.NotChecked },
                            levelCheck = new { f.LevelCheck.ModelAodM, f.LevelCheck.DevMm, f.LevelCheck.Note },
                            issues = f.IssueCount,
                        }).ToList(),
                        elevations = elevNotes.Count > 0 ? elevNotes : null,
                        issues = SurveyConfig.ToJson(result.Issues.Take(max).ToList()), issuesTotal = result.Issues.Count,
                        problems = problems.Count > 0 ? problems : null,
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
        private static (List<ElevMatch> matched, List<ElevMissing> missing, string note) ElevationOnFacade(Document doc, JsonObject spec)
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
            var winLines = lines.Where(l => DwgFile.LayerMatches(l.Layer, new[] { "*Window*" })).ToList();
            var reveal = lines.Where(l => DwgFile.LayerMatches(l.Layer, new[] { "*Wall*" })).ToList();
            var heights = ElevationFit.Heights(file.Texts.Where(t => DwgFile.LayerMatches(t.Layer, new[] { "*Level*" }) && t.At.Y >= yMin && t.At.Y <= yMax),
                                               lines.Where(l => DwgFile.LayerMatches(l.Layer, new[] { "*Level*" })), xMin, xMax);
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

        /// <summary>One floor's model and DWG content for the score.</summary>
        private static FidelityInput FidelityInputFor(Document doc, Level level, ImportInstance imp, double datumMm)
        {
            var entities = CadLink.Read(doc, imp, new ReadStats());
            var input = new FidelityInput
            {
                Level = level.Name, LevelZ = level.Elevation * Mm, DatumMm = datumMm,
                WallFaces = CadLink.Segments(entities, DefaultWallLayers),
                OpeningLines = CadLink.Segments(entities, OpeningLayers),
            };
            input.Sources.Add("plan: " + CadLink.NameOf(doc, imp));
            input.Walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
                .Where(w => w.LevelId == level.Id && w.WallType.Kind == WallKind.Basic && w.Location is LocationCurve lc && lc.Curve is Line)
                .Select(w =>
                {
                    var c = ((LocationCurve)w.Location).Curve;
                    return new FitWall { Id = w.Id.Value, A = CadLink.ToMm(c.GetEndPoint(0)), B = CadLink.ToMm(c.GetEndPoint(1)), ThicknessMm = w.Width * Mm, Type = w.WallType.Name };
                }).ToList();
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

            // labels and spot heights: read from the DWG file itself (Revit's link drops text), placed in the model
            string path = CadLink.FilePath(doc, imp);
            if (path != null && File.Exists(path))
            {
                var file = DwgFile.Read(path);
                var map = CadLink.FileToModel(doc, imp, file, out string note);
                if (map != null)
                {
                    input.Labels = file.Texts.Where(t => Fidelity.Label(t.Value) != null).Select(t => new DwgText { Value = t.Value, At = map(t.At), Layer = t.Layer }).ToList();
                    // spot heights inside the floor (not the street outside): within the walls' extent, 500 mm in
                    if (input.Walls.Count > 0)
                    {
                        double x0 = input.Walls.Min(w => Math.Min(w.A.X, w.B.X)) + 500, x1 = input.Walls.Max(w => Math.Max(w.A.X, w.B.X)) - 500;
                        double y0 = input.Walls.Min(w => Math.Min(w.A.Y, w.B.Y)) + 500, y1 = input.Walls.Max(w => Math.Max(w.A.Y, w.B.Y)) - 500;
                        foreach (var t in file.Texts.Where(t => DwgFile.LayerMatches(t.Layer, new[] { "*Level*" })))
                        {
                            if (!(ElevationFit.LevelValueMm(t.Value) is double v)) continue;
                            var p = map(t.At);
                            if (p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1) input.SpotHeights.Add((p, v));
                        }
                    }
                }
                else input.Sources.Add("labels and spot heights not read: " + note);
            }
            return input;
        }
    }
}
