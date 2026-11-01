using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AST
{
    internal sealed class TrackedWindow
    {
        public IntPtr Handle;
        public string Title;
        public bool IsMinimized;
        public bool IsActive;
        public IntPtr Monitor;
    }

    /// <summary>
    /// Keeps the list of open windows current. Uses out-of-context WinEvents, which only notify this process
    /// and load nothing into other programs, so it stays clear of games and their anti-cheat software.
    /// </summary>
    internal sealed class WindowTracker : IDisposable
    {
        private static readonly HashSet<string> IgnoredClasses = new HashSet<string>
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
            "XamlExplorerHostIslandWindow", "XamlExplorerHostIslandWindow_WASDK", "ForegroundStaging",
        };

        private readonly Native.WinEventProc _callback;
        private readonly List<IntPtr> _hooks = new List<IntPtr>();
        private readonly DispatcherTimer _debounce;
        private readonly DispatcherTimer _poll;
        private readonly Dictionary<IntPtr, IntPtr> _lastMonitor = new Dictionary<IntPtr, IntPtr>();
        private readonly uint _ownPid = (uint)Process.GetCurrentProcess().Id;

        public event Action<List<TrackedWindow>> Changed;

        public WindowTracker()
        {
            _callback = OnWinEvent;
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _debounce.Tick += (s, e) => { _debounce.Stop(); Refresh(); };
            // A slow poll catches anything the events miss.
            _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _poll.Tick += (s, e) => Refresh();

            Hook(0x0003, 0x0003); // foreground
            Hook(0x0016, 0x0017); // minimize start and end
            Hook(0x8000, 0x8003); // create, destroy, show, hide
            Hook(0x800B, 0x800C); // location, name
            Hook(0x8017, 0x8018); // cloaked, uncloaked
            _poll.Start();
        }

        private void Hook(uint min, uint max)
        {
            IntPtr h = Native.SetWinEventHook(min, max, IntPtr.Zero, _callback, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
            if (h != IntPtr.Zero) _hooks.Add(h);
        }

        private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0 || idChild != 0) return;
            if (!_debounce.IsEnabled) _debounce.Start();
        }

        public void Refresh()
        {
            var fg = Native.GetForegroundWindow();
            var list = new List<TrackedWindow>();
            var seen = new HashSet<IntPtr>();
            Native.EnumWindows((h, l) =>
            {
                var w = Describe(h, fg);
                if (w != null) { list.Add(w); seen.Add(h); }
                return true;
            }, IntPtr.Zero);

            var stale = new List<IntPtr>();
            foreach (var k in _lastMonitor.Keys) if (!seen.Contains(k)) stale.Add(k);
            foreach (var k in stale) _lastMonitor.Remove(k);

            Changed?.Invoke(list);
        }

        private TrackedWindow Describe(IntPtr h, IntPtr fg)
        {
            if (!Native.IsWindowVisible(h)) return null;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == _ownPid) return null;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
            if (!appWindow)
            {
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0 || (ex & Native.WS_EX_NOACTIVATE) != 0) return null;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return null;
            }
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return null;
            if (IgnoredClasses.Contains(Native.ClassOf(h))) return null;
            string title = Native.TitleOf(h);
            if (string.IsNullOrWhiteSpace(title)) return null;

            bool minimized = Native.IsIconic(h);
            return new TrackedWindow
            {
                Handle = h,
                Title = title,
                IsMinimized = minimized,
                IsActive = h == fg && !minimized,
                Monitor = MonitorOf(h, minimized),
            };
        }

        /// <summary>A minimized window sits off-screen, so remember which screen it was last on.</summary>
        private IntPtr MonitorOf(IntPtr h, bool minimized)
        {
            if (!minimized)
            {
                var m = Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST);
                _lastMonitor[h] = m;
                return m;
            }
            if (_lastMonitor.TryGetValue(h, out var known)) return known;
            var p = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT)) };
            if (Native.GetWindowPlacement(h, ref p)) known = Native.MonitorFromRect(ref p.rcNormal, Native.MONITOR_DEFAULTTONEAREST);
            else known = Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST);
            _lastMonitor[h] = known;
            return known;
        }

        /// <summary>True when the topmost window on the screen is fullscreen, whether or not it has focus.</summary>
        public static bool IsFullscreenOn(MonitorInfo monitor, List<TrackedWindow> zOrdered)
        {
            // Some games have no title or use tool-window styles, so the list of open windows leaves them out.
            if (ForegroundIsFullscreenOn(monitor)) return true;
            foreach (var w in zOrdered)
            {
                if (w.Monitor != monitor.Handle || w.IsMinimized) continue;
                return CoversMonitor(w.Handle, monitor);
            }
            return false;
        }

        private static bool ForegroundIsFullscreenOn(MonitorInfo monitor)
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST) != monitor.Handle) return false;
            Native.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == Process.GetCurrentProcess().Id || IgnoredClasses.Contains(Native.ClassOf(fg))) return false;
            return CoversMonitor(fg, monitor);
        }

        private static bool CoversMonitor(IntPtr hwnd, MonitorInfo monitor)
        {
            if (!Native.GetWindowRect(hwnd, out var r)) return false;
            var b = monitor.Bounds;
            if (r.Left > b.Left || r.Top > b.Top || r.Right < b.Right || r.Bottom < b.Bottom) return false;
            long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
            bool captioned = (style & Native.WS_CAPTION) == Native.WS_CAPTION;
            // A maximized window with a title bar can slightly overhang the screen but is not fullscreen.
            return !(captioned && Native.IsZoomed(hwnd));
        }

        public void Dispose()
        {
            _poll.Stop();
            _debounce.Stop();
            foreach (var h in _hooks) Native.UnhookWinEvent(h);
            _hooks.Clear();
        }
    }

    internal static class WindowIcons
    {
        private static readonly Dictionary<string, ImageSource> ByExe = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static ImageSource Get(IntPtr hwnd)
        {
            try
            {
                IntPtr icon = FromWindow(hwnd);
                if (icon != IntPtr.Zero)
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
            }
            catch (Exception ex) { Log.Write("Window icon failed: " + ex.Message); }
            return FromExe(ExeFor(hwnd));
        }

        private static IntPtr FromWindow(IntPtr hwnd)
        {
            foreach (int kind in new[] { 2, 1, 0 }) // ICON_SMALL2, ICON_BIG, ICON_SMALL
            {
                if (Native.SendMessageTimeout(hwnd, Native.WM_GETICON, new IntPtr(kind), IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 40, out IntPtr r) != IntPtr.Zero && r != IntPtr.Zero)
                    return r;
            }
            IntPtr c = Native.GetClassLongPtr(hwnd, Native.GCLP_HICONSM);
            return c != IntPtr.Zero ? c : Native.GetClassLongPtr(hwnd, Native.GCLP_HICON);
        }

        /// <summary>Store apps run inside a host process, so their icon comes from the hosted child window's program.</summary>
        private static string ExeFor(IntPtr hwnd)
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (Native.ClassOf(hwnd) == "ApplicationFrameWindow")
            {
                uint inner = 0;
                Native.EnumChildWindows(hwnd, (c, l) =>
                {
                    Native.GetWindowThreadProcessId(c, out uint p);
                    if (p != pid) { inner = p; return false; }
                    return true;
                }, IntPtr.Zero);
                if (inner != 0) pid = inner;
            }
            return Native.ExePathOf(pid);
        }

        private static ImageSource FromExe(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (ByExe.TryGetValue(path, out var cached)) return cached;
            ImageSource result = null;
            try
            {
                using (var icon = Icon.ExtractAssociatedIcon(path))
                {
                    if (icon != null)
                    {
                        var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                        result = src;
                    }
                }
            }
            catch (Exception ex) { Log.Write("Program icon failed: " + ex.Message); }
            ByExe[path] = result;
            return result;
        }
    }
}
