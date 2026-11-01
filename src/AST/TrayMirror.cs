using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Threading;

namespace AST
{
    internal enum TrayKind { Other, HiddenIcons, Network, Volume, Notifications }

    internal sealed class TrayEntry
    {
        public string Name;
        public string Glyph;
        public TrayKind Kind;
        public AutomationElement Element;
    }

    /// <summary>
    /// Reads the Windows taskbar's tray buttons through UI Automation, the accessibility API, so bars can offer
    /// matching buttons with live status. Pressing one presses the real button, which opens Windows' own flyout,
    /// and that flyout is then moved to the bar's screen. Scans run off the UI thread so a busy Explorer can't
    /// freeze the bars.
    /// </summary>
    internal sealed class TrayMirror : IDisposable
    {
        private const string TrayButtonId = "SystemTrayIcon";
        private const string ClockClass = "SystemTray.OmniButtonLeft";
        private const string ShowDesktopClass = "SystemTray.ShowDesktopButton";
        private const string NetworkClass = "SystemTray.AccentButton";
        private const string VolumeClass = "SystemTray.OmniButtonRight";
        private const int MaxTreeDepth = 8;

        // Only these named windows are ever moved. The notification panel already opens on the active screen.
        private const string QuickSettingsWindow = "ControlCenterWindow";
        private const string HiddenIconsWindow = "TopLevelWindowForOverflowXamlIsland";
        private const int FlyoutWatchMilliseconds = 900;
        private const int FlyoutWatchStep = 30;

        // Segoe MDL2 Assets, which ships with Windows 10 and 11.
        private const string GlyphChevron = "", GlyphEthernet = "", GlyphWifi = "", GlyphNoInternet = "";
        private const string GlyphVolume = "", GlyphMute = "", GlyphBattery = "", GlyphMic = "", GlyphBluetooth = "", GlyphBell = "", GlyphOther = "";

        private readonly DispatcherTimer _timer;
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private string _signature = "";
        private bool _scanning;

        public List<TrayEntry> Current { get; private set; } = new List<TrayEntry>();
        public event Action Changed;

        public TrayMirror()
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += (s, e) => Scan();
            _timer.Start();
            Scan();
        }

        private void Scan()
        {
            if (_scanning) return;
            _scanning = true;
            Task.Run(() =>
            {
                List<TrayEntry> entries;
                try { entries = Read(); }
                catch (Exception ex)
                {
                    // Expected while Explorer restarts. The next scan picks the buttons up again.
                    Log.Write("Tray scan failed: " + ex.Message);
                    entries = new List<TrayEntry>();
                }
                _dispatcher.BeginInvoke(new Action(() => Publish(entries)));
            });
        }

        private void Publish(List<TrayEntry> entries)
        {
            _scanning = false;
            // An empty result is a passing state, such as Explorer restarting, so keep the buttons on show.
            if (entries.Count == 0) return;
            string signature = string.Join("\n", entries.Select(e => e.Glyph + e.Name));
            if (signature == _signature) return;
            _signature = signature;
            Current = entries;
            Changed?.Invoke();
        }

        private static List<TrayEntry> Read()
        {
            IntPtr taskbar = Native.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return new List<TrayEntry>();

            // The raw tree, because the filtered view can leave these buttons out while a fullscreen app is active.
            var found = new List<AutomationElement>();
            Collect(AutomationElement.FromHandle(taskbar), found, 0);
            return found
                .Where(e => e.Current.ClassName != ClockClass && e.Current.ClassName != ShowDesktopClass)
                .OrderBy(e => e.Current.BoundingRectangle.X)
                .Select(Describe)
                .ToList();
        }

        private static void Collect(AutomationElement parent, List<AutomationElement> found, int depth)
        {
            if (depth > MaxTreeDepth) return;
            var walker = TreeWalker.RawViewWalker;
            for (var child = walker.GetFirstChild(parent); child != null; child = walker.GetNextSibling(child))
            {
                if (child.Current.AutomationId == TrayButtonId) found.Add(child);
                Collect(child, found, depth + 1);
            }
        }

