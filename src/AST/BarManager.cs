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
                bar.ShowsPinned = Config.ShowPinnedPrograms;
                bar.HidesForFullscreen = !Config.KeepBarsOverFullscreen;
            }
            foreach (var id in _bars.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                _bars[id].Close();
                _bars.Remove(id);
            }
            // A bar on the main screen takes the place of the Windows taskbar, which would otherwise sit alongside it.
            _replacingTaskbar = _monitors.Any(m => m.IsPrimary && Config.For(m).EdgeValue != BarEdge.Off);
            if (_replacingTaskbar) MainTaskbar.Engage(Config);
            else MainTaskbar.Release(Config);
            Config.Save();
            TickClock();
            ApplyPinned();
            ApplyOrder();
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
            if (Config.Theme == ThemeManager.FallbackThemeId) return;
            Log.Write("Switching to the built-in theme after an error in " + Config.Theme);
            Config.Theme = ThemeManager.FallbackThemeId;
            Sync();
        }

        private void OnWindowsChanged(List<TrackedWindow> windows)
        {
            if (_replacingTaskbar) MainTaskbar.KeepHidden();
            foreach (var bar in _bars.Values)
            {
                bar.SetFullscreenHidden(!Config.KeepBarsOverFullscreen && WindowTracker.IsFullscreenOn(bar.Monitor, windows));
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
        private bool IsPinned(TaskItem item)
        {
            if (item.AppId == null) return false;
            if (Config.ExtraPins.Any(x => SameApp(x.AppId, item.AppId))) return true;
            return !IsHiddenPin(item.AppId) && _tray.TaskOrder.Any(o => o.IsPinned && SameApp(o.AppId, item.AppId));
        }

        /// <summary>
        /// Windows doesn't let other programs change its taskbar, so these pins belong to AST's bars.
        /// Pinning also turns the option on, because otherwise the pin wouldn't appear.
        /// </summary>
        private void TogglePin(TaskItem item)
        {
            if (item.AppId == null) return;
            if (IsPinned(item))
            {
                Config.ExtraPins.RemoveAll(x => SameApp(x.AppId, item.AppId));
                if (_tray.TaskOrder.Any(o => o.IsPinned && SameApp(o.AppId, item.AppId))) Config.HiddenPins.Add(item.AppId);
            }
            else
            {
                Config.HiddenPins.RemoveAll(h => SameApp(h, item.AppId));
                if (!_tray.TaskOrder.Any(o => o.IsPinned && SameApp(o.AppId, item.AppId)))
                    Config.ExtraPins.Add(new ExtraPin { AppId = item.AppId, Name = item.ProgramName ?? item.Title, Path = item.ExePath });
                Config.ShowPinnedPrograms = true;
            }
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
            _tracker.Dispose();
            _tray.Dispose();
            // Never leave someone without a taskbar once AST has closed.
            MainTaskbar.Release(Config);
            foreach (var bar in _bars.Values) bar.Close();
            _bars.Clear();
        }
    }
}
