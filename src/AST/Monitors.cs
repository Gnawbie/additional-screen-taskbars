using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AST
{
    public sealed class MonitorInfo
    {
        /// <summary>Stable across reboots and rearranging, so per-screen settings follow the physical monitor.</summary>
        public string Id;
        public string Name;
        public bool IsPrimary;
        /// <summary>The number in Windows' name for this screen, such as 2 for \\.\DISPLAY2. Zero if it has none.</summary>
        public int Number;
        internal IntPtr Handle;
        internal Native.RECT Bounds;

        public int Width => Bounds.Width;
        public int Height => Bounds.Height;

        public double DpiScale
        {
            get
            {
                try
                {
                    if (Native.GetDpiForMonitor(Handle, 0, out uint x, out _) == 0) return x / 96.0;
                }
                catch (DllNotFoundException) { }
                return 1.0;
            }
        }
    }

    internal static class Monitors
    {
        public static List<MonitorInfo> Enumerate()
        {
            var names = ReadDisplayConfig();
            var list = new List<MonitorInfo>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, dc, r, d) =>
            {
                var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX)) };
                if (!Native.GetMonitorInfo(h, ref info)) return true;
                names.TryGetValue(info.szDevice ?? "", out var n);
                list.Add(new MonitorInfo
                {
                    Handle = h,
                    Bounds = info.rcMonitor,
                    IsPrimary = (info.dwFlags & 1) != 0,
                    Number = NumberFrom(info.szDevice),
                    Id = !string.IsNullOrEmpty(n.path) ? n.path : info.szDevice,
                    Name = !string.IsNullOrEmpty(n.name) ? n.name : info.szDevice,
                });
                return true;
            }, IntPtr.Zero);
            list.Sort((a, b) => a.Number.CompareTo(b.Number));
            return list;
        }

        private static int NumberFrom(string device)
        {
            var match = System.Text.RegularExpressions.Regex.Match(device ?? "", @"(\d+)$");
            return match.Success && int.TryParse(match.Groups[1].Value, out int n) ? n : 0;
        }

        private static Dictionary<string, (string name, string path)> ReadDisplayConfig()
        {
            var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return result;
                var paths = new Native.PATH_INFO[np];
                var modes = new Native.MODE_INFO[nm];
                if (Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return result;
                for (int i = 0; i < np; i++)
                {
                    var src = new Native.SOURCE_DEVICE_NAME();
                    src.header.type = Native.GET_SOURCE_NAME;
                    src.header.size = (uint)Marshal.SizeOf(typeof(Native.SOURCE_DEVICE_NAME));
                    src.header.adapterId = paths[i].source.adapterId;
                    src.header.id = paths[i].source.id;
                    if (Native.GetSourceName(ref src) != 0) continue;

                    var tgt = new Native.TARGET_DEVICE_NAME();
                    tgt.header.type = Native.GET_TARGET_NAME;
                    tgt.header.size = (uint)Marshal.SizeOf(typeof(Native.TARGET_DEVICE_NAME));
                    tgt.header.adapterId = paths[i].target.adapterId;
                    tgt.header.id = paths[i].target.id;
                    if (Native.GetTargetName(ref tgt) != 0) continue;

                    result[src.viewGdiDeviceName] = (tgt.monitorFriendlyDeviceName, tgt.monitorDevicePath);
                }
            }
            catch (Exception ex) { Log.Write("Display config lookup failed: " + ex.Message); }
            return result;
        }
    }
}
