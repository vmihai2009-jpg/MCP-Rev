using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace BarrGazetas.RevitMcp
{
    /// <summary>
    /// The Connect window: the switch, how to connect Claude (ready to copy), the options, the tools on offer
    /// from this add-in and the others, and the latest calls. Built in code: no XAML to ship.
    /// </summary>
    internal sealed class ConnectWindow : Window
    {
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x00, 0x54, 0x93));
        private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x1E, 0x8E, 0x3E));
        private static readonly Brush Grey = new SolidColorBrush(Color.FromRgb(0x5F, 0x63, 0x68));
        private static readonly Brush Soft = new SolidColorBrush(Color.FromRgb(0xF2, 0xF5, 0xF9));

        private readonly App _app;
        private Hub Hub => _app.Hub;
        private readonly TextBlock _state = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _switch = new Button { Padding = new Thickness(14, 4, 14, 4), MinWidth = 96 };
        private readonly TextBox _code = Copyable(false), _desktop = Copyable(true), _port = new TextBox { Width = 64, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly StackPanel _tools = new StackPanel(), _calls = new StackPanel();
        private readonly TextBlock _note = new TextBlock { Foreground = Grey, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBlock _version = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly TextBox _updates = new TextBox { IsReadOnly = true, Background = Soft, BorderThickness = new Thickness(0), Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center };

        public static void Show(App app, IntPtr owner)
        {
            var window = new ConnectWindow(app);
            new WindowInteropHelper(window).Owner = owner;
            window.ShowDialog();
        }

        private ConnectWindow(App app)
        {
            _app = app;
            Title = "Barr Gazetas MCP: connect Claude to Revit";
            Width = 640; Height = 720; MinWidth = 520; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 12.5;
            Background = Brushes.White;

            var root = new DockPanel();

            // the switch
            var head = new DockPanel { Background = Soft, LastChildFill = true };
            var headInner = new DockPanel { Margin = new Thickness(16, 12, 16, 12) };
            DockPanel.SetDock(_switch, Dock.Right);
            _switch.Click += (_, __) => { string problem = _app.Toggle(); Refresh(); if (problem != null) _note.Text = problem; };
            headInner.Children.Add(_switch);
            headInner.Children.Add(_state);
            head.Children.Add(headInner);
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);

            // the foot
            var foot = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 8, 16, 12) };
            foot.Children.Add(Link("Open the log folder", () => Open(CallLog.Folder)));
            var close = new Button { Content = "Close", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(12, 0, 0, 0), IsCancel = true, IsDefault = true };
            close.Click += (_, __) => Close();
            foot.Children.Add(close);
            DockPanel.SetDock(foot, Dock.Bottom);
            root.Children.Add(foot);

            var body = new StackPanel { Margin = new Thickness(16, 4, 16, 8) };

            body.Children.Add(Heading("1. Connect Claude (once)"));
            body.Children.Add(Label("Claude Code: run this in a terminal."));
            body.Children.Add(WithCopy(_code));
            body.Children.Add(Label("Claude Desktop: add this to claude_desktop_config.json (Settings > Developer > Edit Config), then restart Claude. Needs Node.js."));
            body.Children.Add(WithCopy(_desktop));

            body.Children.Add(Heading("2. Options"));
            body.Children.Add(Check("Start with Revit (otherwise it is off until you click MCP)", () => Hub.Settings.AutoStart, v => Hub.Settings.AutoStart = v));
            body.Children.Add(Check("Read only: Claude can look but not change the model", () => Hub.Settings.ReadOnly, v => Hub.Settings.ReadOnly = v));
            body.Children.Add(Check("Keep a log of calls (tool, time taken, sizes; never the content)", () => Hub.Settings.LogCalls, v => Hub.Settings.LogCalls = v));
            var portRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            portRow.Children.Add(new TextBlock { Text = "Port ", VerticalAlignment = VerticalAlignment.Center });
            portRow.Children.Add(_port);
            portRow.Children.Add(new TextBlock { Text = "  (change it only if another program uses this one)", Foreground = Grey, VerticalAlignment = VerticalAlignment.Center });
            _port.LostFocus += (_, __) => ApplyPort();
            _port.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { ApplyPort(); e.Handled = true; } };
            body.Children.Add(portRow);
            body.Children.Add(_note);

            body.Children.Add(Heading("Updates"));
            body.Children.Add(_version);
            var updatesRow = new DockPanel { Margin = new Thickness(0, 4, 0, 2) };
            var browse = new Button { Content = "Change...", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
            browse.Click += (_, __) => PickUpdatesFolder();
            var check = new Button { Content = "Check now", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
            check.Click += (_, __) => { UpdateFlow.Run(); Refresh(); };
            DockPanel.SetDock(check, Dock.Right);
            DockPanel.SetDock(browse, Dock.Right);
            updatesRow.Children.Add(check);
            updatesRow.Children.Add(browse);
            updatesRow.Children.Add(_updates);
            body.Children.Add(updatesRow);
            body.Children.Add(Check("Show \"Update ready\" on the ribbon when a newer zip is waiting there", () => Hub.Settings.CheckAtStartup, v => Hub.Settings.CheckAtStartup = v));

            body.Children.Add(Heading("3. What Claude can use"));
            body.Children.Add(_tools);

            body.Children.Add(Heading("Latest calls"));
            body.Children.Add(_calls);

            root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;
            Refresh();
        }

        private void Refresh()
        {
            bool on = Hub.Server.IsRunning;
            string url = on ? Hub.Server.Url : $"http://localhost:{Hub.Settings.Port}/mcp";
            _state.Text = on ? $"On   {url}" + (Hub.Settings.ReadOnly ? "   (read only)" : "") : "Off: Claude cannot reach Revit";
            _state.Foreground = on ? Green : Grey;
            _switch.Content = on ? "Switch off" : "Switch on";
            _port.Text = Hub.Settings.Port.ToString();
            _code.Text = $"claude mcp add --transport http revit {url}";
            _desktop.Text = "{\n  \"mcpServers\": {\n    \"revit\": {\n      \"command\": \"npx\",\n      \"args\": [\"-y\", \"mcp-remote\", \"" + url + "\"]\n    }\n  }\n}";

            string running = Updater.Running.ToString(3);
            _version.Text = _app.WaitingVersion != null
                ? $"Running {running}. {_app.WaitingVersion.ToString(3)} is installed and starts the next time Revit starts."
                : $"Running {running}. The Updater looks for BGMCP-x.y.z.zip in:";
            _updates.Text = Hub.Settings.Updates;

            _tools.Children.Clear();
            try
            {
                foreach (var group in Hub.Tools().GroupBy(t => t.Provider))
                {
                    _tools.Children.Add(new TextBlock
                    {
                        Text = $"{(group.Key == "Revit" ? "This add-in (general Revit tools)" : group.Key)}: {group.Count()} tools",
                        FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 1),
                    });
                    _tools.Children.Add(new TextBlock { Text = string.Join(", ", group.Select(t => t.Name + (t.Write ? "*" : ""))), TextWrapping = TextWrapping.Wrap, Foreground = Grey });
                }
                _tools.Children.Add(new TextBlock { Text = "* changes the model (one undo step each; refused when Read only is ticked)", Foreground = Grey, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 4, 0, 0) });
                foreach (var p in Hub.Providers.All().Where(p => p.Problem != null))
                    _tools.Children.Add(new TextBlock { Text = $"{p.Name} could not list its tools: {p.Problem}", Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap });
            }
            catch (Exception ex) { _tools.Children.Add(new TextBlock { Text = "The tools could not be listed: " + ex.Message, Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap }); }

            _calls.Children.Clear();
            var recent = Hub.Log.Recent(12);
            if (recent.Count == 0) _calls.Children.Add(new TextBlock { Text = "None yet in this Revit session.", Foreground = Grey });
            foreach (var r in recent)
                _calls.Children.Add(new TextBlock
                {
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = r.Ok ? Brushes.Black : Brushes.Firebrick,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Text = $"{r.When:HH:mm:ss}  {r.Tool,-26} {(r.Ms < 1000 ? r.Ms + " ms" : (r.Ms / 1000.0).ToString("0.0") + " s"),8}  " +
                           (r.Ok ? $"{r.ResultChars:N0} chars" : "failed: " + r.Error),
                });
            var (calls, failed, chars) = Hub.Log.Totals();
            if (calls > 0)
                _calls.Children.Add(new TextBlock { Text = $"This session: {calls} calls, {failed} failed, about {chars / 4:N0} tokens of answers sent to Claude.", Foreground = Grey, Margin = new Thickness(0, 4, 0, 0) });
        }

        private void ApplyPort()
        {
            if (!int.TryParse(_port.Text.Trim(), out int port) || port <= 1023 || port > 65535)
            {
                _note.Text = "The port is a number from 1024 to 65535.";
                _port.Text = Hub.Settings.Port.ToString();
                return;
            }
            if (port == Hub.Settings.Port) return;
            bool wasOn = Hub.Server.IsRunning;
            int old = Hub.Settings.Port;
            Hub.Settings.Port = port;
            Hub.Settings.Save();
            _note.Text = "Port changed. Connect Claude again with the new address above.";
            if (wasOn)
            {
                Hub.Stop();
                string problem = _app.Toggle();
                if (problem != null)
                {
                    Hub.Settings.Port = old; Hub.Settings.Save();
                    _app.Toggle();
                    _note.Text = $"Port {port} could not be used, so it stays {old}.";
                }
            }
            _app.RefreshButton();
            Refresh();
        }

        private void PickUpdatesFolder()
        {
            var pick = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Where the Updater looks for BGMCP-x.y.z.zip (Downloads, or a shared folder)",
                InitialDirectory = Directory.Exists(Hub.Settings.Updates) ? Hub.Settings.Updates : Updater.DefaultUpdates,
            };
            if (pick.ShowDialog(this) != true) return;
            // Downloads is the default: store nothing, so it follows the user if Windows moves it
            Hub.Settings.UpdatesFolder = string.Equals(pick.FolderName.TrimEnd('\\'), Updater.DefaultUpdates.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? null : pick.FolderName;
            Hub.Settings.Save();
            _app.RefreshUpdate(Updater.Newer(Hub.Settings.Updates, Updater.Running));
            Refresh();
        }

        // ---------- small parts ----------

        private static TextBlock Heading(string text) =>
            new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Accent, Margin = new Thickness(0, 14, 0, 4) };

        private static TextBlock Label(string text) =>
            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 2) };

        private static TextBox Copyable(bool lines) => new TextBox
        {
            IsReadOnly = true, FontFamily = new FontFamily("Consolas"), Background = Soft, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6), TextWrapping = lines ? TextWrapping.NoWrap : TextWrapping.Wrap, AcceptsReturn = lines,
        };

        private static UIElement WithCopy(TextBox box)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var copy = new Button { Content = "Copy", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            copy.Click += (_, __) =>
            {
                try { Clipboard.SetText(box.Text); copy.Content = "Copied"; }
                catch { copy.Content = "Select and Ctrl+C"; } // the clipboard is sometimes held by another program
            };
            DockPanel.SetDock(copy, Dock.Right);
            row.Children.Add(copy);
            row.Children.Add(box);
            return row;
        }

        private CheckBox Check(string text, Func<bool> get, Action<bool> set)
        {
            var box = new CheckBox { Content = text, IsChecked = get(), Margin = new Thickness(0, 3, 0, 3) };
            RoutedEventHandler changed = (_, __) =>
            {
                set(box.IsChecked == true);
                Hub.Settings.Save();
                _app.RefreshButton();
                Refresh();
            };
            box.Checked += changed;
            box.Unchecked += changed;
            return box;
        }

        private static Button Link(string text, Action click)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4) };
            b.Click += (_, __) => click();
            return b;
        }

        private static void Open(string folder)
        {
            try { Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); } catch { }
        }
    }
}
