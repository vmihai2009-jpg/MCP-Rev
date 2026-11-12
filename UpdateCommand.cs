using System;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BarrGazetas.RevitMcp
{
    /// <summary>The Update button: finds a newer BGMCP-x.y.z.zip and installs it for the next Revit start.</summary>
    [Transaction(TransactionMode.Manual)]
    public class UpdateCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UpdateFlow.Run();
            return Result.Succeeded;
        }
    }

    internal static class UpdateFlow
    {
        private const string Title = "Barr Gazetas MCP: Updater";

        /// <summary>Looks for an update, offers it, installs it. Call from a Revit command (it shows dialogs).</summary>
        public static void Run()
        {
            var app = App.Instance;
            var settings = app.Hub.Settings;
            Version running = Updater.Running;
            string folder = settings.Updates;

            if (app.WaitingVersion != null)
            {
                var waiting = new TaskDialog(Title)
                {
                    MainInstruction = $"BG MCP {app.WaitingVersion.ToString(3)} is installed",
                    MainContent = $"It starts the next time Revit starts. Until then {running.ToString(3)} keeps running.",
                    CommonButtons = TaskDialogCommonButtons.Close,
                };
                waiting.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Install a different zip instead...");
                if (waiting.Show() == TaskDialogResult.CommandLink1) Choose(folder, running);
                return;
            }

            var newer = Updater.Newer(folder, running);
            var dialog = new TaskDialog(Title) { CommonButtons = TaskDialogCommonButtons.Close };
            if (newer != null)
            {
                dialog.MainInstruction = $"BG MCP {newer} is ready to install";
                dialog.MainContent = $"Running {running.ToString(3)}. Found {Path.GetFileName(newer.Zip)} in {folder}.\n\n" +
                                     "It takes effect the next time Revit starts; until then this version keeps working. The current version is kept, to go back to.";
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, $"Install {newer}");
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Choose another zip...");
            }
            else
            {
                dialog.MainInstruction = $"BG MCP is up to date ({running.ToString(3)})";
                dialog.MainContent = $"No newer BGMCP-x.y.z.zip in {folder}.\n\nSave a new release zip there (no need to unzip it), or choose one. The folder can be changed in Connect.";
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Choose a zip...");
            }

            var pick = dialog.Show();
            if (pick == TaskDialogResult.CommandLink1 && newer != null) Install(newer.Zip, running);
            else if (pick == TaskDialogResult.CommandLink2) Choose(folder, running);
        }

        private static void Choose(string folder, Version running)
        {
            var open = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a BG MCP release zip",
                Filter = "BG MCP release (BGMCP-*.zip)|BGMCP-*.zip|Zip files (*.zip)|*.zip",
                InitialDirectory = Directory.Exists(folder) ? folder : Updater.DefaultUpdates,
            };
            if (open.ShowDialog() == true) Install(open.FileName, running);
        }

        private static void Install(string zip, Version running)
        {
            var named = Updater.VersionOfName(zip);
            if (named != null && named < running)
            {
                var back = new TaskDialog(Title)
                {
                    MainInstruction = $"Go back to {named.ToString(3)}?",
                    MainContent = $"That zip is older than the version running now ({running.ToString(3)}).",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No,
                };
                if (back.Show() != TaskDialogResult.Yes) return;
            }
            try
            {
                Updater.Install(zip, Updater.AddinsFolder, Updater.RunningFolder, out Version version);
                App.Instance.WaitingVersion = version;
                App.Instance.RefreshUpdate(null);
                TaskDialog.Show(Title, $"BG MCP {version.ToString(3)} is installed.\n\nRestart Revit to use it. Until then {running.ToString(3)} keeps working.");
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Title, "Nothing was changed.\n\n" + ex.Message);
            }
        }
    }
}
