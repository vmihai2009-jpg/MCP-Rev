using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace BarrGazetas.RevitMcp
{
    /// <summary>
    /// Tools offered by other add-ins. Any add-in loaded in Revit can offer some by having a public static class
    /// called McpBridge (any namespace) with:
    ///
    ///   public static string Name { get; }                                         // optional: what to call the add-in
    ///   public static string[][] List();                                            // rows: name, description, input schema (JSON), flags
    ///   public static string Call(UIApplication app, string name, string argsJson); // returns JSON text; throw to report a problem
    ///   public static string[][] Prompts();                                         // optional rows: name, description, text
    ///
    /// Flags (comma separated): "background" = needs no Revit and may run off Revit's thread (app is then null);
    /// "write" = changes the model; "long" = can take minutes.
    /// Only strings cross between the add-ins, so neither needs a reference to the other, and each can be updated
    /// on its own. List() is asked again on every listing, so an add-in that updates itself without a restart
    /// (as Survey to Revit does) shows its new tools at once.
    /// </summary>
    internal sealed class Providers
    {
        internal sealed class Provider
        {
            public string Name;
            public Assembly Assembly;
            public MethodInfo List, Call, Prompts;
            public string Problem;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<Assembly, Provider> _seen = new Dictionary<Assembly, Provider>();
        /// <summary>Runs a function on Revit's own thread, handing it the UIApplication (as an object here: no Revit in this file).</summary>
        private readonly Func<Func<object, string>, Task<string>> _onRevit;

        private static readonly string[] NotAddIns =
        {
            "System", "Microsoft", "mscorlib", "netstandard", "WindowsBase", "Presentation", "UIAutomation", "ReachFramework", "DirectWriteForwarder",
            "Autodesk", "RevitAPI", "RevitDB", "AdWindows", "UIFramework", "Xceed", "Newtonsoft", "Anonymously", "BGMCP",
        };

        public Providers(Func<Func<object, string>, Task<string>> onRevit) => _onRevit = onRevit;

        /// <summary>Every add-in found so far that has a McpBridge.</summary>
        public List<Provider> All()
        {
            lock (_gate)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (_seen.ContainsKey(assembly)) continue;
                    _seen[assembly] = Look(assembly);
                }
                return _seen.Values.Where(p => p != null).OrderBy(p => p.Name).ToList();
            }
        }

        private static Provider Look(Assembly assembly)
        {
            try
            {
                if (assembly.IsDynamic) return null;
                string name = assembly.GetName().Name ?? "";
                if (NotAddIns.Any(n => name.StartsWith(n, StringComparison.OrdinalIgnoreCase))) return null;
                Type bridge = assembly.GetExportedTypes().FirstOrDefault(t => t.Name == "McpBridge" && t.IsAbstract && t.IsSealed); // a static class
                if (bridge == null) return null;
                const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
                var list = bridge.GetMethod("List", Static, null, Type.EmptyTypes, null);
                var call = bridge.GetMethod("Call", Static);
                if (list == null || call == null || call.GetParameters().Length != 3) return null;
                string title = null;
                try { title = bridge.GetProperty("Name", Static)?.GetValue(null) as string; } catch { }
                return new Provider
                {
                    Name = string.IsNullOrWhiteSpace(title) ? name : title,
                    Assembly = assembly, List = list, Call = call,
                    Prompts = bridge.GetMethod("Prompts", Static, null, Type.EmptyTypes, null),
                };
            }
            catch { return null; } // an assembly that cannot be looked through has nothing to offer
        }

        public List<ToolInfo> Tools()
        {
            var tools = new List<ToolInfo>();
            foreach (var provider in All())
            {
                string[][] rows;
                try { rows = provider.List.Invoke(null, null) as string[][] ?? Array.Empty<string[]>(); provider.Problem = null; }
                catch (Exception ex) { provider.Problem = McpServer.Unwrap(ex).Message; continue; }
                foreach (var row in rows)
                {
                    if (row == null || row.Length < 2 || string.IsNullOrWhiteSpace(row[0])) continue;
                    string name = row[0];
                    var flags = (row.Length > 3 ? row[3] ?? "" : "").Split(',').Select(f => f.Trim().ToLowerInvariant()).ToList();
                    bool background = flags.Contains("background");
                    JsonObject schema = null;
                    try { schema = row.Length > 2 && !string.IsNullOrWhiteSpace(row[2]) ? JsonNode.Parse(row[2]) as JsonObject : null; } catch { }
                    tools.Add(new ToolInfo
                    {
                        Name = name,
                        Description = row[1] ?? "",
                        InputSchema = schema ?? McpServer.Obj(),
                        Provider = provider.Name,
                        Write = flags.Contains("write"),
                        Long = flags.Contains("long"),
                        Invoke = args =>
                        {
                            string json = args.ToJsonString();
                            Func<object, string> call = app => provider.Call.Invoke(null, new object[] { app, name, json }) as string ?? "";
                            return background ? Task.Run(() => call(null)) : _onRevit(call);
                        },
                    });
                }
            }
            return tools;
        }

        public List<PromptInfo> Prompts()
        {
            var prompts = new List<PromptInfo>();
            foreach (var provider in All().Where(p => p.Prompts != null))
            {
                try
                {
                    foreach (var row in provider.Prompts.Invoke(null, null) as string[][] ?? Array.Empty<string[]>())
                        if (row != null && row.Length >= 3 && !string.IsNullOrWhiteSpace(row[0]))
                            prompts.Add(new PromptInfo { Name = row[0], Description = row[1] ?? "", Text = row[2] ?? "", Provider = provider.Name });
                }
                catch { /* prompts are an extra */ }
            }
            return prompts;
        }
    }
}
