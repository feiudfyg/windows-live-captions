using System.Threading.Channels;
using LiveCaptions.Models;
using LiveCaptions.Services.Asr;

namespace LiveCaptions.Services;

/// <summary>
/// Audio -> segmentation -> ASR -> translation orchestration.
/// Emits <see cref="ItemUpdated"/> on background threads; the UI marshals to the dispatcher.
/// </summary>
public sealed class CaptionPipeline : IAsyncDisposable
{
    private const int SampleRate = AudioCaptureService.TargetSampleRate;
    private const int BlockSamples = SampleRate / 10; // 100 ms blocks

    private readonly AppSettings _settings;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _ui;
    private readonly Channel<float[]> _audioChannel = Channel.CreateUnbounded<float[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly Channel<TranslationJob> _translationChannel = Channel.CreateUnbounded<TranslationJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly List<float> _pending = [];
    private readonly List<float> _utterance = [];

    private IAsrEngine? _asr;
    private TranslationService? _translator;
    private CancellationTokenSource? _cts;
    private Task? _mainLoop;
    private Task? _translateLoop;

    private bool _speech;
    private int _silenceMs;
    private long _lastDecodeMs;
    private double _levelAccum;
    private int _levelCount;
    private double _noiseFloor = 0.0005;
    private DateTime _lastPartialAt = DateTime.MinValue;
    private int _nextId = 1;
    private CaptionItem? _liveItem;
    private string _lastFinalText = "";
    private CancellationTokenSource? _partialDebounce;

    public CaptionPipeline(AppSettings settings, Microsoft.UI.Dispatching.DispatcherQueue? ui = null)
    {
        _settings = settings;
        _ui = ui;
    }

    /// <summary>
    /// Caption items are bound to XAML elements, so every property change must
    /// happen on the UI thread (otherwise WinRT throws RPC_E_WRONG_THREAD).
    /// </summary>
    private Task UiAsync(Action action)
    {
        if (_ui is null || _ui.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ui.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }))
        {
            tcs.SetException(new InvalidOperationException("UI dispatcher unavailable"));
        }

