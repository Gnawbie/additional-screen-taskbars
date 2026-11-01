using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AST
{
    /// <summary>Win32 declarations. AST only uses public, documented APIs and never injects into other processes.</summary>
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        // ---- App bars ----
        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_ACTIVATE = 6, ABM_WINDOWPOSCHANGED = 9;
        public const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;
        public const int ABN_POSCHANGED = 1;

        [DllImport("shell32.dll")]
        public static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);

        // ---- Monitors ----
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hMonitor, int type, out uint dpiX, out uint dpiY);
        public const uint MONITOR_DEFAULTTONEAREST = 2;

        // ---- Display configuration, used for stable ids and friendly monitor names ----
        [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint Low; public int High; }

        [StructLayout(LayoutKind.Sequential)]
        public struct PATH_SOURCE { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

        [StructLayout(LayoutKind.Sequential)]
        public struct PATH_TARGET
        {
            public LUID adapterId; public uint id; public uint modeInfoIdx; public int outputTechnology; public int rotation; public int scaling;
            public uint refreshNumerator; public uint refreshDenominator; public int scanLineOrdering; public int targetAvailable; public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PATH_INFO { public PATH_SOURCE source; public PATH_TARGET target; public uint flags; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MODE_INFO { public int infoType; public uint id; public LUID adapterId; public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVICE_INFO_HEADER { public int type; public uint size; public LUID adapterId; public uint id; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SOURCE_DEVICE_NAME
        {
            public DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct TARGET_DEVICE_NAME
        {
            public DEVICE_INFO_HEADER header;
            public uint flags; public int outputTechnology; public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
        public const uint SWP_NOZORDER = 4;

        public const uint QDC_ONLY_ACTIVE_PATHS = 2;
        public const int GET_SOURCE_NAME = 1, GET_TARGET_NAME = 2;

        [DllImport("user32.dll")] public static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] public static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH_INFO[] paths, ref uint numModes, [Out] MODE_INFO[] modes, IntPtr topology);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] public static extern int GetSourceName(ref SOURCE_DEVICE_NAME info);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] public static extern int GetTargetName(ref TARGET_DEVICE_NAME info);

        // ---- Windows ----
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);
        [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] public static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPLACEMENT
        {
            public int length; public int flags; public int showCmd; public POINT ptMin; public POINT ptMax; public RECT rcNormal;
        }
        [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        // ---- Events ----
        public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        public const uint WINEVENT_OUTOFCONTEXT = 0;

        // ---- Input, only used for the Start button (Ctrl+Esc) ----
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        public const byte VK_CONTROL = 0x11, VK_ESCAPE = 0x1B;
        public const uint KEYEVENTF_KEYUP = 2;

        // ---- Constants ----
        public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        public const int GCLP_HICON = -14, GCLP_HICONSM = -34;
        public const long WS_CAPTION = 0x00C00000L;
        public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000L, WS_EX_TOPMOST = 0x8;
        public const uint GW_OWNER = 4;
        public const int DWMWA_CLOAKED = 14;
        public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_MINIMIZE = 6, SW_RESTORE = 9;
        public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint WM_GETICON = 0x7F, WM_CLOSE = 0x10, WM_ACTIVATE = 6, WM_WINDOWPOSCHANGED = 0x47, WM_DPICHANGED = 0x2E0, WM_MOUSEACTIVATE = 0x21;
        public const uint SMTO_ABORTIFHUNG = 2;
        public const int MA_NOACTIVATE = 3;

        public static string TitleOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(512);
            GetWindowTextW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string ClassOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetClassNameW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string ExePathOf(uint pid)
        {
            IntPtr p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (p == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(p, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(p); }
        }
    }
}
