using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace AST
{
    public enum BarEdge { Off, Top, Bottom, Left, Right }

    [DataContract]
    public sealed class MonitorConfig
    {
        [DataMember] public string Id;
        [DataMember] public string Name;
        /// <summary>Off, Top, Bottom, Left or Right.</summary>
        [DataMember] public string Edge;
        /// <summary>Bar thickness in device-independent pixels. Zero means the default for the edge.</summary>
        [DataMember] public int Thickness;

        public BarEdge EdgeValue
        {
            get => Enum.TryParse(Edge, out BarEdge e) ? e : BarEdge.Off;
            set => Edge = value.ToString();
        }

        public bool IsVertical => EdgeValue == BarEdge.Left || EdgeValue == BarEdge.Right;
        public int EffectiveThickness => Thickness > 0 ? Thickness : (IsVertical ? Config.DefaultVerticalThickness : Config.DefaultHorizontalThickness);
    }

    [DataContract]
    public sealed class Config
    {
        public const int DefaultHorizontalThickness = 40;
        public const int DefaultVerticalThickness = 48;
        public const int MinThickness = 28;
        public const int MaxThickness = 480;

        [DataMember] public string Theme = "modern";
        [DataMember] public bool ShowClock = true;
        [DataMember] public bool ShowStartButton = true;
        /// <summary>Icons only is the default, and a value missing from an older config file reads as false.</summary>
        [DataMember] public bool ShowProgramNames;
        /// <summary>Stops bars being moved or resized by dragging.</summary>
        [DataMember] public bool LockBars;
        /// <summary>Hiding for fullscreen programs is the default, and a value missing from an older config file reads as false.</summary>
        [DataMember] public bool KeepBarsOverFullscreen;
        /// <summary>Also lists the Windows taskbar's pinned programs that aren't running.</summary>
        [DataMember] public bool ShowPinnedPrograms;
        /// <summary>Tray buttons show by default, and a value missing from an older config file reads as false.</summary>
        [DataMember] public bool HideTrayButtons;
        /// <summary>Lists only a bar's own screen. Programs from all screens is the default, and a missing value reads as false.</summary>
        [DataMember] public bool OwnScreenProgramsOnly;
        /// <summary>
        /// Keeps program names upright on side bars. Stored this way round because vertical names are the default
        /// and a value missing from an older config file reads as false.
        /// </summary>
        [DataMember] public bool HorizontalSideLabels;
        public bool VerticalSideLabels { get => !HorizontalSideLabels; set => HorizontalSideLabels = !value; }
        [DataMember] public List<MonitorConfig> Monitors = new List<MonitorConfig>();
        /// <summary>File names of code plugins the user has switched on. Plugins never load without being listed here.</summary>
        [DataMember] public List<string> EnabledPlugins = new List<string>();

        public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AST");
        public static string ThemesDir => Path.Combine(DataDir, "themes");
        public static string PluginsDir => Path.Combine(DataDir, "plugins");
        private static string FilePath => Path.Combine(DataDir, "config.json");

        /// <summary>The primary screen starts off because the Windows taskbar lives there.</summary>
        public MonitorConfig For(MonitorInfo monitor)
        {
            var c = Monitors.FirstOrDefault(m => string.Equals(m.Id, monitor.Id, StringComparison.OrdinalIgnoreCase));
            if (c == null)
            {
                c = new MonitorConfig { Id = monitor.Id, EdgeValue = monitor.IsPrimary ? BarEdge.Off : BarEdge.Bottom };
                Monitors.Add(c);
            }
            c.Name = monitor.Name;
            return c;
        }

        public static Config Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    using (var s = File.OpenRead(FilePath))
                    {
                        var c = (Config)new DataContractJsonSerializer(typeof(Config)).ReadObject(s);
                        c.Monitors = c.Monitors ?? new List<MonitorConfig>();
                        c.EnabledPlugins = c.EnabledPlugins ?? new List<string>();
                        c.Theme = c.Theme ?? "modern";
                        return c;
                    }
                }
            }
            catch (Exception ex) { Log.Write("Config could not be read, using defaults: " + ex.Message); }
            return new Config();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                string tmp = FilePath + ".tmp";
                using (var s = File.Create(tmp))
                using (var w = JsonReaderWriterFactory.CreateJsonWriter(s, Encoding.UTF8, false, true))
                    new DataContractJsonSerializer(typeof(Config)).WriteObject(w, this);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Log.Write("Config could not be saved: " + ex.Message); }
        }
    }

    internal static class Log
    {
        public static void Write(string message)
        {
            try
            {
                Directory.CreateDirectory(Config.DataDir);
                File.AppendAllText(Path.Combine(Config.DataDir, "log.txt"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
