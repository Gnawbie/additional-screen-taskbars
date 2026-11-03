using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace AST
{
    /// <summary>One program in the Start menu's list.</summary>
    internal sealed class StartMenuApp : Observable
    {
        private ImageSource _icon;
        public string Name { get; set; }
        public string AppId { get; set; }
        public string Letter => Name.Length > 0 && char.IsLetter(Name[0]) ? char.ToUpperInvariant(Name[0]).ToString() : "#";
        public ImageSource Icon { get => _icon; set => Set(ref _icon, value); }
    }

    /// <summary>
    /// A Windows 10 style Start menu: buttons down the left edge, an alphabetical app list with search, and pinned
    /// tiles. It's an ordinary AST window, so it never touches Explorer or the real Start menu.
    /// </summary>
    internal sealed class StartMenuWindow : Window
    {
        private const double MenuWidth = 600, MenuHeight = 640;
        private static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(350);
        private static readonly TimeSpan OpeningGrace = TimeSpan.FromMilliseconds(500);
        private DateTime _lostFocusAt = DateTime.MinValue;
        private DateTime _shownAt = DateTime.MinValue;

        private readonly Config _config;
        private readonly Func<IEnumerable<StartTile>> _defaultPins;
        private readonly ObservableCollection<StartMenuApp> _apps = new ObservableCollection<StartMenuApp>();
        private readonly ICollectionView _view;
        private readonly TextBox _search;
        private readonly TextBlock _searchHint;
        private readonly ListBox _list;
        private readonly StackPanel _pins;
        private readonly TextBlock _pinsHint;
        private readonly Style _pinStyle;
        private readonly FrameworkElement _root;
        private DateTime _lastRefresh = DateTime.MinValue;
        private bool _refreshing;
        private Point _dragStart;

        public StartMenuWindow(Config config, Func<IEnumerable<StartTile>> defaultPins)
        {
            _config = config;
            _defaultPins = defaultPins;
            Title = "AST Start";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = true;
            Width = MenuWidth;
            Height = MenuHeight;
            UseLayoutRounding = true;

            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("AST.Themes.startmenu.xaml"))
                _root = (FrameworkElement)XamlReader.Load(s);
            Content = _root;
            _search = (TextBox)_root.FindName("Search");
            _searchHint = (TextBlock)_root.FindName("SearchHint");
            _list = (ListBox)_root.FindName("Apps");
            _pins = (StackPanel)_root.FindName("Pins");
            _pinsHint = (TextBlock)_root.FindName("PinsHint");
            _pinStyle = (Style)_root.Resources["PinButton"];

            _view = CollectionViewSource.GetDefaultView(_apps);
            _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(StartMenuApp.Letter)));
            _view.Filter = o => string.IsNullOrEmpty(_search.Text) || ((StartMenuApp)o).Name.IndexOf(_search.Text, StringComparison.OrdinalIgnoreCase) >= 0;
            _list.ItemsSource = _view;

            _search.TextChanged += (s, e) =>
            {
                _searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                _view.Refresh();
            };
            _search.PreviewKeyDown += OnSearchKey;
            _list.PreviewMouseLeftButtonUp += (s, e) => { if (AppUnder(e) is StartMenuApp app) Launch(app.AppId); };
            _list.PreviewMouseRightButtonUp += OnAppRightClick;
            _pins.Drop += OnPinDropped;
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Hide(); };
            Deactivated += OnLostFocus;
            SourceInitialized += (s, e) =>
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
            };

            WireRail();
        }

        private void WireRail()
        {
            var user = (Button)_root.FindName("UserButton");
            user.ToolTip = Environment.UserName;
            user.Click += (s, e) => Open("ms-settings:accounts");
            ((Button)_root.FindName("DocumentsButton")).Click += (s, e) => Open(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            ((Button)_root.FindName("PicturesButton")).Click += (s, e) => Open(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            ((Button)_root.FindName("SettingsButton")).Click += (s, e) => Open("ms-settings:");
            ((Button)_root.FindName("PowerButton")).Click += (s, e) => ShowPowerMenu();
        }

        private void ApplyLook()
        {
            bool classic = _config.Theme == "classic";
            SetBrush("Menu.Back", classic ? "#C0C0C0" : "#1F1F1F");
            SetBrush("Menu.Panel", classic ? "#A8A8A8" : "#181818");
            SetBrush("Menu.Text", classic ? "#000000" : "#F2F2F2");
            SetBrush("Menu.Dim", classic ? "#404040" : "#A8A8A8");
            SetBrush("Menu.Hover", classic ? "#FFFFFF" : "#3A3A3A");
            SetBrush("Menu.Tile", classic ? "#D8D8D8" : "#2B2B2B");
            Background = (Brush)_root.Resources["Menu.Back"];
            _root.SetValue(TextElement.FontFamilyProperty, new FontFamily(classic ? "Tahoma" : "Segoe UI"));
        }

        private void SetBrush(string key, string color) =>
            _root.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

        /// <summary>Windows can refuse focus just as the menu opens, which shouldn't count as clicking away from it.</summary>
        private void OnLostFocus(object sender, EventArgs e)
        {
            if (DateTime.Now - _shownAt < OpeningGrace)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!IsVisible) return;
                    Activate();
                    Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
                    _search.Focus();
                }), DispatcherPriority.Input);
                return;
            }
            Hide();
            _lostFocusAt = DateTime.Now;
        }

        /// <summary>
        /// Pressing Start while the menu is open can take focus first, which closes the menu just before the press
        /// arrives. Without this check, that press would then open it straight back up.
        /// </summary>
        public bool JustClosed => DateTime.Now - _lostFocusAt < ReopenGuard;

        /// <summary>Opens next to the given bar edge, on that bar's screen.</summary>
        public void ShowAt(Native.RECT bar, BarEdge edge, MonitorInfo monitor)
        {
            ApplyLook();
            Refresh();
            BuildPins();
            _search.Text = "";
            _view.Refresh();
            _shownAt = DateTime.Now;
            Show();

            IntPtr h = new WindowInteropHelper(this).Handle;
            double scale = monitor.DpiScale;
            int w = (int)Math.Round(MenuWidth * scale), height = (int)Math.Round(MenuHeight * scale);
            var b = monitor.Bounds;
            int x, y;
            switch (edge)
            {
                case BarEdge.Left: x = bar.Right; y = bar.Top; break;
                case BarEdge.Right: x = bar.Left - w; y = bar.Top; break;
                case BarEdge.Top: x = bar.Left; y = bar.Bottom; break;
                default: x = bar.Left; y = bar.Top - height; break;
            }
            x = Math.Max(b.Left, Math.Min(x, b.Right - w));
            y = Math.Max(b.Top, Math.Min(y, b.Bottom - height));
            Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, w, height, Native.SWP_SHOWWINDOW);
            Activate();
            Native.SetForegroundWindow(h);
            _search.Focus();
            Keyboard.Focus(_search);
        }

        // ---- The app list ----

        /// <summary>Reads the list on its own thread, because hundreds of icons would otherwise freeze the bars.</summary>
        public void Refresh()
        {
            if (_refreshing || DateTime.Now - _lastRefresh < RefreshAfter) return;
            _refreshing = true;
            var thread = new Thread(() =>
            {
                try
                {
                    var found = ReadInstalledApps();
                    var known = new Dictionary<string, StartMenuApp>(StringComparer.OrdinalIgnoreCase);
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var a in _apps) known[a.AppId] = a;
                        var merged = found.Select(f => known.TryGetValue(f.AppId, out var old) ? old : f).ToList();
                        _apps.Clear();
                        foreach (var a in merged) _apps.Add(a);
                        found = merged;
                    });
                    foreach (var app in found.Where(a => a.Icon == null))
                    {
                        var icon = PinnedPrograms.AppIcon(app.AppId);
                        Dispatcher.BeginInvoke(new Action(() => app.Icon = icon), DispatcherPriority.Background);
                    }
                }
                catch (Exception ex) { Log.Write("Could not read the app list: " + ex.Message); }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _refreshing = false;
                    _lastRefresh = DateTime.Now;
                }));
            }) { IsBackground = true, Name = "AST app list" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private static List<StartMenuApp> ReadInstalledApps()
        {
            var apps = new List<StartMenuApp>();
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name, id = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(id)) continue;
                if (name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                apps.Add(new StartMenuApp { Name = name, AppId = id });
            }
            // Windows leaves AST out of this list on at least some PCs.
            if (File.Exists(Installer.InstalledExe) && !apps.Any(a => a.Name == Installer.AppName))
                apps.Add(new StartMenuApp { Name = Installer.AppName, AppId = Installer.InstalledExe });
            return apps.OrderBy(a => a.Letter == "#" ? 0 : 1).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static StartMenuApp AppUnder(MouseEventArgs e) =>
            (e.OriginalSource as FrameworkElement)?.DataContext as StartMenuApp;

        private void OnSearchKey(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            var first = _view.Cast<StartMenuApp>().FirstOrDefault();
            if (first != null) Launch(first.AppId);
            e.Handled = true;
        }

        private void OnAppRightClick(object sender, MouseButtonEventArgs e)
        {
            var app = AppUnder(e);
            if (app == null) return;
            e.Handled = true;
            var menu = new ContextMenu();
            menu.Items.Add(Item("Open", () => Launch(app.AppId)));
            var pin = _config.StartPins.FirstOrDefault(p => SameApp(p.AppId, app.AppId));
            if (pin != null) menu.Items.Add(Item("Unpin from Start", () => RemovePin(pin)));
            else menu.Items.Add(Item("Pin to Start", () =>
            {
                _config.StartPins.Add(new StartTile { AppId = app.AppId, Name = app.Name });
                SavePins();
            }));
            menu.IsOpen = true;
        }

        // ---- Programs pinned above the search bar ----

        private static bool SameApp(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>The first time, this fills the list from the old tiles if there were any, or the taskbar's pins.</summary>
        private void SetUpPins()
        {
            if (_config.StartPinsSetUp) return;
            var first = _config.StartTilesSetUp ? _config.StartTiles : _defaultPins();
            foreach (var pin in first)
                if (!_config.StartPins.Any(p => SameApp(p.AppId, pin.AppId))) _config.StartPins.Add(pin);
            _config.StartTiles.Clear();
            _config.StartPinsSetUp = true;
            _config.Save();
        }

        private void BuildPins()
        {
            SetUpPins();
            _pins.Children.Clear();
            foreach (var pin in _config.StartPins) _pins.Children.Add(MakePin(pin));
            _pinsHint.Visibility = _config.StartPins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private FrameworkElement MakePin(StartTile pin)
        {
            var content = new DockPanel();
            content.Children.Add(new Image { Source = PinnedPrograms.AppIcon(pin.AppId), Width = 24, Height = 24, Margin = new Thickness(0, 0, 10, 0) });
            content.Children.Add(new TextBlock { Text = pin.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

            var button = new Button { Style = _pinStyle, Content = content, ToolTip = pin.Name, Tag = pin, AllowDrop = true };
            button.Click += (s, e) => Launch(pin.AppId);
            button.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                var menu = new ContextMenu();
                menu.Items.Add(Item("Open", () => Launch(pin.AppId)));
                menu.Items.Add(Item("Unpin from Start", () => RemovePin(pin)));
                menu.IsOpen = true;
            };
            AllowDragging(button, pin);
            button.Drop += OnPinDropped;
            return button;
        }

        private void OnPinDropped(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (MoveDropped(_config.StartPins, e, sender)) SavePins();
        }

        private void RemovePin(StartTile pin)
        {
            _config.StartPins.Remove(pin);
            SavePins();
        }

        private void SavePins()
        {
            _config.Save();
            BuildPins();
        }

        private void AllowDragging(Button button, StartTile item)
        {
            button.PreviewMouseLeftButtonDown += (s, e) => _dragStart = e.GetPosition(this);
            button.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                var moved = e.GetPosition(this) - _dragStart;
                if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                DragDrop.DoDragDrop(button, item, DragDropEffects.Move);
            };
        }

        private static bool MoveDropped(List<StartTile> list, DragEventArgs e, object droppedOn)
        {
            var dragged = e.Data.GetData(typeof(StartTile)) as StartTile;
            var target = (droppedOn as Button)?.Tag as StartTile;
            if (dragged == null || dragged == target || !list.Contains(dragged)) return false;
            list.Remove(dragged);
            int at = target == null || !list.Contains(target) ? list.Count : list.IndexOf(target);
            list.Insert(at, dragged);
            return true;
        }

        // ---- Actions ----

        private void Launch(string appId)
        {
            Hide();
            PinnedPrograms.Launch(new PinnedEntry { AppId = appId });
        }

        private void Open(string target)
        {
            Hide();
            TrayMirror.OpenSettingsPage(target);
        }

        private void ShowPowerMenu()
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Lock", () => { Hide(); LockWorkStation(); }));
            menu.Items.Add(Item("Sign out", () => { Hide(); RunShutdown("/l"); }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Sleep", () => { Hide(); System.Windows.Forms.Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, false, false); }));
            menu.Items.Add(Item("Shut down", () => { Hide(); RunShutdown("/s /t 0"); }));
            menu.Items.Add(Item("Restart", () => { Hide(); RunShutdown("/r /t 0"); }));
            menu.IsOpen = true;
        }

        private static void RunShutdown(string arguments)
        {
            try { Process.Start(new ProcessStartInfo("shutdown.exe", arguments) { UseShellExecute = false, CreateNoWindow = true }); }
            catch (Exception ex) { Log.Write("Power action failed: " + ex.Message); }
        }

        private static MenuItem Item(string text, Action action)
        {
            var i = new MenuItem { Header = text };
            i.Click += (s, e) => action();
            return i;
        }

        [DllImport("user32.dll")] private static extern bool LockWorkStation();
    }
}
