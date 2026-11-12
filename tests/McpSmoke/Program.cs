using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BarrGazetas.RevitMcp;

namespace McpSmoke
{
    /// <summary>A stand-in for another add-in: found by the hub the same way Survey to Revit's bridge is.</summary>
    public static class McpBridge
    {
        public static string Name => "Smoke add-in";

        public static string[][] List() => new[]
        {
            new[] { "smoke_echo", "Says back what it was given.", "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[]}", "background" },
            new[] { "smoke_slow", "Takes a few seconds.", "{\"type\":\"object\",\"properties\":{\"seconds\":{\"type\":\"number\"}},\"required\":[]}", "background,long" },
            new[] { "smoke_boom", "Always fails.", "{\"type\":\"object\",\"properties\":{},\"required\":[]}", "background" },
            new[] { "smoke_write", "Would change the model.", "", "write" },
            new[] { "smoke_revit", "Runs on the Revit thread.", "not json", "" },
            new[] { "smoke_big", "A very long answer.", "", "background" },
            new[] { "smoke_image", "An answer with a picture.", "", "background" },
            new[] { "bad name!", "Must be left out.", "", "" },
        };

        public static string Call(object app, string name, string argsJson)
        {
            var args = JsonNode.Parse(argsJson) as JsonObject ?? new JsonObject();
            switch (name)
            {
                case "smoke_echo": return new JsonObject { ["echo"] = args["text"]?.ToString(), ["app"] = app?.ToString() }.ToJsonString();
                case "smoke_slow": Thread.Sleep(TimeSpan.FromSeconds(double.Parse(args["seconds"]?.ToString() ?? "3"))); return "{\"slept\":true}";
                case "smoke_boom": throw new InvalidOperationException("the wall fell over");
                case "smoke_write": return "{\"written\":true}";
                case "smoke_revit": return new JsonObject { ["app"] = app?.ToString() }.ToJsonString();
                case "smoke_big": return new string('x', 200000);
                case "smoke_image":
                {
                    var c = new ToolContent { Text = "{\"drawn\":1}" };
                    c.Images.Add(("image/png", BarrGazetas.RevitMcp.Survey.LineImage.Encode(new byte[] { 255, 0, 0, 0, 255, 0 }, 2, 1)));
                    return c.Encode();
                }
                default: throw new ArgumentException("no such tool");
            }
        }

        public static string[][] Prompts() => new[] { new[] { "smoke_prompt", "A prompt from an add-in.", "Do the smoke thing." } };
    }

    internal static class Program
    {
        private static int _failed, _passed;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static string _url;
        private static int _id;

        private static void Check(string what, bool ok, string detail = null)
        {
            if (ok) { _passed++; Console.WriteLine("  ok    " + what); }
            else { _failed++; Console.WriteLine("  FAIL  " + what + (detail == null ? "" : "\n          " + detail)); }
        }

        private static async Task<JsonObject> Rpc(string method, JsonObject p = null)
        {
            var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++_id, ["method"] = method, ["params"] = p ?? new JsonObject() };
            var res = await Http.PostAsync(_url, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
            return JsonNode.Parse(await res.Content.ReadAsStringAsync()) as JsonObject;
        }

