using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace LiveCaptions.Services.Asr;

/// <summary>
/// Qwen3-ASR (speech-LLM) running through llama.cpp's mtmd audio path.
/// The GGUF model and the audio projector (mmproj) are loaded separately.
/// </summary>
public sealed class Qwen3AsrEngine : IAsrEngine
{
    private readonly string _modelPath;
    private readonly string _mmprojPath;
    private readonly string _gpuBackend;
    private readonly int _gpuLayers;
    private readonly int _contextSize;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private MtmdWeights? _mtmd;
    private string _mediaMarker = "<media>";
    private InferenceParams? _inferParams;

    public Qwen3AsrEngine(string modelPath, string mmprojPath, string gpuBackend, int gpuLayers, int contextSize)
    {
        _modelPath = modelPath;
        _mmprojPath = mmprojPath;
        _gpuBackend = gpuBackend;
        _gpuLayers = gpuLayers;
        _contextSize = contextSize;
    }

    public string Name => "Qwen3-ASR";
    public string Backend { get; private set; } = "未加载";
    public bool IsLoaded => _weights is not null && _mtmd is not null && _context is not null;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (IsLoaded) return;

        LlamaRuntime.EnsureConfigured(_gpuBackend);

        var modelParams = new ModelParams(_modelPath)
        {
            GpuLayerCount = _gpuBackend.Equals("cpu", StringComparison.OrdinalIgnoreCase) ? 0 : _gpuLayers,
            ContextSize = (uint)_contextSize,
            BatchSize = 2048,
            UBatchSize = 512,
            UseMemorymap = true,
            UseMemoryLock = false,
        };

        _weights = await LLamaWeights.LoadFromFileAsync(modelParams, ct).ConfigureAwait(false);
        _context = _weights.CreateContext(modelParams);

        var mtmdParams = MtmdContextParams.Default();
        mtmdParams.UseGpu = !_gpuBackend.Equals("cpu", StringComparison.OrdinalIgnoreCase);
        mtmdParams.NThreads = Math.Min(16, Environment.ProcessorCount);

        // The executor builds prompts with the *native default* marker, so pin the
        // context parameter to the same string to keep marker/bitmap counts aligned.
        _mediaMarker = NativeApi.MtmdDefaultMarker() ?? "<media>";
        mtmdParams.MediaMarker = _mediaMarker;

        _mtmd = await MtmdWeights.LoadFromFileAsync(_mmprojPath, _weights, mtmdParams).ConfigureAwait(false);

        if (!_mtmd.SupportsAudio)
        {
            throw new InvalidOperationException("模型不支持音频输入（mmproj 文件不正确）");
        }

        _inferParams = new InferenceParams
        {
            MaxTokens = 1024,
            SamplingPipeline = new GreedySamplingPipeline(),
            AntiPrompts = ["<|im_end|>", "<|im_start|>"],
        };

        Backend = LlamaRuntime.DescribeBackend(_gpuBackend);
    }

    public async Task<string> TranscribeAsync(float[] samples, Models.LanguageOption? language, string? context, CancellationToken ct = default)
    {
        if (!IsLoaded) throw new InvalidOperationException("ASR 模型尚未加载");

        var clip = _mtmd!;
        var rate = clip.SampleRate > 0 ? clip.SampleRate : 16000;
        if (rate != AudioCaptureService.TargetSampleRate)
        {
            samples = AudioUtil.Resample(samples, AudioCaptureService.TargetSampleRate, rate);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _context!.NativeHandle.MemoryClear();
            _mtmd!.ClearMedia();

            // mtmd only accepts audio through a container, so wrap the PCM in a WAV buffer.
            using var embed = _mtmd.LoadMedia(AudioUtil.ToWav(samples, rate));
            var executor = new InteractiveExecutor(_context, _mtmd);
            executor.Embeds.Clear();
            executor.Embeds.Add(embed);

            var prompt = BuildPrompt(language, context);
            var sb = new StringBuilder();

            var sw = Stopwatch.StartNew();
            await foreach (var token in executor.InferAsync(prompt, _inferParams!, ct).ConfigureAwait(false))
            {
                sb.Append(token);
                if (sb.Length > 6000) break;
            }

            sw.Stop();
            LastLatencyMs = sw.ElapsedMilliseconds;
            executor.Embeds.Clear();
            return Clean(sb.ToString());
        }
        finally
        {
            _gate.Release();
        }
    }

    public long LastLatencyMs { get; private set; }

    private string BuildPrompt(Models.LanguageOption? language, string? context)
    {
        var instruction = language is null || language.Code.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? "Transcribe the audio."
            : $"Transcribe the audio in {language.EnglishName}.";

        var sb = new StringBuilder();
        sb.Append("<|im_start|><|im_end|>\n").Append("<|im_start|>user\n");
        sb.Append(_mediaMarker);
        sb.Append(instruction);

        if (!string.IsNullOrWhiteSpace(context) && context.Length <= 240)
        {
            sb.Append('\n').Append("Context (previous text): ").Append(context.Trim());
        }

        sb.Append("<|im_end|>\n<|im_start|>assistant\n");
        return sb.ToString();
    }

    private static string Clean(string raw)
    {
        var text = raw.Replace("<|im_end|>", " ").Replace("<|im_start|>", " ");

        // Qwen3-ASR prefixes output with "language <Lang>" followed by a marker.
        var markerIndex = text.IndexOf("<asr_text>", StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            text = text[(markerIndex + "<asr_text>".Length)..];
        }
        else
        {
            var languageIndex = text.IndexOf("language ", StringComparison.OrdinalIgnoreCase);
            if (languageIndex >= 0 && languageIndex <= 4)
            {
                var newline = text.IndexOf('\n');
                text = newline >= 0 ? text[(newline + 1)..] : text;
            }
        }

        // Drop any reasoning block the model may emit.
        text = text.Replace("<think>", " ").Replace("</think>", " ");
        var start = text.IndexOf(" thinking", StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            var end = text.IndexOf("<｜end▁of▁thinking｜>", StringComparison.OrdinalIgnoreCase);
            if (end > start)
            {
                text = text.Remove(start, end - start + "<｜end▁of▁thinking｜>".Length);
            }
        }

        return text.Trim();
    }

    public void Dispose()
    {
        _mtmd?.Dispose();
        _mtmd = null;
        _context?.Dispose();
        _context = null;
        _weights?.Dispose();
        _weights = null;
        _gate.Dispose();
    }
}
