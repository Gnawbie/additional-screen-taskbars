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
            Config.Save();
            TickClock();
            ApplyPinned();
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
            foreach (var bar in _bars.Values)
            {
                bar.SetFullscreenHidden(!Config.KeepBarsOverFullscreen && WindowTracker.IsFullscreenOn(bar.Monitor, windows));
                bar.ViewModel.Update(Config.OwnScreenProgramsOnly ? windows.Where(w => w.Monitor == bar.Monitor.Handle) : windows);
            }
        }

        private void ApplyPinned()
        {
            var pinned = Config.ShowPinnedPrograms ? _tray.Pinned : new List<PinnedEntry>();
            foreach (var bar in _bars.Values) bar.ViewModel.SetPinned(pinned);
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
            foreach (var bar in _bars.Values) bar.Close();
            _bars.Clear();
        }
    }
}
