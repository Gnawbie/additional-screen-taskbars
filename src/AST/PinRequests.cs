using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace AST
{
    /// <summary>
    /// "Pin to taskbar (AST)" and "Pin to Start (AST)" in File Explorer's right-click menu. Windows' own pin items only
    /// change Windows' taskbar and Start menu, and Windows doesn't always show them. Each item starts AST with the
    /// file, and that copy hands the request to the running one through a file in AST's settings folder.
    /// </summary>
    internal static class PinRequests
    {
        public const string TaskbarArg = "--pin-taskbar", StartArg = "--pin-start";
        public const string EventName = @"Local\AST.PinRequest";

        private static string Folder => Path.Combine(Config.DataDir, "pin requests");

        private static readonly string[] FileTypes = { "exefile", "lnkfile" };
        // Registry key, menu text, argument.
        private static readonly string[][] Verbs =
        {
            new[] { "AST.PinTaskbar", "Pin to taskbar (AST)", TaskbarArg },
            new[] { "AST.PinStart", "Pin to Start (AST)", StartArg },
        };

        /// <summary>Returns true if the arguments were a pin request.</summary>
        public static bool TryQueue(string[] args)
        {
            if (args.Length != 2 || (args[0] != TaskbarArg && args[0] != StartArg)) return false;
            try
            {
                Directory.CreateDirectory(Folder);
                string file = Path.Combine(Folder, DateTime.Now.Ticks + "-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllLines(file, new[] { args[0], args[1] });
            }
            catch (Exception ex) { Log.Write("Could not pass on the request to pin " + args[1] + ": " + ex.Message); }
            return true;
        }

        /// <summary>Takes the waiting requests, oldest first, as (argument, file) pairs.</summary>
        public static List<KeyValuePair<string, string>> Take()
        {
            var requests = new List<KeyValuePair<string, string>>();
            if (!Directory.Exists(Folder)) return requests;
            foreach (var file in Directory.GetFiles(Folder, "*.txt").OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    var lines = File.ReadAllLines(file);
                    File.Delete(file);
                    if (lines.Length >= 2) requests.Add(new KeyValuePair<string, string>(lines[0], lines[1]));
                }
                catch (Exception ex) { Log.Write("Could not read a pin request: " + ex.Message); }
            }
            return requests;
        }

        /// <summary>Adds the menu items for the current user, pointing at the given copy of AST.</summary>
        public static void Register(string exePath)
        {
            try
            {
                foreach (var type in FileTypes)
                    foreach (var verb in Verbs)
                        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + type + @"\shell\" + verb[0]))
                        {
                            key.SetValue("MUIVerb", verb[1]);
                            key.SetValue("Icon", "\"" + exePath + "\",0");
                            using (var command = key.CreateSubKey("command"))
                                command.SetValue("", "\"" + exePath + "\" " + verb[2] + " \"%1\"");
                        }
            }
            catch (Exception ex) { Log.Write("Could not add AST's items to File Explorer's menu: " + ex.Message); }
        }

        public static void Unregister()
        {
            try
            {
                foreach (var type in FileTypes)
                    foreach (var verb in Verbs)
                        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + type + @"\shell\" + verb[0], false);
            }
            catch (Exception ex) { Log.Write("Could not remove AST's items from File Explorer's menu: " + ex.Message); }
        }
    }
}
