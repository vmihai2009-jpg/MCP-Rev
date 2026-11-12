using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Autodesk.Revit.UI;

namespace BarrGazetas.RevitMcp
{
    /// <summary>
    /// Puts it together: the hub's own Revit tools, the tools of every other add-in that offers some, the server
    /// that Claude talks to, the settings and the call log.
    /// </summary>
    internal sealed class Hub
    {
        // A resolver set up front and frozen at once: shared options first used by two calls at the same time could
        // otherwise fail with "must specify a TypeInfoResolver" (seen from job_status in 1.3.0).
        private static readonly JsonSerializerOptions Compact = Frozen(new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        });

        private static JsonSerializerOptions Frozen(JsonSerializerOptions o) { o.MakeReadOnly(); return o; }

        private readonly RevitDispatcher _dispatcher;
        private readonly List<ToolInfo> _own;

        public McpSettings Settings { get; private set; }
        public CallLog Log { get; } = new CallLog();
        public Providers Providers { get; }
        public McpServer Server { get; }
        public static string Version => typeof(Hub).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        public Hub(RevitDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            Settings = McpSettings.Load();
            Log.Enabled = () => Settings.LogCalls;

            var revitTools = RevitTools.All().Concat(RevitTools.Repair()).Concat(BuildTools.All()).Concat(SurveyTools.All()).Concat(SurveyTools.Facade())
                .Concat(SurveyTools.Phase1()).Concat(SurveyTools.Phase2()).Concat(SurveyTools.Phase2Index()).Concat(SurveyTools.Phase3())
                .Concat(ReferenceTools.All()).Concat(HostTools.All()).Concat(SurveyTools.Phase4())
                .Concat(SurveyTools.Terrain()).Concat(RoomTools.All()).ToList();
            // batch runs the others (and other add-ins' tools, called directly: it is already on Revit's thread) as one undo step
            revitTools.Add(BatchTool.Make(revitTools, (app, tool, args) => Providers.CallHere(app, tool, args), Compact));
            _own = revitTools.Select(t => new ToolInfo
            {
                Name = t.Name, Description = t.Description, InputSchema = WithDocument(t), Write = t.Write,
                Invoke = async args => (string)await _dispatcher.InvokeAsync(app =>
                {
                    // read tools can look at a reference model opened with reference_open instead of the active project
                    string docName = t.Write || OwnDocArg(t.Name) ? null : Args.Str(args, "document");
                    object result;
                    using (Reference.Use(docName)) result = t.Run(app, args);
                    return result as string ?? JsonSerializer.Serialize(result, Compact);
                }),
            }).ToList();

            Providers = new Providers(async call => (string)await _dispatcher.InvokeAsync(app => call(app)));

            Server = new McpServer(Settings.Port, Tools, Prompts, new ServerOptions
            {
                Version = Version,
                ReadOnly = () => Settings.ReadOnly,
                OnCall = Log.Add,
                Status = Status,
            });
        }

        private static bool OwnDocArg(string name) => name == "reference_open" || name == "reference_close" || name == "reference_compare" || name == "reference_copy";

        /// <summary>Read tools get a "document" argument: look at a reference model instead of the active project.</summary>
        private static JsonObject WithDocument(RevitTool t)
        {
            if (t.Write || OwnDocArg(t.Name) || !(t.InputSchema?.DeepClone() is JsonObject schema)) return t.InputSchema;
            if (!(schema["properties"] is JsonObject props)) schema["properties"] = props = new JsonObject();
            if (!props.ContainsKey("document"))
                props["document"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Look at a reference model opened with reference_open (its name) instead of the active project",
                };
            return schema;
        }

        public IReadOnlyList<ToolInfo> Tools()
        {
            var taken = new HashSet<string>(_own.Select(t => t.Name));
            var all = new List<ToolInfo>(_own);
            foreach (var t in Providers.Tools())
                if (taken.Add(t.Name)) all.Add(t); // the hub's own names win; a provider keeps to its prefix
            return all;
        }

        public IReadOnlyList<PromptInfo> Prompts()
        {
            var prompts = new List<PromptInfo>
            {
                new PromptInfo
                {
                    Name = "revit_model_check",
                    Description = "A quick health check of the open Revit model.",
                    Text = "Check the Revit model that is open, and report briefly.\n" +
                           "1. mcp_status, then get_model_info: say what the model is and how big.\n" +
                           "2. get_warnings: the five commonest warnings and what they mean in practice.\n" +
                           "3. list_rooms: rooms not enclosed or unplaced, rooms with no name or number.\n" +
                           "4. query_elements for Doors and Windows with parameter_name Mark and empty parameter_contains: how many have no Mark.\n" +
                           "5. list_links: anything not loaded.\n" +
                           "Change nothing. Finish with a short list of what a person should look at first, with element ids so they can be selected.",
                },
                new PromptInfo
                {
                    Name = "survey_to_one_to_one",
                    Description = "Take survey DWGs to a 1:1 Revit model: Survey to Revit builds the base, then every wall, window, floor and facade detail is checked and corrected against the DWGs.",
                    Text = SurveyGuide.OneToOne,
                },
                new PromptInfo
                {
                    Name = "dwg_survey_build",
                    Description = "Build from a linked DWG by layer rules (dwg_analyze, dwg_build_model, survey_qa_check): the workflow, the actions, the limits and an example config.",
                    Text = SurveyGuide.ConfigGuide,
                },
            };
            prompts.AddRange(Providers.Prompts());
            return prompts;
        }

        private JsonObject Status()
        {
            var tools = Tools();
            var groups = new JsonArray();
            foreach (var g in tools.GroupBy(t => t.Provider))
                groups.Add(new JsonObject
                {
                    ["from"] = g.Key,
                    ["tools"] = g.Count(),
                    ["names"] = new JsonArray(g.Select(t => (JsonNode)t.Name).ToArray()),
                });
            var problems = Providers.All().Where(p => p.Problem != null).Select(p => (JsonNode)$"{p.Name}: {p.Problem}").ToArray();
            var status = new JsonObject { ["addIns"] = groups };
            var waiting = App.Instance?.WaitingVersion;
            if (waiting != null) status["updateInstalled"] = $"{waiting.ToString(3)} starts the next time Revit starts.";
            if (problems.Length > 0) status["problems"] = new JsonArray(problems);
            status["next"] = "get_model_info for the open project. Tools with a prefix (survey_...) come from other add-ins; their own status tool says where to start.";
            return status;
        }

        public void Apply(McpSettings changed)
        {
            Settings = changed;
            changed.Save();
        }

        public void Start() => Server.Start(Settings.Port);
        public void Stop() => Server.Stop();
    }
}
