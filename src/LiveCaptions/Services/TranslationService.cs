using System.Text;
using System.Text.RegularExpressions;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using LiveCaptions.Models;

namespace LiveCaptions.Services;

/// <summary>
/// Local LLM translation using LLamaSharp (llama.cpp). One model instance is
/// kept resident; requests are serialised through a queue.
/// </summary>
public sealed class TranslationService : ITranslator
{
    private readonly string _modelPath;
    private readonly AppSettings _settings;

    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TranslationService(string modelPath, AppSettings settings)
    {
        _modelPath = modelPath;
        _settings = settings;
    }

    public bool IsLoaded => _weights is not null && _context is not null;
    public string Backend { get; private set; } = "未加载";
    public string ModelName => Path.GetFileNameWithoutExtension(_modelPath);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (IsLoaded) return;

        LlamaRuntime.EnsureConfigured(_settings.GpuBackend);

        var modelParams = new ModelParams(_modelPath)
        {
            GpuLayerCount = _settings.GpuBackend.Equals("cpu", StringComparison.OrdinalIgnoreCase) ? 0 : _settings.GpuLayerCount,
            ContextSize = (uint)_settings.ContextSize,
            BatchSize = 2048,
            UBatchSize = 512,
            UseMemorymap = true,
            UseMemoryLock = false,
        };

        _weights = await LLamaWeights.LoadFromFileAsync(modelParams, ct).ConfigureAwait(false);
        _context = _weights.CreateContext(modelParams);
        Backend = LlamaRuntime.DescribeBackend(_settings.GpuBackend);

        // Warm up so the first real subtitle is not slowed down by shader/graph setup.
        try
        {
            await TranslateAsync("Hello.", LanguageCatalog.FindTarget(_settings.TargetLanguage), null, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Warmup is best-effort.
        }
    }

    /// <summary>
    /// Translate <paramref name="text"/> into <paramref name="target"/>. Tokens are streamed
    /// through <paramref name="onToken"/> so the overlay can update while generating.
    /// </summary>
    public async Task<string> TranslateAsync(string text, LanguageOption target, Action<string>? onToken, CancellationToken ct = default)
    {
        if (!IsLoaded) throw new InvalidOperationException("翻译模型尚未加载");
        if (string.IsNullOrWhiteSpace(text)) return "";

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _context!.NativeHandle.MemoryClear();

            var executor = new InteractiveExecutor(_context);
            var prompt = TranslationText.ChatMlPrompt(text, target);

            var maxTokens = Math.Clamp(text.Length * 3, 64, _settings.MaxTranslateTokens);
            var inferenceParams = new InferenceParams
            {
                MaxTokens = maxTokens,
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = (float)_settings.TranslateTemperature,
                    RepeatPenalty = 1.1f,
                    PenaltyCount = 128,
                },
                AntiPrompts = ["<|im_end|>", "<|im_start|>", "<|endoftext|>"],
            };

            var sb = new StringBuilder();
            await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct).ConfigureAwait(false))
            {
                sb.Append(token);
                if (onToken is not null && sb.Length % 4 == 0)
                {
                    onToken(TranslationText.Clean(sb.ToString()));
                }

                if (sb.Length > 8000) break;
            }

            var result = TranslationText.Clean(sb.ToString());
            onToken?.Invoke(result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _context?.Dispose();
        _context = null;
        _weights?.Dispose();
        _weights = null;
        _gate.Dispose();
    }
}
