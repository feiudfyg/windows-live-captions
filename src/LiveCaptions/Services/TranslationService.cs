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
public sealed partial class TranslationService : IDisposable
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
            var prompt = BuildPrompt(text, target);

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
                    onToken(Clean(sb.ToString()));
                }

                if (sb.Length > 8000) break;
            }

            var result = Clean(sb.ToString());
            onToken?.Invoke(result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string BuildPrompt(string text, LanguageOption target)
    {
        var sb = new StringBuilder();
        sb.Append("<|im_start|>system\n");
        sb.Append("You are a professional real-time subtitle translator. ");
        sb.Append($"Translate the user's text into {target.EnglishName} ({target.DisplayName}). ");
        sb.Append("Output only the translation itself: no explanations, no notes, no quotes, no original text. ");
        sb.Append("Keep names, numbers, technical terms and the original tone. ");
        sb.Append($"If the text is already in {target.EnglishName}, output it unchanged.");
        sb.Append("<|im_end|>\n");
        sb.Append("<|im_start|>user\n").Append(text.Trim()).Append("<|im_end|>\n");
        sb.Append("<|im_start|>assistant\n");

        // Qwen3.5 chat template: prefill an empty <think> block to disable thinking mode.
        sb.Append("<think>\n\n</think>\n\n");
        return sb.ToString();
    }

    private static string Clean(string raw)
    {
        var text = raw;

        var endThink = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (endThink >= 0)
        {
            text = text[(endThink + "</think>".Length)..];
        }
        else
        {
            var startThink = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (startThink == 0)
            {
                return ""; // reasoning in progress - nothing to display yet
            }

            if (startThink > 0)
            {
                text = text[..startThink];
            }
        }

        text = ThinkBlockRegex().Replace(text, "");
        text = text.Replace("<think>", " ").Replace("</think>", " ");
        text = text.Replace("<|im_end|>", " ").Replace("<|im_start|>", " ").Replace("<|endoftext|>", " ");
        text = text.Replace("\r", " ").Replace("\n", " ");
        text = text.Trim().Trim('"', '\'', '“', '”', '‘', '’').Trim();

        // Defensive: drop an echoed "Translation:" style prefix.
        var prefixIndex = text.IndexOf(':');
        if (prefixIndex is > 0 and < 24)
        {
            var prefix = text[..prefixIndex].ToLowerInvariant();
            if (prefix.Contains("translation") || prefix.Contains("译文") || prefix.Contains("翻译"))
            {
                text = text[(prefixIndex + 1)..].Trim();
            }
        }

        return Asr.TextGuards.TruncateRepetition(text);
    }

    [GeneratedRegex(@" thinking.*?<｜end▁of▁thinking｜>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlockRegex();

    public void Dispose()
    {
        _context?.Dispose();
        _context = null;
        _weights?.Dispose();
        _weights = null;
        _gate.Dispose();
    }
}