        /// <summary>Names are in the user's language, so unknown ones still get a usable generic icon.</summary>
        private static TrayEntry Describe(AutomationElement element)
        {
            string name = element.Current.Name ?? "";
            string className = element.Current.ClassName;
            string n = name.ToLowerInvariant();
            var entry = new TrayEntry { Name = name, Element = element, Glyph = GlyphOther };

            if (n.StartsWith("show hidden"))
            {
                entry.Kind = TrayKind.HiddenIcons;
                entry.Glyph = GlyphChevron;
            }
            else if (n.StartsWith("network") || className == NetworkClass)
            {
                entry.Kind = TrayKind.Network;
                entry.Glyph = n.Contains("no internet") || n.Contains("not connected") ? GlyphNoInternet
                    : n.Contains("wi-fi") || n.Contains("wireless") ? GlyphWifi : GlyphEthernet;
            }
            else if (n.StartsWith("volume") || className == VolumeClass)
            {
                entry.Kind = TrayKind.Volume;
                entry.Glyph = n.Contains("muted") || n.TrimEnd().EndsWith(": 0%") ? GlyphMute : GlyphVolume;
            }
            else if (n.StartsWith("notifications"))
            {
                entry.Kind = TrayKind.Notifications;
                entry.Glyph = GlyphBell;
            }
            else if (n.StartsWith("battery")) entry.Glyph = GlyphBattery;
            else if (n.Contains("microphone")) entry.Glyph = GlyphMic;
            else if (n.Contains("bluetooth")) entry.Glyph = GlyphBluetooth;
            return entry;
        }

        private static string FlyoutWindowFor(TrayKind kind)
        {
            switch (kind)
            {
                case TrayKind.HiddenIcons: return HiddenIconsWindow;
                case TrayKind.Network:
                case TrayKind.Volume: return QuickSettingsWindow;
                default: return null;
            }
        }

        public static void Press(TrayEntry entry, Func<MonitorInfo> monitor, Func<string> edge)
        {
            Task.Run(() =>
            {
                try
                {
                    string flyout = FlyoutWindowFor(entry.Kind);
                    // Pressing an open flyout's button closes it, so there is nothing to move.
                    bool wasOpen = flyout != null && IsOpen(flyout);
                    ((InvokePattern)entry.Element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    if (flyout != null && !wasOpen) MoveFlyout(flyout, monitor(), edge());
                }
                catch (Exception ex) { Log.Write("Tray button failed: " + ex.Message); }
            });
        }

        private static bool IsOpen(string windowClass)
        {
            IntPtr h = Native.FindWindow(windowClass, null);
            return h != IntPtr.Zero && Native.IsWindowVisible(h);
        }

        /// <summary>Windows may place the flyout again as it animates in, so keep watching for a moment.</summary>
        private static void MoveFlyout(string windowClass, MonitorInfo monitor, string edge)
        {
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX)) };
            if (!Native.GetMonitorInfo(monitor.Handle, ref info)) return;
            var work = info.rcWork;

            for (int waited = 0; waited < FlyoutWatchMilliseconds; waited += FlyoutWatchStep)
            {
                IntPtr h = Native.FindWindow(windowClass, null);
                if (h != IntPtr.Zero && Native.IsWindowVisible(h) && Native.GetWindowRect(h, out var r))
                {
                    int x = edge == "Left" ? work.Left : work.Right - r.Width;
                    int y = edge == "Top" ? work.Top : work.Bottom - r.Height;
                    if (r.Left != x || r.Top != y)
                        Native.SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                }
                Thread.Sleep(FlyoutWatchStep);
            }
        }

        public static void OpenSettingsPage(string uri)
        {
            try { Process.Start(uri); }
            catch (Exception ex) { Log.Write("Could not open " + uri + ": " + ex.Message); }
        }

        public void Dispose() => _timer.Stop();
    }
}
