using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace AST
{
    public sealed class ThemeEntry
    {
        public string Id;
        public string Name;
        public bool BuiltIn;
        public string Folder;
        public IAstStyle Plugin;
        public override string ToString() => BuiltIn ? Name : Name + " (third-party)";
    }

    [DataContract]
    internal sealed class ThemeManifest
    {
        [DataMember(Name = "name")] public string Name = null;
    }

    /// <summary>
    /// Finds and loads bar looks: the two built-in themes, XAML themes in the themes folder, and enabled code plugins.
    /// The key "AST.Bar" in a theme's ResourceDictionary is a DataTemplate that receives a BarViewModel.
    /// </summary>
    internal sealed class ThemeManager
    {
        public const string BarTemplateKey = "AST.Bar";
        public const string FallbackThemeId = "modern";

        private readonly Config _config;
        private readonly List<IAstStyle> _pluginStyles = new List<IAstStyle>();
        private readonly HashSet<string> _loadedPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ResourceDictionary> _xaml = new Dictionary<string, ResourceDictionary>();

        public ThemeManager(Config config)
        {
            _config = config;
            LoadEnabledPlugins();
        }

        public IEnumerable<string> AvailablePluginFiles()
        {
            if (!Directory.Exists(Config.PluginsDir)) return Enumerable.Empty<string>();
            return Directory.GetFiles(Config.PluginsDir, "*.dll").Select(Path.GetFileName).OrderBy(n => n);
        }

        /// <summary>Plugins are ordinary code, so only those the user has switched on are ever loaded.</summary>
        public void LoadEnabledPlugins()
        {
            foreach (var file in _config.EnabledPlugins.ToList())
            {
                if (_loadedPlugins.Contains(file)) continue;
                string path = Path.Combine(Config.PluginsDir, Path.GetFileName(file));
                if (!File.Exists(path)) continue;
                try
                {
                    var asm = Assembly.LoadFrom(path);
                    foreach (var t in asm.GetExportedTypes().Where(t => typeof(IAstStyle).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface))
                        _pluginStyles.Add((IAstStyle)Activator.CreateInstance(t));
                    _loadedPlugins.Add(file);
                }
                catch (Exception ex) { Log.Write("Plugin " + file + " failed to load: " + ex); }
            }
        }

        public List<ThemeEntry> List()
        {
            var list = new List<ThemeEntry>
            {
                new ThemeEntry { Id = "modern", Name = "Modern", BuiltIn = true },
                new ThemeEntry { Id = "classic", Name = "Classic", BuiltIn = true },
            };
            if (Directory.Exists(Config.ThemesDir))
            {
                foreach (var dir in Directory.GetDirectories(Config.ThemesDir))
                {
                    if (!File.Exists(Path.Combine(dir, "theme.xaml"))) continue;
                    string id = "user:" + Path.GetFileName(dir);
                    list.Add(new ThemeEntry { Id = id, Name = ReadName(dir), Folder = dir });
                }
            }
            foreach (var p in _pluginStyles)
            {
                string name = p.Id;
                try { name = p.Name; } catch (Exception ex) { Log.Write("Plugin name failed: " + ex.Message); }
                list.Add(new ThemeEntry { Id = "plugin:" + p.Id, Name = name, Plugin = p });
            }
            return list;
        }

        private static string ReadName(string dir)
        {
            string fallback = Path.GetFileName(dir);
            string json = Path.Combine(dir, "theme.json");
            if (!File.Exists(json)) return fallback;
            try
            {
                using (var s = File.OpenRead(json))
                {
                    var m = (ThemeManifest)new DataContractJsonSerializer(typeof(ThemeManifest)).ReadObject(s);
                    return string.IsNullOrWhiteSpace(m.Name) ? fallback : m.Name;
                }
            }
            catch (Exception ex) { Log.Write("theme.json in " + dir + " could not be read: " + ex.Message); return fallback; }
        }

        /// <summary>Builds the bar's visual tree. A broken third-party look falls back to the built-in theme.</summary>
        public FrameworkElement CreateBar(string themeId, BarViewModel vm, out string usedThemeId)
        {
            usedThemeId = themeId;
            try { return Build(themeId, vm); }
            catch (Exception ex)
            {
                Log.Write("Theme " + themeId + " failed, using " + FallbackThemeId + ": " + ex);
                usedThemeId = FallbackThemeId;
                return Build(FallbackThemeId, vm);
            }
        }

        private FrameworkElement Build(string themeId, BarViewModel vm)
        {
            var entry = List().FirstOrDefault(t => t.Id == themeId) ?? throw new InvalidOperationException("Theme not found: " + themeId);
            if (entry.Plugin != null)
                return entry.Plugin.CreateBar(vm) ?? throw new InvalidOperationException("The plugin returned no bar.");

            var dict = LoadXaml(entry);
            var template = dict[BarTemplateKey] as DataTemplate ?? throw new InvalidOperationException("The theme has no DataTemplate with key " + BarTemplateKey + ".");
            var host = new ContentControl { Content = vm, ContentTemplate = template, Focusable = false };
            host.Resources.MergedDictionaries.Add(dict);
            return host;
        }

        private ResourceDictionary LoadXaml(ThemeEntry entry)
        {
            if (_xaml.TryGetValue(entry.Id, out var cached)) return cached;
            ResourceDictionary dict;
            if (entry.BuiltIn)
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("AST.Themes." + entry.Id + ".xaml"))
                    dict = (ResourceDictionary)XamlReader.Load(s);
            }
            else
            {
                string file = Path.Combine(entry.Folder, "theme.xaml");
                using (var s = File.OpenRead(file))
                {
                    // Relative paths in the theme, such as images, resolve against its own folder.
                    var ctx = new ParserContext { BaseUri = new Uri(file) };
                    dict = (ResourceDictionary)XamlReader.Load(s, ctx);
                }
            }
            _xaml[entry.Id] = dict;
            return dict;
        }

        /// <summary>Lets a theme's edits show up without restarting the app.</summary>
        public void ClearCache() => _xaml.Clear();
    }
}
