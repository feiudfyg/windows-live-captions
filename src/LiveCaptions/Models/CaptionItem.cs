using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LiveCaptions.Models;

/// <summary>
/// One caption line: the recognised source text plus its translation.
/// </summary>
public sealed class CaptionItem : INotifyPropertyChanged
{
    private string _original = "";
    private string _translation = "";
    private bool _isPartial = true;
    private bool _isError;

    public int Id { get; init; }
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;

    public string Original
    {
        get => _original;
        set { if (Set(ref _original, value)) Raise(nameof(OriginalVisibility)); }
    }

    public string Translation
    {
        get => _translation;
        set { if (Set(ref _translation, value)) Raise(nameof(TranslationVisibility)); }
    }

    public bool IsPartial
    {
        get => _isPartial;
        set { if (Set(ref _isPartial, value)) Raise(nameof(OriginalOpacity), nameof(TranslationOpacity)); }
    }

    public bool IsError
    {
        get => _isError;
        set => Set(ref _isError, value);
    }

    public double OriginalOpacity => IsPartial ? 0.75 : 1.0;
    public double TranslationOpacity => IsPartial ? 0.8 : 1.0;

    public Microsoft.UI.Xaml.Visibility OriginalVisibility =>
        !string.IsNullOrEmpty(Original) && (UiState.Current?.ShowOriginal ?? true)
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility TranslationVisibility =>
        string.IsNullOrEmpty(Translation) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(params string?[] names)
    {
        foreach (var name in names)
        {
            if (name is null) continue;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
