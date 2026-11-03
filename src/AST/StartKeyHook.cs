using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AST
{
    /// <summary>
    /// Opens AST's Start menu from the Windows key. This watches the keyboard, which anti-cheat software can object
    /// to, so it's a separate option and by default it's removed completely while a fullscreen program is running.
    /// Shortcuts such as Win+D still reach Windows, because only a press of the Windows key on its own is taken over.
    /// </summary>
    internal sealed class StartKeyHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        private const uint LLKHF_INJECTED = 0x10;
        private const byte VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        // An unassigned key. Windows opens Start when the Windows key is released alone, so this hides that it was.
        private const byte MaskKey = 0xE8;

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyInfo { public uint VkCode, ScanCode, Flags, Time; public UIntPtr Extra; }

        private delegate IntPtr HookProc(int code, IntPtr message, ref KeyInfo info);

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, ref KeyInfo info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);

        private readonly HookProc _proc;
        private readonly Action _open;
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private IntPtr _hook;
        private bool _windowsKeyDown, _usedInShortcut;

        public StartKeyHook(Action open)
        {
            _open = open;
            _proc = OnKey;
        }

        public void SetEnabled(bool enabled)
        {
            if (enabled == (_hook != IntPtr.Zero)) return;
            if (enabled)
            {
                _windowsKeyDown = _usedInShortcut = false;
                using (var module = Process.GetCurrentProcess().MainModule)
                    _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
                Log.Write(_hook == IntPtr.Zero ? "Could not watch the Windows key: error " + Marshal.GetLastWin32Error() : "Watching the Windows key");
            }
            else
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
                Log.Write("Stopped watching the Windows key");
            }
        }

        private IntPtr OnKey(int code, IntPtr message, ref KeyInfo info)
        {
            if (code >= 0 && (info.Flags & LLKHF_INJECTED) == 0)
            {
                int m = message.ToInt32();
                bool down = m == WM_KEYDOWN || m == WM_SYSKEYDOWN, up = m == WM_KEYUP || m == WM_SYSKEYUP;
                bool isWindowsKey = info.VkCode == VK_LWIN || info.VkCode == VK_RWIN;
                if (isWindowsKey && down && !_windowsKeyDown)
                {
                    _windowsKeyDown = true;
                    _usedInShortcut = false;
                }
                else if (!isWindowsKey && down && _windowsKeyDown) _usedInShortcut = true;
                else if (isWindowsKey && up)
                {
                    bool alone = _windowsKeyDown && !_usedInShortcut;
                    _windowsKeyDown = false;
                    if (alone)
                    {
                        Native.keybd_event(MaskKey, 0, 0, UIntPtr.Zero);
                        Native.keybd_event(MaskKey, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                        Native.keybd_event((byte)info.VkCode, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                        _dispatcher.BeginInvoke(_open);
                        return new IntPtr(1);
                    }
                }
            }
            return CallNextHookEx(_hook, code, message, ref info);
        }

        public void Dispose() => SetEnabled(false);
    }
}
