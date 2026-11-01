using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace AST
{
    /// <summary>
    /// Hides the Windows taskbar for the "replace the main taskbar" option. Windows has no supported way to remove
    /// it, so this turns on its auto-hide, which frees its screen space, and makes it fully see-through. It stays
    /// running rather than being truly hidden, because a hidden taskbar stops reporting its tray and pinned buttons.
    /// The original setting is saved first, and a small guard process restores everything if AST ever dies.
    /// </summary>
    internal static class MainTaskbar
    {
        private const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10;
        private const int ABS_AUTOHIDE = 1;
        private const string PrimaryClass = "Shell_TrayWnd", SecondaryClass = "Shell_SecondaryTrayWnd";
        private const int RestoreWaitMilliseconds = 5000, RestoreStepMilliseconds = 200;
        private static bool _guardStarted;

        public static void Engage(Config config)
        {
            IntPtr tray = Native.FindWindow(PrimaryClass, null);
            if (tray == IntPtr.Zero) return;
            if (!config.TaskbarIsReplaced)
            {
                config.SavedTaskbarState = GetState(tray);
                config.TaskbarIsReplaced = true;
                config.Save();
            }
            StartGuard();
            SetState(tray, ABS_AUTOHIDE);
            KeepHidden();
        }

        /// <summary>A restarted Explorer creates a new, visible taskbar, so this runs often.</summary>
        public static void KeepHidden()
        {
            ForEachTaskbar(h =>
            {
                long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                if ((ex & Native.WS_EX_LAYERED) != 0 && Native.GetLayeredWindowAttributes(h, out _, out byte alpha, out _) && alpha == 0) return;
                Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_LAYERED));
                Native.SetLayeredWindowAttributes(h, 0, 0, Native.LWA_ALPHA);
            });
        }

        public static void Release(Config config)
        {
            if (!config.TaskbarIsReplaced) return;
            ForEachTaskbar(h =>
            {
                long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                Native.SetLayeredWindowAttributes(h, 0, 255, Native.LWA_ALPHA);
                Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex & ~Native.WS_EX_LAYERED));
            });
            RestoreState(config.SavedTaskbarState);
            config.TaskbarIsReplaced = false;
            config.Save();
        }

        /// <summary>
        /// Windows can ignore the request while the taskbar is settling, so this checks and asks again. Getting
        /// this wrong would leave auto-hide on, and the next run would then save that as the original setting.
        /// </summary>
        private static void RestoreState(int state)
        {
            for (int waited = 0; waited < RestoreWaitMilliseconds; waited += RestoreStepMilliseconds)
            {
                IntPtr tray = Native.FindWindow(PrimaryClass, null);
                if (tray != IntPtr.Zero)
                {
                    SetState(tray, state);
                    Thread.Sleep(RestoreStepMilliseconds);
                    if (GetState(tray) == state) return;
                }
                else Thread.Sleep(RestoreStepMilliseconds);
            }
            Log.Write("The Windows taskbar's auto-hide setting could not be restored.");
        }

        private static void StartGuard()
        {
            if (_guardStarted) return;
            _guardStarted = true;
            try
            {
                Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--guard " + Process.GetCurrentProcess().Id)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            catch (Exception ex) { Log.Write("Could not start the taskbar guard: " + ex.Message); }
        }

        /// <summary>Runs as its own small process. It waits for AST to end, then restores the taskbar if AST didn't.</summary>
        public static void RunGuard(int appProcessId)
        {
            try { Process.GetProcessById(appProcessId).WaitForExit(); }
            catch (ArgumentException) { }
            Release(Config.Load());
        }

        private static void ForEachTaskbar(Action<IntPtr> action)
        {
            IntPtr tray = Native.FindWindow(PrimaryClass, null);
            if (tray != IntPtr.Zero) action(tray);
            IntPtr next = IntPtr.Zero;
            while ((next = Native.FindWindowEx(IntPtr.Zero, next, SecondaryClass, null)) != IntPtr.Zero) action(next);
        }

        private static Native.APPBARDATA Data(IntPtr tray) => new Native.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf(typeof(Native.APPBARDATA)),
            hWnd = tray,
        };

        private static int GetState(IntPtr tray)
        {
            var d = Data(tray);
            return (int)Native.SHAppBarMessage(ABM_GETSTATE, ref d).ToUInt32();
        }

        private static void SetState(IntPtr tray, int state)
        {
            var d = Data(tray);
            d.lParam = new IntPtr(state);
            Native.SHAppBarMessage(ABM_SETSTATE, ref d);
        }
    }
}