        return tcs.Task;
    }

    private void UiPost(Action action)
    {
        if (_ui is null || _ui.HasThreadAccess)
        {
            action();
            return;
        }

        _ui.TryEnqueue(() =>
        {
            try
            {
                action();
            }
            catch
            {
                // UI updates are best-effort.
            }
        });
    }

    public event Action<CaptionItem>? ItemUpdated;
    public event Action<string>? ErrorOccurred;

    public bool IsRunning => _mainLoop is not null;

    public void SetEngines(IAsrEngine asr, TranslationService? translator)
    {
        _asr = asr;
        _translator = translator;
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _mainLoop = Task.Run(() => AudioLoopAsync(_cts.Token));
        _translateLoop = Task.Run(() => TranslateLoopAsync(_cts.Token));
    }

    public void PushAudio(float[] samples, int count)
    {
        if (count <= 0) return;
        var copy = new float[count];
        Array.Copy(samples, copy, count);
        _audioChannel.Writer.TryWrite(copy);
    }

    public async Task StopAsync()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        if (_mainLoop is not null)
        {
            try
            {
                await _mainLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        if (_translateLoop is not null)
        {
            try
            {
                await _translateLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        _mainLoop = null;
        _translateLoop = null;
        _cts?.Dispose();
        _cts = null;

        _pending.Clear();
        _utterance.Clear();
        _speech = false;
        _silenceMs = 0;
        _liveItem = null;
        _lastFinalText = "";
    }

    private async Task AudioLoopAsync(CancellationToken ct)
    {
        var reader = _audioChannel.Reader;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(ct).ConfigureAwait(false)) break;
                while (reader.TryRead(out var chunk)) _pending.AddRange(chunk);
                await ProcessPendingAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"识别管道错误: {ex}");
            }
        }
    }

    private async Task ProcessPendingAsync(CancellationToken ct)
    {
        while (_pending.Count >= BlockSamples && !ct.IsCancellationRequested)
        {
            var block = new float[BlockSamples];
            _pending.CopyTo(0, block, 0, BlockSamples);
            _pending.RemoveRange(0, BlockSamples);

            var rms = AudioUtil.Rms(block);
            _utterance.AddRange(block);

            // Adaptive gate: track the ambient noise floor and treat anything well
            // above it as speech. The configured threshold acts as an upper bound.
            var gate = Math.Clamp(_noiseFloor * 3.0, 0.0012, Math.Max(0.0012, _settings.VadThreshold));

            _levelAccum += rms;
            _levelCount++;
            if (_levelCount >= 20)
            {
                Log.Write($"[audio] level avg={_levelAccum / _levelCount:0.00000} gate={gate:0.00000} floor={_noiseFloor:0.00000} utterance={_utterance.Count / 16000.0:0.0}s speech={_speech} silence={_silenceMs}ms");
                _levelAccum = 0;
                _levelCount = 0;
            }

            if (rms >= gate)
            {
                _silenceMs = 0;
                _speech = true;
            }
            else
            {
                if (_speech) _silenceMs += 100;
                _noiseFloor = _noiseFloor * 0.98 + rms * 0.02;
            }

            var utteranceMs = _utterance.Count * 1000.0 / SampleRate;

            // A decode blocks this loop, so back off the partial cadence by the time
            // the previous decode took - otherwise partials run back-to-back and the
            // captions lag behind live audio.
            var partialInterval = Math.Max(_settings.PartialIntervalMs, _lastDecodeMs + 250);
            var duePartial = _speech && utteranceMs >= _settings.MinPartialSeconds * 1000.0
                             && (DateTime.UtcNow - _lastPartialAt).TotalMilliseconds >= partialInterval;

            if (_speech && utteranceMs >= _settings.MaxUtteranceSeconds * 1000.0)
            {
                // Long continuous speech: commit what we have so captions keep flowing.
                await DecodeAsync(final: true, ct).ConfigureAwait(false);
            }
            else if (_speech && _silenceMs >= _settings.FinalSilenceMs && utteranceMs >= 400)
            {
                await DecodeAsync(final: true, ct, silenceEnd: true).ConfigureAwait(false);
            }
            else if (duePartial)
            {
                await DecodeAsync(final: false, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task DecodeAsync(bool final, CancellationToken ct, bool silenceEnd = false)
    {
        var engine = _asr;
        if (engine is null || !engine.IsLoaded)
        {
            _utterance.Clear();
            _speech = false;
            _silenceMs = 0;
            return;
        }

        // Snapshot and clear first: audio arriving while we decode stays in the
        // pending/channel buffers and becomes the start of the next utterance.
        var samples = _utterance.ToArray();
        _utterance.Clear();

        if (silenceEnd)
        {
            _speech = false;
            _silenceMs = 0;
        }
        else if (final)
        {
            _silenceMs = 0;
        }
        else
        {
            _lastPartialAt = DateTime.UtcNow;
        }

        if (AudioUtil.Rms(samples) < Math.Max(0.0008, _noiseFloor * 2.0))
        {
            return; // nothing audible - skip to avoid hallucinated text
        }

        string text;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var language = LanguageCatalog.FindSource(_settings.SourceLanguage);
            var context = _settings.UseAsrContext && _lastFinalText.Length > 0 ? Tail(_lastFinalText, 200) : null;
            text = await engine.TranscribeAsync(samples, language, context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"识别失败: {ex}");
            return;
        }
        finally
        {
            stopwatch.Stop();
        }

        _lastDecodeMs = stopwatch.ElapsedMilliseconds;
        text = TextGuards.TruncateRepetition(TextGuards.Normalize(text));
        if (!IsMeaningful(text)) return;

        if (TextGuards.IsDegenerate(text))
        {
            Log.Write($"[asr] dropped repetitive output: {text[..Math.Min(80, text.Length)]}");
            return;
        }

        // Long partials that clearly end a sentence are committed as finals so that
        // captions keep flowing (and get translated) during continuous speech.
        if (!final && _settings.PartialCommitSeconds > 0
            && samples.Length / (double)SampleRate >= _settings.PartialCommitSeconds
            && EndsWithSentencePunctuation(text))
        {
            final = true;
        }

        Log.Write($"[asr] {(final ? "final" : "partial")} {samples.Length / 16000.0:0.00}s in {stopwatch.ElapsedMilliseconds}ms: {text}");

        if (final && string.Equals(text, _lastFinalText, StringComparison.OrdinalIgnoreCase))
        {
            return; // identical repeat - almost certainly a hallucination
        }

        CaptionItem item;
        if (final)
        {
            item = _liveItem ?? new CaptionItem { Id = _nextId++ };
            await UiAsync(() =>
            {
                item.Original = text;
                item.IsPartial = false;
            }).ConfigureAwait(false);

            _liveItem = null;
            _lastFinalText = text;
            _partialDebounce?.Cancel();
            EnqueueTranslation(item, text, final: true);
        }
        else
        {
            item = _liveItem ??= new CaptionItem { Id = _nextId++ };
            await UiAsync(() =>
            {
                item.Original = text;
                item.IsPartial = true;
            }).ConfigureAwait(false);

            if (_settings.TranslatePartials)
            {
                SchedulePartialTranslation(item, text);
            }
        }

        ItemUpdated?.Invoke(item);
    }

    private static string Tail(string text, int maxChars)
        => text.Length <= maxChars ? text : text[^maxChars..];

    private static bool EndsWithSentencePunctuation(string text)
    {
        if (text.Length == 0) return false;
        return text[^1] switch
        {
            '.' or '!' or '?' or '。' or '！' or '？' or '…' => true,
            _ => false,
        };
    }

    private static bool IsMeaningful(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length < 2) return false;

        // Reject outputs that are only punctuation, brackets or music notes.
        var meaningful = 0;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) meaningful++;
        }

        return meaningful >= Math.Max(1, text.Length / 10);
    }

    private void SchedulePartialTranslation(CaptionItem item, string text)
    {
        _partialDebounce?.Cancel();
        _partialDebounce?.Dispose();
        var cts = new CancellationTokenSource();
        _partialDebounce = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_settings.PartialTranslateDelayMs, cts.Token).ConfigureAwait(false);
                if (!cts.IsCancellationRequested && ReferenceEquals(_liveItem, item) && item.Original == text)
                {
                    EnqueueTranslation(item, text, final: false);
                }
            }
            catch (OperationCanceledException)
            {
                // superseded
            }
        }, CancellationToken.None);
    }

    private void EnqueueTranslation(CaptionItem item, string text, bool final)
    {
        var translator = _translator;
        var target = LanguageCatalog.FindTarget(_settings.TargetLanguage);

        if (translator is null || !translator.IsLoaded || !_settings.TranslateEnabled)
        {
            return;
        }

        if (LanguageCatalog.LooksLike(text, target.Code))
        {
            // Already in the target language - no LLM round trip needed.
            UiPost(() => item.Translation = "");
            return;
        }

        _translationChannel.Writer.TryWrite(new TranslationJob(item, text, final, target));
    }

    private async Task TranslateLoopAsync(CancellationToken ct)
    {
        var reader = _translationChannel.Reader;

        while (!ct.IsCancellationRequested)
        {
            TranslationJob job;
            try
            {
                if (!await reader.WaitToReadAsync(ct).ConfigureAwait(false)) break;
                if (!reader.TryRead(out job!)) continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (job.Item.IsError) continue;
            if (!job.Final && job.Item.Original != job.Text) continue; // stale partial

            try
            {
                var translated = await _translator!.TranslateAsync(
                    job.Text,
                    job.Target,
                    onToken: t => UiPost(() => job.Item.Translation = t),
                    ct).ConfigureAwait(false);

                await UiAsync(() => job.Item.Translation = translated).ConfigureAwait(false);
                ItemUpdated?.Invoke(job.Item);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"翻译失败: {ex}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private readonly record struct TranslationJob(CaptionItem Item, string Text, bool Final, LanguageOption Target);
}
