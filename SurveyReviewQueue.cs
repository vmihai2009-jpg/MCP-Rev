// 1.32.0: review_queue: every check's latest findings in one ranked "review these first" list, and where each built
// element came from (provenance from its Comments tag), with how sure the model is right there.
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
        public static IEnumerable<RevitTool> ReviewQueueTools()
        {
            yield return new RevitTool
            {
                Name = "review_queue",
                Description = "Review these first: the latest findings of every check this hub saved (facade_check, curve_check, registration_check, " +
                              "layer_roles_check, survey_conflicts, elevation_fit_check; and Survey to Revit's review list when review_list is given) " +
                              "merged into one ranked list. An element several checks question rises; each item gives its element id (select it), " +
                              "why, which report, the fix tool, and a confidence 0-1 that the model is right there. Also counts the model's elements " +
                              "by where they came from (Survey to Revit, an MCP tool, untagged = by hand). Reads saved reports: run the checks " +
                              "(fidelity_dashboard runs them all) first. Changes nothing.",
                InputSchema = Schema(
                    ("top", S("integer", "Items to return (default 25)"), false),
                    ("review_list", S("string", "Survey to Revit's review list JSON (its reports folder\\review\\<project>.json), to include its open items"), false)),
                Run = (app, a) =>
                {
                    var doc = Doc(app);
                    string folder = ReportsFolder(doc);
                    int top = (int)(Args.Dbl(a, "top") ?? 25);
                    var entries = new List<ReviewEntry>(); var read = new List<object>();
                    JsonNode Load(string name)
                    {
                        string p = Path.Combine(folder, name);
                        if (!File.Exists(p)) return null;
                        try { var n = JsonNode.Parse(File.ReadAllText(p)); read.Add(new { report = name, saved = File.GetLastWriteTime(p).ToString("yyyy-MM-dd HH:mm") }); return n; } catch { return null; }
                    }
                    static double Num(JsonNode n) { try { return n?.GetValue<double>() ?? 0; } catch { return double.TryParse(n?.ToString(), out var v) ? v : 0; } }
                    static long? Id(JsonNode n) => long.TryParse(n?.ToString(), out var v) ? v : (long?)null;
                    foreach (var p in Directory.GetFiles(folder, "facade-check-*.json"))
                    {
                        string name = Path.GetFileName(p), fid = name.Substring(13, name.Length - 18);
                        var s = Load(name)?["summary"];
                        foreach (var w in (s?["worst"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                        {
                            string kind = w["Kind"]?.ToString();
                            double weight = kind == "part of facade out" ? 20000 : kind == "blocked opening" ? 5000 : kind == "floating detail" || kind == "buried detail" ? 4000 : kind == "window missing" ? 3000 : Math.Max(500, Num(w["what"]?["lengthMm"]));
                            entries.Add(new ReviewEntry { Report = name, Kind = kind, Element = Id(w["Element"]), Where = "facade " + fid, Why = kind, Fix = w["Fix"]?.ToString(), Weight = weight });
                        }
                    }
                    foreach (var f in (Load("curve-check.json")?["floors"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                        foreach (var c in (f["items"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                        {
                            string st = c["State"]?.ToString();
                            entries.Add(new ReviewEntry { Report = "curve-check.json", Kind = "curve " + st, Where = $"{f["level"]} curve r {Math.Round(Num(c["faceRadiusMm"]))} mm at ({c["centre"]?[0]}, {c["centre"]?[1]})", Why = st == "missing" ? "a drawn curve with no wall" : st == "chorded" ? "a drawn curve built as straight pieces" : "a curve only partly built", Fix = st == "chorded" ? "curve_walls" : "build the wall on the curve (curve_walls / survey_autofix)", Weight = 3000 });
                        }
                    foreach (var f in (Load("registration-check.json")?["floors"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                        if (f["off"]?.ToString() == "true")
                            entries.Add(new ReviewEntry { Report = "registration-check.json", Kind = "floor plan off", Where = f["level"]?.ToString(), Why = "its plan does not sit on the others: every wall on it inherits the shift", Fix = "move its plan by the shift shown, then survey_autofix", Weight = 20000 });
                    foreach (var f in (Load("layer-roles-check.json")?["files"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                        foreach (var d in (f["disagreements"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                            entries.Add(new ReviewEntry { Report = "layer-roles-check.json", Kind = "layer role", Where = $"{f["file"]} layer {d["layer"]}", Why = d["issue"]?.ToString(), Fix = $"layers.txt: {d["suggest"]}", Weight = 8000 });
                    foreach (var h in (Load("survey-conflicts.json")?["heights"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                    {
                        if (h["Conflict"]?.ToString() == "true")
                            entries.Add(new ReviewEntry { Report = "survey-conflicts.json", Kind = "height conflict", Where = h["level"]?.ToString(), Why = $"drawings disagree by {h["spreadMm"]} mm; {h["chosen"]} chosen", Fix = "look at both drawings; survey_conflicts precedence to overrule", Weight = 2000 + 50 * Num(h["spreadMm"]) });
                        if (Math.Abs(Num(h["modelMinusChosenMm"])) > 10)
                            entries.Add(new ReviewEntry { Report = "survey-conflicts.json", Kind = "level off survey", Where = h["level"]?.ToString(), Why = $"model level {h["modelMinusChosenMm"]} mm from the survey", Fix = "survey_floor_levels", Weight = 1000 * Math.Min(20, Math.Abs(Num(h["modelMinusChosenMm"])) / 10) });
                    }
                    foreach (var el in (Load("elevation-fit-check.json")?["elevations"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                        foreach (var p in (el["parts"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                            if (p["Agrees"]?.ToString() == "false")
                                entries.Add(new ReviewEntry { Report = "elevation-fit-check.json", Kind = "elevation part off", Where = $"facade {el["facade"]} u {p["u"]?[0]}-{p["u"]?[1]}", Why = $"this part of the elevation is {p["offFitMm"]} mm off the height fit: its detail lands at the wrong height", Fix = "narrow sheet_x_range / index that part on its own (elevation_index)", Weight = 10000 });
                    string rl = Args.Str(a, "review_list");
                    if (rl != null && File.Exists(rl))
                        try
                        {
                            foreach (var it in (JsonNode.Parse(File.ReadAllText(rl))?["Items"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                                if (it["Done"]?.ToString() != "true")
                                    entries.Add(new ReviewEntry { Report = Path.GetFileName(rl), Kind = it["Kind"]?.ToString(), Where = $"{it["Level"]} at ({Math.Round(Num(it["X"]))}, {Math.Round(Num(it["Y"]))})", Why = it["Message"]?.ToString(), Fix = "Survey to Revit's review list", Weight = 1000 + 500 * Num(it["Priority"]) });
                            read.Add(new { report = Path.GetFileName(rl) });
                        }
                        catch { }
                    var queue = ReviewQueue.Rank(entries);
                    // where the model's elements came from
                    var cats = new[] { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors, BuiltInCategory.OST_Stairs, BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_Cornices, BuiltInCategory.OST_Reveals, BuiltInCategory.OST_Columns };
                    var prov = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(cats.ToList())).WhereElementIsNotElementType()
                        .Select(e => (cat: e.Category?.Name ?? "?", src: ReviewQueue.Provenance(e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()).by))
                        .GroupBy(x => x.src).Select(g => new { from = g.Key, elements = g.Count(), byCategory = g.GroupBy(x => x.cat).OrderByDescending(c => c.Count()).ToDictionary(c => c.Key, c => c.Count()) })
                        .OrderByDescending(x => x.elements).ToList();
                    string Label(long? id) => id is long v && doc.GetElement(new ElementId(v)) is Element e ? ReferenceTools.Label(doc, e) : null;
                    string From(long? id) => id is long v && doc.GetElement(new ElementId(v)) is Element e ? ReviewQueue.Provenance(e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()).by : null;
                    var answer = new
                    {
                        items = queue.Count, reportsRead = read,
                        reviewFirst = queue.Take(top).Select(i => new
                        {
                            rank = i.Rank, element = i.Element, label = Label(i.Element), builtBy = From(i.Element), where = i.Where, confidence = i.Confidence,
                            reasons = i.Reasons.Select(r => new { kind = r.Kind, why = r.Why, fix = r.Fix, report = r.Report }).ToList(),
                        }).ToList(),
                        provenance = prov,
                        note = read.Count == 0 ? "No saved check reports yet: run fidelity_dashboard (it runs every check), then review_queue." : null,
                        next = "Work top down: an item with a whole floor or facade part (no element) is one cause behind many elements, fix it first. Select an element by its id to look at it; confidence under 0.5 = check it before anything is built on it.",
                    };
                    File.WriteAllText(Path.Combine(folder, "review-queue.json"), JsonSerializer.Serialize(answer, JsonOpts));
                    return answer;
                }
            };
        }
    }
}