        private static async Task<(string text, bool isError)> Tool(string name, JsonObject args = null)
        {
            var r = await Rpc("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args ?? new JsonObject() });
            var result = r["result"] as JsonObject;
            return (result?["content"]?[0]?["text"]?.ToString(), result?["isError"]?.GetValue<bool>() == true);
        }

        private static async Task<int> Main(string[] argv)
        {
            bool readOnly = false;
            var records = new List<CallRecord>();
            var providers = new Providers(call => Task.Run(() => call("the-revit-app")));
            var own = new List<ToolInfo>
            {
                new ToolInfo { Name = "get_model_info", Description = "Own tool.", InputSchema = McpServer.Obj(), Invoke = _ => Task.FromResult("{\"title\":\"Test\"}") },
            };
            int port = 48990 + new Random().Next(0, 500);
            var server = new McpServer(port, () => own.Concat(providers.Tools()).ToList(), () => providers.Prompts(), new ServerOptions
            {
                Version = "9.9.9", ReadOnly = () => readOnly, OnCall = r => { lock (records) records.Add(r); },
                SyncWait = TimeSpan.FromSeconds(1.5), Status = () => new JsonObject { ["addIns"] = providers.All().Count },
            });
            server.Start();
            _url = server.Url;
            Console.WriteLine("Server at " + _url);

            // ---- handshake ----
            var init = await Rpc("initialize", new JsonObject { ["protocolVersion"] = "2025-03-26", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "smoke" } });
            Check("initialize echoes a protocol version it supports", init["result"]?["protocolVersion"]?.ToString() == "2025-03-26");
            Check("initialize offers tools and prompts", init["result"]?["capabilities"]?["tools"] != null && init["result"]?["capabilities"]?["prompts"] != null);
            Check("initialize gives instructions", (init["result"]?["instructions"]?.ToString() ?? "").Contains("mcp_status"));
            var unknownVersion = await Rpc("initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" });
            Check("an unknown protocol version gets the latest", unknownVersion["result"]?["protocolVersion"]?.ToString() == "2025-06-18");

