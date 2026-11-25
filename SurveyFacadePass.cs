// 1.38.0: facade_detail_pass: the classical-facade detail of a survey built in ONE call, in the order that worked on
// 7 Swallow Place (docs/FACADE-DETAIL.md, docs/SURVEY-TO-1TO1.md step 5): BG Window family and window_look, then per
// facade facade_profile (bands from the section), facade_columns (pilasters/columns from the plan), facade_rustication,
// facade_types (one family per repeated piece), facade_relief (surrounds per window design, storey stonework); then the
// mansard, facade_check, and an optional save. Every step is one of the hub's own tools, so it is as editable and as
// generic as they are: nothing here knows a building, a layer or a facade id. survey_run facade_detail: true ends with it.
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
        private static readonly string[] FacadePassSteps =
            { "windows", "profile", "columns", "rustication", "types", "relief", "mansard", "check" };

        internal static RevitTool FacadePassTool(IReadOnlyList<RevitTool> own, JsonSerializerOptions json) => new RevitTool
        {
            Name = "facade_detail_pass",
            Write = true,
            Description = "The classical facade detail of a survey in ONE call, in the order that worked on 7 Swallow Place: the parametric BG Window family and window_look (windows look like the elevation), then for each facade in elevation-index.json: " +
                          "facade_profile (cornices, string courses, balustrade base from the section, as wall sweeps), facade_columns (pilasters and columns from the plan), facade_rustication (joints as reveals), facade_types (one family per repeated console, keystone, pediment, hood, sill, baluster), " +
                          "facade_relief (a surround family per window design and one per storey for the remaining stonework); then mansard_roof (once, from the sections facade_profile read) and facade_check. " +
                          "Each step is the tool of that name, so everything is editable families, sweeps and roofs, never DirectShapes. A step that fails or finds nothing is reported and the pass carries on. " +
                          "Needs facade_list and elevation_index (and section_index for the bands and mansard) first. Run it AFTER survey_autofix has fixed the windows' sizes. dry_run: each step only reports. Long: comes back as a job (job_status). Saves facade-pass-<time>.json in the reports folder.",
            InputSchema = Schema(
                ("facades", Arr("string", "Facade ids (default: every facade in elevation-index.json)"), false),
                ("steps", Arr("string", "Only these steps: windows, profile, columns, rustication, types, relief, mansard, check (default all, in that order)"), false),
                ("dry_run", S("boolean", "Each step lists what it would do and changes nothing (default false)"), false),
                ("save", S("boolean", "save_model at the end (default false; never with dry_run)"), false)),
            Run = (app, a) =>
            {
                bool dry = Args.Bool(a, "dry_run") == true;
                var doc = dry ? Doc(app) : Writable(app);
                string reports = ReportsFolder(doc);
                foreach (var need in new[] { "facades.json", "elevation-index.json" })
                    if (!File.Exists(Path.Combine(reports, need)))
                        throw new ArgumentException($"No {need} in the reports folder: run facade_list, then elevation_index with the elevation DWGs, first.");
                var steps = Args.Strings(a, "steps");
                var chosen = steps.Count == 0 ? FacadePassSteps.ToList() : FacadePassSteps.Where(s => steps.Any(x => x.Equals(s, StringComparison.OrdinalIgnoreCase))).ToList();
                if (chosen.Count == 0) throw new ArgumentException("steps must name some of: " + string.Join(", ", FacadePassSteps));
                var ids = Args.Strings(a, "facades");
                if (ids.Count == 0)
                    ids = ElevationSpecs(doc, null).Select(s => Args.Str(s, "facade")).Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (ids.Count == 0) throw new ArgumentException("No facades in elevation-index.json: facade_list, then elevation_index, first.");

                var log = new List<object>(); var full = new JsonObject();
                var problems = new List<string>();
                JsonNode Step(string key, string tool, JsonObject args)
                {
                    try
                    {
                        var t = own.FirstOrDefault(x => x.Name == tool) ?? throw new InvalidOperationException($"{tool} is not loaded in this hub.");
                        object r = t.Run(app, args);
                        var node = JsonNode.Parse(r is ToolContent tc ? tc.Text : r as string ?? JsonSerializer.Serialize(r, json));
                        full[key] = node?.DeepClone();
                        log.Add(new { step = key, ok = true, result = Brief(node) });
                        return node;
                    }
                    catch (Exception ex)
                    {
                        string why = McpServer.Unwrap(ex).Message;
                        full[key] = new JsonObject { ["error"] = why };
                        log.Add(new { step = key, ok = false, error = why });
                        problems.Add($"{key}: {why}");
                        return null;
                    }
                }

                if (chosen.Contains("windows"))
                {
                    if (!dry) Step("window_family", "window_family", new JsonObject { ["action"] = "build", ["load"] = true });
                    Step("window_look", "window_look", new JsonObject { ["facades"] = new JsonArray(ids.Select(i => (JsonNode)JsonValue.Create(i)).ToArray()), ["dry_run"] = dry });
                }
                foreach (var fid in ids)
                {
                    if (chosen.Contains("profile")) Step($"facade_profile {fid}", "facade_profile", new JsonObject { ["facade"] = fid, ["dry_run"] = dry });
                    if (chosen.Contains("columns")) Step($"facade_columns {fid}", "facade_columns", new JsonObject { ["facade"] = fid, ["dry_run"] = dry });
                    if (chosen.Contains("rustication")) Step($"facade_rustication {fid}", "facade_rustication", new JsonObject { ["facade"] = fid, ["apply"] = !dry });
                    if (chosen.Contains("types")) Step($"facade_types {fid}", "facade_types", new JsonObject { ["facade"] = fid, ["apply"] = !dry });
                    if (chosen.Contains("relief")) Step($"facade_relief {fid}", "facade_relief", new JsonObject { ["facade"] = fid, ["dry_run"] = dry });
                }
                if (chosen.Contains("mansard") && File.Exists(Path.Combine(reports, "mansard.json")))
                    Step("mansard_roof", "mansard_roof", new JsonObject { ["dry_run"] = dry });
                else if (chosen.Contains("mansard"))
                    log.Add(new { step = "mansard_roof", ok = true, result = (object)"skipped: no mansard.json (facade_profile found no mansard section)" });
                object check = null;
                if (chosen.Contains("check") && !dry)
                {
                    var c = Step("facade_check", "facade_check", new JsonObject { ["facade"] = "all", ["issues"] = 5 });
                    check = c?["facades"]?.DeepClone() ?? c?.DeepClone();
                }
                object saved = null;
                if (Args.Bool(a, "save") == true && !dry)
                    try { saved = JsonNode.Parse(JsonSerializer.Serialize(own.First(x => x.Name == "save_model").Run(app, new JsonObject()), json)); }
                    catch (Exception ex) { problems.Add("Not saved: " + McpServer.Unwrap(ex).Message); }

                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string file = Path.Combine(reports, $"facade-pass-{stamp}.json");
                try
                {
                    full["when"] = stamp; full["version"] = Hub.Version; full["dryRun"] = dry; full["facades"] = new JsonArray(ids.Select(i => (JsonNode)JsonValue.Create(i)).ToArray());
                    File.WriteAllText(file, full.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                catch { file = null; }
                return new
                {
                    dryRun = dry, facades = ids, steps = chosen, log, check, problems = problems.Count > 0 ? problems : null, saved, detail = file,
                    next = dry ? "Read the log; run again without dry_run." : "Look at each facade with check_view + view_image beside its elevation; the worst issues from facade_check each name the tool that fixes them.",
                    undo = dry ? "dry run: nothing kept" : "Each step is its own undo step(s) in Revit.",
                };
            }
        };

        /// <summary>A step's answer cut down to its scalar fields (the full answer goes to the pass's JSON file).</summary>
        private static JsonObject Brief(JsonNode n)
        {
            var o = new JsonObject();
            if (n is not JsonObject src) return o;
            foreach (var kv in src)
            {
                if (kv.Value is JsonValue v)
                {
                    string s = v.ToString();
                    o[kv.Key] = s.Length > 160 ? s.Substring(0, 160) + "..." : v.DeepClone();
                }
                else if (kv.Value is JsonArray arr) o[kv.Key + "Count"] = arr.Count;
            }
            return o;
        }
    }
}
