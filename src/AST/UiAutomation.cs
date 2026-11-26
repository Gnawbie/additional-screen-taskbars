using System;
using System.Runtime.InteropServices;

namespace AST
{
    /// <summary>
    /// Windows' own UI Automation interface, used instead of .NET's System.Windows.Automation. For the parts of the
    /// Windows taskbar that are classic windows, the .NET version builds stand-ins out of .NET objects and hands them to
    /// Windows, which never lets go of them. TrayMirror's scans every 3 seconds piled up about 2 a second, taking AST
    /// past 1 GB over a few days. Through this interface Windows uses its own stand-ins and frees them normally.
    /// Only the members AST uses are spelled out; the placeholders keep the rest in their places in the interface.
    /// </summary>
    internal static class Uia
    {
        public const int InvokePatternId = 10000;

        private static readonly Lazy<IUIAutomation> Instance = new Lazy<IUIAutomation>(() => (IUIAutomation)new CUIAutomation());

        public static IUIAutomationElement FromHandle(IntPtr hwnd) => Instance.Value.ElementFromHandle(hwnd);

        public static IUIAutomationTreeWalker RawViewWalker => Instance.Value.RawViewWalker;

        public static void Invoke(IUIAutomationElement element) =>
            ((IUIAutomationInvokePattern)element.GetCurrentPattern(InvokePatternId)).Invoke();
    }

    [ComImport, Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")]
    internal class CUIAutomation { }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UiaRect
    {
        public int Left, Top, Right, Bottom;
    }

    [ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        IUIAutomationElement ElementFromHandle(IntPtr hwnd);
        void ElementFromPoint();
        void GetFocusedElement();
        void GetRootElementBuildCache();
        void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache();
        void GetFocusedElementBuildCache();
        void CreateTreeWalker();
        void ControlViewWalker();
        void ContentViewWalker();
        IUIAutomationTreeWalker RawViewWalker { get; }
    }

    [ComImport, Guid("4042C624-389C-4AFC-A630-9DF854A541FC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationTreeWalker
    {
        void GetParentElement();
        IUIAutomationElement GetFirstChildElement(IUIAutomationElement element);
        void GetLastChildElement();
        IUIAutomationElement GetNextSiblingElement(IUIAutomationElement element);
    }

    [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        void FindFirst();
        void FindAll();
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        void GetCurrentPropertyValue();
        void GetCurrentPropertyValueEx();
        void GetCachedPropertyValue();
        void GetCachedPropertyValueEx();
        void GetCurrentPatternAs();
        void GetCachedPatternAs();
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetCurrentPattern(int patternId);
        void GetCachedPattern();
        void GetCachedParent();
        void GetCachedChildren();
        int CurrentProcessId { get; }
        int CurrentControlType { get; }
        string CurrentLocalizedControlType { [return: MarshalAs(UnmanagedType.BStr)] get; }
        string CurrentName { [return: MarshalAs(UnmanagedType.BStr)] get; }
        string CurrentAcceleratorKey { [return: MarshalAs(UnmanagedType.BStr)] get; }
        string CurrentAccessKey { [return: MarshalAs(UnmanagedType.BStr)] get; }
        int CurrentHasKeyboardFocus { get; }
        int CurrentIsKeyboardFocusable { get; }
        int CurrentIsEnabled { get; }
        string CurrentAutomationId { [return: MarshalAs(UnmanagedType.BStr)] get; }
        string CurrentClassName { [return: MarshalAs(UnmanagedType.BStr)] get; }
        string CurrentHelpText { [return: MarshalAs(UnmanagedType.BStr)] get; }
        int CurrentCulture { get; }
        int CurrentIsControlElement { get; }
        int CurrentIsContentElement { get; }
        int CurrentIsPassword { get; }
        IntPtr CurrentNativeWindowHandle { get; }
        string CurrentItemType { [return: MarshalAs(UnmanagedType.BStr)] get; }
        int CurrentIsOffscreen { get; }
        int CurrentOrientation { get; }
        string CurrentFrameworkId { [return: MarshalAs(UnmanagedType.BStr)] get; }
        int CurrentIsRequiredForForm { get; }
        string CurrentItemStatus { [return: MarshalAs(UnmanagedType.BStr)] get; }
        UiaRect CurrentBoundingRectangle { get; }
    }

    [ComImport, Guid("FB377FBE-8EA6-46D5-9C73-6499642D3059"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationInvokePattern
    {
        void Invoke();
    }
}
