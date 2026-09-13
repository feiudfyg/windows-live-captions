using System.Text;
using LiveCaptions.Models;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace LiveCaptions.Services.Asr;

/// <summary>
/// Whisper (whisper.cpp) ASR backend. Uses CUDA when available and falls back
/// to the CPU runtime shipped with Whisper.net.
/// </summary>
public sealed class WhisperAsrEngine : IAsrEngine
{
    private readonly string _modelPath;
    private readonly bool _useCuda;

    private WhisperFactory? _factory;

    public WhisperAsrEngine(string modelPath, bool useCuda)
    {
        _modelPath = modelPath;
        _useCuda = useCuda;
    }

    public string Name => "Whisper";
    public string Backend { get; private set; } = "未加载";
    public bool IsLoaded => _factory is not null;

    public Task LoadAsync(CancellationToken ct = default)
    {
        if (IsLoaded) return Task.CompletedTask;

        try
        {
            _factory = WhisperFactory.FromPath(_modelPath, new WhisperFactoryOptions
            {
                UseGpu = _useCuda,
                UseFlashAttention = _useCuda,
            });
        }
        catch when (_useCuda)
        {
            // CUDA runtime missing or driver mismatch: fall back to CPU.
            _factory = WhisperFactory.FromPath(_modelPath, new WhisperFactoryOptions { UseGpu = false });
        }

        Backend = RuntimeOptions.LoadedLibrary?.ToString() ?? (_useCuda ? "GPU" : "CPU");
        return Task.CompletedTask;
    }

    public async Task<string> TranscribeAsync(float[] samples, LanguageOption? language, string? context, CancellationToken ct = default)
    {
        if (_factory is null) throw new InvalidOperationException("ASR 模型尚未加载");

        var builder = _factory.CreateBuilder()
            .WithThreads(Math.Min(8, Environment.ProcessorCount))
            .WithTemperature(0f)
            .WithNoSpeechThreshold(0.6f)
            .WithNoContext();

        if (!string.IsNullOrWhiteSpace(context))
        {
            builder = builder.WithPrompt(context);
        }

        builder = builder.WithLanguage(language is null || language.Code.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? "auto"
            : language.Code);

        using var processor = builder.Build();

        var sb = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples.AsMemory(), ct).ConfigureAwait(false))
        {
            var text = segment.Text?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                sb.Append(text).Append(' ');
            }
        }

        return sb.ToString().Trim();
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
    }
}
