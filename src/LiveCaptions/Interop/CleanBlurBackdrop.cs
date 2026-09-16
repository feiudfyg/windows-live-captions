using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LiveCaptions.Interop;

/// <summary>
/// Live blur of everything behind the window for the "高斯模糊" panel mode.
///
/// Uses the stock acrylic controller with zero tint and zero luminosity, which
/// keeps the backdrop blur but removes acrylic's milky tint and noise layer.
/// </summary>
internal sealed class CleanBlurBackdrop : SystemBackdrop, IDisposable
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        _configuration = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Dark,
        };

        _controller = new DesktopAcrylicController
        {
            TintOpacity = 0.0f,
            LuminosityOpacity = 0.0f,
            TintColor = Color.FromArgb(0, 0x0E, 0x0F, 0x13),
            FallbackColor = Color.FromArgb(0xCC, 0x0E, 0x0F, 0x13),
        };

        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_configuration);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop connectedTarget)
    {
        base.OnTargetDisconnected(connectedTarget);
        _controller?.RemoveSystemBackdropTarget(connectedTarget);
        Dispose();
    }

    public void Dispose()
    {
        _controller?.Dispose();
        _controller = null;
        _configuration = null;
    }
}
