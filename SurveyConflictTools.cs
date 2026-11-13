// 1.31.0: survey_conflicts: where the survey's drawings disagree about the same thing, and which one wins. Each floor's
// height from the plan's spot heights and from every elevation's level marks; values further apart than tol_mm are a
// conflict, resolved by a declared precedence; every resolution is logged with its values so a person can overrule it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        public static IEnumerable<RevitTool> ConflictTools()
        {
            yield return new RevitTool
            {
                Name = "survey_conflicts",
                Description = "Where the survey's drawings disagree about the same height: each floor's level from its plan's spot heights (the " +
                              "commonest spot within reach_mm of the level) and from every elevation's level marks (elevation-index.json). Values " +
                              "further apart than tol_mm are a conflict, resolved by precedence (default plan, then elevation: the first with a " +
                              "value wins); each resolution is logged in survey-conflicts-log.csv with all its values, and the model level's " +
                              "difference from the chosen value is given (survey_floor_levels to move it). Changes nothing.",
                InputSchema = Schema(
                    ("precedence", Arr("string", "Sources in order of trust: plan, elevation, or one elevation as elevation:<facade> (default plan, elevation)"), false),
                    ("tol_mm", S("number", "Values further apart than this conflict (default 30)"), false),
                    ("reach_mm", S("number", "Spot heights and marks within this of a level count for it (default 1000)"), false),
                    ("source", S("string", "As survey_fidelity"), false),
                    ("survey_folder", S("string", "As survey_fidelity"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    var prec = Args.Strings(a, "precedence"); if (prec.Count == 0) prec = new List<string> { "plan", "elevation" };
                    double tol = Args.Dbl(a, "tol_mm") ?? 30, reach = Args.Dbl(a, "reach_mm") ?? 1000, datum = Datum(doc);
                    var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Select(l => (name: l.Name, z: l.Elevation * Mm + datum)).OrderBy(l => l.z).ToList();
                    string LevelOf(double z) { var n = levels.OrderBy(l => Math.Abs(l.z - z)).FirstOrDefault(); return n.name != null && Math.Abs(n.z - z) <= reach ? n.name : null; }
                    var values = new List<ConflictValue>(); var problems = new List<string>();
                    // plans: the commonest spot height near the plan's own level
                    foreach (var (level, imp) in PlanFloors(doc, new List<string>()))
                    {
                        double lz = level.Elevation * Mm + datum;
                        var near = PlanSpots(doc, imp).Select(s => s.aod).Where(z => Math.Abs(z - lz) <= reach).ToList();
                        if (Conflicts.Commonest(near) is double v) values.Add(new ConflictValue { Topic = level.Name, Source = "plan", ValueMm = Math.Round(v), Where = $"{near.Count} spot heights on {(SafePath(doc, imp) is string p ? Path.GetFileName(p) : "its plan")}" });
                    }
                    // elevations: each one's marks near each level
                    Func<IEnumerable<string>, Dictionary<string, string>> roles = null;
                    try { roles = ReadingFor(app, doc, a)?.Roles; } catch { }
                    foreach (var spec in ElevationSpecs(doc, null))
                    {
                        string fid = Args.Str(spec, "facade");
                        try
                        {
                            var xr = Args.Numbers(spec, "sheet_x_range");
                            double xMin = Math.Min(xr[0], xr[1]), xMax = Math.Max(xr[0], xr[1]);
                            var yr = Args.Numbers(spec, "sheet_y_range");
                            double yMin = yr.Count == 2 ? Math.Min(yr[0], yr[1]) : double.MinValue, yMax = yr.Count == 2 ? Math.Max(yr[0], yr[1]) : double.MaxValue;
                            var file = DwgFile.Read(Args.Str(spec, "path"));
                            var role = roles?.Invoke(file.Lines.Select(l => l.Layer).Concat(file.Texts.Select(t => t.Layer)));
                            bool IsLevel(string layer) => role != null && layer != null && role.TryGetValue(layer, out var r) && (r == "LevelText" || r == "HeightText") || DwgFile.LayerMatches(layer, new[] { "*Level*" });
                            var pairs = ElevationFit.MarkPairs(file.Texts.Where(t => IsLevel(t.Layer) && t.At.Y >= yMin && t.At.Y <= yMax),
                                file.Lines.Where(l => IsLevel(l.Layer) && l.A.X >= xMin && l.A.X <= xMax), xMin, xMax);
                            foreach (var g in pairs.GroupBy(p => LevelOf(p.z)).Where(g => g.Key != null))
                                if (Conflicts.Commonest(g.Select(p => p.z)) is double v)
                                    values.Add(new ConflictValue { Topic = g.Key, Source = "elevation:" + fid, ValueMm = Math.Round(v), Where = $"{g.Count()} level marks on {Path.GetFileName(Args.Str(spec, "path"))}" });
                        }
                        catch (Exception ex) { problems.Add($"elevation {fid}: {McpServer.Unwrap(ex).Message}"); }
                    }
                    var found = Conflicts.Resolve(values, prec, tol).OrderBy(f => levels.FindIndex(l => l.name == f.Topic)).ToList();
                    string folder = ReportsFolder(doc), log = Path.Combine(folder, "survey-conflicts-log.csv");
                    var sb = new StringBuilder();
                    if (!File.Exists(log)) sb.AppendLine("time,topic,chosen_source,chosen_mm,spread_mm,conflict,values");
                    string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                    foreach (var f in found)
                        sb.AppendLine($"{now},\"{f.Topic}\",{f.ChosenSource},{f.ChosenMm},{Math.Round(f.SpreadMm)},{f.Conflict},\"{string.Join("; ", f.Values.Select(v => $"{v.Source}={v.ValueMm}"))}\"");
                    File.AppendAllText(log, sb.ToString());
                    var answer = new
                    {
                        precedence = prec, tolMm = tol,
                        conflicts = found.Count(f => f.Conflict),
                        heights = found.Select(f =>
                        {
                            var lz = levels.First(l => l.name == f.Topic).z;
                            return new
                            {
                                level = f.Topic, f.Conflict, spreadMm = Math.Round(f.SpreadMm), chosen = f.ChosenSource, chosenMm = f.ChosenMm, rule = f.Rule,
                                modelMinusChosenMm = Math.Round(lz - f.ChosenMm),
                                values = f.Values.Select(v => new { v.Source, v.ValueMm, v.Where }).ToList(),
                            };
                        }).ToList(),
                        problems = problems.Count == 0 ? null : problems,
                        next = "A conflict: look at both drawings (dwg_image) before trusting the rule; to make another source win for the whole job, call again with precedence. modelMinusChosenMm far from 0: survey_floor_levels. Widths along facades are checked by facade_correspondence.",
                    };
                    File.WriteAllText(Path.Combine(folder, "survey-conflicts.json"), JsonSerializer.Serialize(answer, JsonOpts));
                    return answer;
                }
            };
        }
    }
}
