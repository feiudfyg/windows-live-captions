using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LiveCaptions.Models;

/// <summary>
/// UI-facing state (fonts, panel colour) shared via XAML resources so that
/// DataTemplates can bind to it without a full MVVM stack.
/// </summary>
public sealed class UiState : INotifyPropertyChanged
{
    /// <summary>The instance currently bound to the main window (used by caption items for visibility logic).</summary>
    public static UiState? Current { get; private set; }

    private double _originalFontSize = 15;
    private double _translationFontSize = 22;
    private bool _showOriginal = true;
    private double _panelOpacity = 0.72;
    private SolidColorBrush _panelBrush = null!;
    private string _statusText = "正在初始化…";
    private bool _isListening;

    public UiState()
    {
        Current = this;
        _panelBrush = CreatePanelBrush(_panelOpacity);
    }

    public double OriginalFontSize
    {
        get => _originalFontSize;
        set => Set(ref _originalFontSize, value);
    }

    public double TranslationFontSize
    {
        get => _translationFontSize;
        set => Set(ref _translationFontSize, value);
    }

    public bool ShowOriginal
    {
        get => _showOriginal;
        set => Set(ref _showOriginal, value);
    }

    public double PanelOpacity
    {
        get => _panelOpacity;
        set
        {
            if (Set(ref _panelOpacity, value))
            {
                PanelBrush = CreatePanelBrush(value);
            }
        }
    }

    public SolidColorBrush PanelBrush
    {
        get => _panelBrush;
        private set => Set(ref _panelBrush, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public bool IsListening
    {
        get => _isListening;
        set => Set(ref _isListening, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static SolidColorBrush CreatePanelBrush(double opacity)
    {
        var alpha = (byte)Math.Clamp(opacity * 255.0, 0, 255);
        return new SolidColorBrush(Color.FromArgb(alpha, 0x0E, 0x0F, 0x13));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
