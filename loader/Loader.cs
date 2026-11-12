using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows.Media;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace BarrGazetas.RevitMcp.Loader
{
    /// <summary>
    /// What Revit loads (BGMCP\BGMCP.Loader.dll). Loads the BG MCP core (BGMCP\x.y.z\BGMCP.Revit.dll, the newest) from
    /// its bytes into a load context of its own, so the files stay free to be replaced, and can load another
    /// build in its place while Revit runs: the old core stops its server, the new one starts on the same port.
    /// Only BCL and Revit types cross between the two (by reflection), so neither depends on the other's version.
    /// </summary>
    public class LoaderApp : IExternalApplication
    {
        public const string TabName = "Barr Gazetas";
        public const string PanelName = "MCP";
        private const string CoreDll = "BGMCP.Revit.dll";

        internal static LoaderApp Instance { get; private set; }
        internal Core Current { get; private set; }
        internal string LastProblem;
        private PushButton _toggle;
        private string _pending;          // a reload asked for: the version ("" = newest)
        private DateTime _pendingAt;

        internal static string Root => Path.GetDirectoryName(typeof(LoaderApp).Assembly.Location);

        public Result OnStartup(UIControlledApplication app)
        {
            Instance = this;
            try { app.CreateRibbonTab(TabName); } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
            RibbonPanel panel = app.GetRibbonPanels(TabName).FirstOrDefault(p => p.Name == PanelName) ?? app.CreateRibbonPanel(TabName, PanelName);
            string me = typeof(LoaderApp).Assembly.Location;
            _toggle = (PushButton)panel.AddItem(new PushButtonData("BG_MCP_Toggle", "MCP\nOff", me, typeof(ToggleCommand).FullName) { AvailabilityClassName = typeof(Always).FullName });
            var stacked = panel.AddStackedItems(
                new PushButtonData("BG_MCP_Connect", "Connect", me, typeof(ConnectCommand).FullName)
                { AvailabilityClassName = typeof(Always).FullName, ToolTip = "How to connect Claude, the tools on offer, options, and what has been called." },
                new PushButtonData("BG_MCP_Update", "Update", me, typeof(UpdateCommand).FullName)
                { AvailabilityClassName = typeof(Always).FullName, ToolTip = "Install a newer BG MCP from its release zip. It starts at once, no restart." },
                new PushButtonData("BG_MCP_Reload", "Reload", me, typeof(ReloadCommand).FullName)
                { AvailabilityClassName = typeof(Always).FullName, ToolTip = "Load the newest BG MCP build installed, without restarting Revit." });

            try
            {
                Current = Core.Load(Newest(null));
                Current.Boot(Changed, Request);
            }
            catch (Exception ex) { LastProblem = Unwrap(ex).Message; Current = null; }

            foreach (var b in stacked.OfType<PushButton>())
            {
                string key = b.Name == "BG_MCP_Connect" ? "connect" : b.Name == "BG_MCP_Reload" ? "reload" : "update";
                b.Image = Current?.ButtonImage(key, 16); b.LargeImage = Current?.ButtonImage(key, 32);
            }
            app.Idling += OnIdling;
            Refresh();
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication app)
        {
            Current?.Shutdown();
            return Result.Succeeded;
        }

        /// <summary>The core asks for a reload (mcp_reload, its Update button). Done at the next idle moment, after the answer has gone out.</summary>
        private void Request(string version)
        {
            _pending = version ?? "";
            _pendingAt = DateTime.UtcNow;
        }

        private void OnIdling(object sender, IdlingEventArgs e)
        {
            if (_pending == null) return;
            e.SetRaiseWithoutDelay(); // keep idling coming until it is done
            if ((DateTime.UtcNow - _pendingAt).TotalSeconds < 1.5) return; // let the reply to mcp_reload reach Claude first
            string version = _pending;
            _pending = null;
            Reload(version);
        }

        /// <summary>Swaps the core. Returns a problem, or null. Needs a Revit API context.</summary>
        internal string Reload(string version)
        {
            string folder;
            try { folder = Newest(string.IsNullOrEmpty(version) ? null : version); }
            catch (Exception ex) { return LastProblem = ex.Message; }
            bool wasOn = Current?.IsRunning() == true;
            Core next;
            try { next = Core.Load(folder); }
            catch (Exception ex) { return LastProblem = "The new build could not be loaded; the old one keeps running. " + Unwrap(ex).Message; }
            var old = Current;
            old?.Shutdown();
            try
            {
                Current = next;
                next.Boot(Changed, Request);
                if (wasOn) { string p = next.EnsureOn(); if (p != null) LastProblem = p; }
            }
            catch (Exception ex)
            {
                // put the old one back
                Current = old;
                try { if (wasOn) old?.EnsureOn(); } catch { }
                return LastProblem = "The new build failed to start; the old one is back. " + Unwrap(ex).Message;
            }
            LastProblem = null;
            Refresh();
            return null;
        }

        private void Changed() => Refresh();

        internal void Refresh()
        {
            if (_toggle == null) return;
            bool on = Current?.IsRunning() == true;
            _toggle.ItemText = on ? "MCP\nOn" : "MCP\nOff";
            if (Current != null) { _toggle.LargeImage = Current.Icon(32, on); _toggle.Image = Current.Icon(16, on); }
            _toggle.ToolTip = Current == null ? "BG MCP could not be loaded: " + LastProblem : Current.Tooltip();
        }

        /// <summary>The version folder to load: the one named, or the newest holding a core.</summary>
        internal static string Newest(string version)
        {
            var found = Directory.GetDirectories(Root)
                .Select(d => (ok: Version.TryParse(Path.GetFileName(d), out var v), v, d))
                .Where(x => x.ok && File.Exists(Path.Combine(x.d, CoreDll)))
                .OrderByDescending(x => x.v).ToList();
            if (version != null)
            {
                var hit = found.FirstOrDefault(x => x.v.ToString(3) == version);
                return hit.d ?? throw new InvalidOperationException($"BG MCP {version} is not installed in {Root}.");
            }
            return found.Count > 0 ? found[0].d : throw new InvalidOperationException($"No BG MCP version folder (x.y.z with {CoreDll}) in {Root}.");
        }

        internal static Exception Unwrap(Exception ex) => ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex;
    }

    /// <summary>One loaded core: its load context and the entry points it offers.</summary>
    internal sealed class Core
    {
        private Type _entry;
        public string Folder, Version;

        public static Core Load(string folder)
        {
            var ctx = new CoreContext(folder);
            var asm = ctx.LoadCore();
            var entry = asm.GetType("BarrGazetas.RevitMcp.CoreEntry", true);
            return new Core { _entry = entry, Folder = folder, Version = (string)entry.GetProperty("Version").GetValue(null) };
        }

        private object Call(string name, params object[] args) => _entry.GetMethod(name, BindingFlags.Public | BindingFlags.Static).Invoke(null, args);

        public void Boot(Action changed, Action<string> reload) => Call("Boot", Folder, changed, reload);
        public void Shutdown() { try { Call("Shutdown"); } catch { } }
        public bool IsRunning() { try { return (bool)Call("IsRunning"); } catch { return false; } }
        public string EnsureOn() => (string)Call("EnsureOn");
        public string Tooltip() { try { return (string)Call("Tooltip"); } catch (Exception ex) { return LoaderApp.Unwrap(ex).Message; } }
        public void ToggleButton(IntPtr owner) => Call("ToggleButton", owner);
        public void Connect(IntPtr owner) => Call("Connect", owner);
        public void Update() => Call("Update");
        public ImageSource Icon(int size, bool on) { try { return (ImageSource)Call("Icon", size, on); } catch { return null; } }
        public ImageSource ButtonImage(string key, int size) { try { return (ImageSource)Call("ButtonImage", key, size); } catch { return null; } }
    }

    /// <summary>
    /// The core and the libraries it ships with (ACadSharp...), read from bytes so nothing in the folder is locked.
    /// Everything else (Revit, .NET, WPF) comes from Revit's own context.
    /// </summary>
    internal sealed class CoreContext : AssemblyLoadContext
    {
        private readonly string _folder;
        private readonly Dictionary<string, Assembly> _mine = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        public CoreContext(string folder) : base("BGMCP core " + Path.GetFileName(folder), isCollectible: false) => _folder = folder;

        public Assembly LoadCore() => FromFolder("BGMCP.Revit") ?? throw new FileNotFoundException("BGMCP.Revit.dll is missing in " + _folder);

        protected override Assembly Load(AssemblyName name)
        {
            // Revit's and .NET's assemblies are shared with Revit (null = the default context resolves them)
            string n = name.Name ?? "";
            if (n.StartsWith("RevitAPI", StringComparison.OrdinalIgnoreCase) || n.StartsWith("System", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || n == "netstandard" || n == "mscorlib"
                || n.StartsWith("Presentation", StringComparison.OrdinalIgnoreCase) || n == "WindowsBase") return null;
            return FromFolder(n);
        }

        private Assembly FromFolder(string name)
        {
            if (_mine.TryGetValue(name, out var a)) return a;
            string dll = Path.Combine(_folder, name + ".dll");
            if (!File.Exists(dll)) return null;
            string pdb = Path.ChangeExtension(dll, ".pdb");
            using (var s = new MemoryStream(File.ReadAllBytes(dll)))
            using (var p = File.Exists(pdb) ? new MemoryStream(File.ReadAllBytes(pdb)) : null)
                a = p == null ? LoadFromStream(s) : LoadFromStream(s, p);
            _mine[name] = a;
            return a;
        }
    }

    public class Always : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication app, CategorySet selected) => true;
    }

    [Transaction(TransactionMode.Manual)]
    public class ToggleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var l = LoaderApp.Instance;
            if (l.Current == null) { TaskDialog.Show("Barr Gazetas MCP", "BG MCP could not be loaded.\n\n" + l.LastProblem); return Result.Succeeded; }
            l.Current.ToggleButton(data.Application.MainWindowHandle);
            l.Refresh();
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class ConnectCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            LoaderApp.Instance.Current?.Connect(data.Application.MainWindowHandle);
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class UpdateCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            LoaderApp.Instance.Current?.Update();
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class ReloadCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var l = LoaderApp.Instance;
            string before = l.Current?.Version;
            string problem = l.Reload(null);
            TaskDialog.Show("Barr Gazetas MCP", problem ?? $"BG MCP {l.Current.Version} is running" + (before != null && before != l.Current.Version ? $" (was {before})." : " (reloaded)."));
            return Result.Succeeded;
        }
    }
}
