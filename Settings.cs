using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BarrGazetas.RevitMcp
{
    /// <summary>The user's choices, in %AppData%\BarrGazetas\MCP\settings.json. A damaged file falls back to the defaults.</summary>
    internal sealed class McpSettings
    {
        public const int DefaultPort = 48884;

        public int Port { get; set; } = DefaultPort;
        /// <summary>Turn the server on when Revit starts.</summary>
        public bool AutoStart { get; set; }
        /// <summary>Refuse every tool that changes the model.</summary>
        public bool ReadOnly { get; set; }
        /// <summary>Keep a line per call in the log file (tool, time taken, sizes; never the content).</summary>
        public bool LogCalls { get; set; } = true;
        /// <summary>The Connect window has been shown once, after the first switch-on.</summary>
        public bool Introduced { get; set; }
        /// <summary>Where the Updater looks for BGMCP-x.y.z.zip; empty means Downloads.</summary>
        public string UpdatesFolder { get; set; }
        /// <summary>Show "Update ready" on the ribbon when a newer zip is waiting there.</summary>
        public bool CheckAtStartup { get; set; } = true;
        /// <summary>survey_fidelity's pass gate (mm and %); edit settings.json to tune it.</summary>
        public Survey.FidelityGate FidelityGate { get; set; } = new Survey.FidelityGate();

        [System.Text.Json.Serialization.JsonIgnore]
        public string Updates => string.IsNullOrWhiteSpace(UpdatesFolder) ? Updater.DefaultUpdates : UpdatesFolder;

        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BarrGazetas", "MCP");
        public static string FilePath => Path.Combine(Folder, "settings.json");

        private static readonly JsonSerializerOptions Json = Frozen(new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        });

        private static JsonSerializerOptions Frozen(JsonSerializerOptions o) { o.MakeReadOnly(); return o; }

        public static McpSettings Load()
        {
            McpSettings s = null;
            try { if (File.Exists(FilePath)) s = JsonSerializer.Deserialize<McpSettings>(File.ReadAllText(FilePath), Json); }
            catch { /* fall back to the defaults */ }
            s = s ?? new McpSettings();
            if (s.Port <= 1023 || s.Port > 65535) s.Port = DefaultPort;
            // For IT: a machine-wide port without touching each user's file.
            if (int.TryParse(Environment.GetEnvironmentVariable("BG_REVIT_MCP_PORT"), out int p) && p > 1023 && p < 65536) s.Port = p;
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
                File.Move(temp, FilePath, true);
            }
            catch { /* settings that cannot be saved must never stop the add-in */ }
        }
    }

    /// <summary>
    /// What was called, how long it took and how big the answer was: the last few hundred in memory for the
    /// Connect window, and a line each in mcp-log.jsonl for working out what the assistant costs and saves.
    /// Arguments and answers themselves are not recorded.
    /// </summary>
    internal sealed class CallLog
    {
        private readonly object _gate = new object();
        private readonly LinkedList<CallRecord> _recent = new LinkedList<CallRecord>();
        private const int Keep = 300;
        private const long MaxBytes = 5 * 1024 * 1024;

        public Func<bool> Enabled = () => true;

        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BarrGazetas", "MCP");
        public static string FilePath => Path.Combine(Folder, "mcp-log.jsonl");

        public void Add(CallRecord r)
        {
            lock (_gate)
            {
                _recent.AddFirst(r);
                while (_recent.Count > Keep) _recent.RemoveLast();
                if (!Enabled()) return;
                try
                {
                    Directory.CreateDirectory(Folder);
                    var file = new FileInfo(FilePath);
                    if (file.Exists && file.Length > MaxBytes) File.Move(FilePath, Path.Combine(Folder, "mcp-log.old.jsonl"), true);
                    File.AppendAllText(FilePath, JsonSerializer.Serialize(new
                    {
                        t = r.When.ToString("yyyy-MM-ddTHH:mm:ss"), tool = r.Tool, provider = r.Provider, ms = r.Ms, ok = r.Ok,
                        argChars = r.ArgChars, resultChars = r.ResultChars, job = r.Job, error = r.Error,
                    }) + Environment.NewLine);
                }
                catch { /* a log that cannot be written is not worth a failed call */ }
            }
        }

        public List<CallRecord> Recent(int count)
        {
            lock (_gate) return _recent.Take(count).ToList();
        }

        /// <summary>Calls, failures and characters sent back since Revit started.</summary>
        public (int Calls, int Failed, long ResultChars) Totals()
        {
            lock (_gate) return (_recent.Count, _recent.Count(r => !r.Ok), _recent.Sum(r => (long)r.ResultChars));
        }
    }
}
