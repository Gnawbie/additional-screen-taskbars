using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AST
{
    internal sealed class PinnedEntry
    {
        public string AppId;
        public string Name;
        /// <summary>The taskbar's own shortcut for this item, or the program itself for a pin made in AST.</summary>
        public string Shortcut;
        /// <summary>Pinned from AST's menu, so it isn't on the Windows taskbar and may have a window open.</summary>
        public bool IsExtra;
    }

    /// <summary>One button on the Windows taskbar, in order.</summary>
    internal sealed class TaskOrderEntry
    {
        public string AppId;
        public string Name;
        public bool IsPinned;
    }

    /// <summary>Understands the Windows taskbar's pinned buttons, and finds their icons and starts their programs.</summary>
    internal static class PinnedPrograms
    {
        // Windows names these buttons like "Discord - 1 running window pinned" and gives no other way to tell.
        private static readonly Regex EnglishName = new Regex(@"^(.*?)(?: - \d+ running windows?)?(?: pinned)?$", RegexOptions.Compiled);
        private const string ShellAppsFolder = @"shell:AppsFolder\";
        private const string ExplorerAppId = "Microsoft.Windows.Explorer";

        // Twice the size the bar shows. Asking the shell for the exact size gets a crude resize with jagged edges,
        // while halving a larger image comes out smooth, as on the Windows taskbar.
        private const int IconDisplaySize = 24;
        private static readonly double IconDpi = ReadSystemDpi();
        private static readonly int IconSize = (int)Math.Round(IconDisplaySize * 2 * IconDpi / 96);
        private static readonly int ShortcutIconSize = (int)Math.Round(IconDisplaySize * IconDpi / 96);

        private static double ReadSystemDpi()
        {
            using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX;
        }

        private static string ShortcutFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

        /// <summary>Returns null unless the button is a pinned program with no open window, which is all a launcher is for.</summary>
        public static PinnedEntry FromTaskbarButton(string name, string automationId)
        {
            const string prefix = "Appid: ";
            if (string.IsNullOrEmpty(name) || automationId == null || !automationId.StartsWith(prefix)) return null;
            if (name.Contains("running window")) return null;

            // The longest match, so "Paint Tools" isn't mistaken for a shortcut called "Paint".
            string shortcut = ShortcutNames().Where(s => name.StartsWith(s, StringComparison.OrdinalIgnoreCase)).OrderByDescending(s => s.Length).FirstOrDefault();
            // The word "pinned" is English only, so other languages rely on the taskbar's pinned shortcuts.
            if (!name.EndsWith(" pinned") && shortcut == null) return null;
            return new PinnedEntry
            {
                AppId = automationId.Substring(prefix.Length),
                Name = shortcut ?? EnglishName.Match(name).Groups[1].Value,
                Shortcut = shortcut == null ? null : Path.Combine(ShortcutFolder, shortcut + ".lnk"),
            };
        }

        public static TaskOrderEntry OrderEntryFromButton(string name, string automationId)
        {
            const string prefix = "Appid: ";
            if (string.IsNullOrEmpty(name) || automationId == null || !automationId.StartsWith(prefix)) return null;
            string shortcut = ShortcutNames().Where(s => name.StartsWith(s, StringComparison.OrdinalIgnoreCase)).OrderByDescending(s => s.Length).FirstOrDefault();
            return new TaskOrderEntry
            {
                AppId = automationId.Substring(prefix.Length),
                Name = shortcut ?? EnglishName.Match(name).Groups[1].Value,
                IsPinned = name.EndsWith(" pinned") || shortcut != null,
            };
        }

        /// <summary>The program's own name, for matching a window to a taskbar button when its id doesn't line up.</summary>
        public static string ProgramName(string exePath)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return null;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exePath);
                return !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : info.ProductName;
            }
            catch (Exception) { return null; }
        }

        public static string ExePathOfWindow(IntPtr hwnd)
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            return Native.ExePathOf(pid);
        }

        /// <summary>Finds the program behind a pinned item, for "Open file location" and "Run as administrator".</summary>
        public static string ExePathOfPin(PinnedEntry pinned)
        {
            if (IsExistingFile(pinned.AppId)) return pinned.AppId;
            foreach (var folder in KnownFolders)
            {
                if (!pinned.AppId.StartsWith(folder.Value + @"\", StringComparison.OrdinalIgnoreCase)) continue;
                string path = Environment.GetFolderPath(folder.Key) + pinned.AppId.Substring(folder.Value.Length);
                if (File.Exists(path)) return path;
            }
            if (pinned.Shortcut == null || !File.Exists(pinned.Shortcut)) return null;
            if (!pinned.Shortcut.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return pinned.Shortcut;
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                string target = shell.CreateShortcut(pinned.Shortcut).TargetPath;
                return !string.IsNullOrEmpty(target) && File.Exists(target) ? target : null;
            }
            catch (Exception) { return null; }
        }

        public static void RunAsAdministrator(string exePath)
        {
            try { Process.Start(new ProcessStartInfo(exePath) { Verb = "runas", UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exePath) }); }
            catch (System.ComponentModel.Win32Exception) { /* The person chose No at the Windows prompt. */ }
            catch (Exception ex) { Log.Write("Could not run " + exePath + " as administrator: " + ex.Message); }
        }

        public static void OpenFileLocation(string exePath)
        {
            try { Process.Start("explorer.exe", "/select,\"" + exePath + "\""); }
            catch (Exception ex) { Log.Write("Could not open the location of " + exePath + ": " + ex.Message); }
        }

        /// <summary>
        /// The id Windows groups this window under, in the form the taskbar's buttons use. A window without an
        /// explicit id falls back to its program's path, with well-known folders written as Windows writes them.
        /// </summary>
        public static string AppIdOfWindow(IntPtr hwnd)
        {
            string explicitId = ExplicitAppId(hwnd);
            if (!string.IsNullOrEmpty(explicitId)) return explicitId;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            string packaged = PackagedAppId(pid);
            if (!string.IsNullOrEmpty(packaged)) return packaged;
            string exe = Native.ExePathOf(pid);
            if (exe == null) return null;
            if (Path.GetFileName(exe).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) return ExplorerAppId;
            foreach (var folder in KnownFolders)
            {
                string root = Environment.GetFolderPath(folder.Key);
                if (!string.IsNullOrEmpty(root) && exe.StartsWith(root + @"\", StringComparison.OrdinalIgnoreCase))
                    return folder.Value + exe.Substring(root.Length);
            }
            return exe;
        }

        private static readonly KeyValuePair<Environment.SpecialFolder, string>[] KnownFolders =
        {
            new KeyValuePair<Environment.SpecialFolder, string>(Environment.SpecialFolder.System, "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"),
            new KeyValuePair<Environment.SpecialFolder, string>(Environment.SpecialFolder.ProgramFilesX86, "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"),
            new KeyValuePair<Environment.SpecialFolder, string>(Environment.SpecialFolder.ProgramFiles, "{6D809377-6AF0-444B-8957-A3773F02200E}"),
        };

        /// <summary>The id of a store-packaged program, which it doesn't have to set on its windows.</summary>
        private static string PackagedAppId(uint pid)
        {
            IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return null;
            try
            {
                int length = 512;
                var id = new System.Text.StringBuilder(length);
                return Native.GetApplicationUserModelId(process, ref length, id) == 0 ? id.ToString() : null;
            }
            finally { Native.CloseHandle(process); }
        }

        private static string ExplicitAppId(IntPtr hwnd)
        {
            try
            {
                var iid = typeof(IPropertyStore).GUID;
                SHGetPropertyStoreForWindow(hwnd, ref iid, out IPropertyStore store);
                var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
                store.GetValue(ref key, out PropVariant value);
                try { return value.Type == VT_LPWSTR ? Marshal.PtrToStringUni(value.Pointer) : null; }
                finally { PropVariantClear(ref value); }
            }
            catch (Exception) { return null; }
        }

        private static readonly Dictionary<string, ImageSource> AppIcons = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The icon Windows shows for this app, or null if it isn't listed as an app.</summary>
        public static ImageSource AppIcon(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return null;
            // The Start menu loads its icons on a background thread.
            lock (AppIcons)
            {
                if (AppIcons.TryGetValue(appId, out var cached)) return cached;
            }
            ImageSource icon = null;
            if (IsExistingFile(appId)) icon = ReadIcon(appId);
            // A path can't be an installed app's id, and asking anyway returns a blank page icon.
            else if (!Path.IsPathRooted(appId)) icon = ReadIcon(ShellAppsFolder + appId);
            lock (AppIcons) AppIcons[appId] = icon;
            return icon;
        }

        private static IEnumerable<string> ShortcutNames()
        {
            if (!Directory.Exists(ShortcutFolder)) return Enumerable.Empty<string>();
            return Directory.GetFiles(ShortcutFolder, "*.lnk").Select(Path.GetFileNameWithoutExtension);
        }

        public static void Launch(PinnedEntry pinned)
        {
            try
            {
                string appId = pinned.AppId;
                if (appId == ExplorerAppId) Process.Start("explorer.exe");
                else if (IsExistingFile(appId))
                    Process.Start(new ProcessStartInfo(appId) { WorkingDirectory = Path.GetDirectoryName(appId), UseShellExecute = true });
                else if (ReadIcon(ShellAppsFolder + appId) != null)
                    Process.Start("explorer.exe", "\"" + ShellAppsFolder + appId + "\"");
                // Not every pinned item is listed among the installed apps, but its shortcut still opens it.
                else if (pinned.Shortcut != null && File.Exists(pinned.Shortcut))
                    Process.Start(new ProcessStartInfo(pinned.Shortcut) { UseShellExecute = true });
                else Log.Write("Nothing found to start " + appId);
            }
            catch (Exception ex) { Log.Write("Could not start " + pinned.AppId + ": " + ex.Message); }
        }

        private static bool IsExistingFile(string appId) => Path.IsPathRooted(appId) && File.Exists(appId);

        public static ImageSource IconFor(PinnedEntry pinned)
        {
            ImageSource icon = null;
            if (IsExistingFile(pinned.AppId)) icon = ReadIcon(pinned.AppId);
            // A path can't be an installed app's id. Asking the shell anyway returns a blank page icon, not an error.
            else if (!Path.IsPathRooted(pinned.AppId)) icon = ReadIcon(ShellAppsFolder + pinned.AppId);
            // A shortcut whose program has moved only has a small icon left in Windows' cache. Asking for more gets a blank page.
            if (icon == null && pinned.Shortcut != null) icon = ReadIcon(pinned.Shortcut, ShortcutIconSize);
            if (icon == null) Log.Write("No icon for " + pinned.AppId);
            return icon;
        }

        // The shell supplies icons for every kind of pinned item, including store apps that have no exe to read.
        private static ImageSource ReadIcon(string parsingName) => ReadIcon(parsingName, IconSize);

        private static ImageSource ReadIcon(string parsingName, int size)
        {
            IntPtr bitmap = IntPtr.Zero;
            try
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out IShellItemImageFactory factory);
                factory.GetImage(new NativeSize { Width = size, Height = size }, SIIGBF_ICONONLY, out bitmap);
                return ToImage(bitmap, size);
            }
            catch (Exception)
            {
                // Expected for items that aren't listed this way. The caller tries the next source.
                return null;
            }
            finally
            {
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            }
        }

        /// <summary>Copies the pixels by hand, because WPF's own conversion drops the transparency and leaves a black box.</summary>
        private static ImageSource ToImage(IntPtr bitmap, int size)
        {
            var header = new BitmapInfoHeader { Size = 40, Width = size, Height = -size, Planes = 1, BitCount = 32 };
            var pixels = new byte[size * size * 4];
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(dc, bitmap, 0, (uint)size, pixels, ref header, 0) == 0) return null;
            }
            finally { DeleteDC(dc); }
            var image = BitmapSource.Create(size, size, IconDpi, IconDpi, PixelFormats.Pbgra32, null, pixels, size * 4);
            image.Freeze();
            return image;
        }

        private const int SIIGBF_ICONONLY = 4;
        private const ushort VT_LPWSTR = 31;

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { public Guid Format; public uint Id; }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant { public ushort Type; public ushort Reserved1, Reserved2, Reserved3; public IntPtr Pointer; public IntPtr Extra; }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropVariant value);
            void SetValue(ref PropertyKey key, ref PropVariant value);
            void Commit();
        }

        [DllImport("shell32.dll", PreserveSig = false)]
        private static extern void SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, out IPropertyStore store);
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize { public int Width, Height; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size; public int Width, Height; public ushort Planes, BitCount;
            public uint Compression, SizeImage; public int XPels, YPels; public uint ClrUsed, ClrImportant;
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            void GetImage(NativeSize size, int flags, out IntPtr bitmap);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItemImageFactory item);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);
    }
}
