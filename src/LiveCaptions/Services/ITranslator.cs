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

    /// <param name="context">Previous subtitle, used only to keep names and
    /// pronouns consistent. Never translated.</param>
    Task<string> TranslateAsync(string text, LanguageOption target, Action<string>? onToken, string? context = null, CancellationToken ct = default);
}
