using SherpaOnnx;

namespace LiveCaptions.Services.Asr;

/// <summary>
/// sherpa-onnx offline ASR. Supports two model families:
///  - Zipformer transducer (encoder/decoder/joiner, e.g. ja-reazonspeech)
///  - Cohere Transcribe (encoder/decoder, 14 languages, needs an explicit language)
/// Both decode with greedy search and cannot fall into the repetition loops of
/// autoregressive speech-LLMs. Inference runs on CPU with real-time factors well
/// below 0.1.
/// </summary>
public sealed class SherpaAsrEngine : IAsrEngine
{
    private readonly string _modelDirectory;
    private readonly string? _defaultLanguage;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private OfflineRecognizer? _recognizer;
    private string? _recognizerLanguage;
    private bool _isCohere;

    public SherpaAsrEngine(string modelDirectory, string? defaultLanguage = null)
    {
        _modelDirectory = modelDirectory;
        _defaultLanguage = defaultLanguage;
    }

    public string Name => _isCohere ? "Cohere Transcribe" : "Zipformer";
    public string Backend { get; private set; } = "未加载";
    public bool IsLoaded => _recognizer is not null;

    public Task LoadAsync(CancellationToken ct = default)
    {
        if (IsLoaded) return Task.CompletedTask;

        var encoder = FindModelFile("encoder") ?? throw new FileNotFoundException($"未找到 encoder 模型: {_modelDirectory}");
        var decoder = FindModelFile("decoder") ?? throw new FileNotFoundException($"未找到 decoder 模型: {_modelDirectory}");
        _isCohere = FindModelFile("joiner") is null;

        EnsureRecognizer(encoder, decoder, _defaultLanguage ?? "en");
        return Task.CompletedTask;
    }

    private void EnsureRecognizer(string encoder, string decoder, string language)
    {
        if (_recognizer is not null && _recognizerLanguage == language) return;

        _recognizer?.Dispose();
        _recognizer = null;

        var tokens = Path.Combine(_modelDirectory, "tokens.txt");
        if (!File.Exists(tokens)) throw new FileNotFoundException($"未找到 tokens.txt: {_modelDirectory}");

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = AudioCaptureService.TargetSampleRate;
        config.FeatConfig.FeatureDim = 80;

        if (_isCohere)
        {
            config.ModelConfig.CohereTranscribe.Encoder = encoder;
            config.ModelConfig.CohereTranscribe.Decoder = decoder;
            config.ModelConfig.CohereTranscribe.Language = language;
            config.ModelConfig.CohereTranscribe.UsePunct = 1;
            config.ModelConfig.CohereTranscribe.UseItn = 1;
        }
        else
        {
            var joiner = FindModelFile("joiner") ?? throw new FileNotFoundException($"未找到 joiner 模型: {_modelDirectory}");
            config.ModelConfig.Transducer.Encoder = encoder;
            config.ModelConfig.Transducer.Decoder = decoder;
            config.ModelConfig.Transducer.Joiner = joiner;
        }

        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";

        _recognizer = new OfflineRecognizer(config);
        _recognizerLanguage = language;
        Backend = $"sherpa-onnx CPU ({(_isCohere ? "cohere" : "zipformer")}, {Path.GetFileNameWithoutExtension(encoder)})";
    }

    /// <summary>Prefer the int8 quantised variant when the archive ships both.</summary>
    private string? FindModelFile(string kind)
    {
        if (!Directory.Exists(_modelDirectory)) return null;

        return Directory.EnumerateFiles(_modelDirectory, $"{kind}*.onnx")
            .Where(f => Path.GetFileName(f).StartsWith(kind, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Contains(".int8.", StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f.Length)
            .FirstOrDefault();
    }

    public async Task<string> TranscribeAsync(float[] samples, Models.LanguageOption? language, string? context, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_recognizer is null) throw new InvalidOperationException("ASR 模型尚未加载");

            if (_isCohere)
            {
                // Cohere Transcribe needs an explicit language - it is part of the
                // recognizer configuration, so switch (recreate) when it changes.
                var code = language is null || language.Code.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? _defaultLanguage ?? "en"
                    : language.Code;
                var encoder = FindModelFile("encoder")!;
                var decoder = FindModelFile("decoder")!;
                EnsureRecognizer(encoder, decoder, code);
            }

            using var stream = _recognizer.CreateStream();
            stream.AcceptWaveform(AudioCaptureService.TargetSampleRate, samples);
            _recognizer.Decode(stream);
            return stream.Result.Text.Trim();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _recognizer?.Dispose();
        _recognizer = null;
        _gate.Dispose();
    }
}
