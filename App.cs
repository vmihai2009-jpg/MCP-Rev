using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BarrGazetas.RevitMcp
{
    /// <summary>
    /// What Revit loads: the MCP panel on the shared Barr Gazetas tab. "MCP" switches the connection for Claude
    /// on and off; "Connect" shows how to connect, what is on offer, the options and what has been called.
    /// Nothing listens until MCP is switched on (or "Start with Revit" is ticked).
    /// </summary>
    public class App : IExternalApplication
    {
        public const string TabName = "Barr Gazetas"; // shared with the other Barr Gazetas add-ins: must match exactly
        public const string PanelName = "MCP";

        internal static App Instance { get; private set; }
        internal Hub Hub { get; private set; }
        /// <summary>A newer version installed by the Updater, waiting for Revit to restart.</summary>
        internal Version WaitingVersion { get; set; }
        private PushButton _toggle, _update;
        private System.Threading.Tasks.Task<Release> _check;

        public Result OnStartup(UIControlledApplication app)
        {
            Instance = this;
            // The DWG reader (ACadSharp) ships next to this add-in. Revit does not look there by itself, so this does,
            // for that reader only; another add-in's copy (Survey to Revit loads its own, separately) is left alone.
            string here = Path.GetDirectoryName(typeof(App).Assembly.Location);
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            {
                string name = new System.Reflection.AssemblyName(e.Name).Name;
                if (name != "ACadSharp" && name != "CSUtilities") return null;
                string dll = Path.Combine(here, name + ".dll");
                return File.Exists(dll) ? System.Reflection.Assembly.LoadFrom(dll) : null;
            };
            // The ExternalEvent behind the dispatcher must be made here, in a Revit API context.
            Hub = new Hub(new RevitDispatcher());

            try { app.CreateRibbonTab(TabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { /* another Barr Gazetas add-in made it */ }
            RibbonPanel panel = app.GetRibbonPanels(TabName).FirstOrDefault(p => p.Name == PanelName) ?? app.CreateRibbonPanel(TabName, PanelName);

            string me = typeof(App).Assembly.Location;
            _toggle = (PushButton)panel.AddItem(new PushButtonData("BG_MCP_Toggle", "MCP\nOff", me, typeof(ToggleMcpCommand).FullName)
            {
                AvailabilityClassName = typeof(AlwaysAvailable).FullName,
            });
            var stacked = panel.AddStackedItems(
                new PushButtonData("BG_MCP_Connect", "Connect", me, typeof(ConnectCommand).FullName)
                {
                    AvailabilityClassName = typeof(AlwaysAvailable).FullName,
                    ToolTip = "How to connect Claude, the tools on offer (this add-in's and the other Barr Gazetas add-ins'), options, and what has been called.",
                    Image = Icons.Glyph(16, ""),
                    LargeImage = Icons.Glyph(32, ""),
                },
                new PushButtonData("BG_MCP_Update", "Update", me, typeof(UpdateCommand).FullName)
                {
                    AvailabilityClassName = typeof(AlwaysAvailable).FullName,
                    Image = Icons.Glyph(16, ""),
                    LargeImage = Icons.Glyph(32, ""),
                });
            _update = stacked.OfType<PushButton>().FirstOrDefault(b => b.Name == "BG_MCP_Update");

            // Updates: tidy old version folders, see whether one is installed and waiting for a restart,
            // and look for a newer zip off Revit's thread (a slow network folder must not hold up start-up).
            try { Updater.Tidy(Updater.AddinsFolder, Updater.RunningFolder); } catch { }
            try { WaitingVersion = Updater.Waiting(Updater.AddinsFolder, Updater.RunningFolder, Updater.Running); } catch { }
            RefreshUpdate(null);
            if (Hub.Settings.CheckAtStartup && WaitingVersion == null)
            {
                string folder = Hub.Settings.Updates;
                _check = System.Threading.Tasks.Task.Run(() => Updater.Newer(folder, Updater.Running));
                app.Idling += ShowCheck;
            }

            if (Hub.Settings.AutoStart)
                try { Hub.Start(); } catch { /* e.g. the port is taken: the button shows Off and says why when clicked */ }
            RefreshButton();
            return Result.Succeeded;
        }

        /// <summary>Puts the start-up check's answer on the button once it is in (ribbon changes belong on Revit's thread).</summary>
        private void ShowCheck(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            if (_check == null || !_check.IsCompleted) return;
            if (sender is UIApplication ui) ui.Idling -= ShowCheck;
            RefreshUpdate(_check.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? _check.Result : null);
            _check = null;
        }

        internal void RefreshUpdate(Release waitingZip)
        {
            if (_update == null) return;
            string running = Updater.Running.ToString(3);
            if (WaitingVersion != null)
            {
                _update.ItemText = "Restart to update";
                _update.ToolTip = $"BG MCP {WaitingVersion.ToString(3)} is installed and starts the next time Revit starts. Running {running}.";
            }
            else if (waitingZip != null)
            {
                _update.ItemText = "Update ready";
                _update.ToolTip = $"BG MCP {waitingZip} is waiting in {Path.GetDirectoryName(waitingZip.Zip)}. Running {running}. Click to install.";
            }
            else
            {
                _update.ItemText = "Update";
                _update.ToolTip = $"Running BG MCP {running}. Installs a newer release from its zip (BGMCP-x.y.z.zip, no need to unzip). " +
                                  "Looks in Downloads unless another folder is set in Connect. Takes effect the next time Revit starts.";
            }
        }

        public Result OnShutdown(UIControlledApplication app)
        {
            Hub?.Stop();
            return Result.Succeeded;
        }

        internal void RefreshButton()
        {
            bool on = Hub.Server.IsRunning;
            _toggle.ItemText = on ? "MCP\nOn" : "MCP\nOff";
            _toggle.LargeImage = Icons.Make(32, on);
            _toggle.Image = Icons.Make(16, on);
            _toggle.ToolTip = on
                ? $"Claude can reach this Revit session at {Hub.Server.Url}" + (Hub.Settings.ReadOnly ? " (read only: no changes to the model)." : ".") + "\nClick to switch off."
                : "Switch on so Claude (or any MCP client on this computer) can read and work on this Revit session. Nothing is reachable while it is off.";
            _toggle.LongDescription = "Only programs on this computer can connect. Every change Claude makes is a normal undo step. Use Connect for the options.";
        }

        /// <summary>Switches the server on or off. Returns the reason when it could not be switched on.</summary>
        internal string Toggle()
        {
            string problem = null;
            try
            {
                if (Hub.Server.IsRunning) Hub.Stop(); else Hub.Start();
            }
            catch (Exception ex)
            {
                Hub.Stop();
                problem = $"The connection could not be switched on: {ex.Message}\n\n" +
                          $"Port {Hub.Settings.Port} may be in use (another Revit with MCP on?). Change the port in Connect, then try again.";
            }
            RefreshButton();
            return problem;
        }
    }

    /// <summary>Both buttons work with no project open.</summary>
    public class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication app, CategorySet selected) => true;
    }

    [Transaction(TransactionMode.Manual)]
    public class ToggleMcpCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var app = App.Instance;
            string problem = app.Toggle();
            if (problem != null) { TaskDialog.Show("Barr Gazetas MCP", problem); return Result.Succeeded; }
            // The first time it is switched on: show how to connect. After that the button just turns green.
            if (app.Hub.Server.IsRunning && !app.Hub.Settings.Introduced)
            {
                app.Hub.Settings.Introduced = true;
                app.Hub.Settings.Save();
                ConnectWindow.Show(app, data.Application.MainWindowHandle);
            }
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class ConnectCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            ConnectWindow.Show(App.Instance, data.Application.MainWindowHandle);
            return Result.Succeeded;
        }
    }
}
