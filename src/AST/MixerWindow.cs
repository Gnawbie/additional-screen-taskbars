using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AST
{
    /// <summary>
    /// A small volume mixer like Windows 10's: the device first, then a slider and a live level meter for each program
    /// playing sound.
    /// </summary>
    internal sealed class MixerWindow : Window
    {
        private const double ColumnWidth = 84, SliderHeight = 150, MeterWidth = 5;
        private const string SpeakerGlyph = "", MutedGlyph = "";

        private readonly MonitorInfo _monitor;
        private readonly Brush _text, _meterTrack;
        private readonly Brush _meterFill = new SolidColorBrush(Color.FromRgb(16, 200, 64));
        private readonly List<Action> _refreshMeters = new List<Action>();
        private readonly List<Action> _refreshLevels = new List<Action>();
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        private int _ticks;
        private bool _closing;

        public MixerWindow(MonitorInfo monitor, bool classic)
        {
            _monitor = monitor;
            Title = "AST Volume Mixer";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            UseLayoutRounding = true;

            _text = new SolidColorBrush(classic ? Colors.Black : Color.FromRgb(242, 242, 242));
            _meterTrack = new SolidColorBrush(classic ? Color.FromRgb(160, 160, 160) : Color.FromRgb(60, 60, 60));
            Background = new SolidColorBrush(classic ? Color.FromRgb(192, 192, 192) : Color.FromRgb(31, 31, 31));
            Foreground = _text;
            FontFamily = new FontFamily(classic ? "Tahoma" : "Segoe UI");
            BorderBrush = new SolidColorBrush(classic ? Colors.White : Color.FromRgb(70, 70, 70));
            BorderThickness = new Thickness(1);

            var columns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 8) };
            try { AddColumns(columns); }
            catch (Exception ex)
            {
                // A PC with no sound device has nothing to mix, which shouldn't count as an error in AST.
                Log.Write("Could not read the volume mixer: " + ex.Message);
                columns.Children.Add(new TextBlock { Text = "No sound device", Margin = new Thickness(12), Foreground = _text });
            }

            var link = new Button
            {
                Content = new TextBlock { Text = "Sound settings", Foreground = _text, FontSize = 12, TextDecorations = TextDecorations.Underline },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(12, 0, 0, 8),
                Focusable = false,
            };
            link.Click += (s, e) => { Close(); TrayMirror.OpenSettingsPage("ms-settings:sound"); };

            var root = new StackPanel();
            root.Children.Add(new ScrollViewer
            {
                Content = columns,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxWidth = MaxColumnsWidth(),
            });
            root.Children.Add(link);
            Content = root;

            _timer.Tick += (s, e) => Tick();
            KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
            // Closing also takes focus away, and closing a window that's already closing throws.
            Closing += (s, e) => { _closing = true; _timer.Stop(); };
            Deactivated += (s, e) => { if (!_closing) Close(); };
            SourceInitialized += (s, e) =>
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
            };
            Loaded += (s, e) =>
            {
                VolumeWindow.PlaceBesideCursor(this, _monitor, ActualWidth, ActualHeight);
                _timer.Start();
            };
        }

        /// <summary>Keeps the mixer within the bar's screen, scrolling sideways if there are many programs.</summary>
        private double MaxColumnsWidth()
        {
            var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFOEX)) };
            Native.GetMonitorInfo(_monitor.Handle, ref info);
            return Math.Max(ColumnWidth * 2, (info.rcWork.Right - info.rcWork.Left) / _monitor.DpiScale - 40);
        }

        private void AddColumns(Panel columns)
        {
            var device = Column("Device", AudioVolume.DeviceName, null, () => AudioVolume.Level, v => AudioVolume.Level = v,
                () => AudioVolume.IsMuted, m => AudioVolume.IsMuted = m, AudioVolume.DeviceMeter());
            columns.Children.Add(device);
            columns.Children.Add(new Rectangle { Width = 1, Fill = _meterTrack, Margin = new Thickness(6, 8, 6, 0) });

            var apps = AudioVolume.Apps();
            if (apps.Count == 0)
                columns.Children.Add(new TextBlock { Text = "No programs are\nplaying sound", Foreground = _text, Margin = new Thickness(8, 30, 8, 0), TextAlignment = TextAlignment.Center });
            bool first = true;
            foreach (var app in apps)
            {
                // System Sounds has no program, so it gets the speaker, as in Windows 10.
                var icon = app.IsSystemSounds ? null : new Image { Width = 32, Height = 32 };
                var column = Column(first ? "Applications" : "", app.Name, icon, () => app.Level, v => app.Level = v,
                    () => app.IsMuted, m => app.IsMuted = m, () => app.Peak);
                columns.Children.Add(column);
                first = false;
                if (app.ExePath == null) continue;

                // The icon and the program's full name are on disk, which can be slow, so they load afterwards.
                var nameText = (TextBlock)column.Tag;
                string path = app.ExePath;
                bool named = app.Name != System.IO.Path.GetFileNameWithoutExtension(path);
                BackgroundReader.Read(
                    () => Tuple.Create(WindowIcons.FromExe(path), named ? null : PinnedPrograms.ProgramName(path)),
                    found =>
                    {
                        icon.Source = found.Item1;
                        if (!string.IsNullOrWhiteSpace(found.Item2)) { nameText.Text = found.Item2; nameText.ToolTip = found.Item2; }
                    });
            }
        }

        private StackPanel Column(string heading, string name, Image icon, Func<int> getLevel, Action<int> setLevel,
            Func<bool> getMuted, Action<bool> setMuted, Func<float> peak)
        {
            var column = new StackPanel { Width = ColumnWidth };
            column.Children.Add(new TextBlock { Text = heading, FontSize = 11, Foreground = _text, Height = 16, Margin = new Thickness(2, 0, 0, 4) });

            FrameworkElement picture = icon;
            if (picture == null)
                picture = new TextBlock { Text = SpeakerGlyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 28, Foreground = _text, Height = 32, TextAlignment = TextAlignment.Center };
            picture.HorizontalAlignment = HorizontalAlignment.Center;
            column.Children.Add(picture);

            var nameText = new TextBlock
            {
                Text = name, ToolTip = name, FontSize = 12, Foreground = _text, Height = 34, Margin = new Thickness(2, 4, 2, 6),
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            column.Children.Add(nameText);
            column.Tag = nameText;

            var meterFill = new Rectangle { Width = MeterWidth, Height = 0, Fill = _meterFill, VerticalAlignment = VerticalAlignment.Bottom };
            var meter = new Grid { Width = MeterWidth, Height = SliderHeight, Background = _meterTrack, Margin = new Thickness(0, 0, 6, 0) };
            meter.Children.Add(meterFill);
            var slider = new Slider
            {
                Orientation = Orientation.Vertical, Minimum = 0, Maximum = 100, Height = SliderHeight,
                IsMoveToPointEnabled = true, SmallChange = 1, LargeChange = 10,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(MeterWidth + 6, 0, 0, 0) };
            row.Children.Add(slider);
            row.Children.Add(meter);
            column.Children.Add(row);

            var level = new TextBlock { FontSize = 12, Foreground = _text, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 2) };
            column.Children.Add(level);
            var speaker = new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, Foreground = _text };
            var mute = new Button
            {
                Content = speaker, Width = 32, Height = 28, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Focusable = false, ToolTip = "Mute " + name, HorizontalAlignment = HorizontalAlignment.Center,
            };
            column.Children.Add(mute);

            bool showing = false;
            void Show()
            {
                int value = getLevel();
                showing = true;
                slider.Value = value;
                showing = false;
                level.Text = value.ToString();
                speaker.Text = getMuted() || value == 0 ? MutedGlyph : SpeakerGlyph;
            }
            void Disable(Exception ex)
            {
                // The program may have closed while the mixer was open.
                Log.Write("Mixer control for " + name + " failed: " + ex.Message);
                column.IsEnabled = false;
                column.Opacity = 0.5;
            }
            void Safely(Action action)
            {
                if (!column.IsEnabled) return;
                try { action(); } catch (Exception ex) { Disable(ex); }
            }

            Safely(Show);
            slider.ValueChanged += (s, e) =>
            {
                // Only a person moving the slider should change anything, not it catching up.
                if (showing) return;
                Safely(() =>
                {
                    int value = (int)Math.Round(slider.Value);
                    setLevel(value);
                    // Dragging a muted slider should be heard, as in Windows.
                    if (value > 0 && getMuted()) setMuted(false);
                    Show();
                });
            };
            mute.Click += (s, e) => Safely(() => { setMuted(!getMuted()); Show(); });

            _refreshMeters.Add(() => Safely(() => meterFill.Height = Math.Min(1, peak()) * SliderHeight));
            // Picks up changes made elsewhere, such as the volume keys, unless the person is dragging this slider.
            _refreshLevels.Add(() => { if (!slider.IsMouseCaptureWithin) Safely(Show); });
            return column;
        }

        private void Tick()
        {
            foreach (var refresh in _refreshMeters) refresh();
            if (++_ticks % 10 == 0) foreach (var refresh in _refreshLevels) refresh();
        }
    }
}
