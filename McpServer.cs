using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BarrGazetas.RevitMcp
{
    // Nothing in this file refers to Revit, so the server can be run and tested outside it (see tests/).

    /// <summary>One tool an assistant can call.</summary>
    internal sealed class ToolInfo
    {
        public string Name;
        public string Description;
        public JsonObject InputSchema;
        /// <summary>Which add-in it comes from: "Revit" for the hub's own, else the provider's name.</summary>
        public string Provider = "Revit";
        /// <summary>Changes the model: refused in read-only mode.</summary>
        public bool Write;
        /// <summary>Can take minutes.</summary>
        public bool Long;
        /// <summary>Runs it and returns the answer as text (JSON). May throw; the message is shown to the assistant.</summary>
        public Func<JsonObject, Task<string>> Invoke;
    }

    /// <summary>
    /// An answer with pictures. Tools answer in strings (the hub's own and other add-ins' alike), so this travels as a
    /// string with a marker in front; the server turns it into MCP text and image content. An add-in's McpBridge can
    /// return the same string to send images.
    /// </summary>
    internal sealed class ToolContent
    {
        public const string Marker = "\u0001mcp-content/1\u0001";
        public string Text;
        public List<(string mime, byte[] data)> Images = new List<(string, byte[])>();

        public string Encode()
        {
            var imgs = new JsonArray();
            foreach (var (mime, data) in Images) imgs.Add(new JsonObject { ["mimeType"] = mime, ["data"] = Convert.ToBase64String(data) });
            return Marker + new JsonObject { ["text"] = Text ?? "", ["images"] = imgs }.ToJsonString();
        }

        public static bool TryDecode(string s, out ToolContent c)
        {
            c = null;
            if (s == null || !s.StartsWith(Marker, StringComparison.Ordinal)) return false;
            try
            {
                var o = JsonNode.Parse(s.Substring(Marker.Length)) as JsonObject;
                c = new ToolContent { Text = o?["text"]?.ToString() ?? "" };
                foreach (var i in (o?["images"] as JsonArray) ?? new JsonArray())
                    c.Images.Add((i?["mimeType"]?.ToString() ?? "image/png", Convert.FromBase64String(i?["data"]?.ToString() ?? "")));
                return true;
            }
            catch { c = null; return false; }
        }
    }

    /// <summary>Ready-made instructions an assistant can load (MCP "prompts").</summary>
    internal sealed class PromptInfo
    {
        public string Name, Description, Text, Provider = "Revit";
    }

    internal sealed class CallRecord
    {
        public DateTime When = DateTime.Now;
        public string Tool, Provider, Error, Job;
        public long Ms;
        public bool Ok;
        public int ArgChars, ResultChars;
    }

    internal sealed class ServerOptions
    {
        public string Version = "1.0.0";
        public Func<bool> ReadOnly = () => false;
        public Action<CallRecord> OnCall = _ => { };
        /// <summary>Extra facts for the mcp_status tool (providers, hints).</summary>
        public Func<JsonObject> Status = () => new JsonObject();
        /// <summary>How long a call may hold the connection before it is handed back as a job to check on.</summary>
        public TimeSpan SyncWait = TimeSpan.FromSeconds(45);
        /// <summary>Answers longer than this are cut, with a note, so one careless call cannot flood the assistant.</summary>
        public int MaxResultChars = 60000;
    }

    /// <summary>
    /// A small MCP server: the "Streamable HTTP" transport with plain JSON answers (no event stream).
    /// Listens on localhost only. Endpoint: http://localhost:{port}/mcp
    /// </summary>
    internal sealed class McpServer
    {
        private const string LatestProtocol = "2025-06-18";
        private static readonly string[] SupportedProtocols = { "2025-06-18", "2025-03-26", "2024-11-05" };
        private static readonly Regex ToolName = new Regex("^[A-Za-z0-9_-]{1,64}$");

        private readonly Func<IReadOnlyList<ToolInfo>> _tools;
        private readonly Func<IReadOnlyList<PromptInfo>> _prompts;
        private readonly ServerOptions _o;
        private readonly ConcurrentDictionary<string, Job> _jobs = new ConcurrentDictionary<string, Job>();
        private int _jobNumber;
        private HttpListener _listener;
        private CancellationTokenSource _cts;

        private sealed class Job
        {
            public string Id, Tool, Provider;
            public Task<string> Work;
            public Stopwatch Clock;
            public int ArgChars;
            public DateTime Finished = DateTime.MaxValue;
        }

        public int Port { get; private set; }
        public string Url => $"http://localhost:{Port}/mcp";
        public bool IsRunning => _listener?.IsListening == true;
        public int RunningJobs => _jobs.Values.Count(j => !j.Work.IsCompleted);

        public McpServer(int port, Func<IReadOnlyList<ToolInfo>> tools, Func<IReadOnlyList<PromptInfo>> prompts, ServerOptions options = null)
        {
            Port = port;
            _tools = tools;
            _prompts = prompts ?? (() => Array.Empty<PromptInfo>());
            _o = options ?? new ServerOptions();
        }

        public void Start(int? port = null)
        {
            if (IsRunning) return;
            if (port != null) Port = port.Value;
            var listener = new HttpListener();
            // A "localhost" prefix needs no admin rights and is not reachable from other machines.
            listener.Prefixes.Add($"http://localhost:{Port}/");
            listener.Start();
            _listener = listener;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(() => AcceptLoop(listener, token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            _listener = null;
        }

        private async Task AcceptLoop(HttpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
                catch { break; } // stopped
                _ = Task.Run(() => Handle(ctx));
            }
        }

        // ---------- HTTP ----------

        private async Task Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            try
            {
                // Only this machine's own pages may call (stops a web page elsewhere reaching Revit by "DNS rebinding").
                string origin = req.Headers["Origin"];
                if (!string.IsNullOrEmpty(origin) && !IsLocalOrigin(origin)) { await Write(res, 403, null); return; }

                string path = req.Url.AbsolutePath.TrimEnd('/');
                if (path == "" && req.HttpMethod == "GET") { await Write(res, 200, Status()); return; }
                if (path != "/mcp") { await Write(res, 404, null); return; }

                switch (req.HttpMethod)
                {
                    case "POST": break;
                    case "DELETE": await Write(res, 200, null); return; // end of session: nothing is kept per session
                    default: res.Headers["Allow"] = "POST, DELETE"; await Write(res, 405, null); return; // GET: no server-sent stream on offer
                }

                string body;
                using (var reader = new StreamReader(req.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();

                JsonNode parsed;
                try { parsed = JsonNode.Parse(body); }
                catch { await Write(res, 400, Error(null, -32700, "Parse error")); return; }

                bool batch = parsed is JsonArray;
                var messages = batch ? ((JsonArray)parsed).ToList() : new List<JsonNode> { parsed };
                var replies = new JsonArray();
                foreach (var message in messages)
                {
                    var reply = await Dispatch(message as JsonObject, res);
                    if (reply != null) replies.Add(reply);
                }
                if (replies.Count == 0) { await Write(res, 202, null); return; } // notifications only
                await Write(res, 200, batch ? replies : replies[0].DeepClone());
            }
            catch (Exception ex)
            {
                try { await Write(res, 500, Error(null, -32603, ex.Message)); } catch { }
            }
        }

        private async Task<JsonNode> Dispatch(JsonObject msg, HttpListenerResponse res)
        {
            if (msg == null) return Error(null, -32600, "Invalid request");
            string method = (msg["method"] as JsonValue)?.GetValue<string>();
            if (method == null || !msg.ContainsKey("id")) return null; // a reply or a notification: nothing to answer
            JsonNode id = msg["id"]?.DeepClone();
            var p = msg["params"] as JsonObject ?? new JsonObject();
            try
            {
                switch (method)
                {
                    case "initialize":
                    {
                        string asked = (p["protocolVersion"] as JsonValue)?.GetValue<string>();
                        res.Headers["Mcp-Session-Id"] = Guid.NewGuid().ToString("N");
                        return Result(id, new JsonObject
                        {
                            ["protocolVersion"] = SupportedProtocols.Contains(asked) ? asked : LatestProtocol,
                            ["capabilities"] = new JsonObject
                            {
                                ["tools"] = new JsonObject { ["listChanged"] = false },
                                ["prompts"] = new JsonObject { ["listChanged"] = false },
                            },
                            ["serverInfo"] = new JsonObject { ["name"] = "barr-gazetas-revit", ["title"] = "Barr Gazetas Revit", ["version"] = _o.Version },
                            ["instructions"] =
                                "You are connected to the Revit session open on this computer. Start with mcp_status. " +
                                "Element ids are whole numbers. Positions and sizes are millimetres in the model's own X, Y, Z; " +
                                "'display' values use the project's units. Answers are summaries: ask for detail with ids, filters and limits " +
                                "rather than listing everything. Tools that change the model are single undo steps in Revit; say so when you use one, " +
                                "and ask before deleting or rebuilding. A call that takes long comes back as a job: check it with job_status. " +
                                "Revit only runs requests when it is idle: if calls stall, ask the user to close any open dialog or finish the command in Revit. " +
                                "Tools from other Barr Gazetas add-ins appear with their own prefix (survey_...) and do the heavy modelling: prefer them to placing elements one by one.",
                        });
                    }
                    case "ping": return Result(id, new JsonObject());

                    case "tools/list":
                        return Result(id, new JsonObject { ["tools"] = new JsonArray(AllTools().Select(Describe).ToArray()) });

                    case "tools/call": return Result(id, await CallTool(p));

                    case "prompts/list":
                        return Result(id, new JsonObject
                        {
                            ["prompts"] = new JsonArray(_prompts().Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["description"] = x.Description }).ToArray()),
                        });

                    case "prompts/get":
                    {
                        string name = (p["name"] as JsonValue)?.GetValue<string>();
                        var prompt = _prompts().FirstOrDefault(x => x.Name == name);
                        if (prompt == null) return Error(id, -32602, "Unknown prompt: " + name);
                        return Result(id, new JsonObject
                        {
                            ["description"] = prompt.Description,
                            ["messages"] = new JsonArray(new JsonObject
                            {
                                ["role"] = "user",
                                ["content"] = new JsonObject { ["type"] = "text", ["text"] = prompt.Text },
                            }),
                        });
                    }
                    case "resources/list": return Result(id, new JsonObject { ["resources"] = new JsonArray() });
                    case "resources/templates/list": return Result(id, new JsonObject { ["resourceTemplates"] = new JsonArray() });
                    default: return Error(id, -32601, "Method not found: " + method);
                }
            }
            catch (Exception ex) { return Error(id, -32603, Unwrap(ex).Message); }
        }

        // ---------- tools ----------

        /// <summary>The server's own two tools, then everything the hub and the other add-ins offer.</summary>
        private List<ToolInfo> AllTools()
        {
            var list = new List<ToolInfo>
            {
                new ToolInfo
                {
                    Name = "mcp_status",
                    Description = "Start here. What this connection offers: which add-ins have tools, whether changes are allowed (read-only mode), and jobs still running.",
                    InputSchema = Obj(),
                    Invoke = _ => Task.FromResult(Status().ToJsonString()),
                },
                new ToolInfo
                {
                    Name = "job_status",
                    Description = "The result of a call that was handed back as a job because it was taking long (a build, a big query). Waits a little for it to finish. Do not start the same call again while its job is running.",
                    InputSchema = Obj(("job", "string", "The job id that was returned", true), ("wait_seconds", "integer", "How long to wait for it (default 25, at most 45)", false)),
                },
            };
            var names = new HashSet<string>(list.Select(t => t.Name));
            foreach (var t in _tools())
                if (t?.Name != null && ToolName.IsMatch(t.Name) && t.Invoke != null && names.Add(t.Name)) list.Add(t);
            return list;
        }

        private static JsonNode Describe(ToolInfo t) => new JsonObject
        {
            ["name"] = t.Name,
            ["description"] = (t.Provider == "Revit" ? "" : $"[{t.Provider}] ") + t.Description,
            ["inputSchema"] = (t.InputSchema ?? Obj()).DeepClone(),
            ["annotations"] = new JsonObject { ["readOnlyHint"] = !t.Write },
        };

        private async Task<JsonObject> CallTool(JsonObject p)
        {
            string name = (p["name"] as JsonValue)?.GetValue<string>();
            var args = p["arguments"] as JsonObject ?? new JsonObject();
            if (name == "job_status") return await JobStatus(args);

            var tool = AllTools().FirstOrDefault(t => t.Name == name);
            if (tool == null) return Text("There is no tool called " + name + ". See tools/list.", true);
            var clock = Stopwatch.StartNew();
            int argChars = args.ToJsonString().Length;
            if (tool.Write && _o.ReadOnly())
            {
                Record(name, tool.Provider, clock, argChars, Task.FromException<string>(new InvalidOperationException("refused: read-only mode")), null);
                return Text($"{name} changes the model, and this connection is in read-only mode. " +
                            "The user can allow changes in Revit: Barr Gazetas tab > MCP > Connect > untick \"Read only\".", true);
            }

            Task<string> work;
            try { work = tool.Invoke(args) ?? Task.FromResult(""); }
            catch (Exception ex) { work = Task.FromException<string>(ex); }

            if (await Task.WhenAny(work, Task.Delay(_o.SyncWait)) != work)
            {
                // Still going: hand back a job rather than hold the connection until the client gives up.
                Purge();
                var job = new Job { Id = "job-" + Interlocked.Increment(ref _jobNumber), Tool = name, Provider = tool.Provider, Work = work, Clock = clock, ArgChars = argChars };
                _jobs[job.Id] = job;
                _ = work.ContinueWith(t => { job.Finished = DateTime.Now; Record(job.Tool, job.Provider, job.Clock, job.ArgChars, t, job.Id); }, TaskScheduler.Default);
                return Text(new JsonObject
                {
                    ["job"] = job.Id,
                    ["status"] = "running",
                    ["tool"] = name,
                    ["note"] = "Still working. Call job_status with this job id for the result. Revit runs requests only when idle: " +
                               "if this never finishes, ask the user to close any open dialog or finish the command in Revit.",
                }.ToJsonString(), false);
            }
            Record(name, tool.Provider, clock, argChars, work, null);
            return Outcome(work);
        }

        private async Task<JsonObject> JobStatus(JsonObject args)
        {
            string id = (args["job"] as JsonValue)?.ToString();
            if (id == null || !_jobs.TryGetValue(id, out var job))
                return Text($"There is no job {id}. Jobs are forgotten once their result has been read, or an hour after they finish.", true);
            double wait = 25;
            if (args["wait_seconds"] is JsonValue w && double.TryParse(w.ToString(), out var asked)) wait = Math.Max(0, Math.Min(45, asked));
            if (await Task.WhenAny(job.Work, Task.Delay(TimeSpan.FromSeconds(wait))) != job.Work)
                return Text(new JsonObject
                {
                    ["job"] = id, ["status"] = "running", ["tool"] = job.Tool, ["seconds"] = (int)job.Clock.Elapsed.TotalSeconds,
                    ["note"] = "Not finished yet. Call job_status again.",
                }.ToJsonString(), false);
            _jobs.TryRemove(id, out _);
            return Outcome(job.Work);
        }

        private JsonObject Outcome(Task<string> work)
        {
            if (work.IsFaulted || work.IsCanceled)
                return Text("Error: " + (work.IsCanceled ? "the call was cancelled." : Unwrap(work.Exception).Message), true);
            string text = work.Result ?? "";
            if (ToolContent.TryDecode(text, out var content)) return Rich(content);
            if (text.Length > _o.MaxResultChars)
                text = text.Substring(0, _o.MaxResultChars) + $"\n... (cut: the full answer is {text.Length} characters. Ask for less: a filter, a limit, fewer ids.)";
            return Text(text, false);
        }

        private void Record(string tool, string provider, Stopwatch clock, int argChars, Task<string> work, string job)
        {
            try
            {
                bool ok = work.Status == TaskStatus.RanToCompletion;
                _o.OnCall(new CallRecord
                {
                    Tool = tool, Provider = provider, Ms = clock.ElapsedMilliseconds, Ok = ok, Job = job, ArgChars = argChars,
                    ResultChars = ok ? (work.Result?.Length ?? 0) : 0,
                    Error = ok ? null : (work.IsCanceled ? "cancelled" : Unwrap(work.Exception).Message),
                });
            }
            catch { /* the log is never a reason to fail a call */ }
        }

        private void Purge()
        {
            foreach (var j in _jobs.Values.Where(j => j.Finished < DateTime.Now.AddHours(-1)).ToList()) _jobs.TryRemove(j.Id, out _);
        }

        private JsonObject Status()
        {
            var status = new JsonObject
            {
                ["server"] = "Barr Gazetas Revit MCP",
                ["version"] = _o.Version,
                ["endpoint"] = Url,
                ["readOnly"] = _o.ReadOnly(),
                ["runningJobs"] = new JsonArray(_jobs.Values.Where(j => !j.Work.IsCompleted)
                    .Select(j => (JsonNode)new JsonObject { ["job"] = j.Id, ["tool"] = j.Tool, ["seconds"] = (int)j.Clock.Elapsed.TotalSeconds }).ToArray()),
            };
            try { foreach (var kv in _o.Status()) status[kv.Key] = kv.Value?.DeepClone(); } catch { }
            return status;
        }

        // ---------- helpers ----------

        internal static Exception Unwrap(Exception ex)
        {
            while ((ex is AggregateException || ex is TargetInvocationException) && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }

        /// <summary>A JSON Schema object from rows of (name, type, description, required). Types: string, integer, number, boolean, or "x[]" for a list.</summary>
        internal static JsonObject Obj(params (string name, string type, string description, bool required)[] props)
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (var (name, type, description, req) in props)
            {
                JsonObject p = type.EndsWith("[]")
                    ? new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = type.Substring(0, type.Length - 2) } }
                    : new JsonObject { ["type"] = type };
                p["description"] = description;
                properties[name] = p;
                if (req) required.Add(name);
            }
            return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
        }

        /// <summary>A text answer with pictures (view_image, survey_section image): the text is cut as any answer is, the images go as they are.</summary>
        private JsonObject Rich(ToolContent c)
        {
            string text = c.Text ?? "";
            if (text.Length > _o.MaxResultChars) text = text.Substring(0, _o.MaxResultChars) + "\n... (cut)";
            var items = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
            foreach (var (mime, data) in c.Images)
                items.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(data), ["mimeType"] = mime });
            return new JsonObject { ["content"] = items, ["isError"] = false };
        }

        private static JsonObject Text(string text, bool isError) => new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError,
        };

        private static JsonObject Result(JsonNode id, JsonNode result) =>
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

        private static JsonObject Error(JsonNode id, int code, string message) =>
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

        private static bool IsLocalOrigin(string origin) =>
            Uri.TryCreate(origin, UriKind.Absolute, out var u) && (u.Host == "localhost" || u.Host == "127.0.0.1" || u.Host == "[::1]" || u.Host == "::1");

        private static async Task Write(HttpListenerResponse res, int status, JsonNode body)
        {
            res.StatusCode = status;
            if (body != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = bytes.Length;
                await res.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            }
            res.Close();
        }
    }
}
