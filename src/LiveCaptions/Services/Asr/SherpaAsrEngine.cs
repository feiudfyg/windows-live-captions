using SherpaOnnx;

namespace LiveCaptions.Services.Asr;

/// <summary>
/// Zipformer transducer (RNNT) ASR via sherpa-onnx. These models decode with
/// greedy search and cannot fall into the repetition loops of autoregressive
/// speech-LLMs, which makes them very robust for noisy or music-heavy audio.
/// Inference runs on CPU (ONNX Runtime) with real-time factors well below 0.1.
/// </summary>
public sealed class SherpaAsrEngine : IAsrEngine
{
    private readonly string _modelDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private OfflineRecognizer? _recognizer;

    public SherpaAsrEngine(string modelDirectory) => _modelDirectory = modelDirectory;

    public string Name => "Zipformer";
    public string Backend { get; private set; } = "未加载";
    public bool IsLoaded => _recognizer is not null;

    public Task LoadAsync(CancellationToken ct = default)
    {
        if (IsLoaded) return Task.CompletedTask;

        var encoder = FindModelFile("encoder") ?? throw new FileNotFoundException($"未找到 encoder 模型: {_modelDirectory}");
        var decoder = FindModelFile("decoder") ?? throw new FileNotFoundException($"未找到 decoder 模型: {_modelDirectory}");
        var joiner = FindModelFile("joiner") ?? throw new FileNotFoundException($"未找到 joiner 模型: {_modelDirectory}");
        var tokens = Path.Combine(_modelDirectory, "tokens.txt");
        if (!File.Exists(tokens)) throw new FileNotFoundException($"未找到 tokens.txt: {_modelDirectory}");

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = AudioCaptureService.TargetSampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = encoder;
        config.ModelConfig.Transducer.Decoder = decoder;
        config.ModelConfig.Transducer.Joiner = joiner;
        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";

        _recognizer = new OfflineRecognizer(config);
        Backend = $"sherpa-onnx CPU ({Path.GetFileNameWithoutExtension(encoder)})";
        return Task.CompletedTask;
    }

    /// <summary>Prefer the int8 quantised variant when the archive ships both.</summary>
    private string? FindModelFile(string kind)
    {
        var candidates = Directory.EnumerateFiles(_modelDirectory, $"{kind}*.onnx")
            .Where(f => Path.GetFileName(f).StartsWith(kind, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Contains(".int8.", StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f.Length)
            .ToList();

        return candidates.FirstOrDefault();
    }

    public async Task<string> TranscribeAsync(float[] samples, Models.LanguageOption? language, string? context, CancellationToken ct = default)
    {
        if (_recognizer is null) throw new InvalidOperationException("ASR 模型尚未加载");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var recognizer = _recognizer;
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(AudioCaptureService.TargetSampleRate, samples);
            recognizer.Decode(stream);
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
