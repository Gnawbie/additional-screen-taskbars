using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace AST
{
    /// <summary>
    /// One bar on one screen. It registers with Windows as an app bar, the documented way to reserve a screen
    /// edge, so maximized windows stop beside it.
    /// </summary>
    internal sealed class BarWindow : Window
    {
        private static readonly uint TaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        private readonly uint _callbackMessage = Native.RegisterWindowMessage("AST.AppBar.Callback");
        private readonly Action _showSettings;
        private readonly DispatcherTimer _redock;
        private IntPtr _hwnd;
        private const double GripSize = 5;
        private readonly Grid _root = new Grid();
        private readonly Thumb _grip;
        private FrameworkElement _content;
        private Native.RECT _docked;
        private bool _registered, _docking, _hiddenForFullscreen;

        private const int EdgeDragThreshold = 12;
        private bool _edgeDragArmed, _edgeDragging;
        private System.Drawing.Point _edgeDragStart;

        /// <summary>Raised after the user finishes dragging the bar to a new size or edge.</summary>
        public event Action LayoutDragged;
        /// <summary>Raised when the user flips "Lock taskbars" in the bar's right-click menu.</summary>
        public event Action LockToggled;
        /// <summary>Raised when the user chooses "Pin to taskbar" or "Unpin from taskbar" for a program.</summary>
        public event Action<TaskItem> PinToggled;
        /// <summary>Tells the menu whether a program is pinned.</summary>
        public Func<TaskItem, bool> IsItemPinned { get; set; }
        /// <summary>Raised when the user flips "Hide for fullscreen programs" in the right-click menu.</summary>
        public event Action HideForFullscreenToggled;

        /// <summary>Whether bars hide for fullscreen programs. Only used to tick the menu item.</summary>
        public bool HidesForFullscreen { get; set; }
        /// <summary>Raised when the user flips "Show pinned programs" in the right-click menu.</summary>
        public event Action PinnedToggled;

        /// <summary>Whether this bar lists pinned programs. Only used to tick the menu item.</summary>
        public bool ShowsPinned { get; set; }
        /// <summary>Raised when the user flips "Show programs from all screens" in the right-click menu.</summary>
        public event Action AllScreensToggled;

        /// <summary>Whether this bar lists programs from every screen. Only used to tick the menu item.</summary>
        public bool ShowsAllScreens { get; set; }

        private bool _isLocked;
        /// <summary>A locked bar can't be moved or resized by dragging.</summary>
        public bool IsLocked
        {
            get => _isLocked;
            set
            {
                _isLocked = value;
                _grip.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        /// <summary>Raised when the user flips "Show program names" in the bar's right-click menu.</summary>
        public event Action ProgramNamesToggled;

        /// <summary>Where the bar is docked, so its Start menu can open next to it.</summary>
        internal Native.RECT DockedRect => _docked;

        public MonitorInfo Monitor { get; private set; }
        public MonitorConfig Settings { get; private set; }
        public BarViewModel ViewModel { get; }
        public string ThemeId { get; private set; }

        public BarWindow(MonitorInfo monitor, MonitorConfig settings, Action showSettings)
        {
            Monitor = monitor;
            Settings = settings;
            _showSettings = showSettings;
            ViewModel = new BarViewModel(monitor, showSettings);

            Title = "AST";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            Background = Brushes.Black;
            UseLayoutRounding = true;

            _redock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _redock.Tick += (s, e) => { _redock.Stop(); Dock(); };

            var gripLook = new FrameworkElementFactory(typeof(Border));
            gripLook.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            _grip = new Thumb { Focusable = false, Template = new ControlTemplate(typeof(Thumb)) { VisualTree = gripLook } };
            _grip.DragDelta += OnGripDragged;
            _grip.DragCompleted += OnGripReleased;
            Panel.SetZIndex(_grip, 1);
            _root.Children.Add(_grip);
            Content = _root;

            SourceInitialized += OnSourceInitialized;
            PreviewMouseRightButtonUp += OnRightClick;
            MouseLeftButtonDown += OnBarPressed;
            MouseMove += OnBarDragged;
            MouseLeftButtonUp += (s, e) => { if (_edgeDragArmed) ReleaseMouseCapture(); };
            LostMouseCapture += OnBarDragEnded;
            Closed += (s, e) => { _redock.Stop(); Unregister(); };
        }

        public void SetContent(FrameworkElement content, string themeId)
        {
            ThemeId = themeId;
            if (_content != null) _root.Children.Remove(_content);
            _content = content;
            _root.Children.Insert(0, content);
        }

        public void Apply(MonitorInfo monitor, MonitorConfig settings)
        {
            Monitor = monitor;
            ViewModel.Monitor = monitor;
            Settings = settings;
            UpdateViewModelLayout();
            if (_hwnd != IntPtr.Zero) Dock();
        }

        public void UpdateViewModelLayout()
        {
            ViewModel.Thickness = Settings.EffectiveThickness;
            ViewModel.Edge = Settings.EdgeValue.ToString();
            PlaceGrip();
        }

        /// <summary>The grip sits on the edge facing the middle of the screen, above any theme, so every look can be resized.</summary>
        private void PlaceGrip()
        {
            bool vertical = Settings.IsVertical;
            _grip.Width = vertical ? GripSize : double.NaN;
            _grip.Height = vertical ? double.NaN : GripSize;
            _grip.Cursor = vertical ? Cursors.SizeWE : Cursors.SizeNS;
            _grip.HorizontalAlignment = Settings.EdgeValue == BarEdge.Left ? HorizontalAlignment.Right
                : Settings.EdgeValue == BarEdge.Right ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            _grip.VerticalAlignment = Settings.EdgeValue == BarEdge.Top ? VerticalAlignment.Bottom
                : Settings.EdgeValue == BarEdge.Bottom ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        }

        // While dragging only this window changes size. Windows re-fits other programs once, on release.
        private void OnGripDragged(object sender, DragDeltaEventArgs e)
        {
            var cursor = Forms.Cursor.Position;
            int pixels;
            switch (Settings.EdgeValue)
            {
                case BarEdge.Left: pixels = cursor.X - _docked.Left; break;
                case BarEdge.Right: pixels = _docked.Right - cursor.X; break;
                case BarEdge.Top: pixels = cursor.Y - _docked.Top; break;
                default: pixels = _docked.Bottom - cursor.Y; break;
            }
            double scale = Monitor.DpiScale;
            int thickness = Math.Max(Config.MinThickness, Math.Min(Config.MaxThickness, (int)Math.Round(pixels / scale)));
            Settings.Thickness = thickness;
            ViewModel.Thickness = thickness;

            int size = (int)Math.Round(thickness * scale);
            var r = _docked;
            switch (Settings.EdgeValue)
            {
                case BarEdge.Left: r.Right = r.Left + size; break;
                case BarEdge.Right: r.Left = r.Right - size; break;
                case BarEdge.Top: r.Bottom = r.Top + size; break;
                default: r.Top = r.Bottom - size; break;
            }
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOACTIVATE);
        }

        private void OnGripReleased(object sender, DragCompletedEventArgs e)
        {
            Dock();
            LayoutDragged?.Invoke();
        }

        // Buttons keep this event to themselves, so only presses on an empty part of the bar start a move.
        private void OnBarPressed(object sender, MouseButtonEventArgs e)
        {
            if (IsLocked) return;
            _edgeDragStart = Forms.Cursor.Position;
            _edgeDragArmed = true;
            _edgeDragging = false;
            CaptureMouse();
            e.Handled = true;
        }

        private void OnBarDragged(object sender, MouseEventArgs e)
        {
            if (!_edgeDragArmed) return;
            var cursor = Forms.Cursor.Position;
            if (!_edgeDragging)
            {
                int threshold = (int)(EdgeDragThreshold * Monitor.DpiScale);
                if (Math.Abs(cursor.X - _edgeDragStart.X) < threshold && Math.Abs(cursor.Y - _edgeDragStart.Y) < threshold) return;
                _edgeDragging = true;
            }
            var edge = NearestEdge(cursor);
            if (edge == Settings.EdgeValue) return;
            Settings.EdgeValue = edge;
            Apply(Monitor, Settings);
        }

        private void OnBarDragEnded(object sender, MouseEventArgs e)
        {
            if (!_edgeDragArmed) return;
            _edgeDragArmed = false;
            if (_edgeDragging) LayoutDragged?.Invoke();
            _edgeDragging = false;
        }

        /// <summary>
        /// Picks the closest edge of this bar's own screen, measured as a share of the screen's size so that
        /// the screen divides evenly along its diagonals. A cursor on another screen counts as past the near edge.
        /// </summary>
        private BarEdge NearestEdge(System.Drawing.Point cursor)
        {
            var b = Monitor.Bounds;
            double x = Math.Max(0, Math.Min(1, (cursor.X - b.Left) / (double)b.Width));
            double y = Math.Max(0, Math.Min(1, (cursor.Y - b.Top) / (double)b.Height));
            double smallest = Math.Min(Math.Min(x, 1 - x), Math.Min(y, 1 - y));
            if (smallest == x) return BarEdge.Left;
            if (smallest == 1 - x) return BarEdge.Right;
            return smallest == y ? BarEdge.Top : BarEdge.Bottom;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            Register();
            Dock();
            // WPF may adjust the window while showing it for the first time.
            Dispatcher.BeginInvoke(new Action(Dock), DispatcherPriority.Loaded);
        }

        private Native.APPBARDATA Data() => new Native.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf(typeof(Native.APPBARDATA)),
            hWnd = _hwnd,
            uCallbackMessage = _callbackMessage,
        };

        private void Register()
        {
            var d = Data();
            _registered = Native.SHAppBarMessage(Native.ABM_NEW, ref d) != UIntPtr.Zero;
            if (!_registered) Log.Write("Windows refused the app bar registration for " + Monitor.Name);
        }

        private void Unregister()
        {
            if (!_registered) return;
            var d = Data();
            Native.SHAppBarMessage(Native.ABM_REMOVE, ref d);
            _registered = false;
        }

        private uint WinEdge()
        {
            switch (Settings.EdgeValue)
            {
                case BarEdge.Left: return Native.ABE_LEFT;
                case BarEdge.Top: return Native.ABE_TOP;
                case BarEdge.Right: return Native.ABE_RIGHT;
                default: return Native.ABE_BOTTOM;
            }
        }

        private void Dock()
        {
            if (_docking || _hwnd == IntPtr.Zero || !_registered) return;
            _docking = true;
            try
            {
                var d = Data();
                d.uEdge = WinEdge();
                int size = (int)Math.Round(Settings.EffectiveThickness * Monitor.DpiScale);
                d.rc = Monitor.Bounds;
                Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref d);
                // The shell may move the proposed edge inward past other bars. Keep our thickness from there.
                switch (d.uEdge)
                {
                    case Native.ABE_LEFT: d.rc.Right = d.rc.Left + size; break;
                    case Native.ABE_RIGHT: d.rc.Left = d.rc.Right - size; break;
                    case Native.ABE_TOP: d.rc.Bottom = d.rc.Top + size; break;
                    default: d.rc.Top = d.rc.Bottom - size; break;
                }
                Native.SHAppBarMessage(Native.ABM_SETPOS, ref d);
                _docked = d.rc;
                if (!Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, d.rc.Left, d.rc.Top, d.rc.Width, d.rc.Height, Native.SWP_NOACTIVATE))
                    Log.Write("Could not position the bar on " + Monitor.Name + ": error " + Marshal.GetLastWin32Error());
            }
            finally { _docking = false; }
        }

        /// <summary>
        /// Stays out of the way of fullscreen apps by hiding completely, even when that app loses focus.
        /// The shell's own notification is deliberately not used for this because it can get stuck.
        /// </summary>
        public void SetFullscreenHidden(bool hide)
        {
            if (hide == _hiddenForFullscreen || _hwnd == IntPtr.Zero) return;
            _hiddenForFullscreen = hide;
            if (hide) Native.ShowWindow(_hwnd, Native.SW_HIDE);
            else
            {
                Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                // WPF doesn't know the window was hidden, so it would only repaint what has changed since.
                _root.InvalidateVisual();
                Native.RedrawWindow(_hwnd, IntPtr.Zero, IntPtr.Zero, Native.RDW_INVALIDATE | Native.RDW_ALLCHILDREN | Native.RDW_UPDATENOW);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if ((uint)msg == _callbackMessage)
            {
                if (wParam.ToInt32() == Native.ABN_POSCHANGED) QueueDock();
                handled = true;
                return IntPtr.Zero;
            }
            if ((uint)msg == TaskbarCreated)
            {
                // Explorer restarted and forgot every app bar, so register again.
                _registered = false;
                Register();
                Dock();
                return IntPtr.Zero;
            }
            switch ((uint)msg)
            {
                case Native.WM_MOUSEACTIVATE:
                    handled = true;
                    return new IntPtr(Native.MA_NOACTIVATE);
                case Native.WM_ACTIVATE:
                {
                    var d = Data();
                    Native.SHAppBarMessage(Native.ABM_ACTIVATE, ref d);
                    break;
                }
                case Native.WM_WINDOWPOSCHANGED:
                {
                    var d = Data();
                    Native.SHAppBarMessage(Native.ABM_WINDOWPOSCHANGED, ref d);
                    break;
                }
                case Native.WM_DPICHANGED:
                    QueueDock();
                    break;
            }
            return IntPtr.Zero;
        }

        private void QueueDock()
        {
            _redock.Stop();
            _redock.Start();
        }

        // Handled here rather than in themes, so every theme and plugin gets working menus for free.
        private void OnRightClick(object sender, MouseButtonEventArgs e)
        {
            if (IsOnStartButton(e.OriginalSource as DependencyObject))
            {
                ViewModel.OpenStartMenuOptions();
                e.Handled = true;
                return;
            }

            var el = e.OriginalSource as FrameworkElement;
            if ((el?.DataContext as TrayItem)?.Kind == TrayKind.Volume)
            {
                new VolumeWindow(Monitor, ThemeId == "classic").Show();
                e.Handled = true;
                return;
            }

            var menu = new ContextMenu();
            if (el?.DataContext is TaskItem item)
            {
                AddProgramItems(menu, item);
                menu.Items.Add(new Separator());
            }
            var pages = (el?.DataContext as TrayItem)?.SettingsPages;
            if (pages != null && pages.Count > 0)
            {
                // A tray button gets its own short menu, as on the normal taskbar.
                foreach (var page in pages)
                {
                    string uri = page.Value;
                    menu.Items.Add(Item(page.Key, () => TrayMirror.OpenSettingsPage(uri)));
                }
            }
            else AddBarItems(menu);
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            // Without focus the menu would not close when clicking elsewhere.
            Native.SetForegroundWindow(_hwnd);
            menu.IsOpen = true;
            e.Handled = true;
        }

        // The options the Windows taskbar offers for a program, apart from its jump list of recent files.
        /// <summary>Finds the Start button by its command, so this works in every theme and plugin that uses it.</summary>
        private bool IsOnStartButton(DependencyObject element)
        {
            for (var e = element; e != null; e = VisualTreeHelper.GetParent(e))
            {
                if (e is ButtonBase button && button.Command == ViewModel.StartCommand) return true;
                if (!(e is Visual)) return false;
            }
            return false;
        }

        private void AddProgramItems(ContextMenu menu, TaskItem item)
        {
            menu.Items.Add(Item(item.IsPinned ? "Open" : (item.ProgramName ?? item.Title), item.StartProgram));
            if (item.ExePath != null)
            {
                menu.Items.Add(Item("Run as administrator", () => PinnedPrograms.RunAsAdministrator(item.ExePath)));
                menu.Items.Add(Item("Open file location", () => PinnedPrograms.OpenFileLocation(item.ExePath)));
            }
            if (item.AppId != null)
                menu.Items.Add(Item(IsItemPinned(item) ? "Unpin from taskbar" : "Pin to taskbar", () => PinToggled?.Invoke(item)));
            if (item.IsPinned) return;
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(item.IsActive ? "Minimize" : "Switch to", () => { if (item.IsActive) item.Minimize(); else item.Activate(); }));
            menu.Items.Add(Item("Close window", item.Close));
        }

        private void AddBarItems(ContextMenu menu)
        {
            // The Windows taskbar's own options, then AST's.
            menu.Items.Add(Item("Show desktop", TrayMirror.ToggleDesktop));
            menu.Items.Add(Item("Task Manager", () => TrayMirror.OpenSettingsPage("taskmgr.exe")));
            menu.Items.Add(Item("Taskbar settings", () => TrayMirror.OpenSettingsPage("ms-settings:taskbar")));
            menu.Items.Add(new Separator());
            var names = new MenuItem { Header = "Show program names", IsCheckable = true, IsChecked = !ViewModel.IconsOnly };
            names.Click += (s, a) => ProgramNamesToggled?.Invoke();
            menu.Items.Add(names);
            var allScreens = new MenuItem { Header = "Show programs from all screens", IsCheckable = true, IsChecked = ShowsAllScreens };
            allScreens.Click += (s, a) => AllScreensToggled?.Invoke();
            menu.Items.Add(allScreens);
            var pinned = new MenuItem { Header = "Show pinned programs", IsCheckable = true, IsChecked = ShowsPinned };
            pinned.Click += (s, a) => PinnedToggled?.Invoke();
            menu.Items.Add(pinned);
            var hide = new MenuItem { Header = "Hide for fullscreen programs", IsCheckable = true, IsChecked = HidesForFullscreen };
            hide.Click += (s, a) => HideForFullscreenToggled?.Invoke();
            menu.Items.Add(hide);
            var locked = new MenuItem { Header = "Lock taskbars", IsCheckable = true, IsChecked = IsLocked };
            locked.Click += (s, a) => LockToggled?.Invoke();
            menu.Items.Add(locked);
            menu.Items.Add(Item("Options...", _showSettings));
        }

        private static MenuItem Item(string text, Action action)
        {
            var i = new MenuItem { Header = text };
            i.Click += (s, e) => action();
            return i;
        }
    }
}
