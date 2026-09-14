using LiveCaptions.Models;

namespace LiveCaptions.Services;

/// <summary>
/// Machine-translation backend (local GGUF via LLamaSharp, or an
/// OpenAI-compatible HTTP endpoint such as LM Studio).
/// </summary>
public interface ITranslator : IDisposable
{
    bool IsLoaded { get; }

    /// <summary>Compute backend description shown in the UI.</summary>
    string Backend { get; }

    string ModelName { get; }

    Task LoadAsync(CancellationToken ct = default);

    Task<string> TranslateAsync(string text, LanguageOption target, Action<string>? onToken, CancellationToken ct = default);
}
