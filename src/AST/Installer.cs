using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace AST
{
    /// <summary>
    /// The one exe is both the app and its installer. It installs per user, so it needs no admin rights and
    /// nothing has to be downloaded. Renaming the file to contain "setup" makes double-clicking it install.
    /// </summary>
    internal static class Installer
    {
        public const string AppName = "AST";
        private const int CopyWaitMilliseconds = 15000, CopyRetryMilliseconds = 250;
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AST";

        public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
        public static string InstalledExe => Path.Combine(InstallDir, AppName + ".exe");
        private static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
        private const string FolderShortcutName = "Start AST.lnk";
        private static string SettingsFolderShortcutPath => Path.Combine(Config.DataDir, FolderShortcutName);
        private static string DesktopShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");
        private static string CurrentExe => Assembly.GetExecutingAssembly().Location;
        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public static bool IsSetupName => Path.GetFileName(CurrentExe).IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0;

        public static bool StartsWithWindows
        {
            get => File.Exists(StartupShortcutPath) || HasOldRunEntry;
            set
            {
                RemoveOldRunEntry();
                if (value) CreateShortcut(StartupShortcutPath, CurrentExe);
                else if (File.Exists(StartupShortcutPath)) File.Delete(StartupShortcutPath);
            }
        }

        // A shortcut in the Startup folder, because AST's entry under the Run key wasn't started at sign-in on at
        // least one PC. Versions before 0.1.4 used that entry, so it's still recognised and replaced.
        private static string StartupShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), AppName + ".lnk");

        private static bool HasOldRunEntry
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k?.GetValue(AppName) != null;
            }
        }

        private static void RemoveOldRunEntry()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                k.DeleteValue(AppName, false);
        }

        private static bool IsFirstInstall()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                return k == null;
        }

        /// <param name="confirm">False when the person has already agreed, such as from the update button.</param>
        /// <returns>The process exit code.</returns>
        public static int Install(bool silent, bool confirm = true)
        {
            if (!silent && confirm)
            {
                var answer = MessageBox.Show("Install " + AppName + " " + Version + " for your account?\n\nNo admin rights are needed. You can remove it any time from Settings > Apps.",
                    AppName + " setup", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (answer != MessageBoxResult.OK) return 1;
            }
            try
            {
                Program.StopRunningInstance();
                Directory.CreateDirectory(InstallDir);
                if (!string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
                    CopyWhenUnlocked(CurrentExe, InstalledExe);

                CreateShortcut(ShortcutPath);
                // Anyone who goes looking in AST's folders should find an obvious way to start it.
                CreateShortcut(Path.Combine(InstallDir, FolderShortcutName));
                Directory.CreateDirectory(Config.DataDir);
                CreateShortcut(SettingsFolderShortcutPath);
                // Only once, so an update never brings back a shortcut someone has deleted.
                var config = Config.Load();
                if (!config.DesktopShortcutMade)
                {
                    CreateShortcut(DesktopShortcutPath);
                    config.DesktopShortcutMade = true;
                    config.Save();
                }
                // Only on a first install, so an update never undoes someone turning this off.
                if (IsFirstInstall() || HasOldRunEntry)
                {
                    RemoveOldRunEntry();
                    CreateShortcut(StartupShortcutPath);
                }
                using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    k.SetValue("DisplayName", AppName);
                    k.SetValue("DisplayVersion", Version);
                    k.SetValue("Publisher", AppName);
                    k.SetValue("DisplayName", AppName + " - Additional Screen Taskbars");
                    k.SetValue("DisplayIcon", InstalledExe);
                    k.SetValue("InstallLocation", InstallDir);
                    k.SetValue("UninstallString", "\"" + InstalledExe + "\" --uninstall");
                    k.SetValue("QuietUninstallString", "\"" + InstalledExe + "\" --uninstall --silent");
                    k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
                if (!silent) Process.Start(InstalledExe);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write("Install failed: " + ex);
                if (!silent)
                {
                    MessageBox.Show("Setup could not finish:\n\n" + ex.Message, AppName + " setup", MessageBoxButton.OK, MessageBoxImage.Error);
                    // The running copy has already been closed, so bring back whatever is still installed.
                    if (File.Exists(InstalledExe))
                    {
                        try { Process.Start(InstalledExe); }
                        catch (Exception startError) { Log.Write("Could not restart AST after the failed install: " + startError.Message); }
                    }
                }
                return 2;
            }
        }

        /// <summary>The closing app and its taskbar guard hold the file for a few moments after they've been told to stop.</summary>
        private static void CopyWhenUnlocked(string from, string to)
        {
            for (int waited = 0; ; waited += CopyRetryMilliseconds)
            {
                try
                {
                    File.Copy(from, to, true);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && waited < CopyWaitMilliseconds)
                {
                    Thread.Sleep(CopyRetryMilliseconds);
                }
            }
        }

        public static int Uninstall(bool silent)
        {
            bool removeSettings = false;
            if (!silent)
            {
                var answer = MessageBox.Show("Remove " + AppName + "?\n\nChoose Yes to also delete your settings, themes and plugins, or No to keep them.",
                    AppName, MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
                if (answer == MessageBoxResult.Cancel) return 1;
                removeSettings = answer == MessageBoxResult.Yes;
            }
            try
            {
                Program.StopRunningInstance();
                // Covers a crash having left the Windows taskbar hidden, before the settings can be deleted.
                MainTaskbar.Release(Config.Load());
                StartsWithWindows = false;
                if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
                if (File.Exists(DesktopShortcutPath)) File.Delete(DesktopShortcutPath);
                if (File.Exists(SettingsFolderShortcutPath)) File.Delete(SettingsFolderShortcutPath);
                // A later reinstall counts as new, even if the settings are kept.
                var config = Config.Load();
                config.DesktopShortcutMade = false;
                config.Save();
                Registry.CurrentUser.DeleteSubKey(UninstallKey, false);
                if (removeSettings && Directory.Exists(Config.DataDir)) Directory.Delete(Config.DataDir, true);
                DeleteInstallFolderAfterExit();
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write("Uninstall failed: " + ex);
                if (!silent) MessageBox.Show("Could not finish removing AST:\n\n" + ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
                return 2;
            }
        }

        /// <summary>A running exe can't delete itself, so a short-lived command does it once this process has gone.</summary>
        private static void DeleteInstallFolderAfterExit()
        {
            if (!Directory.Exists(InstallDir)) return;
            var cmd = "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\"";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        }

        private static void CreateShortcut(string path) => CreateShortcut(path, InstalledExe);

        private static void CreateShortcut(string path, string exe)
        {
            Type shell = Type.GetTypeFromProgID("WScript.Shell");
            dynamic wsh = Activator.CreateInstance(shell);
            dynamic link = wsh.CreateShortcut(path);
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.IconLocation = exe + ",0";
            link.Description = "Taskbars for your other screens";
            link.Save();
        }
    }
}
