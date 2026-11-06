using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace AST
{
    /// <summary>A small volume slider that opens beside the sound icon, like the one in Windows 10.</summary>
    internal sealed class VolumeWindow : Window
    {
        private const double PopupWidth = 300, PopupHeight = 92, OutputsHeight = 26, OutputsMargin = 6;
        private const string SpeakerGlyph = "", MutedGlyph = "";

        private readonly MonitorInfo _monitor;
        private readonly ComboBox _outputs = new ComboBox { Height = OutputsHeight, Margin = new Thickness(14, OutputsMargin, 12, 0), Visibility = Visibility.Collapsed, ToolTip = "Play sound through" };
        private bool _closing, _showingState;
        private readonly Slider _slider = new Slider { Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true, Focusable = true };
        private readonly TextBlock _level = new TextBlock { Width = 36, FontSize = 18, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _speaker = new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 18 };

        public VolumeWindow(MonitorInfo monitor, bool classic)
        {
            _monitor = monitor;
            Title = "AST Volume";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Width = PopupWidth;
            Height = PopupHeight;
            UseLayoutRounding = true;

            var text = new SolidColorBrush(classic ? Colors.Black : Color.FromRgb(242, 242, 242));
            Background = new SolidColorBrush(classic ? Color.FromRgb(192, 192, 192) : Color.FromRgb(31, 31, 31));
            Foreground = text;
            FontFamily = new FontFamily(classic ? "Tahoma" : "Segoe UI");
            _level.Foreground = text;
            _speaker.Foreground = text;

            var mute = new Button { Content = _speaker, Width = 36, Height = 36, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false, ToolTip = "Mute" };
            mute.Click += (s, e) => Try(() =>
            {
                AudioVolume.IsMuted = !AudioVolume.IsMuted;
                ShowState();
            });

            var row = new DockPanel { Margin = new Thickness(8, 8, 12, 0) };
            DockPanel.SetDock(mute, Dock.Left);
            DockPanel.SetDock(_level, Dock.Right);
            row.Children.Add(mute);
            row.Children.Add(_level);
            _slider.Margin = new Thickness(6, 0, 6, 0);
            row.Children.Add(_slider);

            var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 6, 0, 8) };
            links.Children.Add(Link("Sound settings", "ms-settings:sound", text));
            var mixer = Link("Volume mixer", null, text);
            mixer.Click += (s, e) => new MixerWindow(_monitor, classic).Show();
            links.Children.Add(mixer);

            var root = new StackPanel();
            root.Children.Add(row);
            root.Children.Add(_outputs);
            root.Children.Add(links);
            Content = root;

            Try(ShowState);
            ShowOutputs();
            _slider.ValueChanged += (s, e) => Try(() =>
            {
                // Only a person moving the slider should change anything, not it catching up with another device.
                if (_showingState) return;
                int value = (int)Math.Round(_slider.Value);
                AudioVolume.Level = value;
                // Dragging a muted slider should be heard, as in Windows.
                if (AudioVolume.IsMuted && value > 0) AudioVolume.IsMuted = false;
                ShowLevel(value);
            });

            KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
            // Closing also takes focus away, and closing a window that's already closing throws.
            Closing += (s, e) => _closing = true;
            Deactivated += (s, e) => { if (!_closing) Close(); };
            SourceInitialized += (s, e) =>
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
            };
            Loaded += (s, e) =>
            {
                PlaceBesideCursor(this, _monitor, PopupWidth, Height);
                _slider.Focus();
            };
        }

        private Button Link(string label, string page, Brush color)
        {
            var link = new Button
            {
                Content = new TextBlock { Text = label, Foreground = color, FontSize = 12, TextDecorations = TextDecorations.Underline },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 14, 0),
                Focusable = false,
            };
            link.Click += (s, e) =>
            {
                Close();
                if (page != null) TrayMirror.OpenSettingsPage(page);
            };
            return link;
        }

        /// <summary>The dropdown only appears when there's more than one device to choose from.</summary>
        private void ShowOutputs()
        {
            try
            {
                var outputs = AudioVolume.Outputs();
                if (outputs.Count < 2) return;
                foreach (var output in outputs)
                {
                    var item = new ComboBoxItem { Content = output.Name, Tag = output.Id, ToolTip = output.Name };
                    _outputs.Items.Add(item);
                    if (output.IsDefault) _outputs.SelectedItem = item;
                }
                _outputs.SelectionChanged += (s, e) => Try(() =>
                {
                    AudioVolume.SetDefaultOutput((string)((ComboBoxItem)_outputs.SelectedItem).Tag);
                    ShowState();
                });
                _outputs.Visibility = Visibility.Visible;
                Height = PopupHeight + OutputsMargin + OutputsHeight;
            }
            // The slider still works without the dropdown, so this shouldn't disable it.
            catch (Exception ex) { Log.Write("Could not list the sound devices: " + ex.Message); }
        }

        private void ShowState()
        {
            int level = AudioVolume.Level;
            _showingState = true;
            _slider.Value = level;
            _showingState = false;
            ShowLevel(level);
        }

        private void ShowLevel(int level)
        {
            _level.Text = level.ToString();
            _speaker.Text = AudioVolume.IsMuted || level == 0 ? MutedGlyph : SpeakerGlyph;
        }

        // A PC with no sound device has nothing to control, which shouldn't count as an error in AST.
        private void Try(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                Log.Write("Volume control failed: " + ex.Message);
                _slider.IsEnabled = false;
                _level.Text = "";
            }
        }

        /// <summary>Stays fully on the bar's screen, whichever edge the bar is on.</summary>
        internal static void PlaceBesideCursor(Window window, MonitorInfo monitor, double width, double height)
        {
            IntPtr h = new WindowInteropHelper(window).Handle;
            double scale = monitor.DpiScale;
            int w = (int)Math.Round(width * scale), ht = (int)Math.Round(height * scale);
            var cursor = Forms.Cursor.Position;
            var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFOEX)) };
            Native.GetMonitorInfo(monitor.Handle, ref info);
            var work = info.rcWork;
            int x = Math.Max(work.Left, Math.Min(cursor.X - w / 2, work.Right - w));
            int y = Math.Max(work.Top, Math.Min(cursor.Y - ht / 2, work.Bottom - ht));
            Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, w, ht, Native.SWP_SHOWWINDOW);
            window.Activate();
            Native.SetForegroundWindow(h);
        }
    }
}
