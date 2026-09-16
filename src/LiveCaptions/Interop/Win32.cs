using System.Runtime.InteropServices;
using LiveCaptions.Services;
using WinRT.Interop;

namespace LiveCaptions.Interop;

/// <summary>
/// Thin Win32 helpers for tuning the overlay window style.
/// </summary>
internal static class Win32
{
    private const int GWL_EXSTYLE = -20;
    private const int GWLP_WNDPROC = -4;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_TRANSPARENT = 0x00000020;

    public delegate nint WindowProc(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(nint previous, nint hWnd, uint message, nint wParam, nint lParam);

    /// <summary>Registers a system-wide hotkey (e.g. click-through escape hatch).</summary>
    public static bool RegisterToggleHotKey(nint hWnd, int id, uint modifiers, uint virtualKey)
        => RegisterHotKey(hWnd, id, modifiers | ModNoRepeat, virtualKey);

    public static void UnregisterToggleHotKey(nint hWnd, int id) => UnregisterHotKey(hWnd, id);

    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point32 point);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint LoadLibraryEx(string path, nint fileHandle, uint flags);

    private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

    /// <summary>
    /// Loads a native library so that its own directory is searched before the
    /// application directory (needed to load a self-contained CUDA runtime while
    /// the CPU runtime of the same name sits next to the executable).
    /// </summary>
    public static nint LoadLibraryFromOwnDirectory(string path)
        => LoadLibraryEx(path, 0, LOAD_WITH_ALTERED_SEARCH_PATH);

    [DllImport("kernel32.dll", EntryPoint = "SetDllDirectoryW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string path);

    /// <summary>
    /// Adds a directory to the process-wide DLL search order. Needed because
    /// onnxruntime loads cuDNN itself at runtime (no implicit dependency walk),
    /// so an altered-search-path load is not enough.
    /// </summary>
    public static bool SetDllSearchDirectory(string path) => SetDllDirectory(path);

    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMNCRP_ENABLED = 2;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    private const int PanelColourRef = 0x00130F0E; // COLORREF of the panel colour 0x0E0F13

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Fixes the DWM-drawn window frame that was showing up as a white border
    /// around the panel in the acrylic/blur modes:
    /// - non-client rendering was off for this borderless window, so the side
    ///   frame bands were painted white;
    /// - the top caption strip used the light-theme caption colour;
    /// - a 1px border line was drawn around everything.
    /// </summary>
    public static void PrepareWindowFrame(nint hwnd)
    {
        var enabled = DWMNCRP_ENABLED;
        DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref enabled, sizeof(int));

        var dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        var caption = PanelColourRef;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));

        var none = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));

        Log.Write("[ui] frame: NC rendering on, dark caption, border removed");
    }

    private const int HWND_TOPMOST = -1;
    private const int HWND_NOTOPMOST = -2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// Forces the window above all others. WinUI's <c>OverlappedPresenter.IsAlwaysOnTop</c>
    /// does not reliably set the topmost bit (observed: DWM style stays 0x180), so the
    /// window calls this explicitly instead.
    /// </summary>
    public static bool SetTopMost(nint hwnd, bool topMost)
    {
        var result = SetWindowPos(
            hwnd,
            topMost ? HWND_TOPMOST : HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        return result;
    }

    public static int GetExtendedStyle(nint hwnd) => (int)GetLong(hwnd, GWL_EXSTYLE);

    /// <summary>
    /// Physical (screen) cursor position. Pointer event positions are in logical
    /// units and window-relative, which breaks drag maths on scaled displays.
    /// </summary>
    public static (int X, int Y) GetCursorPosition()
    {
        GetCursorPos(out var point);
        return (point.X, point.Y);
    }

    public static nint GetHwnd(Microsoft.UI.Xaml.Window window) => WindowNative.GetWindowHandle(window);

    private static long GetLong(nint hWnd, int index)
        => IntPtr.Size == 8 ? (long)GetWindowLongPtr64(hWnd, index) : GetWindowLong32(hWnd, index);

    private static void SetLong(nint hWnd, int index, long value)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hWnd, index, (nint)value);
        }
        else
        {
            SetWindowLong32(hWnd, index, (int)value);
        }
    }

    /// <summary>Hides the window from Alt+Tab and the task switcher.</summary>
    public static void HideFromAltTab(nint hWnd)
    {
        SetLong(hWnd, GWL_EXSTYLE, GetLong(hWnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
    }

    /// <summary>Toggles mouse pass-through (click-through) for the window.</summary>
    public static void SetClickThrough(nint hWnd, bool enabled)
    {
        var ex = GetLong(hWnd, GWL_EXSTYLE);
        ex = enabled ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetLong(hWnd, GWL_EXSTYLE, ex);
    }

    /// <summary>Replaces the window procedure so WM_HOTKEY can be handled.</summary>
    public static nint SetWindowProc(nint hWnd, WindowProc proc)
    {
        var previous = (nint)GetLong(hWnd, GWLP_WNDPROC);
        SetLong(hWnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(proc));
        return previous;
    }

    public static nint CallPreviousWindowProc(nint previous, nint hWnd, uint message, nint wParam, nint lParam)
        => CallWindowProc(previous, hWnd, message, wParam, lParam);
}
