using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace AST
{
    /// <summary>
    /// Puts "Pin to taskbar (AST)" and "Pin to Start (AST)" in Windows 11's short right-click menu. Windows 11 only
    /// shows items there from packaged apps, so AST registers a small unsigned package (built from src\ShellMenu)
    /// that points at its install folder, where ASTMenu.dll gives File Explorer the items. Older Windows has no such
    /// menu; the items PinRequests adds to the full menu cover it.
    /// </summary>
    internal static class ExplorerMenu
    {
        private const string PackageName = "AST.ExplorerMenu";
        private const string DllName = "ASTMenu.dll", PackageFile = "ASTMenu.msix";
        private const int FirstWindows11Build = 22000;
        private const int PowerShellTimeoutMilliseconds = 60000;
        private const int UnlockWaitMilliseconds = 10000, UnlockRetryMilliseconds = 250;

        private static bool IsSupported
        {
            get
            {
                // .NET Framework reports an older Windows version to apps that don't declare Windows 10, so read the real build.
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    return int.TryParse(k?.GetValue("CurrentBuildNumber") as string, out int build) && build >= FirstWindows11Build;
            }
        }

        /// <summary>Unpacks the menu files into the install folder and registers the package. Failing only loses the short menu's items.</summary>
        public static void Install(string folder)
        {
            if (!IsSupported) return;
            try
            {
                // Removed first, so File Explorer lets go of the old handler before it's replaced.
                Remove();
                WriteResource("AST." + PackageFile, Path.Combine(folder, PackageFile));
                WriteResource("AST." + DllName, Path.Combine(folder, DllName));
                string error = RunPowerShell("Add-AppxPackage -Path " + Quote(Path.Combine(folder, PackageFile)) +
                    " -ExternalLocation " + Quote(folder) + " -AllowUnsigned -ForceUpdateFromAnyVersion");
                Log.Write(error == null ? "Added AST's items to File Explorer's menu" : "Could not add AST's items to File Explorer's short menu: " + error);
            }
            catch (Exception ex) { Log.Write("Could not add AST's items to File Explorer's short menu: " + ex.Message); }
        }

        public static void Remove()
        {
            if (!IsSupported) return;
            string error = RunPowerShell("Get-AppxPackage -Name " + Quote(PackageName) + " | Remove-AppxPackage");
            if (error != null) Log.Write("Could not remove AST's items from File Explorer's short menu: " + error);
        }

        private static void WriteResource(string resource, string path)
        {
            byte[] bytes;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            using (var m = new MemoryStream())
            {
                s.CopyTo(m);
                bytes = m.ToArray();
            }
            if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(bytes)) return;
            // File Explorer's helper process can hold the old handler for a few moments after the package is removed.
            for (int waited = 0; ; waited += UnlockRetryMilliseconds)
            {
                try
                {
                    File.WriteAllBytes(path, bytes);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && waited < UnlockWaitMilliseconds)
                {
                    Thread.Sleep(UnlockRetryMilliseconds);
                }
            }
        }

        private static string Quote(string text) => "'" + text.Replace("'", "''") + "'";

        /// <summary>Runs a command through Windows PowerShell's app package commands. Returns null, or the error.</summary>
        private static string RunPowerShell(string command)
        {
            try
            {
                var info = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"$ErrorActionPreference='Stop'; " + command.Replace("\"", "\\\"") + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                using (var p = Process.Start(info))
                {
                    var stderr = p.StandardError.ReadToEndAsync();
                    p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(PowerShellTimeoutMilliseconds)) return "PowerShell took too long";
                    string error = stderr.Result.Trim();
                    return p.ExitCode == 0 && error.Length == 0 ? null : (error.Length > 0 ? error : "exit code " + p.ExitCode);
                }
            }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
