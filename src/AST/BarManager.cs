using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AST
{
    /// <summary>Creates, updates and removes one bar per screen so they match the settings.</summary>
    internal sealed class BarManager : IDisposable
    {
        private readonly Dictionary<string, BarWindow> _bars = new Dictionary<string, BarWindow>(StringComparer.OrdinalIgnoreCase);
        private readonly WindowTracker _tracker = new WindowTracker();
        private readonly TrayMirror _tray = new TrayMirror();
        private readonly DispatcherTimer _clock;
        private readonly DispatcherTimer _displayDebounce;
        private readonly Action _showSettings;
        private List<MonitorInfo> _monitors = new List<MonitorInfo>();
        private bool _replacingTaskbar;
        private StartMenuWindow _startMenu;
        private readonly StartKeyHook _startKey;
        private StartMenuWindow StartMenu
        {
            get
            {
                if (_startMenu != null) return _startMenu;
                _startMenu = new StartMenuWindow(Config, DefaultStartTiles, IsPinned, SetTaskbarPin);
                // Bars show alongside the menu over a fullscreen program, then hide again with it.
                _startMenu.IsVisibleChanged += (s, e) => _startMenu.Dispatcher.BeginInvoke(new Action(_tracker.Refresh));
                return _startMenu;
            }
        }

        public Config Config { get; }
        public ThemeManager Themes { get; }
        public IReadOnlyList<MonitorInfo> CurrentMonitors => _monitors;

        /// <summary>Raised when a bar was moved or resized by dragging, so an open Options window can catch up.</summary>
        public event Action BarLayoutChanged;

        public BarManager(Config config, Action showSettings)
        {
            Config = config;
            Themes = new ThemeManager(config);
            _showSettings = showSettings;
            _startKey = new StartKeyHook(OpenStartMenuFromKey);
            _tracker.Changed += OnWindowsChanged;
            _tray.Changed += OnTrayChanged;
            _tray.PinnedChanged += ApplyPinned;
            _tray.OrderChanged += ApplyOrder;

            _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clock.Tick += (s, e) => TickClock();
            _clock.Start();

            _displayDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _displayDebounce.Tick += (s, e) => { _displayDebounce.Stop(); Sync(); };
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            _displayDebounce.Stop();
            _displayDebounce.Start();
        }

        /// <summary>Applies the current settings to the screens that are connected right now.</summary>
        public void Sync()
        {
            _monitors = Monitors.Enumerate();
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in _monitors)
            {
                var cfg = Config.For(m);
                if (cfg.EdgeValue == BarEdge.Off) continue;
                wanted.Add(m.Id);

                if (_bars.TryGetValue(m.Id, out var bar))
                {
                    bar.Apply(m, cfg);
                    if (bar.ThemeId != Config.Theme) ApplyTheme(bar);
                }
                else
                {
                    bar = new BarWindow(m, cfg, _showSettings);
                    bar.LayoutDragged += () =>
                    {
                        Config.Save();
                        BarLayoutChanged?.Invoke();
                    };
                    bar.AllScreensToggled += () =>
                    {
                        Config.OwnScreenProgramsOnly = !Config.OwnScreenProgramsOnly;
                        Sync();
                    };
                    bar.PinnedToggled += () =>
                    {
                        Config.ShowPinnedPrograms = !Config.ShowPinnedPrograms;
                        Sync();
                    };
                    bar.PinToggled += TogglePin;
                    bar.IsItemPinned = IsPinned;
                    bar.StartTileDropped += tile => SetTaskbarPin(tile.AppId, tile.Name, null, true);
                    bar.HideForFullscreenToggled += () =>
                    {
                        Config.KeepBarsOverFullscreen = !Config.KeepBarsOverFullscreen;
                        Sync();
                    };
                    bar.LockToggled += () =>
                    {
                        Config.LockBars = !Config.LockBars;
                        Sync();
                    };
                    bar.ProgramNamesToggled += () =>
                    {
                        Config.ShowProgramNames = !Config.ShowProgramNames;
                        Sync();
                    };
                    bar.UpdateViewModelLayout();
                    ApplyTheme(bar);
                    bar.ViewModel.UpdateTray(_tray.Current);
                    _bars[m.Id] = bar;
                    bar.Show();
                }
                bar.ViewModel.ShowClock = Config.ShowClock;
                bar.ViewModel.ShowStartButton = Config.ShowStartButton;
                bar.ViewModel.VerticalSideLabels = Config.VerticalSideLabels;
                bar.ViewModel.IconsOnly = !Config.ShowProgramNames;
                bar.IsLocked = Config.LockBars;
                bar.ShowsAllScreens = !Config.OwnScreenProgramsOnly;
                bar.ViewModel.ShowTray = !Config.HideTrayButtons;
                bar.ViewModel.SpeedTestUrl = SpeedTests.Find(Config.SpeedTestSite).Url;
                bar.ShowsPinned = Config.ShowPinnedPrograms;
                bar.HidesForFullscreen = !Config.KeepBarsOverFullscreen;
                var thisBar = bar;
                bar.ViewModel.StartOverride = Config.UseAstStartMenu ? () => ToggleStartMenu(thisBar) : (Action)null;
            }
            foreach (var id in _bars.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                _bars[id].Close();
                _bars.Remove(id);
            }
            // A bar on the main screen takes the place of the Windows taskbar, which would otherwise sit alongside it.
            _replacingTaskbar = _monitors.Any(m => m.IsPrimary && Config.For(m).EdgeValue != BarEdge.Off);
            if (_replacingTaskbar) KeepTaskbarReplaced();
            else MainTaskbar.Release(Config);
            Config.Save();
            TickClock();
            ApplyPinned();
            ApplyOrder();
            // Reads the app list ahead of time, so the first open isn't an empty menu.
            if (Config.UseAstStartMenu) StartMenu.Refresh();
            else if (_startMenu != null && _startMenu.IsVisible) _startMenu.Hide();
            _tracker.Refresh();
        }

        private void ApplyTheme(BarWindow bar)
        {
            var content = Themes.CreateBar(Config.Theme, bar.ViewModel, out string used);
            bar.SetContent(content, Config.Theme);
            if (used != Config.Theme) Log.Write("Bar on " + bar.Monitor.Name + " is showing " + used + " instead of " + Config.Theme);
        }

        /// <summary>Called when a third-party look throws while running: switches every bar to the built-in theme.</summary>
        public void FallBackToBuiltInTheme()
        {
            // Built-in themes are left alone, because an error elsewhere in AST says nothing about them.
            if (Config.Theme == ThemeManager.FallbackThemeId || Config.Theme == "classic") return;
            Log.Write("Switching to the built-in theme after an error in " + Config.Theme);
            Config.Theme = ThemeManager.FallbackThemeId;
            Sync();
        }

        public void ReleaseTaskbar() => MainTaskbar.Release(Config);

        private IntPtr _knownTaskbar;
        private DateTime _knownTaskbarSince;
        private bool _taskbarReady;

        /// <summary>
        /// Runs often, and copes with the taskbar not existing yet at sign-in or being replaced when Explorer
        /// restarts. A new taskbar gets time to build its buttons, but not forever, because until then the Windows
        /// taskbar's reserved space stays reserved.
        /// </summary>
        private void KeepTaskbarReplaced()
        {
            IntPtr taskbar = MainTaskbar.TaskbarHandle;
            if (taskbar == IntPtr.Zero) return;
            if (taskbar != _knownTaskbar)
            {
                _knownTaskbar = taskbar;
                _knownTaskbarSince = DateTime.Now;
                _taskbarReady = false;
            }
            if (!_taskbarReady && (_tray.LastScanFoundButtons || DateTime.Now - _knownTaskbarSince > TaskbarBuildLimit))
            {
                _taskbarReady = true;
                Log.Write(_tray.LastScanFoundButtons ? "Windows taskbar's buttons were read" : "Gave up waiting for the Windows taskbar's buttons");
            }
            MainTaskbar.Engage(Config, _taskbarReady);
        }

        private static readonly TimeSpan TaskbarBuildLimit = TimeSpan.FromSeconds(30);

        private void ToggleStartMenu(BarWindow bar)
        {
            if (StartMenu.IsVisible) StartMenu.Hide();
            else if (!StartMenu.JustClosed) StartMenu.ShowAt(bar.DockedRect, bar.Settings.EdgeValue, bar.Monitor);
        }

        /// <summary>Opens on the screen you're working on, or the first bar if that screen doesn't have one.</summary>
        private void OpenStartMenuFromKey()
        {
            if (StartMenu.IsVisible)
            {
                StartMenu.Hide();
                return;
            }
            IntPtr active = Native.MonitorFromWindow(Native.GetForegroundWindow(), Native.MONITOR_DEFAULTTONEAREST);
            var bar = _bars.Values.FirstOrDefault(b => b.Monitor.Handle == active) ?? _bars.Values.FirstOrDefault();
            if (bar != null) ToggleStartMenu(bar);
        }

        /// <summary>The first tiles are what's pinned to the Windows taskbar, leaving out any whose program has gone.</summary>
        private IEnumerable<StartTile> DefaultStartTiles() =>
            _tray.TaskOrder
                .Where(o => o.IsPinned && PinnedPrograms.AppIcon(o.AppId) != null)
                .Select(o => new StartTile { AppId = o.AppId, Name = o.Name, Size = 2 })
                .ToList();

        private void OnWindowsChanged(List<TrackedWindow> windows)
        {
            // Unless asked to, never watch the keyboard alongside a fullscreen program, such as a game with anti-cheat.
            _startKey.SetEnabled(Config.UseAstStartMenu && Config.StartMenuUsesWindowsKey
                && (Config.StartMenuKeyInFullscreen || !_monitors.Any(m => WindowTracker.IsFullscreenOn(m, windows))));
            if (_replacingTaskbar) KeepTaskbarReplaced();
            bool startOpen = (_startMenu != null && _startMenu.IsVisible) || WindowTracker.WindowsStartIsOpen();
            foreach (var bar in _bars.Values)
            {
                bar.SetFullscreenHidden(!Config.KeepBarsOverFullscreen && !startOpen && WindowTracker.IsFullscreenOn(bar.Monitor, windows));
                bar.ViewModel.Update(Config.OwnScreenProgramsOnly ? windows.Where(w => w.Monitor == bar.Monitor.Handle) : windows);
            }
        }

        private void ApplyOrder()
        {
            foreach (var bar in _bars.Values) bar.ViewModel.ApplyOrder(_tray.TaskOrder);
        }

        private void ApplyPinned()
        {
            var pinned = new List<PinnedEntry>();
            if (Config.ShowPinnedPrograms)
            {
                pinned.AddRange(_tray.Pinned.Where(p => !IsHiddenPin(p.AppId)));
                pinned.AddRange(Config.ExtraPins
                    .Where(x => !_tray.Pinned.Any(p => SameApp(p.AppId, x.AppId)))
                    .Select(x => new PinnedEntry { AppId = x.AppId, Name = x.Name, Shortcut = x.Path, IsExtra = true }));
            }
            foreach (var bar in _bars.Values) bar.ViewModel.SetPinned(pinned);
        }

        private static bool SameApp(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private bool IsHiddenPin(string appId) => Config.HiddenPins.Any(h => SameApp(h, appId));

        /// <summary>Whether a program counts as pinned, for choosing between "Pin" and "Unpin" in its menu.</summary>
        private bool IsPinned(TaskItem item) => item.AppId != null && IsPinned(item.AppId);

        private bool IsPinned(string appId)
        {
            if (Config.ExtraPins.Any(x => SameApp(x.AppId, appId))) return true;
            return !IsHiddenPin(appId) && _tray.TaskOrder.Any(o => o.IsPinned && SameApp(o.AppId, appId));
        }

        private void TogglePin(TaskItem item)
        {
            if (item.AppId != null) SetTaskbarPin(item.AppId, item.ProgramName ?? item.Title, item.ExePath, !IsPinned(item));
        }

        /// <summary>
        /// Windows doesn't let other programs change its taskbar, so these pins belong to AST's bars.
        /// Pinning also turns the option on, because otherwise the pin wouldn't appear.
        /// </summary>
        private void SetTaskbarPin(string appId, string name, string path, bool pin)
        {
            if (pin == IsPinned(appId))
            {
                // Already as asked, but a pin dropped on a bar should still be seen.
                if (!pin || Config.ShowPinnedPrograms) return;
            }
            else if (pin)
            {
                Config.HiddenPins.RemoveAll(h => SameApp(h, appId));
                if (!_tray.TaskOrder.Any(o => o.IsPinned && SameApp(o.AppId, appId)))
                    Config.ExtraPins.Add(new ExtraPin { AppId = appId, Name = name, Path = path });
            }
            else
            {
                Config.ExtraPins.RemoveAll(x => SameApp(x.AppId, appId));
                if (_tray.TaskOrder.Any(o => o.IsPinned && SameApp(o.AppId, appId))) Config.HiddenPins.Add(appId);
            }
            if (pin) Config.ShowPinnedPrograms = true;
            Sync();
        }

        private void OnTrayChanged()
        {
            foreach (var bar in _bars.Values) bar.ViewModel.UpdateTray(_tray.Current);
        }

        private void TickClock()
        {
            var now = DateTime.Now;
            string time = now.ToString("t"), date = now.ToString("d");
            foreach (var bar in _bars.Values)
            {
                bar.ViewModel.TimeText = time;
                bar.ViewModel.DateText = date;
            }
        }

        public void Dispose()
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _clock.Stop();
            _displayDebounce.Stop();
            _startKey.Dispose();
            _tracker.Dispose();
            _tray.Dispose();
            // Never leave someone without a taskbar once AST has closed.
            MainTaskbar.Release(Config);
            foreach (var bar in _bars.Values) bar.Close();
            _bars.Clear();
        }
    }
}
