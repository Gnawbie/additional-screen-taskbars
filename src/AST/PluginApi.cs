using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AST
{
    // Everything public in this file is the modding surface. XAML themes bind to BarViewModel and TaskItem,
    // and code plugins implement IAstStyle. See docs/MODDING.md.

    /// <summary>Implement this in a class library to provide a taskbar style written in code.</summary>
    public interface IAstStyle
    {
        /// <summary>A unique id, for example "myname.neon".</summary>
        string Id { get; }
        /// <summary>The name shown in AST's settings.</summary>
        string Name { get; }
        /// <summary>Called once for each bar. The returned element fills the bar and should follow <paramref name="bar"/>.</summary>
        FrameworkElement CreateBar(BarViewModel bar);
    }

    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action _run;
        public RelayCommand(Action run) { _run = run; }
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter) => _run();
    }

    /// <summary>One open window on the bar's screen.</summary>
    public sealed class TaskItem : Observable
    {
        private string _title;
        private bool _isActive, _isMinimized;

        internal TaskItem(IntPtr handle)
        {
            Handle = handle;
            Icon = WindowIcons.Get(handle);
            ActivateCommand = new RelayCommand(Activate);
        }

        public IntPtr Handle { get; }
        public ImageSource Icon { get; }
        public string Title { get => _title; internal set => Set(ref _title, value); }
        public bool IsActive { get => _isActive; internal set => Set(ref _isActive, value); }
        public bool IsMinimized { get => _isMinimized; internal set => Set(ref _isMinimized, value); }

        /// <summary>Switches to the window, or minimizes it if it is already the active window.</summary>
        public ICommand ActivateCommand { get; }

        public void Activate()
        {
            if (Native.GetForegroundWindow() == Handle && !Native.IsIconic(Handle))
            {
                Native.ShowWindowAsync(Handle, Native.SW_MINIMIZE);
                return;
            }
            if (Native.IsIconic(Handle)) Native.ShowWindowAsync(Handle, Native.SW_RESTORE);
            Native.SwitchToThisWindow(Handle, true);
        }

        public void Minimize() => Native.ShowWindowAsync(Handle, Native.SW_MINIMIZE);
        public void Close() => Native.PostMessage(Handle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>A button from the Windows tray, such as network or volume. Invoking it opens Windows' own flyout.</summary>
    public sealed class TrayItem
    {
        internal TrayItem(TrayEntry entry, BarViewModel bar)
        {
            Name = entry.Name;
            Glyph = entry.Glyph;
            Kind = entry.Kind;
            InvokeCommand = new RelayCommand(() => TrayMirror.Press(entry, () => bar.Monitor, () => bar.Edge));
        }

        internal TrayKind Kind { get; }

        /// <summary>The Windows settings pages offered when this button is right-clicked, as on the normal taskbar.</summary>
        internal IReadOnlyList<KeyValuePair<string, string>> SettingsPages
        {
            get
            {
                switch (Kind)
                {
                    case TrayKind.Volume:
                        return new[]
                        {
                            new KeyValuePair<string, string>("Sound settings", "ms-settings:sound"),
                            new KeyValuePair<string, string>("Volume mixer", "ms-settings:apps-volume"),
                        };
                    case TrayKind.Network:
                        return new[] { new KeyValuePair<string, string>("Network and internet settings", "ms-settings:network") };
                    case TrayKind.Notifications:
                        return new[] { new KeyValuePair<string, string>("Notification settings", "ms-settings:notifications") };
                    default:
                        return new KeyValuePair<string, string>[0];
                }
            }
        }

        /// <summary>Windows' live description, for example the current volume. Good for a tooltip.</summary>
        public string Name { get; }
        /// <summary>A character to show in the "Segoe MDL2 Assets" font.</summary>
        public string Glyph { get; }
        public ICommand InvokeCommand { get; }
    }

    /// <summary>Everything a bar's look can bind to.</summary>
    public sealed class BarViewModel : Observable
    {
        private string _time, _date;
        private string _edge;
        private double _thickness;
        private bool _showClock, _showStart;

        internal BarViewModel(MonitorInfo monitor, Action showSettings)
        {
            Monitor = monitor;
            MonitorName = monitor.Name;
            MonitorNumber = monitor.Number;
            StartCommand = new RelayCommand(OpenStart);
            SettingsCommand = new RelayCommand(showSettings);
        }

        public ObservableCollection<TaskItem> Items { get; } = new ObservableCollection<TaskItem>();
        internal MonitorInfo Monitor { get; set; }
        public string MonitorName { get; }
        public int MonitorNumber { get; }
        public string TimeText { get => _time; internal set => Set(ref _time, value); }
        public string DateText { get => _date; internal set => Set(ref _date, value); }
        public ICommand StartCommand { get; }
        public ICommand SettingsCommand { get; }
        public ObservableCollection<TrayItem> TrayItems { get; } = new ObservableCollection<TrayItem>();

        private bool _showTray;
        public bool ShowTray { get => _showTray; internal set => Set(ref _showTray, value); }

        internal void UpdateTray(IEnumerable<TrayEntry> entries)
        {
            TrayItems.Clear();
            foreach (var e in entries) TrayItems.Add(new TrayItem(e, this));
        }

        /// <summary>Top, Bottom, Left or Right.</summary>
        public string Edge
        {
            get => _edge;
            internal set
            {
                Set(ref _edge, value);
                OnLayoutChanged();
            }
        }

        public double Thickness { get => _thickness; internal set { Set(ref _thickness, value); OnLayoutChanged(); } }
        public bool ShowClock { get => _showClock; internal set => Set(ref _showClock, value); }
        public bool ShowStartButton { get => _showStart; internal set => Set(ref _showStart, value); }

        private bool _isVertical, _isHorizontal = true, _showLabels;
        public bool IsVertical { get => _isVertical; private set => Set(ref _isVertical, value); }
        public bool IsHorizontal { get => _isHorizontal; private set => Set(ref _isHorizontal, value); }

        /// <summary>False when the user wants icons only, and on narrow side bars unless the names are turned sideways.</summary>
        public bool ShowLabels { get => _showLabels; private set => Set(ref _showLabels, value); }

        private bool _iconsOnly;
        /// <summary>The user's option to hide program names. Use it to make task buttons compact.</summary>
        public bool IconsOnly
        {
            get => _iconsOnly;
            internal set { Set(ref _iconsOnly, value); OnLayoutChanged(); }
        }

        private bool _rotateLabels, _isNarrow, _verticalSideLabels;
        /// <summary>True when program names should run sideways: a side bar with the user's option turned on.</summary>
        public bool RotateLabels { get => _rotateLabels; private set => Set(ref _rotateLabels, value); }
        /// <summary>True on a side bar too thin for horizontal text, such as a clock. Themes can turn it sideways.</summary>
        public bool IsNarrow { get => _isNarrow; private set => Set(ref _isNarrow, value); }

        internal bool VerticalSideLabels
        {
            set { _verticalSideLabels = value; OnLayoutChanged(); }
        }

        private void OnLayoutChanged()
        {
            bool vertical = _edge == "Left" || _edge == "Right";
            IsVertical = vertical;
            IsHorizontal = !vertical;
            RotateLabels = vertical && _verticalSideLabels && !_iconsOnly;
            IsNarrow = vertical && _thickness < 90;
            ShowLabels = !_iconsOnly && (!vertical || RotateLabels || _thickness >= 90);
        }

        private static void OpenStart()
        {
            Native.keybd_event(Native.VK_CONTROL, 0, 0, UIntPtr.Zero);
            Native.keybd_event(Native.VK_ESCAPE, 0, 0, UIntPtr.Zero);
            Native.keybd_event(Native.VK_ESCAPE, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Native.keybd_event(Native.VK_CONTROL, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        /// <summary>Applies a new window list, keeping existing items in place so buttons don't jump around.</summary>
        internal void Update(IEnumerable<TrackedWindow> windows)
        {
            var current = windows.ToDictionary(w => w.Handle);
            for (int i = Items.Count - 1; i >= 0; i--)
                if (!current.ContainsKey(Items[i].Handle)) Items.RemoveAt(i);
            var known = new HashSet<IntPtr>(Items.Select(i => i.Handle));
            // Enumeration is newest-on-top, so reverse to add new windows at the end of the bar.
            foreach (var w in current.Values.Reverse())
                if (!known.Contains(w.Handle)) Items.Add(new TaskItem(w.Handle));
            foreach (var item in Items)
            {
                var w = current[item.Handle];
                item.Title = w.Title;
                item.IsActive = w.IsActive;
                item.IsMinimized = w.IsMinimized;
            }
        }
    }

    /// <summary>
    /// Lays out task buttons in a row or column. Items use the preferred length and shrink evenly when they
    /// run out of room. Themes can use it as an ItemsPanel with xmlns:eb="clr-namespace:AST;assembly=AST".
    /// </summary>
    public sealed class TaskPanel : Panel
    {
        public static readonly DependencyProperty IsVerticalProperty =
            DependencyProperty.Register(nameof(IsVertical), typeof(bool), typeof(TaskPanel),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));
        public static readonly DependencyProperty ItemLengthProperty =
            DependencyProperty.Register(nameof(ItemLength), typeof(double), typeof(TaskPanel),
                new FrameworkPropertyMetadata(180.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
        public static readonly DependencyProperty VerticalItemLengthProperty =
            DependencyProperty.Register(nameof(VerticalItemLength), typeof(double), typeof(TaskPanel),
                new FrameworkPropertyMetadata(36.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>Sideways names need the longer ItemLength even though the bar is vertical.</summary>
        public static readonly DependencyProperty IsRotatedProperty =
            DependencyProperty.Register(nameof(IsRotated), typeof(bool), typeof(TaskPanel),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>Buttons without names use the compact IconItemLength on either axis.</summary>
        public static readonly DependencyProperty IsIconsOnlyProperty =
            DependencyProperty.Register(nameof(IsIconsOnly), typeof(bool), typeof(TaskPanel),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));
        public static readonly DependencyProperty IconItemLengthProperty =
            DependencyProperty.Register(nameof(IconItemLength), typeof(double), typeof(TaskPanel),
                new FrameworkPropertyMetadata(44.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public bool IsIconsOnly { get => (bool)GetValue(IsIconsOnlyProperty); set => SetValue(IsIconsOnlyProperty, value); }
        public double IconItemLength { get => (double)GetValue(IconItemLengthProperty); set => SetValue(IconItemLengthProperty, value); }
        public bool IsVertical { get => (bool)GetValue(IsVerticalProperty); set => SetValue(IsVerticalProperty, value); }
        public bool IsRotated { get => (bool)GetValue(IsRotatedProperty); set => SetValue(IsRotatedProperty, value); }
        public double ItemLength { get => (double)GetValue(ItemLengthProperty); set => SetValue(ItemLengthProperty, value); }
        public double VerticalItemLength { get => (double)GetValue(VerticalItemLengthProperty); set => SetValue(VerticalItemLengthProperty, value); }

        private double LengthFor(double available)
        {
            double preferred = IsIconsOnly ? IconItemLength : IsVertical && !IsRotated ? VerticalItemLength : ItemLength;
            int n = InternalChildren.Count;
            if (n == 0 || double.IsInfinity(available)) return preferred;
            return Math.Min(preferred, available / n);
        }

        protected override Size MeasureOverride(Size available)
        {
            double length = LengthFor(IsVertical ? available.Height : available.Width);
            double cross = IsVertical ? available.Width : available.Height;
            var size = IsVertical ? new Size(cross, length) : new Size(length, cross);
            foreach (UIElement c in InternalChildren) c.Measure(size);
            double total = length * InternalChildren.Count;
            double usedCross = double.IsInfinity(cross) ? 0 : cross;
            return IsVertical ? new Size(usedCross, total) : new Size(total, usedCross);
        }

        protected override Size ArrangeOverride(Size final)
        {
            double length = LengthFor(IsVertical ? final.Height : final.Width);
            double pos = 0;
            foreach (UIElement c in InternalChildren)
            {
                c.Arrange(IsVertical ? new Rect(0, pos, final.Width, length) : new Rect(pos, 0, length, final.Height));
                pos += length;
            }
            return final;
        }
    }
}
