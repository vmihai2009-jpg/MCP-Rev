// 1.7.0: survey_autofix. Per floor, the deterministic fixes the 1:1 prompt used to make one call at a time:
// walls fitted to the DWG (moderate confidence), windows given the sill and head their S/H labels say and the width
// and place the plan's reveals say, windows the elevations draw that the model lacks placed, gaps in window columns
// from Survey to Revit's review list filled. Then the floor is scored again and only what still fails comes back.
// One undo step per floor. Survey to Revit is reached through the hub's provider calls only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        public static IEnumerable<RevitTool> Phase2()
        {
            yield return new RevitTool
            {
                Name = "survey_autofix",
                Write = true,
                Description = "Fix a floor (or several) towards 1:1 in one go, then score it again and return only what still fails. It measures against the same plan reading as survey_fidelity (Survey to Revit's, when loaded). Per floor: walls moved and resized onto the plan's faces (dwg_wall_fit, moderate confidence only); windows Survey to Revit read on the plan that the model lacks placed (plan width, labelled heights; missing doors listed for a person); windows given the sill and head their labels give (only labels that clearly belong to one window); widths and positions from the plan's masonry reveals (only when both reveals are within 150 mm); windows the indexed elevations draw that the model lacks placed (sill, head and width from the elevation); gaps in window columns from Survey to Revit's review list filled from the window above or below, and ticked. Each floor is one undo step; a change Revit refuses is skipped, never forced. dry_run: everything is tried and rolled back, the counts say what would change. Takes a while on a big floor: it comes back as a job.",
                InputSchema = Schema(
                    ("levels", Arr("string", "Floors to fix (default: every floor with a plan DWG that fails survey_fidelity)"), false),
                    ("walls", S("boolean", "Fit walls (default true)"), false),
                    ("openings", S("boolean", "Fix and place windows (default true)"), false),
                    ("dry_run", S("boolean", "Try everything and roll it back (default false)"), false),
                    ("max_issues", S("integer", "Remaining issues to return per floor (default 10)"), false),
                    ("elevations", Arr("object", "As survey_fidelity (default: elevation-index.json)"), false),
                    ("source", S("string", "As survey_fidelity: auto (Survey to Revit's reading if loaded), survey_to_revit or cad_link"), false),
                    ("survey_folder", S("string", "As survey_fidelity"), false)),
                Run = (app, a) =>
                {
                    var doc = Writable(app);
                    var source = ReadingFor(app, doc, a);
                    bool dry = Args.Bool(a, "dry_run") == true, doWalls = Args.Bool(a, "walls") != false, doOpenings = Args.Bool(a, "openings") != false;
                    int maxIssues = Math.Clamp(Args.Int(a, "max_issues", 10), 0, 100);
                    var gate = McpSettings.Load().FidelityGate ?? new FidelityGate();
                    double datumMm = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).Elevation * Mm;
                    var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                    var plans = CadLink.All(doc).Where(i => i.ViewSpecific && doc.GetElement(i.OwnerViewId) is ViewPlan vp && vp.GenLevel != null)
                        .GroupBy(i => ((ViewPlan)doc.GetElement(i.OwnerViewId)).GenLevel.Id.Value).ToDictionary(g => g.Key, g => g.First());
                    var wanted = Args.Strings(a, "levels");
                    var floors = levels.Where(l => source.Has(l, plans) && (wanted.Count == 0 || wanted.Any(w => w.Equals(l.Name, StringComparison.OrdinalIgnoreCase)))).ToList();
                    if (floors.Count == 0) throw new ArgumentException("No floor with a plan to fix against (" + source.Note + "). list_cad shows the links.");

                    // elevations once
                    var elevMissing = new List<ElevMissing>();
                    var elevMatched = new List<ElevMatch>();
                    foreach (var spec in ElevationSpecs(doc, a))
                        try { var r = ElevationOnFacade(doc, spec, source.Roles); elevMissing.AddRange(r.missing); elevMatched.AddRange(r.matched); } catch { /* reported by survey_fidelity */ }
                    // a window on an elevation takes its sill and head from it (outside, frame to frame); the plan's S/H labels
                    // are measured inside the room and only stand in where no elevation draws the window
                    var elevById = elevMatched.GroupBy(e => e.Insert.Id).ToDictionary(g => g.Key, g => g.First());

                    ImportInstance Plan(Level l) => plans.TryGetValue(l.Id.Value, out var imp) ? imp : null;
                    FidelityInput Scored(Level l)
                    {
                        var inp = FidelityInputFor(doc, l, Plan(l), datumMm, source);
                        inp.Elevation = elevMatched.Where(e => e.Insert.Level == l.Name && inp.Inserts.Any(i => i.Id == e.Insert.Id)).ToList();
                        inp.ElevationMissing = elevMissing.Where(m => m.Level == l.Name).ToList();
                        return inp;
                    }
                    var results = new List<object>();
                    foreach (var level in floors)
                    {
                        var before = Fidelity.Measure(Scored(level), gate).score;
                        if (wanted.Count == 0 && before.Pass) { results.Add(new { level = level.Name, pass = true, note = "already passes: left alone" }); continue; }
                        var log = new FloorFixLog();
                        using (var g = new TransactionGroup(doc, $"MCP: survey autofix {level.Name}"))
                        {
                            g.Start();
                            if (doWalls) FixWalls(doc, level, Plan(level), FidelityInputFor(doc, level, Plan(level), datumMm, source), log);
                            if (doWalls) FixArcWalls(doc, FidelityInputFor(doc, level, Plan(level), datumMm, source), log);
                            if (doOpenings)
                            {
                                var input = FidelityInputFor(doc, level, Plan(level), datumMm, source); // walls may have moved
                                FixOpenings(doc, level, input, log, elevById);
                                PlacePlanMissing(doc, level, input, log);
                                PlaceMissing(doc, level, input, elevMissing.Where(m => m.Level == level.Name).ToList(), log);
                                FillRhythmGaps(app, doc, level, input, log, tick: !dry);
                            }
                            var after = Fidelity.Measure(Scored(level), gate);
                            if (dry || log.Changed == 0) g.RollBack(); else g.Assimilate();
                            results.Add(new
                            {
                                level = level.Name,
                                pass = after.score.Pass, failedOn = after.score.FailedOn,
                                score = new { before = before.Score, after = after.score.Score },
                                walls = new { fitted = log.WallsFitted, failed = log.WallsFailed, withinTolPct = new { before = before.Walls.WithinTolPct, after = after.score.Walls.WithinTolPct }, missing = after.score.Walls.Missing },
                                openings = new { reshaped = log.Reshaped, placed = log.Placed, gapsFilled = log.GapsFilled, failed = log.OpeningsFailed },
                                levelDevMm = after.score.LevelCheck.DevMm,
                                stillFailing = SurveyConfig.ToJson(after.issues.Take(maxIssues).ToList()), stillFailingTotal = after.issues.Count,
                                problems = log.Problems.Count > 0 ? log.Problems.Take(15).ToList() : null, problemsTotal = log.Problems.Count,
                                fallbacks = log.Fallbacks.Count > 0 ? log.Fallbacks.Take(15).ToList() : null, fallbacksTotal = log.Fallbacks.Count,
                                forReview = log.Unconfirmed.Count > 0 ? log.Unconfirmed.Take(15).ToList() : null, forReviewTotal = log.Unconfirmed.Count,
                                undo = dry ? "dry run: nothing kept" : log.Changed > 0 ? "one undo step in Revit for this floor" : "nothing changed",
                            });
                        }
                    }
                    return new
                    {
                        dryRun = dry, plans = source.Note, floors = results,
                        next = "Work the stillFailing items by hand (each says how), then survey_fidelity for the whole building.",
                    };
                }
            };
        }

        public static IEnumerable<RevitTool> Phase2Index()
        {
            yield return new RevitTool
            {
                Name = "elevation_index",
                Description = "Match the elevations on survey sheets to the model's facades, so dwg_elevation and survey_fidelity need no manual input: each sheet is split into its drawings (groups of window lines with clear space between), each drawing is matched to a facade from facade_list by its window jambs (both directions tried), and its title (\"Elevation 3\") read from the text under it. Writes elevation-index.json in the reports folder (sheet_x_range, sheet_y_range, facade id, direction, how many jambs matched). Run facade_list first. Changes nothing in the model.",
                InputSchema = Schema(
                    ("paths", Arr("string", "Elevation DWGs, or a folder (every DWG in it whose name or text says elevation)"), true),
                    ("facades", Arr("string", "Only these facade ids (default: the main ones in facades.json)"), false),
                    ("min_matched", S("integer", "Jambs that must land on the model's windows to accept a match (default 6)"), false),
                    ("write", S("boolean", "Write elevation-index.json (default true)"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    string facadesFile = Path.Combine(ReportsFolder(doc), "facades.json");
                    if (!File.Exists(facadesFile)) throw new ArgumentException("Run facade_list first: elevation_index matches drawings to its facades.");
                    var only = Args.Strings(a, "facades");
                    var facades = JsonNode.Parse(File.ReadAllText(facadesFile))["facades"].AsArray().OfType<JsonObject>()
                        .Where(f => only.Count > 0 ? only.Any(o => o.Equals(f["id"].GetValue<string>(), StringComparison.OrdinalIgnoreCase)) : f["main"]?.GetValue<bool>() == true).ToList();
                    if (facades.Count == 0) throw new ArgumentException("No facades to match (facades.json has no main ones?): give facades ids.");
                    int minMatched = Math.Max(3, Args.Int(a, "min_matched", 6));

                    // the model's window jambs along each facade
                    var windows = new List<(double x, double y, double w)>();
                    foreach (var cat in new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors })
                        foreach (var fi in new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().OfType<FamilyInstance>())
                            if (fi.Location is LocationPoint lp && WidthOf(fi) is double w && w > 0) { var p = CadLink.ToMm(lp.Point); windows.Add((p.X, p.Y, w)); }
                    var modelJambs = new Dictionary<string, (List<double> jambs, double length)>();
                    foreach (var f in facades)
                    {
                        var l = f["line"].AsArray().Select(v => v.GetValue<double>()).ToArray();
                        double fl = Math.Sqrt((l[2] - l[0]) * (l[2] - l[0]) + (l[3] - l[1]) * (l[3] - l[1])), ux = (l[2] - l[0]) / fl, uy = (l[3] - l[1]) / fl;
                        var j = new List<double>();
                        foreach (var (x, y, w) in windows)
                        {
                            double u = (x - l[0]) * ux + (y - l[1]) * uy, off = Math.Abs((x - l[0]) * -uy + (y - l[1]) * ux);
                            if (off <= 800 && u > -500 && u < fl + 500) { j.Add(u - w / 2); j.Add(u + w / 2); }
                        }
                        modelJambs[f["id"].GetValue<string>()] = (j, fl);
                    }

                    var files = new List<string>();
                    foreach (var p in Args.Strings(a, "paths"))
                    {
                        if (Directory.Exists(p)) files.AddRange(Directory.GetFiles(p, "*.dwg").Concat(Directory.GetFiles(p, "*.dxf")));
                        else if (File.Exists(p)) files.Add(p);
                    }
                    if (files.Count == 0) throw new ArgumentException("No DWG or DXF found at those paths.");

                    var parts = new List<(string path, double x0, double x1, double y0, double y1, string title, List<double> jambs)>();
                    var skipped = new List<string>();
                    foreach (var path in files)
                    {
                        DwgContent file;
                        try { file = DwgFile.Read(path); } catch (Exception ex) { skipped.Add($"{Path.GetFileName(path)}: {ex.Message}"); continue; }
                        bool isElevation = Path.GetFileName(path).IndexOf("elev", StringComparison.OrdinalIgnoreCase) >= 0
                            || file.Texts.Any(t => t.Value != null && t.Value.TrimStart().StartsWith("Elevation", StringComparison.OrdinalIgnoreCase));
                        if (!isElevation) { skipped.Add($"{Path.GetFileName(path)}: no elevation title"); continue; }
                        var win = file.Lines.Where(l => DwgFile.LayerMatches(l.Layer, new[] { "*Window*" })).ToList();
                        foreach (var (x0, x1, y0, y1) in ElevationFit.SheetParts(win))
                        {
                            var jambs = ElevationFit.Jambs(win.Where(l => l.A.X >= x0 && l.A.X <= x1 && l.A.Y >= y0 && l.A.Y <= y1));
                            if (jambs.Count < 6) continue;
                            // the title: "Elevation N" under the drawing, nearest its middle
                            string title = file.Texts.Where(t => t.Value != null && t.Value.TrimStart().StartsWith("Elevation", StringComparison.OrdinalIgnoreCase)
                                                                 && t.At.X >= x0 - 3000 && t.At.X <= x1 + 3000 && t.At.Y < y0 && t.At.Y > y0 - 15000)
                                .OrderBy(t => Math.Abs(t.At.X - (x0 + x1) / 2) + (y0 - t.At.Y)).Select(t => t.Value.Trim()).FirstOrDefault();
                            parts.Add((path, x0, x1, y0, y1, title, jambs));
                        }
                    }

                    // every part against every facade; then the best pairs, each facade and part used once
                    var cands = new List<(int part, string facade, AlongFit fit, double ratio)>();
                    for (int i = 0; i < parts.Count; i++)
                        foreach (var kv in modelJambs)
                        {
                            if (kv.Value.jambs.Count < 4) continue;
                            var fit = ElevationFit.Along(parts[i].jambs, kv.Value.jambs, 1, 60);
                            double ratio = (double)fit.Matched / Math.Max(parts[i].jambs.Count, kv.Value.jambs.Count);
                            if (fit.Matched >= minMatched) cands.Add((i, kv.Key, fit, ratio));
                        }
                    var usedParts = new HashSet<int>(); var usedFacades = new HashSet<string>();
                    var chosen = new List<JsonObject>();
                    foreach (var c in cands.OrderByDescending(c => c.ratio).ThenByDescending(c => c.fit.Matched))
                    {
                        if (usedParts.Contains(c.part) || usedFacades.Contains(c.facade)) continue;
                        usedParts.Add(c.part); usedFacades.Add(c.facade);
                        var p = parts[c.part];
                        chosen.Add(new JsonObject
                        {
                            ["name"] = p.title ?? $"{Path.GetFileName(p.path)} x {p.x0:0}-{p.x1:0}", ["facade"] = c.facade,
                            ["path"] = p.path, ["sheet_x_range"] = new JsonArray(Math.Round(p.x0 - 500), Math.Round(p.x1 + 500)),
                            ["sheet_y_range"] = new JsonArray(Math.Round(p.y0 - 8000), Math.Round(p.y1 + 8000)),
                            ["along_direction"] = c.fit.Dir, ["matched"] = c.fit.Matched, ["elevationJambs"] = p.jambs.Count, ["modelJambs"] = modelJambs[c.facade].jambs.Count,
                            ["ambiguous"] = c.fit.Ambiguous, ["share"] = Math.Round(c.ratio, 2),
                        });
                    }
                    string written = null;
                    if (Args.Bool(a, "write") != false && chosen.Count > 0)
                    {
                        written = Path.Combine(ReportsFolder(doc), "elevation-index.json");
                        File.WriteAllText(written, new JsonObject
                        {
                            ["note"] = "Made by elevation_index: each elevation drawing matched to a facade from facades.json. Edit by hand if a match is wrong.",
                            ["when"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                            ["elevations"] = new JsonArray(chosen.Select(c => (JsonNode)c.DeepClone()).ToArray()),
                        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    }
                    var unmatchedParts = Enumerable.Range(0, parts.Count).Where(i => !usedParts.Contains(i))
                        .Select(i => $"{parts[i].title ?? "untitled"} ({Path.GetFileName(parts[i].path)} x {parts[i].x0:0}-{parts[i].x1:0}, {parts[i].jambs.Count} jambs)").ToList();
                    return new
                    {
                        matched = chosen, drawingsFound = parts.Count,
                        drawingsNotMatched = unmatchedParts.Count > 0 ? unmatchedParts : null,
                        facadesWithoutElevation = facades.Select(f => f["id"].GetValue<string>() + " " + f["name"]?.GetValue<string>()).Where(s => !usedFacades.Contains(s.Split(' ')[0])).ToList(),
                        skipped = skipped.Count > 0 ? skipped : null, written,
                        next = "survey_fidelity uses the index; dwg_elevation takes facade + sheet_x_range from it.",
                    };
                }
            };
        }

        private static double DistToSeg(Pt p, Pt a, Pt b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
            double t = l2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2));
            return p.Dist2D(new Pt(a.X + dx * t, a.Y + dy * t));
        }

        private sealed class FloorFixLog
        {
            public int WallsFitted, WallsFailed, Reshaped, Placed, GapsFilled, OpeningsFailed;
            public List<string> Problems = new List<string>();
            public List<string> Fallbacks = new List<string>();
            public void Took(IEnumerable<string> revitNotes, string what) => Fallbacks.AddRange(revitNotes.Where(n => n.StartsWith("Fallback:")).Select(n => what + ": " + n.Substring(9).Trim()));
            public List<string> Unconfirmed = new List<string>();
            public int Changed => WallsFitted + Reshaped + Placed + GapsFilled;
        }

        /// <summary>Walls fitted to the plan's face lines: the same lines the score measures against (Survey to Revit's reading, or the linked plan).</summary>
        private static void FixWalls(Document doc, Level level, ImportInstance imp, FidelityInput input, FloorFixLog log)
        {
            int refusedInARow = 0;
            foreach (var p in WallFit.Fit(input.Walls, input.WallFaces, new FitOptions()).Where(p => p.Confidence == "moderate"))
            {
                // 1.14.7: a floor where Revit keeps refusing wall moves is left for a person instead of retrying wall after wall
                if (refusedInARow >= MaxRefusedWallsInARow)
                {
                    log.Problems.Add($"walls: stopped fitting after {MaxRefusedWallsInARow} walls in a row that Revit refused; fit the rest with dwg_wall_fit_apply a few ids at a time, or stop the joins first (set_wall_joins)");
                    break;
                }
                var wall = doc.GetElement(new ElementId(p.Id)) as Wall;
                if (wall == null) continue;
                int failedBefore = log.WallsFailed;
                try
                {
                    void typeChange()
                    {
                        if (Math.Abs(p.NewThicknessMm - p.ThicknessMm) > 1)
                        {
                            var type = BuildTools.WallTypeOf(doc, p.NewThicknessMm);
                            if (type.Id != wall.WallType.Id) wall.ChangeTypeId(type.Id);
                        }
                    }
                    var lc = (LocationCurve)wall.Location;
                    double z = lc.Curve.GetEndPoint(0).Z;
                    var line = Line.CreateBound(new XYZ(p.NewLine[0] / Mm, p.NewLine[1] / Mm, z), new XYZ(p.NewLine[2] / Mm, p.NewLine[3] / Mm, z));
                    if (Math.Max(Math.Abs(p.ShiftStartMm), Math.Abs(p.ShiftEndMm)) > 1)
                        log.Took(BuildTools.SetWallLineSafely(doc, wall, line, "MCP: fit wall " + p.Id, typeChange), $"wall {p.Id}");
                    else BuildTools.Tx.Run(doc, "MCP: fit wall " + p.Id, typeChange);
                    if (wall.IsValidObject) log.WallsFitted++; else { log.WallsFailed++; log.Problems.Add($"wall {p.Id}: Revit removed it"); }
                }
                catch (Exception ex) { log.WallsFailed++; log.Problems.Add($"wall {p.Id}: {McpServer.Unwrap(ex).Message}"); }
                refusedInARow = log.WallsFailed > failedBefore ? refusedInARow + 1 : 0;
            }
        }

        private const int MaxRefusedWallsInARow = 4;

        /// <summary>
        /// Curved walls re-curved onto the DWG: the centre and radius the DWG faces give (CurvedFit), over the same
        /// angles; the thickness too when both faces were fitted. Only walls out of tolerance, and only modest moves.
        /// </summary>
        private static void FixArcWalls(Document doc, FidelityInput input, FloorFixLog log)
        {
            var gate = McpSettings.Load().FidelityGate ?? new FidelityGate();
            foreach (var w in input.ArcWalls)
            {
                var r = CurvedFit.Measure(w, CurvedFit.FacesWithArcs(input), gate.WallTolMm);
                if (!r.Verified || r.WorstFaceMm <= gate.WallTolMm || !(r.NewRadius is double nr) || r.NewCx == null) continue;
                double shift = Math.Sqrt(Math.Pow(r.NewCx.Value - w.Cx, 2) + Math.Pow(r.NewCy.Value - w.Cy, 2));
                if (Math.Abs(nr - w.Radius) > 300 || shift > 500) { log.Problems.Add($"curved wall {w.Id}: the DWG arc is {Math.Abs(nr - w.Radius):0} mm off in radius, {shift:0} mm in centre; left for a person"); continue; }
                var wall = doc.GetElement(new ElementId(w.Id)) as Wall;
                if (wall == null) continue;
                try
                {
                    BuildTools.Tx.Run(doc, "MCP: fit curved wall " + w.Id, () =>
                    {
                        if (r.NewThicknessMm is double nt && Math.Abs(nt - w.ThicknessMm) > gate.ThicknessMm)
                        {
                            var type = BuildTools.WallTypeOf(doc, Math.Round(nt));
                            if (type.Id != wall.WallType.Id) wall.ChangeTypeId(type.Id);
                        }
                        var lc = (LocationCurve)wall.Location;
                        double z = lc.Curve.GetEndPoint(0).Z;
                        double a0 = Math.Min(w.Start, w.Start + w.Sweep), a1 = Math.Max(w.Start, w.Start + w.Sweep);
                        lc.Curve = Arc.Create(new XYZ(r.NewCx.Value / Mm, r.NewCy.Value / Mm, z), nr / Mm, a0, a1, XYZ.BasisX, XYZ.BasisY);
                    });
                    log.WallsFitted++;
                }
                catch (Exception ex) { log.WallsFailed++; log.Problems.Add($"curved wall {w.Id}: {McpServer.Unwrap(ex).Message}"); }
            }
        }

        /// <summary>A floor's curved Basic walls, for the score.</summary>
        internal static List<ArcWall> ArcWallsOn(Document doc, Level level)
        {
            var list = new List<ArcWall>();
            foreach (var w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
            {
                if (w.LevelId != level.Id || w.WallType.Kind != WallKind.Basic || !(w.Location is LocationCurve lc) || !(lc.Curve is Arc arc) || !arc.IsBound) continue;
                var c = arc.Center; var p0 = arc.GetEndPoint(0); var p1 = arc.GetEndPoint(1); var pm = arc.Evaluate(0.5, true);
                double a0 = Math.Atan2(p0.Y - c.Y, p0.X - c.X), a1 = Math.Atan2(p1.Y - c.Y, p1.X - c.X), am = Math.Atan2(pm.Y - c.Y, pm.X - c.X);
                double Norm(double v) { v %= 2 * Math.PI; return v < 0 ? v + 2 * Math.PI : v; }
                double ccw = Norm(a1 - a0);
                double sweep = Norm(am - a0) < ccw ? ccw : -(2 * Math.PI - ccw);
                list.Add(new ArcWall { Id = w.Id.Value, Cx = c.X * Mm, Cy = c.Y * Mm, Radius = arc.Radius * Mm, Start = a0, Sweep = sweep, ThicknessMm = w.Width * Mm, Type = w.WallType.Name });
            }
            return list;
        }

        private static void FixOpenings(Document doc, Level level, FidelityInput input, FloorFixLog log, Dictionary<long, ElevMatch> elevById = null)
        {
            var wallById = input.Walls.ToDictionary(w => w.Id);
            foreach (var ins in input.Inserts.Where(i => i.Category != null && i.Category.IndexOf("Window", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var fi = doc.GetElement(new ElementId(ins.Id)) as FamilyInstance;
                if (fi == null) continue;
                double? newSill = null, newHeight = null, newWidth = null; XYZ slide = null;

                double curSill = ins.Sill ?? 0, curHead = ins.Head ?? 0;
                double? sl, hl;
                if (elevById != null && elevById.TryGetValue(ins.Id, out var em))
                {
                    // the elevation decides; a change of more than 600 mm is more likely a wrong pairing than a wrong window,
                    // unless the drawn opening is also this window's width (within 150 mm): then the pairing is sure, and a
                    // window Survey to Revit made 1.2 m too short is put right
                    // (and not a part of it: a transom light drawn alone is far shorter than the window)
                    bool sameWidth = Math.Abs(em.Width - ins.W) <= 150 && em.Head - em.Sill >= Math.Min(0.6 * (curHead - curSill), 1200);
                    bool sane = (sameWidth || Math.Abs(em.Sill - curSill) <= 600 && Math.Abs(em.Head - curHead) <= 600) && em.Head - em.Sill >= 300;
                    if (!sane) { log.Unconfirmed.Add($"{level.Name}: window {ins.Id}: the elevation gives sill {em.Sill:0} head {em.Head:0} against the model's {curSill:0}/{curHead:0}; left for a look"); sl = hl = null; }
                    else { sl = Math.Round(em.Sill); hl = Math.Round(em.Head); }
                }
                else (sl, hl) = Fidelity.LabelsFor(ins.At, input);
                if (sl is double s && Math.Abs(s - curSill) > 30) newSill = s;
                if (hl is double h && Math.Abs(h - curHead) > 30 || (newSill != null && hl != null)) newHeight = (hl ?? curHead) - (newSill ?? curSill);
                if (newHeight != null && newHeight < 300) newHeight = null;

                // width and place from the plan's masonry reveals (wall layer only), both within 150 mm
                var jambs = wallById.TryGetValue(ins.HostWallId, out var host) ? Fidelity.PlanJambs(host, ins, input.WallFaces) : null;
                if (jambs != null && Math.Abs(jambs.Value.left) <= 150 && Math.Abs(jambs.Value.right) <= 150 && (Math.Abs(jambs.Value.left) > 30 || Math.Abs(jambs.Value.right) > 30))
                {
                    double l = jambs.Value.left, r = jambs.Value.right;
                    double L = host.A.Dist2D(host.B), ux = (host.B.X - host.A.X) / L, uy = (host.B.Y - host.A.Y) / L;
                    newWidth = ins.W + r - l;
                    double shift = (l + r) / 2;
                    if (Math.Abs(shift) > 5) slide = new XYZ((ins.At.X + ux * shift) / Mm, (ins.At.Y + uy * shift) / Mm, 0);
                    if (newWidth < 300) { newWidth = null; slide = null; }
                }
                if (newSill == null && newHeight == null && newWidth == null && slide == null) continue;
                try
                {
                    log.Took(BuildTools.RunInsertChange(doc, "MCP: window " + ins.Id + " to the survey", ctx => BuildTools.ReshapeInsert(doc, fi, newWidth, newHeight, newSill, slide, ctx), canKeepSize: true), $"window {ins.Id}");
                    log.Reshaped++;
                }
                catch (Exception ex) { log.OpeningsFailed++; log.Problems.Add($"window {ins.Id}: {McpServer.Unwrap(ex).Message}"); }
            }
        }

        /// <summary>
        /// Windows Survey to Revit read on the plan that the model lacks (removed, or refused by Revit at the build):
        /// placed in the wall there, as wide as the plan, at the heights its labels give. Doors are left for a person
        /// (their family and swing matter), and listed.
        /// </summary>
        private static void PlacePlanMissing(Document doc, Level level, FidelityInput input, FloorFixLog log)
        {
            foreach (var po in input.PlanOpenings)
            {
                if (input.Inserts.Any(i => i.At.Dist2D(po.At) <= Math.Max(400, po.W / 2))) continue;
                if (po.Door) { log.Unconfirmed.Add($"{level.Name}: the plan has a {po.W:0} wide door at {po.At.X:0},{po.At.Y:0} the model lacks (place it by hand: family and swing)"); continue; }
                var (ls, lh) = Fidelity.LabelsFor(po.At, input);
                double? sill = ls ?? po.Sill, head = lh ?? po.Head;
                if (po.W < 300 || sill == null || head == null || head - sill < 300) { log.Unconfirmed.Add($"{level.Name}: a window at {po.At.X:0},{po.At.Y:0} on the plan has no usable size"); continue; }
                var host = input.Walls.Select(w => (w, d: DistToSeg(po.At, w.A, w.B))).Where(x => x.d <= x.w.ThicknessMm / 2 + 150).OrderBy(x => x.d).Select(x => x.w).FirstOrDefault();
                if (host == null) { log.Unconfirmed.Add($"{level.Name}: the plan has a window at {po.At.X:0},{po.At.Y:0} but no model wall there"); continue; }
                var item = new JsonObject
                {
                    ["category"] = "Windows", ["x"] = Math.Round(po.At.X), ["y"] = Math.Round(po.At.Y), ["level"] = level.Name,
                    ["width_mm"] = Math.Round(po.W), ["height_mm"] = Math.Round(head.Value - sill.Value), ["sill_mm"] = Math.Round(sill.Value),
                    ["wall_id"] = host.Id,
                    ["comments"] = "MCP|autofix|plan window (Survey to Revit's reading)" + (ls != null ? " + labels" : po.HeightsFromSurvey ? "" : ", heights assumed"),
                };
                try
                {
                    BuildTools.PlaceInsertSafely(doc, item, "MCP: place window from the plan", out var n);
                    log.Took(n, $"plan window at {po.At.X:0},{po.At.Y:0}");
                    log.Placed++;
                    input.Inserts.Add(new FidInsert { At = po.At, W = po.W, Category = "Windows", HostWallId = host.Id, Sill = sill, Head = head }); // the elevation pass must not place it again
                }
                catch (Exception ex) { log.OpeningsFailed++; log.Problems.Add($"plan window at {po.At.X:0},{po.At.Y:0}: {McpServer.Unwrap(ex).Message}"); }
            }
        }

        private static void PlaceMissing(Document doc, Level level, FidelityInput input, List<ElevMissing> missing, FloorFixLog log)
        {
            foreach (var m in missing)
            {
                if (m.Sill == null || m.Head == null || m.W < 300) continue;
                // the plan must agree: an opening in a wall there, both reveals within 150 mm of the elevation's.
                // Width and centre then come from the plan (it cuts through the opening), heights from the labels or the elevation.
                var at = new Pt(m.X, m.Y);
                var host = input.Walls.Select(w => (w, d: DistToSeg(at, w.A, w.B))).Where(x => x.d <= x.w.ThicknessMm / 2 + 400).OrderBy(x => x.d).Select(x => x.w).FirstOrDefault();
                var probe = new FidInsert { At = at, W = m.W, HostWallId = host?.Id ?? 0 };
                var jambs = host == null ? null : Fidelity.PlanJambs(host, probe, input.WallFaces);
                if (jambs == null || Math.Abs(jambs.Value.left) > 150 || Math.Abs(jambs.Value.right) > 150)
                {
                    log.Unconfirmed.Add($"{level.Name}: the elevation draws a {m.W:0} wide window at {m.X:0},{m.Y:0} (sill {m.Sill:0}) but the plan shows no opening there");
                    continue;
                }
                double L = host.A.Dist2D(host.B), ux = (host.B.X - host.A.X) / L, uy = (host.B.Y - host.A.Y) / L;
                double width = m.W + jambs.Value.right - jambs.Value.left, shift = (jambs.Value.left + jambs.Value.right) / 2;
                m.X += ux * shift; m.Y += uy * shift; m.W = width;
                var (ls, lh) = Fidelity.LabelsFor(new Pt(m.X, m.Y), input);
                if (ls != null) m.Sill = ls;
                if (lh != null) m.Head = lh;
                // not where a window already is (the elevation may have matched another storey's)
                var here = new XYZ(m.X / Mm, m.Y / Mm, level.Elevation);
                bool taken = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Windows).WhereElementIsNotElementType().OfType<FamilyInstance>()
                    .Any(f => f.LevelId == level.Id && f.Location is LocationPoint lp && new XYZ(lp.Point.X, lp.Point.Y, level.Elevation).DistanceTo(here) * Mm < Math.Max(400, m.W / 2));
                if (taken) continue;
                var item = new JsonObject
                {
                    ["category"] = "Windows", ["x"] = Math.Round(m.X), ["y"] = Math.Round(m.Y), ["level"] = level.Name,
                    ["width_mm"] = Math.Round(m.W), ["height_mm"] = Math.Round(m.Head.Value - m.Sill.Value), ["sill_mm"] = Math.Round(m.Sill.Value),
                    ["wall_id"] = host.Id,
                    ["comments"] = "MCP|autofix|elevation + plan reveals" + (ls != null ? " + labels" : ""),
                };
                try { BuildTools.PlaceInsertSafely(doc, item, "MCP: place window from the elevation", out var n); log.Took(n, $"window at {m.X:0},{m.Y:0}"); log.Placed++; }
                catch (Exception ex) { log.OpeningsFailed++; log.Problems.Add($"window at {m.X:0},{m.Y:0}: {McpServer.Unwrap(ex).Message}"); }
            }
        }

        private static void FillRhythmGaps(UIApplication app, Document doc, Level level, FidelityInput input, FloorFixLog log, bool tick)
        {
            var hub = App.Instance?.Hub;
            if (hub == null) return;
            string json;
            try { json = hub.Providers.CallHere(app, "survey_review", new JsonObject { ["level"] = level.Name, ["kind"] = "WindowRhythmGap", ["limit"] = 50 }); }
            catch (Exception ex) { log.Problems.Add("Survey to Revit's review list: " + ex.Message); return; }
            if (json == null) return; // Survey to Revit not loaded
            JsonArray items;
            try { items = JsonNode.Parse(json)?["items"] as JsonArray; } catch { return; }
            if (items == null) return;

            var windows = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Windows).WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();
            var ticked = new List<long>();
            foreach (var it in items.OfType<JsonObject>())
            {
                if (it["done"]?.GetValue<bool>() == true) continue;
                double x = it["x"]?.GetValue<double>() ?? double.NaN, y = it["y"]?.GetValue<double>() ?? double.NaN;
                if (double.IsNaN(x)) continue;
                var p = new XYZ(x / Mm, y / Mm, 0);
                // the window above or below at this place: its width and type; the sill and head from this floor's labels if any
                var twin = windows.Where(f => f.LevelId != level.Id && f.Location is LocationPoint lp && new XYZ(lp.Point.X, lp.Point.Y, 0).DistanceTo(p) * Mm < 600)
                    .OrderBy(f => Math.Abs(((LocationPoint)f.Location).Point.Z - level.Elevation)).FirstOrDefault();
                if (twin == null) continue;
                var (tw, th) = BuildTools.InsertSizeOf(doc, twin);
                var (sl, hl) = Fidelity.LabelsFor(new Pt(x, y), input);
                double sill = sl ?? (twin.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble() * Mm ?? 900);
                double height = hl is double h && sl is double s ? h - s : th ?? 1500;
                var item = new JsonObject
                {
                    ["category"] = "Windows", ["x"] = x, ["y"] = y, ["level"] = level.Name,
                    ["width_mm"] = tw ?? 1000, ["height_mm"] = height, ["sill_mm"] = sill,
                    ["comments"] = "MCP|autofix|window column gap" + (sl != null ? ", sill from label" : ", sill from the window " + (twin.LevelId == level.Id ? "" : "above/below")),
                };
                try
                {
                    BuildTools.PlaceInsertSafely(doc, item, "MCP: fill window column gap", out var n);
                    log.Took(n, $"column gap at {x:0},{y:0}");
                    log.GapsFilled++;
                    if (it["id"] is JsonNode id) ticked.Add(id.GetValue<long>());
                }
                catch (Exception ex) { log.OpeningsFailed++; log.Problems.Add($"column gap at {x:0},{y:0}: {McpServer.Unwrap(ex).Message}"); }
            }
            if (tick && ticked.Count > 0)
                try { hub.Providers.CallHere(app, "survey_review_tick", new JsonObject { ["ids"] = new JsonArray(ticked.Select(t => (JsonNode)t).ToArray()), ["note"] = "filled by survey_autofix from the window above or below" }); }
                catch { /* the list file only: never stops the fixes */ }
        }
    }
}
