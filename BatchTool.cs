using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BarrGazetas.RevitMcp
{
    /// <summary>
    /// 1.10.0: several tool calls as ONE undo step (a Revit TransactionGroup round each call's own transaction). If a
    /// call fails, everything before it is undone too, so a half-done correction never stays in the model. A dry run
    /// does it all and undoes it all, to see what each call would do. A later call can use an earlier one's result:
    /// "$0.floor_id" is the floor_id the first call returned.
    /// </summary>
    internal static class BatchTool
    {
        private static readonly string[] NotInBatch = { "batch", "save_model", "mcp_reload", "reference_open", "reference_close", "reference_copy", "job_status", "mcp_status" };
        private static readonly Regex Ref = new Regex(@"^\$(\d+)\.([A-Za-z0-9_.\[\]]+)$");

        public static RevitTool Make(IReadOnlyList<RevitTool> own, Func<object, string, JsonObject, string> other, JsonSerializerOptions json) => new RevitTool
        {
            Name = "batch",
            Write = true,
            Description = "Run several tool calls as ONE undo step in Revit: e.g. move a wall, fit its windows and remake the floor edge, undone together with one Ctrl+Z. " +
                          "If any call fails, all of them are undone and the error says which (unless keep_going). dry_run: do them all, report each result, undo everything. " +
                          "A later call can use an earlier result: an argument \"$0.floor_id\" is the first call's floor_id (\"$1.results[0].room_id\" works too). " +
                          "Not for save_model, reference_open/close, mcp_reload. At most 50 calls.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["calls"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "In order: [{ \"tool\": \"create_floor\", \"arguments\": { ... } }, ...]",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["tool"] = new JsonObject { ["type"] = "string" },
                                ["arguments"] = new JsonObject { ["type"] = "object" },
                            },
                            ["required"] = new JsonArray("tool"),
                        },
                    },
                    ["name"] = new JsonObject { ["type"] = "string", ["description"] = "What the undo step is called in Revit (default: MCP: batch of N)" },
                    ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "Do everything, report, undo everything (default false)" },
                    ["keep_going"] = new JsonObject { ["type"] = "boolean", ["description"] = "On a failure, carry on with the rest and keep what worked (default false: undo all)" },
                },
                ["required"] = new JsonArray("calls"),
            },
            Run = (app, a) => Run(app, a, own, other, json),
        };

        private static object Run(UIApplication app, JsonObject a, IReadOnlyList<RevitTool> own, Func<object, string, JsonObject, string> other, JsonSerializerOptions json)
        {
            var doc = app?.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No Revit project is open. Ask the user to open one.");
            if (doc.IsReadOnly) throw new InvalidOperationException("This project is read-only.");
            if (doc.IsModifiable) throw new InvalidOperationException("Another command is still changing the model. Ask the user to finish it in Revit, then try again.");
            var calls = (a["calls"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            if (calls.Count == 0) throw new ArgumentException("calls is needed: [{ \"tool\": ..., \"arguments\": { ... } }].");
            if (calls.Count > 50) throw new ArgumentException("At most 50 calls in one batch.");
            foreach (var c in calls)
            {
                string n = c["tool"]?.ToString();
                if (string.IsNullOrEmpty(n)) throw new ArgumentException("Each call needs tool.");
                if (NotInBatch.Contains(n)) throw new ArgumentException($"{n} cannot run inside a batch.");
            }
            bool dry = a["dry_run"] is JsonValue dv && dv.ToString() == "true";
            bool keepGoing = a["keep_going"] is JsonValue kv && kv.ToString() == "true";
            string name = a["name"]?.ToString() is string s && s.Length > 0 ? (s.StartsWith("MCP") ? s : "MCP: " + s) : $"MCP: batch of {calls.Count}";

            var results = new List<JsonNode>();
            var report = new List<object>();
            int failed = -1; string error = null;
            using (var g = new TransactionGroup(doc, name))
            {
                g.Start();
                try
                {
                    for (int i = 0; i < calls.Count; i++)
                    {
                        string tool = calls[i]["tool"].ToString();
                        try
                        {
                            var args = Resolve(calls[i]["arguments"]?.DeepClone() as JsonObject ?? new JsonObject(), results) as JsonObject;
                            string text;
                            var t = own.FirstOrDefault(x => x.Name == tool);
                            if (t != null)
                            {
                                object r = t.Run(app, args);
                                text = r is ToolContent tc ? tc.Text : r as string ?? JsonSerializer.Serialize(r, json); // pictures are left out of a batch
                            }
                            else text = other(app, tool, args) ?? throw new ArgumentException($"There is no tool \"{tool}\".");
                            JsonNode node;
                            try { node = JsonNode.Parse(text); } catch { node = JsonValue.Create(text); }
                            results.Add(node);
                            report.Add(new { call = i, tool, ok = true, result = node });
                        }
                        catch (Exception ex)
                        {
                            string why = McpServer.Unwrap(ex).Message;
                            results.Add(null);
                            report.Add(new { call = i, tool, ok = false, error = why });
                            if (!keepGoing) { failed = i; error = why; break; }
                        }
                    }
                }
                catch
                {
                    if (g.HasStarted() && !g.HasEnded()) g.RollBack();
                    throw;
                }
                if (dry || failed >= 0) g.RollBack();
                else g.Assimilate();
            }
            int ok = report.Count(r => (bool)r.GetType().GetProperty("ok").GetValue(r));
            return new
            {
                done = !dry && failed < 0, calls = calls.Count, succeeded = ok,
                undoneBecause = failed >= 0 ? $"call {failed} ({calls[failed]["tool"]}) failed: {error}" : dry ? "dry run" : null,
                results = report,
                undo = failed >= 0 ? "Nothing was changed: every call was undone." : dry ? "Dry run: done and undone, nothing changed." : $"One undo step in Revit: \"{name}\".",
            };
        }

        /// <summary>"$i.path" strings replaced by that part of call i's result.</summary>
        private static JsonNode Resolve(JsonNode node, List<JsonNode> results)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var key in o.Select(kv => kv.Key).ToList()) o[key] = Resolve(o[key]?.DeepClone(), results);
                    return o;
                case JsonArray arr:
                    for (int i = 0; i < arr.Count; i++) arr[i] = Resolve(arr[i]?.DeepClone(), results);
                    return arr;
                case JsonValue v when v.TryGetValue(out string s) && Ref.Match(s) is Match m && m.Success:
                    int idx = int.Parse(m.Groups[1].Value);
                    if (idx >= results.Count || results[idx] == null) throw new ArgumentException($"{s}: call {idx} has no result yet.");
                    JsonNode cur = results[idx];
                    foreach (Match part in Regex.Matches(m.Groups[2].Value, @"([A-Za-z0-9_]+)|\[(\d+)\]"))
                    {
                        if (part.Groups[1].Success) cur = (cur as JsonObject)?[part.Groups[1].Value];
                        else cur = cur is JsonArray ca && int.Parse(part.Groups[2].Value) < ca.Count ? ca[int.Parse(part.Groups[2].Value)] : null;
                        if (cur == null) throw new ArgumentException($"{s}: call {idx}'s result has no such value.");
                    }
                    return cur.DeepClone();
                default:
                    return node;
            }
        }
    }
}
