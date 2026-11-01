using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace AST
{
    internal static class Program
    {
        private const string MutexName = @"Local\AST.SingleInstance";
        private const string ShowEventName = @"Local\AST.ShowSettings";
        private const string QuitEventName = @"Local\AST.Quit";

        private static BarManager _manager;
        private static SettingsWindow _settings;
        private static Forms.NotifyIcon _tray;

        [STAThread]
        public static int Main(string[] args)
        {
            // A plugin compiled against AST must bind to this exe, even if the file was renamed.
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
                new AssemblyName(e.Name).Name == "AST" ? Assembly.GetExecutingAssembly() : null;

            bool silent = args.Contains("--silent");
            if (args.Contains("--uninstall")) return Installer.Uninstall(silent);
            if (args.Contains("--install") || Installer.IsSetupName) return Installer.Install(silent);
            if (args.Contains("--quit"))
            {
                StopRunningInstance();
                return 0;
            }

            using (var mutex = new Mutex(true, MutexName, out bool first))
            {
                if (!first)
                {
                    // Already running: bring up its settings instead of adding a second set of bars.
                    Signal(ShowEventName);
                    return 0;
                }
                RunApp(args);
                mutex.ReleaseMutex();
            }
            return 0;
        }

        private static void Signal(string name)
        {
            try
            {
                using (var e = EventWaitHandle.OpenExisting(name)) e.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
        }

        /// <summary>Asks a running copy to exit and waits for it, so its exe can be replaced or deleted.</summary>
        public static void StopRunningInstance()
        {
            Signal(QuitEventName);
            try
            {
                using (var m = new Mutex(false, MutexName))
                {
                    try
                    {
                        if (m.WaitOne(TimeSpan.FromSeconds(5))) m.ReleaseMutex();
                    }
                    catch (AbandonedMutexException) { m.ReleaseMutex(); }
                }
            }
            catch (Exception ex) { Log.Write("Waiting for the running copy failed: " + ex.Message); }
        }

        private static void RunApp(string[] args)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += OnUnhandled;
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Fatal: " + e.ExceptionObject);

            var config = Config.Load();
            app.Startup += (s, e) =>
            {
                _manager = new BarManager(config, ShowSettings);
                _manager.Sync();
                CreateTray();
                WatchSignals(app);
                if (args.Contains("--settings")) ShowSettings();
            };
            app.Exit += (s, e) =>
            {
                _manager?.Dispose();
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            };
            app.Run();
        }

        private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Write("Unhandled: " + e.Exception);
            // Most runtime errors come from third-party themes, so a broken mod can't take down the bars.
            e.Handled = true;
            _manager?.FallBackToBuiltInTheme();
        }

        private static void WatchSignals(Application app)
        {
            var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
            var thread = new Thread(() =>
            {
                while (true)
                {
                    int which = WaitHandle.WaitAny(new WaitHandle[] { show, quit });
                    if (which == 0) app.Dispatcher.BeginInvoke(new Action(ShowSettings));
                    else
                    {
                        app.Dispatcher.BeginInvoke(new Action(app.Shutdown));
                        return;
                    }
                }
            }) { IsBackground = true, Name = "AST signals" };
            thread.Start();
        }

        private static void CreateTray()
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Options...", null, (s, e) => ShowSettings());
            menu.Items.Add("Exit", null, (s, e) => Application.Current.Shutdown());
            System.Drawing.Icon icon;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AST.app.ico"))
                icon = new System.Drawing.Icon(stream);
            _tray = new Forms.NotifyIcon { Icon = icon, Text = "AST", ContextMenuStrip = menu, Visible = true };
            _tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) ShowSettings(); };
        }

        private static void ShowSettings()
        {
            if (_settings != null)
            {
                _settings.Activate();
                return;
            }
            Show(new SettingsWindow(_manager));
        }

        public static void ReplaceSettingsWindow(SettingsWindow old, SettingsWindow fresh)
        {
            old.Close();
            Show(fresh);
        }

        private static void Show(SettingsWindow window)
        {
            _settings = window;
            window.Closed += (s, e) => { if (_settings == window) _settings = null; };
            window.Show();
            window.Activate();
        }
    }
}