            var note = await Http.PostAsync(_url, new StringContent("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", Encoding.UTF8, "application/json"));
            Check("a notification gets 202 and no body", note.StatusCode == HttpStatusCode.Accepted);
            Check("ping answers", (await Rpc("ping"))["result"] != null);
            Check("an unknown method is a JSON-RPC error", (await Rpc("nope/nope"))["error"]?["code"]?.GetValue<int>() == -32601);
            var bad = await Http.PostAsync(_url, new StringContent("{not json", Encoding.UTF8, "application/json"));
            Check("bad JSON gets 400", bad.StatusCode == HttpStatusCode.BadRequest);
            Check("GET /mcp is refused (no event stream)", (await Http.GetAsync(_url)).StatusCode == HttpStatusCode.MethodNotAllowed);
            var status = JsonNode.Parse(await Http.GetStringAsync(_url.Replace("/mcp", "/")));
            Check("GET / is a small status page", status["version"]?.ToString() == "9.9.9");
            var evil = new HttpRequestMessage(HttpMethod.Post, _url) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            evil.Headers.Add("Origin", "https://evil.example");
            Check("a web page from elsewhere is refused (403)", (await Http.SendAsync(evil)).StatusCode == HttpStatusCode.Forbidden);
            var local = new HttpRequestMessage(HttpMethod.Post, _url) { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", Encoding.UTF8, "application/json") };
            local.Headers.Add("Origin", "http://localhost:5173");
            Check("a local page is allowed", (await Http.SendAsync(local)).StatusCode == HttpStatusCode.OK);

            // ---- tools ----
            var tools = ((await Rpc("tools/list"))["result"]?["tools"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            var names = tools.Select(t => t["name"]?.ToString()).ToList();
            Check("tools/list has the server's own, the hub's and the add-in's tools",
                new[] { "mcp_status", "job_status", "get_model_info", "smoke_echo", "smoke_revit" }.All(names.Contains), string.Join(", ", names));
            Check("a tool with a bad name is left out", !names.Contains("bad name!"));
            Check("add-in tools are labelled with where they come from", tools.First(t => t["name"].ToString() == "smoke_echo")["description"].ToString().StartsWith("[Smoke add-in]"));
            Check("a broken schema falls back to an empty object schema", tools.First(t => t["name"].ToString() == "smoke_revit")["inputSchema"]?["type"]?.ToString() == "object");
            Check("write tools are marked", tools.First(t => t["name"].ToString() == "smoke_write")["annotations"]?["readOnlyHint"]?.GetValue<bool>() == false);
            Check("every tool has an object schema", tools.All(t => t["inputSchema"]?["type"]?.ToString() == "object"));

            var echo = await Tool("smoke_echo", new JsonObject { ["text"] = "hello" });
            Check("a background tool runs with no Revit application", !echo.isError && echo.text.Contains("\"echo\":\"hello\"") && !echo.text.Contains("the-revit-app"), echo.text);
            var revit = await Tool("smoke_revit");
            Check("a Revit tool is handed the application on the Revit thread", revit.text.Contains("the-revit-app"), revit.text);
            var boom = await Tool("smoke_boom");
            Check("a failing tool reports its own message, not a wrapper", boom.isError && boom.text == "Error: the wall fell over", boom.text);
            var missing = await Tool("no_such_tool");
            Check("an unknown tool is a tool error", missing.isError);
            var big = await Tool("smoke_big");
            Check("a huge answer is cut with a note", !big.isError && big.text.Length < 61000 && big.text.Contains("cut: the full answer is 200000"), big.text.Length.ToString());

            var img = await Rpc("tools/call", new JsonObject { ["name"] = "smoke_image", ["arguments"] = new JsonObject() });
            var imgContent = img["result"]?["content"] as JsonArray;
            Check("an answer with a picture comes back as text and image content",
                imgContent?.Count == 2 && imgContent[0]?["text"]?.ToString() == "{\"drawn\":1}" && imgContent[1]?["type"]?.ToString() == "image"
                && imgContent[1]?["mimeType"]?.ToString() == "image/png" && Convert.FromBase64String(imgContent[1]?["data"]?.ToString() ?? "").Take(4).SequenceEqual(new byte[] { 137, 80, 78, 71 }), img.ToJsonString().Substring(0, Math.Min(300, img.ToJsonString().Length)));

            // ---- read-only ----
            var write = await Tool("smoke_write");
            Check("a write tool runs when changes are allowed", !write.isError && write.text.Contains("written"), write.text);
            readOnly = true;
            var refused = await Tool("smoke_write");
            Check("a write tool is refused in read-only mode", refused.isError && refused.text.Contains("read-only"), refused.text);
            var stillReads = await Tool("smoke_echo", new JsonObject { ["text"] = "x" });
            Check("reading still works in read-only mode", !stillReads.isError);
            Check("mcp_status says read-only", (await Tool("mcp_status")).text.Contains("\"readOnly\":true"));
            readOnly = false;

            // ---- jobs ----
            var slow = await Tool("smoke_slow", new JsonObject { ["seconds"] = 4 });
            var job = JsonNode.Parse(slow.text) as JsonObject;
            Check("a slow call comes back as a running job", !slow.isError && job?["status"]?.ToString() == "running" && job["job"] != null, slow.text);
            string jobId = job?["job"]?.ToString();
            Check("mcp_status lists the running job", (await Tool("mcp_status")).text.Contains(jobId ?? "?"));
            var waiting = await Tool("job_status", new JsonObject { ["job"] = jobId, ["wait_seconds"] = 0 });
            Check("job_status says running while it runs", waiting.text.Contains("\"status\":\"running\""), waiting.text);
            var done = await Tool("job_status", new JsonObject { ["job"] = jobId, ["wait_seconds"] = 20 });
            Check("job_status hands over the result when it finishes", !done.isError && done.text.Contains("slept"), done.text);
            var gone = await Tool("job_status", new JsonObject { ["job"] = jobId });
            Check("a job is forgotten once read", gone.isError);
            var quick = await Tool("smoke_slow", new JsonObject { ["seconds"] = 0.2 });
            Check("a quick call is answered directly, not as a job", quick.text.Contains("slept"), quick.text);

            // ---- prompts ----
            var prompts = (await Rpc("prompts/list"))["result"]?["prompts"] as JsonArray;
            Check("prompts/list has the add-in's prompt", prompts?.Any(p => p["name"]?.ToString() == "smoke_prompt") == true);
            var prompt = await Rpc("prompts/get", new JsonObject { ["name"] = "smoke_prompt" });
            Check("prompts/get returns its text as a user message", prompt["result"]?["messages"]?[0]?["content"]?["text"]?.ToString() == "Do the smoke thing.");
            Check("an unknown prompt is an error", (await Rpc("prompts/get", new JsonObject { ["name"] = "zzz" }))["error"] != null);

            // ---- the log ----
            await Task.Delay(300);
            lock (records)
            {
                Check("every tool call is recorded (job_status and unknown tools are not calls)", records.Count == 12, records.Count.ToString());
                Check("a change refused in read-only mode is recorded", records.Any(r => r.Tool == "smoke_write" && !r.Ok && r.Error.Contains("read-only")));
                Check("a failure is recorded with its message", records.Any(r => r.Tool == "smoke_boom" && !r.Ok && r.Error == "the wall fell over"));
                Check("a job is recorded when it finishes, with its time", records.Any(r => r.Tool == "smoke_slow" && r.Job == jobId && r.Ok && r.Ms >= 3500));
                Check("records carry the add-in's name", records.Any(r => r.Provider == "Smoke add-in"));
            }

            server.Stop();
            bool refusedAfterStop;
            try { await Http.GetAsync(_url.Replace("/mcp", "/")); refusedAfterStop = false; } catch { refusedAfterStop = true; }
            Check("nothing listens after it is switched off", refusedAfterStop);

            // ---- settings ----
            var s = new McpSettings { Port = 5 };
            Check("settings default to off, changes allowed, port 48884", !s.AutoStart && !s.ReadOnly && McpSettings.DefaultPort == 48884);

            // ---- the Updater (optional: --bgmcp path to a built BGMCP.Revit.dll) ----
            string bgmcp = Arg(argv, "--bgmcp");
            if (bgmcp != null) UpdaterChecks(bgmcp);
            string release = Arg(argv, "--release"); // a zip made by package.ps1: it must install through the Updater
            if (release != null)
            {
                string addins = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bgmcp-release-" + Guid.NewGuid().ToString("N"));
                try
                {
                    System.IO.Directory.CreateDirectory(addins);
                    Updater.Install(release, addins, null, out var rv);
                    Check($"the release zip {System.IO.Path.GetFileName(release)} installs through the Updater",
                        rv == Updater.VersionOfName(release) && System.IO.File.Exists(System.IO.Path.Combine(Updater.Root(addins), rv.ToString(3), Updater.DllName)));
                }
                catch (Exception ex) { Check("the release zip installs through the Updater", false, ex.Message); }
                finally { try { System.IO.Directory.Delete(addins, true); } catch { } }
            }

            // ---- Survey to Revit's tool list (optional: --engine path to its built engine) ----
            string enginePath = Arg(argv, "--engine");
            if (enginePath != null)
            {
                Console.WriteLine("Survey to Revit tools from " + enginePath);
                try
                {
                    var engine = Assembly.LoadFrom(enginePath);
                    var type = engine.GetType("SurveyToRevit.Revit.McpTools", true);
                    var rows = (string[][])type.GetMethod("List").Invoke(null, null);
                    Check("the engine lists its tools", rows.Length >= 12, rows.Length.ToString());
                    var nameOk = new Regex("^[a-z0-9_]{1,64}$");
                    foreach (var row in rows)
                    {
                        JsonObject schema = null; string problem = null;
                        try { schema = JsonNode.Parse(row[2]) as JsonObject; } catch (Exception ex) { problem = ex.Message; }
                        bool ok = row.Length == 4 && nameOk.IsMatch(row[0]) && row[0].StartsWith("survey_") && row[1].Length > 20
                                  && schema?["type"]?.ToString() == "object" && schema["properties"] is JsonObject && schema["required"] is JsonArray
                                  && row[3].Split(',').All(f => f == "" || f == "background" || f == "write" || f == "long");
                        Check($"{row[0],-24} schema and flags are sound [{row[3]}]", ok, problem ?? row[2]);
                        if (schema?["required"] is JsonArray req && schema["properties"] is JsonObject props)
                            foreach (var r in req) if (!props.ContainsKey(r.ToString())) Check($"{row[0]}: required '{r}' is a property", false);
                    }
                    Check("tool names are not repeated", rows.Select(r => r[0]).Distinct().Count() == rows.Length);
                    var promptRows = (string[][])type.GetMethod("Prompts").Invoke(null, null);
                    Check("the engine offers its workflow prompt", promptRows.Length >= 1 && promptRows[0].Length == 3 && promptRows[0][2].Contains("survey_analyze"));
                    int chars = rows.Sum(r => r[0].Length + r[1].Length + r[2].Length);
                    Console.WriteLine($"  The Survey tool list costs about {chars / 4:N0} tokens each time Claude loads it.");
                }
                catch (Exception ex) { Check("the engine's tool list can be read", false, McpServer.Unwrap(ex).ToString()); }
            }

            Console.WriteLine();
            Console.WriteLine(_failed == 0 ? $"ALL {_passed} CHECKS PASSED" : $"{_failed} FAILED, {_passed} passed");
            return _failed == 0 ? 0 : 1;
        }

        private static string Arg(string[] argv, string name)
        {
            int i = Array.IndexOf(argv, name);
            return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
        }

        /// <summary>A zip holding the given entries: entry name -> file to copy, or null for a small text file.</summary>
        private static string Zip(string folder, string name, params (string entry, string file)[] entries)
        {
            string path = System.IO.Path.Combine(folder, name);
            using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
                foreach (var (entry, file) in entries)
                {
                    if (file != null) System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, file, entry);
                    else using (var w = new System.IO.StreamWriter(zip.CreateEntry(entry).Open())) w.Write("readme");
                }
            return path;
        }

        private static bool Throws(Action a, string contains, out string message)
        {
            try { a(); message = "no error"; return false; }
            catch (Exception ex) { message = ex.Message; return ex.Message.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0; }
        }

        private static void UpdaterChecks(string dll)
        {
            Console.WriteLine("Updater, with " + dll);
            var real = Updater.Short(AssemblyName.GetAssemblyName(dll).Version);
            string v = real.ToString(3);
            string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bgmcp-updater-" + Guid.NewGuid().ToString("N"));
            string downloads = System.IO.Path.Combine(temp, "Downloads"), addins = System.IO.Path.Combine(temp, "Addins");
            System.IO.Directory.CreateDirectory(downloads);
            System.IO.Directory.CreateDirectory(addins);
            try
            {
                // what a Downloads folder may hold
                string good = Zip(downloads, $"BGMCP-{v}.zip", ("BGMCP/BGMCP.Revit.dll", dll), ("install.cmd", null));
                Zip(downloads, "BGMCP-0.9.0.zip", ("BGMCP/BGMCP.Revit.dll", dll));
                Zip(downloads, "BGMCP-latest.zip", ("BGMCP/BGMCP.Revit.dll", dll));
                Zip(downloads, "SurveyToRevit-0.23.0.zip", ("x.txt", null));
                var found = Updater.Find(downloads);
                Check("the Updater finds release zips by name, newest first, and ignores others",
                    found.Count == 2 && found[0].Version == real && found[1].Version == new Version(0, 9, 0), string.Join(", ", found.Select(r => r.Zip)));
                Check("a newer zip is offered", Updater.Newer(downloads, new Version(0, 1, 0))?.Version == real);
                Check("nothing is offered when the running version is the newest", Updater.Newer(downloads, real) == null);
                Check("a missing folder has no zips", Updater.Find(System.IO.Path.Combine(temp, "nope")).Count == 0);

                // first install, with Revit running the 1.1.0 layout (the add-in straight in BGMCP\)
                string root = Updater.Root(addins);
                System.IO.Directory.CreateDirectory(root);
                System.IO.File.Copy(dll, System.IO.Path.Combine(root, Updater.DllName));
                System.IO.File.WriteAllText(System.IO.Path.Combine(addins, Updater.ManifestName), Updater.Manifest(@"BGMCP\BGMCP.Revit.dll"));
                string target = Updater.Install(good, addins, root, out var installed);
                Check("it installs into a folder of its own version", installed == real && System.IO.File.Exists(System.IO.Path.Combine(root, v, Updater.DllName)));
                Check("only the add-in's files are copied", System.IO.Directory.GetFiles(target).All(f => f.EndsWith(".dll") || f.EndsWith(".pdb")));
                var manifest = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(addins, Updater.ManifestName));
                var addIn = manifest.Root.Element("AddIn");
                Check("the manifest now points at the new version",
                    addIn.Element("Assembly").Value == $@"BGMCP\{v}\BGMCP.Revit.dll" && addIn.Element("FullClassName").Value == "BarrGazetas.RevitMcp.App"
                    && addIn.Element("AddInId").Value == Updater.AddInId);
                Check("the manifest has the same add-in id as the shipped BGMCP.addin",
                    System.IO.File.ReadAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dll), "..", "..", "BGMCP.addin")).Contains(Updater.AddInId));
                Check("the new version is seen as waiting for a restart", Updater.Waiting(addins, root, new Version(1, 1, 0)) == real);
                Check("no staging folders are left behind", !System.IO.Directory.GetDirectories(root).Any(d => d.Contains(".new-")));

