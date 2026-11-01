using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AST
{
    /// <summary>Changes apply immediately, so there is no Save button to forget.</summary>
    internal sealed class SettingsWindow : Window
    {
        private readonly BarManager _manager;
        private readonly Config _config;

        public SettingsWindow(BarManager manager)
        {
            _manager = manager;
            _config = manager.Config;
            Title = "AST options";
            Width = 700;
            SizeToContent = SizeToContent.Height;
            MaxHeight = SystemParameters.WorkArea.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanMinimize;
            FontSize = 13;
            Background = SystemColors.WindowBrush;

            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(Heading("Screens"));
            foreach (var m in _manager.CurrentMonitors) root.Children.Add(MonitorRow(m));
            root.Children.Add(Note("You can also drag an empty part of a bar to another edge, or drag its inner edge to resize it. To stop this, right-click a bar and choose Lock taskbars."));
            var identify = new Button { Content = "Identify screens", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            identify.Click += (s, e) => IdentifyScreens();
            root.Children.Add(identify);

            root.Children.Add(Heading("Look"));
            root.Children.Add(ThemeRow());
            root.Children.Add(Check("Show program names vertically on side bars", _config.VerticalSideLabels, v => { _config.VerticalSideLabels = v; _manager.Sync(); }));
            root.Children.Add(Check("Show tray buttons (hidden icons, network, volume)", !_config.HideTrayButtons, v => { _config.HideTrayButtons = !v; _manager.Sync(); }));
            root.Children.Add(Check("Show clock", _config.ShowClock, v => { _config.ShowClock = v; _manager.Sync(); }));
            root.Children.Add(Check("Show Start button", _config.ShowStartButton, v => { _config.ShowStartButton = v; _manager.Sync(); }));

            root.Children.Add(Heading("Mods"));
            root.Children.Add(Note("Third-party themes and plugins can run code on your PC, just like any program you install. Only add ones you trust."));
            var folders = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
            folders.Children.Add(FolderButton("Open themes folder", Config.ThemesDir));
            folders.Children.Add(FolderButton("Open plugins folder", Config.PluginsDir));
            root.Children.Add(folders);
            var plugins = _manager.Themes.AvailablePluginFiles().ToList();
            if (plugins.Count == 0) root.Children.Add(Note("No plugins found."));
            foreach (var file in plugins)
            {
                string f = file;
                root.Children.Add(Check("Enable plugin: " + f, _config.EnabledPlugins.Contains(f), v => TogglePlugin(f, v)));
            }

            root.Children.Add(Heading("Startup"));
            root.Children.Add(Check("Start AST with Windows", Installer.StartsWithWindows, v => Installer.StartsWithWindows = v));

            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private UIElement MonitorRow(MonitorInfo m)
        {
            var cfg = _config.For(m);
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });

            string label = "Screen " + m.Number + ":  " + m.Name + "  (" + m.Width + "x" + m.Height + (m.IsPrimary ? ", main screen" : "") + ")";
            var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = label };
            grid.Children.Add(name);

            var edge = new ComboBox { ItemsSource = Enum.GetValues(typeof(BarEdge)), SelectedItem = cfg.EdgeValue, ToolTip = "Where the bar sits on this screen. Off means no bar." };
            Grid.SetColumn(edge, 1);
            grid.Children.Add(edge);

            var size = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
            var slider = new Slider { Minimum = Config.MinThickness, Maximum = Config.MaxThickness, Width = 130, Value = cfg.EffectiveThickness, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Bar thickness" };
            var value = new TextBlock { Text = cfg.EffectiveThickness + " px", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            size.Children.Add(slider);
            size.Children.Add(value);
            size.IsEnabled = cfg.EdgeValue != BarEdge.Off;
            Grid.SetColumn(size, 2);
            grid.Children.Add(size);

            edge.SelectionChanged += (s, e) =>
            {
                // Already applied: the bar was dragged to this edge and this list is only catching up.
                if (cfg.EdgeValue == (BarEdge)edge.SelectedItem) return;
                cfg.EdgeValue = (BarEdge)edge.SelectedItem;
                // Switching between side and top or bottom needs a different default thickness.
                cfg.Thickness = 0;
                slider.Value = cfg.EffectiveThickness;
                value.Text = cfg.EffectiveThickness + " px";
                size.IsEnabled = cfg.EdgeValue != BarEdge.Off;
                _manager.Sync();
            };
            slider.PreviewMouseUp += (s, e) => { cfg.Thickness = (int)slider.Value; _manager.Sync(); };
            slider.ValueChanged += (s, e) => value.Text = (int)slider.Value + " px";

            Action showDraggedLayout = () =>
            {
                edge.SelectedItem = cfg.EdgeValue;
                slider.Value = cfg.EffectiveThickness;
            };
            _manager.BarLayoutChanged += showDraggedLayout;
            Closed += (s, e) => _manager.BarLayoutChanged -= showDraggedLayout;
            return grid;
        }

        /// <summary>Shows each screen's number on that screen for a moment, like the Identify button in Windows.</summary>
        private void IdentifyScreens()
        {
            foreach (var m in _manager.CurrentMonitors)
            {
                var monitor = m;
                var badge = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Topmost = true,
                    Width = 220,
                    Height = 220,
                    Background = new SolidColorBrush(Color.FromRgb(0, 103, 192)),
                    Content = new TextBlock { Text = monitor.Number.ToString(), FontSize = 120, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
                badge.Loaded += (s, e) =>
                {
                    int size = (int)(220 * monitor.DpiScale);
                    int x = monitor.Bounds.Left + (monitor.Width - size) / 2;
                    int y = monitor.Bounds.Top + (monitor.Height - size) / 2;
                    Native.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(badge).Handle, Native.HWND_TOPMOST, x, y, size, size, Native.SWP_NOACTIVATE);
                };
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                timer.Tick += (s, e) => { timer.Stop(); badge.Close(); };
                badge.Show();
                timer.Start();
            }
        }

        private UIElement ThemeRow()
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
            var themes = _manager.Themes.List();
            var combo = new ComboBox { ItemsSource = themes, SelectedItem = themes.FirstOrDefault(t => t.Id == _config.Theme) ?? themes[0], MinWidth = 240 };
            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem is ThemeEntry t)
                {
                    _config.Theme = t.Id;
                    _manager.Themes.ClearCache();
                    _manager.Sync();
                }
            };
            row.Children.Add(new TextBlock { Text = "Theme", Width = 60, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(combo);
            return row;
        }

        private void TogglePlugin(string file, bool on)
        {
            if (on && !_config.EnabledPlugins.Contains(file))
            {
                var ok = MessageBox.Show(this, "Plugins run as normal programs with full access to your account.\n\nOnly enable \"" + file + "\" if you trust where it came from.",
                    "Enable plugin?", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (ok != MessageBoxResult.OK)
                {
                    Rebuild();
                    return;
                }
                _config.EnabledPlugins.Add(file);
                _manager.Themes.LoadEnabledPlugins();
            }
            else if (!on) _config.EnabledPlugins.Remove(file);
            _config.Save();
            Rebuild();
        }

        private void Rebuild()
        {
            var fresh = new SettingsWindow(_manager) { Left = Left, Top = Top, WindowStartupLocation = WindowStartupLocation.Manual };
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                Program.ReplaceSettingsWindow(this, fresh);
            }));
        }

        private static TextBlock Heading(string text) => new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };

        private static TextBlock Note(string text) => new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };

        private static CheckBox Check(string text, bool value, Action<bool> changed)
        {
            var c = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 3, 0, 3) };
            c.Click += (s, e) => changed(c.IsChecked == true);
            return c;
        }

        private static Button FolderButton(string text, string path)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
            b.Click += (s, e) =>
            {
                Directory.CreateDirectory(path);
                Process.Start("explorer.exe", "\"" + path + "\"");
            };
            return b;
        }
    }
}
