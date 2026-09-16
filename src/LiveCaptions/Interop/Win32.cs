using System.Runtime.InteropServices;
using WinRT.Interop;

namespace LiveCaptions.Interop;

/// <summary>
/// Thin Win32 helpers for tuning the overlay window style.
/// </summary>
internal static class Win32
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_TRANSPARENT = 0x00000020;

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
}
