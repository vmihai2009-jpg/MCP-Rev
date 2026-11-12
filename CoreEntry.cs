using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;

namespace BarrGazetas.RevitMcp
{
    /// <summary>
    /// Set when this core runs under BGMCP.Loader (the small add-in Revit loads once). The loader owns the ribbon
    /// and can swap the core for a newer build without restarting Revit (mcp_reload, or the Reload button).
    /// </summary>
    internal static class Hosted
    {
        public static bool Active => RequestReload != null;
        /// <summary>Tell the loader the on/off state changed (it redraws its buttons).</summary>
        public static Action Changed;
        /// <summary>Ask the loader to swap in another core version ("" or null = the newest installed).</summary>
        public static Action<string> RequestReload;
        public static string Folder;
    }

    /// <summary>What BGMCP.Loader calls, by reflection (only BCL and Revit types cross over).</summary>
    public static class CoreEntry
    {
        public static string Version => Hub.Version;

        /// <summary>Starts this core. Called in a Revit API context (start-up or Idling).</summary>
        public static void Boot(string folder, Action changed, Action<string> reload)
        {
            Hosted.Folder = folder;
            Hosted.Changed = changed;
            Hosted.RequestReload = reload;
            Updater.HostedFolder = folder;
            App.Boot();
        }

        /// <summary>The MCP button: on/off, with the first-time introduction. Shows its own dialogs.</summary>
        public static void ToggleButton(IntPtr owner)
        {
            var app = App.Instance;
            string problem = app.Toggle();
            if (problem != null) { TaskDialog.Show("Barr Gazetas MCP", problem); return; }
            if (app.Hub.Server.IsRunning && !app.Hub.Settings.Introduced)
            {
                app.Hub.Settings.Introduced = true;
                app.Hub.Settings.Save();
                ConnectWindow.Show(app, owner);
            }
        }

        /// <summary>Switches the server on if it is off. Returns a problem, or null.</summary>
        public static string EnsureOn()
        {
            var app = App.Instance;
            if (app?.Hub == null || app.Hub.Server.IsRunning) return null;
            return app.Toggle();
        }

        public static bool IsRunning() => App.Instance?.Hub?.Server.IsRunning == true;

        public static string Tooltip()
        {
            var hub = App.Instance?.Hub;
            if (hub == null) return "BG MCP is not loaded.";
            return hub.Server.IsRunning
                ? $"BG MCP {Version}: Claude can reach this Revit session at {hub.Server.Url}" + (hub.Settings.ReadOnly ? " (read only)." : ".") + "\nClick to switch off."
                : $"BG MCP {Version}. Switch on so Claude (or any MCP client on this computer) can read and work on this Revit session. Nothing is reachable while it is off.";
        }

        public static void Connect(IntPtr owner) => ConnectWindow.Show(App.Instance, owner);
        public static void Update() => UpdateFlow.Run();

        /// <summary>Stops the server and lets go of Revit events, before the loader swaps in another core.</summary>
        public static void Shutdown()
        {
            try { App.Instance?.Hub?.Stop(); } catch { }
            try { App.Instance?.Unhook(); } catch { }
        }

        public static object Icon(int size, bool on) => Icons.Make(size, on);

        public static object ButtonImage(string button, int size) =>
            Icons.Glyph(size, button == "connect" ? "" : button == "reload" ? "" : "");
    }

    /// <summary>mcp_reload: swap in a newer build without restarting Revit.</summary>
    internal static class HostTools
    {
        public static IEnumerable<RevitTool> All()
        {
            yield return new RevitTool
            {
                Name = "mcp_reload",
                Description = "Load another BG MCP build without restarting Revit: the newest version folder installed (or the version named). " +
                              "This connection drops for a few seconds and comes back on the same port; call mcp_status after. Open projects and " +
                              "reference models are not touched (reference models stay open but must be opened again by name with reference_open).",
                InputSchema = McpServer.Obj(("version", "string", "x.y.z to load (default: the newest installed)", false)),
                Run = (app, a) =>
                {
                    if (!Hosted.Active)
                        return new { reloaded = false, why = "This Revit started BG MCP without the loader (an older install). Restart Revit once; after that reloads need no restart." };
                    string want = Args.Str(a, "version");
                    var folders = Installed();
                    var target = want != null ? folders.FirstOrDefault(f => f.version.ToString(3) == want) : folders.FirstOrDefault();
                    if (target.folder == null)
                        throw new InvalidOperationException(want != null ? $"BG MCP {want} is not installed. Installed: {string.Join(", ", folders.Select(f => f.version.ToString(3)))}." : "No BG MCP version folder found.");
                    Hosted.RequestReload(target.version.ToString(3));
                    return new { reloading = target.version.ToString(3), running = Hub.Version, next = "Wait 3-5 seconds, then mcp_status." };
                }
            };
        }

        /// <summary>Version folders holding a core, newest first.</summary>
        public static List<(Version version, string folder)> Installed()
        {
            string root = Updater.Root(Updater.AddinsFolder);
            if (!Directory.Exists(root)) return new List<(Version, string)>();
            return Directory.GetDirectories(root)
                .Select(d => (ok: System.Version.TryParse(Path.GetFileName(d), out var v), v, d))
                .Where(x => x.ok && File.Exists(Path.Combine(x.d, Updater.DllName)))
                .OrderByDescending(x => x.v).Select(x => (x.v, x.d)).ToList();
        }
    }
}
