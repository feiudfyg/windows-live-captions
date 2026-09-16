using System.Runtime.InteropServices;
using LiveCaptions.Services;

namespace LiveCaptions.Interop;

/// <summary>
/// Makes a window background truly transparent (no blur, no tint) through the
/// legacy accent policy API.
///
/// WinUI only offers backdrops that paint something; when
/// <c>Window.SystemBackdrop</c> is null DWM fills the client area with the
/// opaque fallback colour (black in dark theme), which is why a translucent
/// panel over "no backdrop" still looks solid black.
/// </summary>
internal static class WindowTransparency
{
    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableTransparentGradient = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public nint Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowCompositionAttribute(nint hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    /// <summary>
    /// Window background becomes fully transparent; only XAML content is drawn.
    /// WinUI ignores the accent state on its own, so the DWM frame extension
    /// (the classic "sheet of glass") does the real work here.
    /// </summary>
    public static bool EnableSimple(nint hwnd)
    {
        ExtendFrame(hwnd, true);
        return Apply(hwnd, AccentEnableTransparentGradient, 0);
    }

    /// <summary>
    /// Extends the client area over the window frame. Without this, backdrop
    /// windows keep a few pixels of DWM frame around the XAML content, which
    /// shows up as a light band outside the panel (visible in the acrylic and
    /// blur modes).
    /// </summary>
    public static bool ExtendFrame(nint hwnd, bool extend)
    {
        var margins = new Margins
        {
            Left = extend ? -1 : 0,
            Right = extend ? -1 : 0,
            Top = extend ? -1 : 0,
            Bottom = extend ? -1 : 0,
        };

        var result = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        Log.Write($"[ui] dwm frame {(extend ? "extended" : "reset")}: hr=0x{result:X8}");
        return result == 0;
    }

    /// <summary>Drops the legacy accent policy so system backdrops work again.</summary>
    public static bool DisableAccent(nint hwnd) => Apply(hwnd, AccentDisabled, 0);

    private static bool Apply(nint hwnd, int state, int gradientColor)
    {
        var policy = new AccentPolicy
        {
            AccentState = state,
            AccentFlags = 2,
            GradientColor = gradientColor,
            AnimationId = 0,
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = pointer,
                SizeOfData = size,
            };

            var result = SetWindowCompositionAttribute(hwnd, ref data);
            Log.Write($"[ui] accent policy {state} {(result != 0 ? "applied" : "failed")}");
            return result != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }
}
