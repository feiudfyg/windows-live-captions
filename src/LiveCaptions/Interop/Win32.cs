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