                string before = System.IO.File.ReadAllText(System.IO.Path.Combine(addins, Updater.ManifestName));
                bool unchanged() => System.IO.File.ReadAllText(System.IO.Path.Combine(addins, Updater.ManifestName)) == before;

                string liar = Zip(downloads, "BGMCP-9.9.9.zip", ("BGMCP/BGMCP.Revit.dll", dll));
                Check("a zip whose name and contents disagree is refused", Throws(() => Updater.Install(liar, addins, root, out _), "says 9.9.9", out var m1) && unchanged(), m1);
                string broken = System.IO.Path.Combine(downloads, "BGMCP-2.0.0.zip");
                System.IO.File.WriteAllBytes(broken, Encoding.UTF8.GetBytes("this is not a zip at all"));
                Check("a damaged zip is refused with a readable reason", Throws(() => Updater.Install(broken, addins, root, out _), "damaged", out var m2) && unchanged(), m2);
                string empty = Zip(downloads, "BGMCP-2.1.0.zip", ("readme.txt", null));
                Check("a zip with no add-in in it is refused", Throws(() => Updater.Install(empty, addins, root, out _), "no BGMCP.Revit.dll", out var m3) && unchanged(), m3);
                string other = Zip(downloads, "BGMCP-2.2.0.zip", ("BGMCP/BGMCP.Revit.dll", typeof(Program).Assembly.Location));
                Check("another program renamed BGMCP.Revit.dll is refused", Throws(() => Updater.Install(other, addins, root, out _), "not BG MCP", out var m4) && unchanged(), m4);
                Check("installing the version that is running is refused",
                    Throws(() => Updater.Install(good, addins, target, out _), "running now", out var m5) && unchanged(), m5);

