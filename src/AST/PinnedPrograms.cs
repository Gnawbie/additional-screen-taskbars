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
        /// <summary>The taskbar's own shortcut for this item, if it has one.</summary>
        public string Shortcut;
    }

    /// <summary>Understands the Windows taskbar's pinned buttons, and finds their icons and starts their programs.</summary>
    internal static class PinnedPrograms
    {
        // Windows names these buttons like "Discord - 1 running window pinned" and gives no other way to tell.
        private static readonly Regex EnglishName = new Regex(@"^(.*?)(?: - \d+ running windows?)?(?: pinned)?$", RegexOptions.Compiled);
        private const string ShellAppsFolder = @"shell:AppsFolder\";
        private const string ExplorerAppId = "Microsoft.Windows.Explorer";
        private const int IconSize = 32;

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
            ImageSource icon = IsExistingFile(pinned.AppId) ? ReadIcon(pinned.AppId) : ReadIcon(ShellAppsFolder + pinned.AppId);
            if (icon == null && pinned.Shortcut != null) icon = ReadIcon(pinned.Shortcut);
            if (icon == null) Log.Write("No icon for " + pinned.AppId);
            return icon;
        }

        // The shell supplies icons for every kind of pinned item, including store apps that have no exe to read.
        private static ImageSource ReadIcon(string parsingName)
        {
            IntPtr bitmap = IntPtr.Zero;
            try
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out IShellItemImageFactory factory);
                factory.GetImage(new NativeSize { Width = IconSize, Height = IconSize }, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out bitmap);
                return ToImage(bitmap);
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
        private static ImageSource ToImage(IntPtr bitmap)
        {
            var header = new BitmapInfoHeader { Size = 40, Width = IconSize, Height = -IconSize, Planes = 1, BitCount = 32 };
            var pixels = new byte[IconSize * IconSize * 4];
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(dc, bitmap, 0, IconSize, pixels, ref header, 0) == 0) return null;
            }
            finally { DeleteDC(dc); }
            var image = BitmapSource.Create(IconSize, IconSize, 96, 96, PixelFormats.Pbgra32, null, pixels, IconSize * 4);
            image.Freeze();
            return image;
        }

        private const int SIIGBF_BIGGERSIZEOK = 1, SIIGBF_ICONONLY = 4;

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
