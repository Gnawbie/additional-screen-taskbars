using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AST
{
    /// <summary>
    /// The calendar that opens from the clock, like the one in Windows 10: the time and date at the top, then a month
    /// to page through with the arrows or the mouse wheel. Clicking the month's name goes back to today.
    /// </summary>
    internal sealed class CalendarWindow : Window
    {
        private const double PopupWidth = 330, PopupHeight = 400, CellSize = 40;
        private const int Weeks = 6;
        private const string UpGlyph = "\uE70E", DownGlyph = "\uE70D";
        private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(300);

        private static CalendarWindow _open;
        private static DateTime _closedAt = DateTime.MinValue;

        private readonly MonitorInfo _monitor;
        private readonly bool _classic;
        private readonly Brush _text, _dim, _accent, _hover;
        private readonly TextBlock _time = new TextBlock { FontSize = 36, FontWeight = FontWeights.Light };
        private readonly TextBlock _date = new TextBlock { FontSize = 13, Margin = new Thickness(0, 2, 0, 0) };
        private readonly Button _monthTitle;
        private readonly UniformGrid _days = new UniformGrid { Columns = 7, Rows = Weeks + 1 };
        private readonly DispatcherTimer _tick;
        private DateTime _month;
        private bool _closing;

        /// <summary>Clicking the clock again closes the calendar, rather than closing and reopening it.</summary>
        public static void Toggle(MonitorInfo monitor, bool classic)
        {
            if (_open != null)
            {
                _open.Close();
                return;
            }
            if (DateTime.Now - _closedAt < ReopenGuard) return;
            _open = new CalendarWindow(monitor, classic);
            _open.Show();
        }

        private CalendarWindow(MonitorInfo monitor, bool classic)
        {
            _monitor = monitor;
            _classic = classic;
            Title = "AST Calendar";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Width = PopupWidth;
            Height = PopupHeight;
            UseLayoutRounding = true;

            _text = new SolidColorBrush(classic ? Colors.Black : Color.FromRgb(242, 242, 242));
            _dim = new SolidColorBrush(classic ? Color.FromRgb(110, 110, 110) : Color.FromRgb(130, 130, 130));
            _accent = new SolidColorBrush(classic ? Color.FromRgb(0, 0, 128) : Color.FromRgb(0, 120, 215));
            _hover = new SolidColorBrush(classic ? Color.FromRgb(224, 224, 224) : Color.FromRgb(58, 58, 58));
            Background = new SolidColorBrush(classic ? Color.FromRgb(192, 192, 192) : Color.FromRgb(31, 31, 31));
            BorderBrush = new SolidColorBrush(classic ? Colors.White : Color.FromRgb(70, 70, 70));
            BorderThickness = new Thickness(1);
            Foreground = _text;
            FontFamily = new FontFamily(classic ? "Tahoma" : "Segoe UI");
            _time.Foreground = _text;
            _date.Foreground = _accent;

            var header = new StackPanel { Margin = new Thickness(20, 14, 20, 10) };
            header.Children.Add(_time);
            header.Children.Add(_date);

            _monthTitle = FlatButton(new TextBlock { FontSize = 15, Foreground = _text }, "Go to today");
            _monthTitle.HorizontalAlignment = HorizontalAlignment.Left;
            _monthTitle.Click += (s, e) => ShowMonth(DateTime.Today);
            var up = Arrow(UpGlyph, "Previous month", -1);
            var down = Arrow(DownGlyph, "Next month", 1);
            var nav = new DockPanel { Margin = new Thickness(12, 0, 12, 4) };
            DockPanel.SetDock(down, Dock.Right);
            DockPanel.SetDock(up, Dock.Right);
            nav.Children.Add(down);
            nav.Children.Add(up);
            nav.Children.Add(_monthTitle);

            _days.Margin = new Thickness(12, 0, 12, 0);
            _days.Width = CellSize * 7;
            _days.HorizontalAlignment = HorizontalAlignment.Center;

            var settings = FlatButton(new TextBlock { Text = "Date and time settings", FontSize = 12, Foreground = _text, TextDecorations = TextDecorations.Underline }, null);
            settings.HorizontalAlignment = HorizontalAlignment.Left;
            settings.Margin = new Thickness(14, 8, 0, 10);
            settings.Click += (s, e) =>
            {
                Close();
                TrayMirror.OpenSettingsPage("ms-settings:dateandtime");
            };

            var line = new Border { Height = 1, Background = BorderBrush, Margin = new Thickness(0, 0, 0, 8) };
            var root = new StackPanel();
            root.Children.Add(header);
            root.Children.Add(line);
            root.Children.Add(nav);
            root.Children.Add(_days);
            root.Children.Add(settings);
            Content = root;
            // Each notch of the wheel is a month, as in Windows 10.
            _days.MouseWheel += (s, e) => ShowMonth(_month.AddMonths(e.Delta > 0 ? -1 : 1));

            ShowMonth(DateTime.Today);
            ShowTime();
            _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _tick.Tick += (s, e) => ShowTime();
            _tick.Start();

            KeyDown += OnKey;
            Closing += (s, e) => _closing = true;
            Closed += (s, e) =>
            {
                _tick.Stop();
                _closedAt = DateTime.Now;
                if (_open == this) _open = null;
            };
            Deactivated += (s, e) => { if (!_closing) Close(); };
            SourceInitialized += (s, e) =>
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
            };
            Loaded += (s, e) => VolumeWindow.PlaceBesideCursor(this, _monitor, PopupWidth, ActualHeight);
            SizeToContent = SizeToContent.Height;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape: Close(); break;
                case Key.PageUp: case Key.Up: ShowMonth(_month.AddMonths(-1)); break;
                case Key.PageDown: case Key.Down: ShowMonth(_month.AddMonths(1)); break;
                case Key.Home: ShowMonth(DateTime.Today); break;
            }
        }

        private void ShowTime()
        {
            var now = DateTime.Now;
            _time.Text = now.ToString("T", CultureInfo.CurrentCulture);
            _date.Text = now.ToString("D", CultureInfo.CurrentCulture);
            // The highlighted day moves on at midnight while the calendar is open.
            if (_shownToday != DateTime.Today) ShowMonth(_month);
        }

        private DateTime _shownToday;

        private void ShowMonth(DateTime anyDay)
        {
            var culture = CultureInfo.CurrentCulture;
            var format = culture.DateTimeFormat;
            _month = new DateTime(anyDay.Year, anyDay.Month, 1);
            _shownToday = DateTime.Today;
            ((TextBlock)_monthTitle.Content).Text = _month.ToString("Y", culture);

            _days.Children.Clear();
            var firstDay = format.FirstDayOfWeek;
            for (int i = 0; i < 7; i++)
            {
                var day = (DayOfWeek)(((int)firstDay + i) % 7);
                _days.Children.Add(new TextBlock
                {
                    Text = format.GetShortestDayName(day),
                    FontSize = 12,
                    Foreground = _text,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            int lead = ((int)_month.DayOfWeek - (int)firstDay + 7) % 7;
            var start = _month.AddDays(-lead);
            for (int i = 0; i < Weeks * 7; i++)
            {
                var date = start.AddDays(i);
                bool today = date == _shownToday, inMonth = date.Month == _month.Month;
                var cell = new Border
                {
                    Width = CellSize,
                    Height = CellSize,
                    Background = today ? _accent : Brushes.Transparent,
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(2),
                    ToolTip = date.ToString("D", culture),
                    Child = new TextBlock
                    {
                        Text = date.Day.ToString(culture),
                        FontSize = 14,
                        Foreground = today ? Brushes.White : inMonth ? _text : _dim,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                if (!today)
                {
                    cell.MouseEnter += (s, e) => cell.BorderBrush = _hover;
                    cell.MouseLeave += (s, e) => cell.BorderBrush = Brushes.Transparent;
                }
                _days.Children.Add(cell);
            }
        }

        private Button Arrow(string glyph, string tip, int months)
        {
            var arrow = FlatButton(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, Foreground = _text }, tip);
            arrow.Width = 32;
            arrow.Height = 32;
            arrow.Click += (s, e) => ShowMonth(_month.AddMonths(months));
            return arrow;
        }

        private Button FlatButton(object content, string tip)
        {
            var button = new Button
            {
                Content = content,
                ToolTip = tip,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 4, 6, 4),
                Focusable = false,
                Cursor = Cursors.Hand,
            };
            // The standard button paints its own light background on hover, which looks wrong on the dark calendar.
            button.Template = FlatTemplate();
            button.MouseEnter += (s, e) => button.Background = _hover;
            button.MouseLeave += (s, e) => button.Background = Brushes.Transparent;
            return button;
        }

        private static ControlTemplate FlatTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }
    }
}
