// 1.27.0: fidelity_dashboard: the whole 1:1 picture in one call, with history. Runs the scores (survey_fidelity, facade_check,
// curve_check, elevation_fit_check), turns each into a 0-1 share, and keeps one line per run in fidelity-history.csv, so
// each run says what moved since the last one. The overall score is the lowest share: the model is 1:1 only when every
// part is.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        private static readonly string[] DashParts = { "walls", "openings", "levels", "facades", "curves", "heights" };

        internal static RevitTool FidelityDashboard(IReadOnlyList<RevitTool> own, JsonSerializerOptions json) => new RevitTool
        {
            Name = "fidelity_dashboard",
            Description = "The whole 1:1 picture in one call, with history: runs survey_fidelity (walls within tolerance, openings, levels), " +
                          "facade_check on every indexed facade, curve_check and elevation_fit_check, turns each into a share from 0 to 1, and " +
                          "appends one line to fidelity-history.csv. Answers with each share, the change since the last run, the overall 1:1 " +
                          "score (the lowest share: 1:1 only when every part is) and which part to work on next. Slow on a big model (it runs " +
                          "every check); facades: false or floors to narrow it. Changes nothing in the model.",
            InputSchema = Schema(
                ("levels", Arr("string", "Floors for survey_fidelity and curve_check (default all)"), false),
                ("facades", S("boolean", "Run facade_check on every facade (default true)"), false),
                ("note", S("string", "A note for this line of the history (what changed since the last run)"), false),
                ("source", S("string", "As survey_fidelity"), false),
                ("survey_folder", S("string", "As survey_fidelity"), false)),
            Run = (app, a) =>
            {
                var doc = Doc(app);
                JsonObject Pass(JsonObject x) { foreach (var k in new[] { "source", "survey_folder", "levels" }) if (a[k] != null) x[k] = a[k].DeepClone(); return x; }
                var problems = new List<string>();
                JsonNode Call(string tool, JsonObject args)
                {
                    try
                    {
                        var t = own.FirstOrDefault(x => x.Name == tool) ?? throw new InvalidOperationException($"{tool} is not loaded");
                        object r = t.Run(app, args);
                        return JsonNode.Parse(r is ToolContent tc ? tc.Text : r as string ?? JsonSerializer.Serialize(r, json));
                    }
                    catch (Exception ex) { problems.Add($"{tool}: {McpServer.Unwrap(ex).Message}"); return null; }
                }
                double? D(JsonNode n) { try { return n?.GetValue<double>(); } catch { return null; } }
                (double over, double of) OutOf(JsonNode n) { var m = Regex.Match(n?.ToString() ?? "", @"(\d+) out of (\d+)"); return m.Success ? (double.Parse(m.Groups[1].Value), double.Parse(m.Groups[2].Value)) : (0, 0); }
                var share = new Dictionary<string, double?>(); var detail = new Dictionary<string, string>();

                var fid = Call("survey_fidelity", Pass(new JsonObject { ["max_issues"] = 0, ["write_report"] = false }));
                var floors = (fid?["floors"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
                if (floors.Count > 0)
                {
                    double len = floors.Sum(f => D(f["walls"]?["FaceLengthM"]) ?? 0);
                    share["walls"] = len > 0 ? floors.Sum(f => (D(f["walls"]?["FaceLengthM"]) ?? 0) * (D(f["walls"]?["withinTolPct"]) ?? 0) / 100) / len : null;
                    detail["walls"] = $"{floors.Count(f => f["pass"]?.GetValue<bool>() == true)} of {floors.Count} floors pass; wall faces within tolerance, by length";
                    double over = 0, of = 0;
                    foreach (var f in floors)
                    {
                        var o = f["openings"];
                        foreach (var k in new[] { "plan", "labels", "elevation" }) { var (x, y) = OutOf(o?[k]); over += x; of += y; }
                        double miss = (D(o?["Missing"]) ?? 0) + (D(o?["MissingOnPlan"]) ?? 0); over += miss; of += miss;
                    }
                    share["openings"] = of > 0 ? 1 - over / of : null;
                    detail["openings"] = $"{of - over:0} of {of:0} opening checks right (plan jambs, sill/head labels, elevations; missing count as wrong)";
                    var lv = floors.Select(f => D(f["levelCheck"]?["DevMm"])).Where(x => x.HasValue).ToList();
                    share["levels"] = lv.Count > 0 ? (double)lv.Count(x => Math.Abs(x.Value) <= 10) / lv.Count : null;
                    detail["levels"] = $"{lv.Count(x => Math.Abs(x.Value) <= 10)} of {lv.Count} floor levels within 10 mm of their spot heights";
                }
                if (Args.Bool(a, "facades") != false)
                {
                    var fc = Call("facade_check", new JsonObject { ["facade"] = "all", ["issues"] = 3 });
                    var fs = (fc?["facades"] as JsonArray)?.OfType<JsonObject>().Select(f => D(f["score"])).Where(x => x.HasValue).ToList();
                    if (fs != null && fs.Count > 0) { share["facades"] = fs.Average(x => x.Value); detail["facades"] = $"{fc["passing"]} of {fc["of"]} facades pass; mean of each facade's lowest check"; }
                }
                var cc = Call("curve_check", Pass(new JsonObject()));
                var cf = (cc?["floors"] as JsonArray)?.OfType<JsonObject>().ToList();
                if (cf != null)
                {
                    double all = cf.Sum(f => D(f["curves"]) ?? 0), ok = cf.Sum(f => D(f["curved"]) ?? 0);
                    share["curves"] = all > 0 ? ok / all : (double?)null;
                    detail["curves"] = all > 0 ? $"{ok:0} of {all:0} drawn curves built as one curved wall" : "no curved walls drawn";
                }
                var ef = Call("elevation_fit_check", new JsonObject());
                var ee = (ef?["elevations"] as JsonArray)?.OfType<JsonObject>().ToList();
                if (ee != null)
                {
                    var parts = ee.SelectMany(e => (e["parts"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>()).ToList();
                    share["heights"] = parts.Count > 0 ? (double)parts.Count(p => p["Agrees"]?.GetValue<bool>() == true || p["agrees"]?.GetValue<bool>() == true) / parts.Count : (double?)null;
                    detail["heights"] = parts.Count > 0 ? $"{share["heights"] * parts.Count:0} of {parts.Count} elevation parts agree with their height fit" : "no level marks on the elevations";
                }

                var scored = share.Where(kv => kv.Value.HasValue).ToList();
                double overall = scored.Count > 0 ? scored.Min(kv => kv.Value.Value) : 0;
                string worst = scored.OrderBy(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();

                // history: one line per run
                string hist = Path.Combine(ReportsFolder(doc), "fidelity-history.csv");
                Dictionary<string, double?> last = null;
                if (File.Exists(hist))
                {
                    var rows = File.ReadAllLines(hist).Where(l => l.Length > 0).ToList();
                    if (rows.Count >= 2)
                    {
                        var head = rows[0].Split(','); var prev = rows[^1].Split(',');
                        last = DashParts.ToDictionary(p => p, p => { int i = Array.IndexOf(head, p); return i >= 0 && i < prev.Length && double.TryParse(prev[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null; });
                        int oi = Array.IndexOf(head, "overall"); if (oi >= 0 && oi < prev.Length && double.TryParse(prev[oi], NumberStyles.Float, CultureInfo.InvariantCulture, out var ov)) last["overall"] = ov;
                    }
                }
                string F(double? v) => v.HasValue ? v.Value.ToString("0.000", CultureInfo.InvariantCulture) : "";
                if (!File.Exists(hist)) File.WriteAllText(hist, "time,model," + string.Join(",", DashParts) + ",overall,note\n");
                string note = (Args.Str(a, "note") ?? "").Replace(',', ';').Replace('\n', ' ');
                File.AppendAllText(hist, $"{DateTime.Now:yyyy-MM-dd HH:mm},{doc.Title.Replace(',', ';')}," + string.Join(",", DashParts.Select(p => F(share.TryGetValue(p, out var v) ? v : null))) + $",{F(overall)},{note}\n");

                return new
                {
                    overall = Math.Round(overall, 3), target = 0.95, oneToOne = overall >= 0.95,
                    parts = DashParts.Select(p => new
                    {
                        part = p, share = share.TryGetValue(p, out var v) && v.HasValue ? Math.Round(v.Value, 3) : (double?)null,
                        change = last != null && last.TryGetValue(p, out var lv) && lv.HasValue && share.TryGetValue(p, out var nv) && nv.HasValue ? Math.Round(nv.Value - lv.Value, 3) : (double?)null,
                        detail = detail.TryGetValue(p, out var d) ? d : "not measured",
                    }).ToList(),
                    overallChange = last != null && last.TryGetValue("overall", out var lo) && lo.HasValue ? Math.Round(overall - lo.Value, 3) : (double?)null,
                    workOnNext = worst == null ? null : worst switch
                    {
                        "walls" => "walls: survey_run (its causes, grouped) on the worst floor",
                        "openings" => "openings: survey_autofix (openings) / window_look",
                        "levels" => "levels: survey_floor_levels, survey_floor_steps",
                        "facades" => "facades: facade_check on the lowest facade, work its worst issues top down",
                        "curves" => "curves: curve_walls (list, then apply)",
                        "heights" => "heights: elevation_fit_check, split the elevations whose parts disagree",
                        _ => worst,
                    },
                    history = hist, problems = problems.Count > 0 ? problems : null,
                };
            }
        };
    }
}