                string wrapped = Zip(downloads, $"BGMCP-{v}-copy.zip", ($"BGMCP-{v}/BGMCP/BGMCP.Revit.dll", dll));
                Check("a zip wrapped in a top folder still installs", Updater.Install(wrapped, addins, root, out var w) == target && w == real);

                // tidying at the next start, now running the new version
                foreach (var old in new[] { "1.0.0", "1.0.5", "1.1.0" })
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, old));
                    System.IO.File.Copy(dll, System.IO.Path.Combine(root, old, Updater.DllName));
                }
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "1.3.0.new-abc123"));
                List<string> removed;
                using (new System.IO.FileStream(System.IO.Path.Combine(root, "1.0.0", Updater.DllName), System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read))
                    removed = Updater.Tidy(addins, target);
                var left = System.IO.Directory.GetDirectories(root).Select(System.IO.Path.GetFileName).OrderBy(n => n).ToList();
                Check("tidying keeps the running version and the one before it", left.Contains(v) && left.Contains("1.1.0"), string.Join(", ", left));
                Check("tidying removes older versions", !left.Contains("1.0.5") && removed.Contains("1.0.5"), string.Join(", ", removed));
                Check("a version another Revit still has open is left for next time", left.Contains("1.0.0"));
                Check("half-finished installs are removed", !left.Any(n => n.Contains(".new-")));
                Check("the 1.1.0 layout's add-in file is removed once nothing runs from it", !System.IO.File.Exists(System.IO.Path.Combine(root, Updater.DllName)));
                Check("nothing is waiting once the new version runs", Updater.Waiting(addins, target, real) == null);
            }
            catch (Exception ex) { Check("the Updater checks ran", false, ex.ToString()); }
            finally { try { System.IO.Directory.Delete(temp, true); } catch { } }
        }
    }
}
