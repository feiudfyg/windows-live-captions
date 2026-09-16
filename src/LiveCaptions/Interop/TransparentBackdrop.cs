using System.Runtime.InteropServices;
using LiveCaptions.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LiveCaptions.Interop;

/// <summary>
/// System backdrop that paints nothing at all: the window stays in "backdrop
/// window" mode while the system backdrop itself is set to DWMSBT_NONE, so DWM
/// should composite the XAML content straight against the desktop.
/// </summary>
internal sealed class TransparentBackdrop : SystemBackdrop
{
    private readonly nint _hwnd;

    public TransparentBackdrop(nint hwnd) => _hwnd = hwnd;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);

        var none = 1; // DWMSBT_NONE
        var result = DwmSetWindowAttribute(_hwnd, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, ref none, sizeof(int));
        Log.Write($"[ui] transparent backdrop attached (DWMSBT_NONE hr=0x{result:X8})");
    }
}
