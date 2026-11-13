// 1.22.0: facade_check: every facade check in one call (docs/FACADE-CHECKS.md 2.5), one line per check and one score per
// facade, the worst issues across them with the tool that fixes each. Ends every facade build step; survey_run calls it
// with facades: true.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BarrGazetas.RevitMcp.Survey;

namespace BarrGazetas.RevitMcp
{
    internal static partial class SurveyTools
    {
        internal static RevitTool FacadeCheckAll(IReadOnlyList<RevitTool> own, JsonSerializerOptions json) => new RevitTool
        {
            Name = "facade_check",
            Description = "Every facade check in one call, for one facade or all: facade_correspondence (coverage and precision against the " +
                          "elevation's lines), opening_clear_check (glass not blocked), facade_course_check (cornices, string courses, " +
                          "window rows at the drawn heights) and detail_support_check (no floating or buried detail). One line per check, " +
                          "one score per facade (the lowest of the four; 1:1 = every check 0.90 or more), and the worst issues across them, " +
                          "each with the tool that fixes it. Writes facade-check-<F>.json. Run it after every facade build step. Changes nothing.",
            InputSchema = Schema(
                ("facade", S("string", "Facade id (F1...) or all (default all: every facade in elevation-index.json)"), false),
                ("tol_mm", S("number", "Tolerance for the line checks (default 20)"), false),
                ("issues", S("integer", "Worst issues to list per facade (default 20)"), false)),
            Run = (app, a) =>
            {
                var doc = Doc(app);
                string want = Args.Str(a, "facade") ?? "all";
                int issues = Math.Clamp(Args.Int(a, "issues", 20), 1, 100);
                var ids = string.Equals(want, "all", StringComparison.OrdinalIgnoreCase)
                    ? ElevationSpecs(doc, null).Select(s => Args.Str(s, "facade")).Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    : new List<string> { want };
                if (ids.Count == 0) throw new ArgumentException("No facades in elevation-index.json: facade_list, then elevation_index, first.");
                JsonNode Call(string tool, JsonObject args)
                {
                    var t = own.FirstOrDefault(x => x.Name == tool) ?? throw new InvalidOperationException($"{tool} is not loaded in this hub.");
                    object r = t.Run(app, args);
                    return JsonNode.Parse(r is ToolContent tc ? tc.Text : r as string ?? JsonSerializer.Serialize(r, json));
                }
                double? D(JsonNode n, string k) { try { return n?[k]?.GetValue<double>(); } catch { return null; } }
                var facades = new List<object>(); int passing = 0;
                foreach (var fid in ids)
                {
                    var checks = new List<FacadeCheckLine>();
                    var found = new List<FacadeIssue>();
                    var raw = new JsonObject();
                    JsonNode Run(string tool, JsonObject args)
                    {
                        try { var r = Call(tool, args); raw[tool] = r?.DeepClone(); return r; }
                        catch (Exception ex) { checks.Add(new FacadeCheckLine { Check = tool, Error = McpServer.Unwrap(ex).Message }); return null; }
                    }
                    var tol = Args.Dbl(a, "tol_mm") ?? 20;
                    var corr = Run("facade_correspondence", new JsonObject { ["facade"] = fid, ["tol_mm"] = tol, ["list"] = issues });
                    if (corr != null)
                    {
                        if (D(corr, "coverage") is double cov) { checks.Add(new FacadeCheckLine { Check = "facade_correspondence", Name = "coverage", Score = cov, Detail = $"{cov:P0} of the elevation's lines have a model edge" });
                            checks.Add(new FacadeCheckLine { Check = "facade_correspondence", Name = "precision", Score = D(corr, "precision") ?? 0, Detail = $"{D(corr, "precision") ?? 0:P0} of the model's detail edges are drawn" }); }
                        else checks.Add(new FacadeCheckLine { Check = "facade_correspondence", Note = corr["note"]?.ToString() });
                        foreach (var e in (corr["shifted"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                            found.Add(new FacadeIssue { Kind = "shifted", Element = e["id"]?.ToString(), What = e.ToJsonString(), Weight = D(e, "lengthMm") ?? 0, Fix = "move it by its shiftMm: facade_shift_fix" });
                        foreach (var e in (corr["spurious"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                            found.Add(new FacadeIssue { Kind = "spurious", Element = e["id"]?.ToString(), What = e.ToJsonString(), Weight = 0.5 * (D(e, "lengthMm") ?? 0), Fix = "nothing drawn there: delete it or rebuild that detail" });
                        foreach (var g in (corr["gaps"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                            found.Add(new FacadeIssue { Kind = "gap", What = g.ToJsonString(), Weight = D(g, "lengthMm") ?? 0, Fix = "drawn but not modelled: facade_detail_map names it; build it with the tool its build hint gives" });
                    }
                    var open = Run("opening_clear_check", new JsonObject { ["facade"] = fid, ["list"] = issues });
                    if (open != null)
                    {
                        checks.Add(new FacadeCheckLine { Check = "opening_clear_check", Name = "openings clear", Score = D(open, "score") ?? 0, Detail = $"{open["clear"]} of {open["openings"]} model openings clear; {open["drawnWithNoModelOpening"]} drawn openings with no model window" });
                        foreach (var b in (open["blockedOpenings"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                            found.Add(new FacadeIssue { Kind = "blocked opening", Element = b["id"]?.ToString(), What = b.ToJsonString(), Weight = 5000 * (D(b, "share") ?? 0), Fix = "rebuild that facade's relief (facade_relief); if a wall blocks it, check the window's host" });
                        foreach (var m in (open["missing"] as JsonArray)?.OfType<JsonNode>() ?? Enumerable.Empty<JsonNode>())
                            found.Add(new FacadeIssue { Kind = "window missing", What = m.ToJsonString(), Weight = 3000, Fix = "survey_autofix (openings) or place_inserts" });
                    }
                    var course = Run("facade_course_check", new JsonObject { ["facade"] = fid, ["tol_mm"] = tol, ["list"] = issues });
                    if (course != null)
                    {
                        if (D(course, "carried") is double car) checks.Add(new FacadeCheckLine { Check = "facade_course_check", Name = "courses carried", Score = car, Detail = $"{car:P0} of the drawn courses' length at the drawn height; {course["windowsOffTheirDrawnOpening"]} windows off their drawn sill/head" });
                        else checks.Add(new FacadeCheckLine { Check = "facade_course_check", Note = course["note"]?.ToString() });
                        foreach (var c in (course["commonOffsets"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                            found.Add(new FacadeIssue { Kind = "part of facade out", What = c.ToJsonString(), Weight = 20000, Fix = c["likely"]?.ToString() });
                        foreach (var st in (course["stretches"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                        {
                            var u = st["u"] as JsonArray; double len = u != null && u.Count == 2 ? Math.Abs(u[1].GetValue<double>() - u[0].GetValue<double>()) : 0;
                            found.Add(new FacadeIssue { Kind = "course " + st["state"], What = st.ToJsonString(), Weight = len, Fix = st["state"]?.ToString() == "missing" ? "the band isn't modelled there (facade_profile)" : "move its elements by -dzMm or rebuild that band" });
                        }
                    }
                    var sup = Run("detail_support_check", new JsonObject { ["facade"] = fid, ["list"] = issues });
                    if (sup != null)
                    {
                        double n = D(sup, "elementsChecked") ?? 0, bad = (D(sup, "floatingElements") ?? 0) + (D(sup, "buriedElements") ?? 0);
                        checks.Add(new FacadeCheckLine { Check = "detail_support_check", Name = "detail supported", Score = n > 0 ? Math.Max(0, 1 - bad / n) : 1, Detail = $"{sup["floatingElements"]} floating, {sup["buriedElements"]} buried of {n} detail elements" });
                        foreach (var e in (sup["elements"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                            found.Add(new FacadeIssue { Kind = (D(e, "floating") ?? 0) > 0 ? "floating detail" : "buried detail", Element = e["id"]?.ToString(), What = e.ToJsonString(), Weight = 4000, Fix = "floating relief: rebuild it (facade_relief puts set-back detail on the roof); others: move back onto the wall or delete" });
                    }
                    var scored = checks.Where(c => c.Score.HasValue).ToList();
                    double score = scored.Count == 0 ? 0 : scored.Min(c => c.Score.Value);
                    var answer = new
                    {
                        facade = fid, score = Math.Round(score, 3), pass = scored.Count >= 4 && score >= 0.9,
                        checks = checks.Select(c => new { c.Check, c.Name, score = c.Score.HasValue ? Math.Round(c.Score.Value, 3) : (double?)null, c.Detail, c.Note, c.Error }).ToList(),
                        worst = found.OrderByDescending(f => f.Weight).Take(issues).Select(f => new { f.Kind, f.Element, f.Fix, what = Parse(f.What) }).ToList(),
                        issuesTotal = found.Count,
                    };
                    var file = new JsonObject { ["summary"] = JsonNode.Parse(JsonSerializer.Serialize(answer, json)), ["raw"] = raw };
                    File.WriteAllText(Path.Combine(ReportsFolder(doc), $"facade-check-{fid}.json"), file.ToJsonString(new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }));
                    facades.Add(answer); if (answer.pass) passing++;
                }
                return new
                {
                    facades, passing, of = facades.Count,
                    target = "every check 0.90 or more on every facade",
                    next = "Work each facade's worst issues top down: a 'part of facade out' is one cause (a level or the elevation's height fit), fix it before single elements. Full results in facade-check-<F>.json.",
                };
            }
        };

        private sealed class FacadeCheckLine { public string Check, Name, Detail, Note, Error; public double? Score; }
        private sealed class FacadeIssue { public string Kind, Element, What, Fix; public double Weight; }
    }
}
