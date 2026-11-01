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
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AST";

        public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
        public static string InstalledExe => Path.Combine(InstallDir, AppName + ".exe");
        private static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
        private static string CurrentExe => Assembly.GetExecutingAssembly().Location;
        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public static bool IsSetupName => Path.GetFileName(CurrentExe).IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0;

        public static bool StartsWithWindows
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k?.GetValue(AppName) != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(AppName, "\"" + CurrentExe + "\"");
                    else k.DeleteValue(AppName, false);
                }
            }
        }

        /// <returns>The process exit code.</returns>
        public static int Install(bool silent)
        {
            if (!silent)
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
                    File.Copy(CurrentExe, InstalledExe, true);

                CreateShortcut();
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
                if (!silent) MessageBox.Show("Setup could not finish:\n\n" + ex.Message, AppName + " setup", MessageBoxButton.OK, MessageBoxImage.Error);
                return 2;
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
                StartsWithWindows = false;
                if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
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

        private static void CreateShortcut()
        {
            Type shell = Type.GetTypeFromProgID("WScript.Shell");
            dynamic wsh = Activator.CreateInstance(shell);
            dynamic link = wsh.CreateShortcut(ShortcutPath);
            link.TargetPath = InstalledExe;
            link.WorkingDirectory = InstallDir;
            link.IconLocation = InstalledExe + ",0";
            link.Description = "Taskbars for your other screens";
            link.Save();
        }
    }
}
