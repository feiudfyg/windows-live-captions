using System.Threading.Channels;
using LiveCaptions.Models;
using LiveCaptions.Services.Asr;

namespace LiveCaptions.Services;

/// <summary>
/// Audio -> segmentation -> ASR -> translation orchestration.
/// Emits <see cref="ItemUpdated"/> on background threads; the UI marshals to the dispatcher.
///
/// Segmentation is adaptive and content aware:
/// - a Silero VAD (when available) finds speech runs; each run is transcribed on its
///   own, which matters because utterance-level models (e.g. Cohere) silently drop
///   earlier phrases when a window contains several pauses;
/// - run transcripts are merged into one caption until a pause exceeds the speaker's
///   learned rhythm (fast talkers commit sooner, slow talkers later);
/// - online tuning keeps adjusting the gap and caption size from how the captions
///   actually come out (mid-sentence cuts, characters per second).
/// </summary>
public sealed class CaptionPipeline : IAsyncDisposable
{
    private const int SampleRate = AudioCaptureService.TargetSampleRate;
    private const int BlockSamples = SampleRate / 10; // 100 ms blocks

    // Adaptive segmentation tuning.
    private const double MinCommitGapMs = 380;
    private const double MaxCommitGapMs = 900;
    private const double DefaultCommitGapMs = 500;
    private const double SpeechHoldMs = 150;         // bridge short pauses inside a sentence
    private const int PrerollBlocks = 5;             // 500 ms of audio kept before a speech onset
    private const double TrailingContextMs = 400;    // silence kept after speech ends
    private const double HeldFragmentFlushMs = 1800; // interjections wait this long for company
    private const double MinRunMs = 350;             // shorter blips are ignored
    private const double MaxAsrWindowMs = 8000;      // 4s windows cut words mid-syllable on continuous anime dialogue
    private const long AsrFailureRetryMs = 2000;     // audio stays buffered; retry the window after this long
    private const int MaxRecentGaps = 24;

    // Input level normalization: the captured signal sits wherever the system
    // volume puts it (often -30 dB), which makes Silero VAD miss most speech.
    // Blocks are scaled towards a fixed target level before VAD and ASR.
    private const double TargetRms = 0.05;
    private const double MaxInputGain = 40.0;

    private readonly AppSettings _settings;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _ui;

    // Bounded so a stalled engine cannot grow memory without limit: live captions
    // matter more than stale audio, so the oldest entries are dropped.
    private const int MaxQueuedAudioBlocks = 300;  // 30 s of audio
    private const int MaxQueuedTranslations = 64;

    private readonly Channel<float[]> _audioChannel = Channel.CreateBounded<float[]>(
        new BoundedChannelOptions(MaxQueuedAudioBlocks) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });

    private readonly Channel<TranslationJob> _translationChannel = Channel.CreateBounded<TranslationJob>(
        new BoundedChannelOptions(MaxQueuedTranslations) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });

    private long _droppedAudioBlocks;
    private long _droppedTranslations;

    private readonly List<float> _pending = [];
    private readonly List<float> _utterance = [];
    private readonly Queue<float[]> _preroll = new();
    private readonly List<double> _recentGaps = [];

    private IAsrEngine? _asr;
    private ITranslator? _translator;
    private IVad? _vad;
    private bool _vadMode;
    private bool _vadFallbackLogged;
    private CancellationTokenSource? _cts;
    private Task? _mainLoop;
    private Task? _translateLoop;

    // Energy-gate fallback state (no VAD model available).
    private bool _speech;
    private int _silenceMs;
    private double _levelAccum;
    private int _levelCount;
    private double _noiseFloor = 0.0005;

    // Automatic input gain (runs on raw RMS).
    private double _speechRms;
    private double _inputGain = 1.0;
    private bool _gainInitialized;

    // VAD timeline.
    private long _fedSamples;
    private double _speechHold;
    private bool _wasSpeech;
    private long _lastSpeechStop;
    private DateTime _lastSpeechStopAt;
    private bool _utteranceHasSpeech;
    private long _lastAsrFailureAt;

    // Caption assembly: run transcripts merge into one caption until a long pause.
    private string _captionText = "";
    private double _captionSeconds;
    private bool _captionPending;

    // Online self-tuning: the gap learned from the speaker's rhythm is scaled by
    // feedback from how the previous captions actually came out.
    private double _gapBaseMs = DefaultCommitGapMs;
    private double _gapAdaptFactor = 1.0;
    private double _charsPerSecond;
    private double _maxChars = 48;
    private int _finalsSinceTune;
    private int _midSentenceCuts;
    private int _cleanCuts;

    private long _lastDecodeMs;
    private DateTime _lastPartialAt = DateTime.MinValue;
    private int _nextId = 1;
    private CaptionItem? _liveItem;
    private string _lastFinalText = "";
    private DateTime _lastFinalAt = DateTime.MinValue;
    private string? _lastPartialText;
    private CancellationTokenSource? _partialDebounce;

    private double EffectiveGapMs => Math.Clamp(_gapBaseMs * _gapAdaptFactor, MinCommitGapMs, MaxCommitGapMs);

    private double AsrWindowMs => Math.Clamp(
        Math.Min(_settings.MaxUtteranceSeconds * 1000.0, MaxAsrWindowMs), 3000, MaxAsrWindowMs);

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

    public void SetEngines(IAsrEngine asr, ITranslator? translator)
    {
        _asr = asr;
        _translator = translator;
    }

    public void SetVad(IVad? vad)
    {
        _vad = vad;
        _vadMode = vad?.IsAvailable == true;
        Log.Write(_vadMode
            ? $"[vad] adaptive segmentation active ({vad!.Engine})"
            : "[vad] no VAD model - using energy gate fallback");
    }

    /// <summary>Drops the in-flight partial line (used by the "clear" button so the
    /// next token does not resurrect it).</summary>
    public void ResetLiveCaption() => _liveItem = null;

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

        if (!_audioChannel.Writer.TryWrite(copy) && _audioChannel.Reader.TryRead(out _))
        {
            // Queue full: ASR is behind real time. Drop the oldest block, keep the
            // newest audio, and say so occasionally instead of growing forever.
            var dropped = Interlocked.Increment(ref _droppedAudioBlocks);
            if (dropped % 100 == 1)
            {
                Log.Write($"[audio] processing is behind; dropped {dropped} queued blocks");
            }

            _audioChannel.Writer.TryWrite(copy);
        }
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
        _preroll.Clear();
        _recentGaps.Clear();
        _speech = false;
        _silenceMs = 0;
        _speechHold = 0;
        _wasSpeech = false;
        _fedSamples = 0;
        _lastSpeechStop = 0;
        _utteranceHasSpeech = false;
        _captionText = "";
        _captionSeconds = 0;
        _captionPending = false;
        _inputGain = 1.0;
        _speechRms = 0;
        _gainInitialized = false;
        _gapBaseMs = DefaultCommitGapMs;
        _gapAdaptFactor = 1.0;
        _charsPerSecond = 0;
        _maxChars = 48;
        _finalsSinceTune = 0;
        _midSentenceCuts = 0;
        _cleanCuts = 0;
        _liveItem = null;
        _lastFinalText = "";
        _lastFinalAt = DateTime.MinValue;
        _vad?.Reset();

        // Drop leftovers so a restart starts from a clean slate instead of
        // replaying audio or inheriting tuning state.
        while (_audioChannel.Reader.TryRead(out _))
        {
        }

        while (_translationChannel.Reader.TryRead(out _))
        {
        }

        _pending.Clear();
        _partialDebounce?.Cancel();
        _partialDebounce = null;
        _lastPartialText = null;
        _lastPartialAt = DateTime.MinValue;
        _lastDecodeMs = 0;
        _lastAsrFailureAt = 0;
        _lastSpeechStopAt = default;
        _noiseFloor = 0.0005;
        _levelAccum = 0;
        _levelCount = 0;
        _vadFallbackLogged = false;
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

                if (_pending.Count > SampleRate * 90)
                {
                    var drop = _pending.Count - SampleRate * 60;
                    _pending.RemoveRange(0, drop);
                    Log.Write($"[audio] dropped {drop / 16000.0:0.0}s of queued audio (processing is behind)");
                }

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
            _fedSamples += block.Length;

            // Normalize the level before anything looks at the audio: Silero VAD
            // is level-sensitive (quiet speech is treated as silence) and the ASR
            // window is more reliable at the target level.
            UpdateInputGain(rms);
            ApplyGain(block, _inputGain);
            var scaledRms = rms * _inputGain;

            double gate = 0;
            bool speechNow;
            bool runEnded = false;
            if (_vadMode && _vad is not null && _vad.IsAvailable)
            {
                _vad.Accept(block, block.Length);
                _speechHold = _vad.SpeechActive ? SpeechHoldMs : Math.Max(0, _speechHold - 100);
                speechNow = _speechHold > 0;

                if (speechNow && !_wasSpeech && _lastSpeechStop > 0)
                {
                    NoteGap((_fedSamples - _lastSpeechStop) / 16.0);
                }
                else if (!speechNow && _wasSpeech)
                {
                    _lastSpeechStop = _fedSamples;
                    _lastSpeechStopAt = DateTime.UtcNow;
                    runEnded = true;
                }

                _wasSpeech = speechNow;
            }
            else
            {
                if (_vadMode && _vad is not null && !_vadFallbackLogged)
                {
                    _vadFallbackLogged = true;
                    Log.Write($"[vad] {_vad.Engine} unavailable; falling back to the energy gate for this session");
                }

                speechNow = UpdateEnergyGate(scaledRms, out gate);
                runEnded = _speech && _silenceMs >= _settings.FinalSilenceMs;
                if (runEnded) _lastSpeechStopAt = DateTime.UtcNow;
                _wasSpeech = speechNow;
            }

            UpdateUtterance(block, speechNow);

            _levelAccum += rms;
            _levelCount++;
            if (_levelCount >= 20)
            {
                var sinceStopLog = _lastSpeechStop > 0 ? (_fedSamples - _lastSpeechStop) / 16.0 : 0;
                Log.Write($"[audio] level avg={_levelAccum / _levelCount:0.00000} gain={_inputGain:0.0} gate={gate:0.00000} floor={_noiseFloor:0.00000} utterance={_utterance.Count / 16000.0:0.0}s speech={speechNow} sinceStop={sinceStopLog:0}ms pending={_captionPending}");
                _levelAccum = 0;
                _levelCount = 0;
            }

            var utteranceMs = _utterance.Count * 1000.0 / SampleRate;
            var sinceStopMs = _lastSpeechStop > 0 ? (_fedSamples - _lastSpeechStop) / 16.0 : double.MaxValue;

            // A decode blocks this loop, so back off the partial cadence by the time
            // the previous decode took - otherwise partials run back-to-back and the
            // captions lag behind live audio.
            var partialInterval = Math.Max(_settings.PartialIntervalMs, _lastDecodeMs + 250);
            var duePartial = _utteranceHasSpeech
                             && (speechNow || sinceStopMs < TrailingContextMs)
                             && utteranceMs >= _settings.MinPartialSeconds * 1000.0
                             && (DateTime.UtcNow - _lastPartialAt).TotalMilliseconds >= partialInterval;

            if (runEnded && _utteranceHasSpeech && utteranceMs >= MinRunMs)
            {
                // Speech run finished: transcribe it on its own (single-utterance
                // input keeps utterance-level models from dropping earlier phrases).
                if (_vadMode) await DecodeRunAsync(ct, emit: false).ConfigureAwait(false);
                else await DecodeRunAsync(ct, emit: true).ConfigureAwait(false);
            }
            else if (_utteranceHasSpeech && utteranceMs >= AsrWindowMs)
            {
                // Long continuous speech: cut the ASR input window at the quietest
                // point near the limit (never split a word at the boundary) but keep
                // assembling the caption until it ends cleanly or outgrows a size.
                await DecodeRunAsync(ct, emit: false, cutSamples: FindQuietCut()).ConfigureAwait(false);
            }
            else if (_captionPending && !speechNow)
            {
                // A caption that already ends at a sentence boundary needs no pause
                // to confirm it: emit right away instead of waiting for a long gap
                // (continuous dialogue can otherwise pile up for many seconds).
                var complete = EndsWithSentencePunctuation(_captionText) && _captionText.Length >= 12;
                var required = IsInterjection(_captionText) ? HeldFragmentFlushMs : EffectiveGapMs;
                if (complete || sinceStopMs >= required)
                {
                    await EmitPendingAsync().ConfigureAwait(false);
                }
            }
            else if (duePartial)
            {
                await DecodePartialAsync(ct).ConfigureAwait(false);
            }

            // Drop the run buffer if it turned out to be an inaudible blip.
            if (runEnded && _utteranceHasSpeech && utteranceMs < MinRunMs)
            {
                _utterance.Clear();
                _utteranceHasSpeech = false;
            }
        }
    }

    /// <summary>
    /// Learn the speaker's rhythm. Only pauses around the typical sentence gap are
    /// counted (shorter ones are usually bridged inside a sentence); fast talkers
    /// therefore commit sooner and slow talkers get longer captions.
    /// </summary>
    private void NoteGap(double gapMs)
    {
        if (gapMs < 120 || gapMs > 5000) return;

        _recentGaps.Add(gapMs);
        if (_recentGaps.Count > MaxRecentGaps) _recentGaps.RemoveAt(0);

        var sorted = _recentGaps.OrderBy(g => g).ToArray();
        var median = sorted[sorted.Length / 2];
        var updated = Math.Clamp(median * 1.2, MinCommitGapMs, 700);
        if (Math.Abs(updated - _gapBaseMs) >= 20)
        {
            _gapBaseMs = updated;
            Log.Write($"[vad] rhythm: pause {gapMs:0}ms median {median:0}ms -> commit target {EffectiveGapMs:0}ms");
        }
        else
        {
            _gapBaseMs = updated;
        }
    }

    private void UpdateUtterance(float[] block, bool speechNow)
    {
        if (!_vadMode)
        {
            if (speechNow) _utteranceHasSpeech = true;
            _utterance.AddRange(block);
            return;
        }

        // Keep a short pre-roll so speech onsets are not clipped - the VAD window
        // itself lags the audio by design.
        var copy = new float[block.Length];
        Array.Copy(block, copy, block.Length);
        _preroll.Enqueue(copy);
        while (_preroll.Count > PrerollBlocks) _preroll.Dequeue();

        if (speechNow)
        {
            if (!_utteranceHasSpeech)
            {
                _utterance.Clear();
                foreach (var pre in _preroll) _utterance.AddRange(pre);
            }
            else
            {
                _utterance.AddRange(block);
            }

            _utteranceHasSpeech = true;
        }
        else if (_utteranceHasSpeech)
        {
            var sinceStop = _lastSpeechStop > 0 ? (_fedSamples - _lastSpeechStop) / 16.0 : 0;
            if (sinceStop < TrailingContextMs) _utterance.AddRange(block);
        }
    }

    /// <summary>
    /// Keeps the input near a fixed level regardless of the system volume. The
    /// gain follows the long-term speech level, rises slowly (no pumping) and
    /// drops quickly when the source gets loud.
    /// </summary>
    private void UpdateInputGain(double rms)
    {
        if (rms > Math.Max(0.0025, _noiseFloor * 2.0))
        {
            _speechRms = _speechRms <= 0 ? rms : _speechRms * 0.96 + rms * 0.04;
        }

        var desired = Math.Clamp(TargetRms / Math.Max(_speechRms, 0.0015), 1.0, MaxInputGain);
        if (!_gainInitialized && _speechRms > 0)
        {
            // Apply the first estimate immediately so the opening words are not lost.
            _gainInitialized = true;
            _inputGain = desired;
            return;
        }

        _inputGain = desired < _inputGain
            ? desired
            : _inputGain * 0.97 + desired * 0.03;
    }

    private static void ApplyGain(float[] block, double gain)
    {
        if (gain <= 1.0001) return;

        var g = (float)gain;
        for (var i = 0; i < block.Length; i++)
        {
            block[i] = Math.Clamp(block[i] * g, -1f, 1f);
        }
    }

    private bool UpdateEnergyGate(double rms, out double gate)
    {
        // Adaptive gate: track the ambient noise floor and treat anything well
        // above it as speech. The configured threshold acts as an upper bound.
        gate = Math.Clamp(_noiseFloor * 3.0, 0.0012, Math.Max(0.0012, _settings.VadThreshold));

        if (rms >= gate)
        {
            _silenceMs = 0;
            _speech = true;
        }
        else
        {
            if (_speech) _silenceMs += 100;
            _speech = false;
            _noiseFloor = _noiseFloor * 0.98 + rms * 0.02;
        }

        return _speech;
    }

    /// <summary>
    /// Picks where to cut a long speech run for ASR: the quietest 100 ms block in
    /// the last 1.5 s of the window, so the cut does not split a word in half.
    /// </summary>
    private int FindQuietCut()
    {
        const int block = SampleRate / 10;
        var limit = (int)Math.Min(AsrWindowMs / 1000.0 * SampleRate, _utterance.Count);
        var searchStart = Math.Max(block, limit - (int)(SampleRate * 1.5));
        var best = limit;
        var bestRms = double.MaxValue;

        for (var end = searchStart + block; end <= limit; end += block)
        {
            var rms = AudioUtil.Rms(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_utterance).Slice(end - block, block));
            if (rms < bestRms)
            {
                bestRms = rms;
                best = end;
            }
        }

        return best;
    }

    /// <summary>
    /// Transcribes the current speech run and merges the text into the pending
    /// caption. <paramref name="emit"/> forces the caption out immediately.
    /// <paramref name="cutSamples"/> decodes only the head of the buffer and keeps
    /// the rest for the next window.
    /// </summary>
    private async Task DecodeRunAsync(CancellationToken ct, bool emit, int cutSamples = 0)
    {
        var engine = _asr;
        if (engine is null)
        {
            _utterance.Clear();
            _utteranceHasSpeech = false;
            _speech = false;
            _silenceMs = 0;
            return;
        }

        // After a failure, wait before touching the engine again: the audio is
        // still buffered, so no speech is lost, and a broken engine does not get
        // hammered once per 100 ms block.
        if (Environment.TickCount64 - _lastAsrFailureAt < AsrFailureRetryMs) return;

        if (!engine.IsLoaded)
        {
            // The engine dropped out (sidecar crash, CUDA loss, ...): try to bring
            // it back and keep the audio until it answers again.
            _lastAsrFailureAt = Environment.TickCount64;
            try
            {
                Log.Write($"[asr] {engine.Name} not loaded; attempting reload");
                await engine.LoadAsync(ct).ConfigureAwait(false);
                Log.Write("[asr] engine reloaded");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                ErrorOccurred?.Invoke($"识别引擎重载失败: {ex.Message}");
            }

            return;
        }

        var take = cutSamples > 0 && cutSamples < _utterance.Count ? cutSamples : _utterance.Count;
        var samples = new float[take];
        _utterance.CopyTo(0, samples, 0, take);

        var seconds = samples.Length / (double)SampleRate;

        var (text, failed) = await RecognizeAsync(samples, ct).ConfigureAwait(false);
        if (failed)
        {
            // Keep the audio: a dead/slow engine must not swallow speech. The
            // backoff at the top prevents a retry storm; the cap below keeps a
            // permanently broken engine from growing the buffer forever.
            _lastAsrFailureAt = Environment.TickCount64;
            if (_utterance.Count > SampleRate * 90)
            {
                var drop = _utterance.Count - SampleRate * 60;
                _utterance.RemoveRange(0, drop);
                Log.Write($"[asr] dropped {drop / 16000.0:0.0}s of buffered audio (engine unavailable)");
            }

            return;
        }

        // The decode succeeded (or the window was digital silence): consume it.
        if (take < _utterance.Count)
        {
            // Still inside the same speech run: the remainder seeds the next window.
            _utterance.RemoveRange(0, take);
        }
        else
        {
            _utterance.Clear();
            _preroll.Clear();
            _utteranceHasSpeech = false;
            _silenceMs = 0;
        }

        if (text is null) return;

        AppendCaption(text, seconds);

        // Window cuts happen mid-sentence, so only emit when the caption ended
        // cleanly at a punctuation mark (and is long enough to be worth a line)
        // or when it became clearly too long to keep growing.
        var cleanEnd = EndsWithSentencePunctuation(_captionText) && _captionText.Length >= 12;
        if (emit || cleanEnd || _captionText.Length >= _maxChars * 2)
        {
            await EmitPendingAsync().ConfigureAwait(false);
        }
        else
        {
            await ShowPendingAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Live partial for the speech run in progress (never translated here
    /// unless partial translations are enabled).</summary>
    private async Task DecodePartialAsync(CancellationToken ct)
    {
        if (_asr is null || !_asr.IsLoaded) return;

        var samples = _utterance.ToArray();
        var seconds = samples.Length / (double)SampleRate;
        _lastPartialAt = DateTime.UtcNow;

        var (text, failed) = await RecognizeAsync(samples, ct, partial: true).ConfigureAwait(false);
        if (failed) _lastAsrFailureAt = Environment.TickCount64;
        if (text is null) return;

        // Punctuation-less engines (Zipformer) never mark a sentence end: if the
        // text stopped changing the speaker finished a phrase. Cohere partials are
        // NOT promoted on length/punctuation - they are often kana-only renderings
        // of an unfinished window, while the 6 s window cut produces clean text.
        var stable = string.Equals(text, _lastPartialText, StringComparison.Ordinal)
                     && seconds >= _settings.PartialCommitSeconds;
        _lastPartialText = text;

        if (stable)
        {
            _utterance.Clear();
            _preroll.Clear();
            _utteranceHasSpeech = false;
            AppendCaption(text, seconds);
            await EmitPendingAsync().ConfigureAwait(false);
            return;
        }

        var display = _captionText.Length == 0 ? text : Combine(_captionText, text);
        var item = _liveItem ??= new CaptionItem { Id = _nextId++ };
        await UiAsync(() =>
        {
            item.Original = display;
            item.IsPartial = true;
        }).ConfigureAwait(false);

        if (_settings.TranslatePartials)
        {
            SchedulePartialTranslation(item, display);
        }

        ItemUpdated?.Invoke(item);
    }

    /// <summary>Emits the pending caption as a final, translated item.</summary>
    private async Task EmitPendingAsync()
    {
        var text = _captionText;
        var seconds = _captionSeconds;
        _captionText = "";
        _captionSeconds = 0;
        _captionPending = false;
        if (text.Length == 0) return;

        // Only an *immediate* repeat is treated as an ASR loop; a line repeated a
        // few seconds later is legitimate speech and must not be swallowed.
        var sinceLastFinal = (DateTime.UtcNow - _lastFinalAt).TotalMilliseconds;
        if (string.Equals(text, _lastFinalText, StringComparison.OrdinalIgnoreCase) && sinceLastFinal < 2500)
        {
            Log.Write($"[asr] dropped repeat final: {text[..Math.Min(60, text.Length)]}");
            return;
        }

        var item = _liveItem ?? new CaptionItem { Id = _nextId++ };
        await UiAsync(() =>
        {
            item.Original = text;
            item.IsPartial = false;
        }).ConfigureAwait(false);

        var previous = _lastFinalText;
        _liveItem = null;
        _lastFinalText = text;
        _lastFinalAt = DateTime.UtcNow;
        _partialDebounce?.Cancel();

        var lag = _lastSpeechStopAt == default ? 0 : (DateTime.UtcNow - _lastSpeechStopAt).TotalMilliseconds;
        Log.Write($"[latency] caption {lag:0}ms after speech stop ({text.Length} chars)");

        EnqueueTranslation(item, text, final: true, context: previous);
        UpdateAdaptiveStats(text, seconds);
        ItemUpdated?.Invoke(item);
    }

    /// <summary>Shows the pending caption while waiting for a possible continuation.</summary>
    private async Task ShowPendingAsync()
    {
        var item = _liveItem ??= new CaptionItem { Id = _nextId++ };
        await UiAsync(() =>
        {
            item.Original = _captionText;
            item.IsPartial = true;
        }).ConfigureAwait(false);
        ItemUpdated?.Invoke(item);
    }

    private void AppendCaption(string text, double seconds)
    {
        _captionText = _captionText.Length == 0 ? text : Combine(_captionText, text);
        _captionSeconds += seconds;
        _captionPending = true;
    }

    /// <summary>Shared ASR call: quiet-skip, guards, timing, logging.
    /// <c>Failed</c> means the engine could not answer (transport/timeout/crash)
    /// as opposed to silence or filtered output, so callers can keep the audio.</summary>
    private async Task<(string? Text, bool Failed)> RecognizeAsync(float[] samples, CancellationToken ct, bool partial = false)
    {
        var engine = _asr;
        if (engine is null || !engine.IsLoaded) return (null, false);

        // Only skip when the window is essentially digital silence. Do NOT scale
        // this by the noise floor: with background music the floor climbs to the
        // music level and would silently eat quiet-but-real speech.
        var rms = AudioUtil.Rms(samples);
        if (rms < 0.0005)
        {
            Log.Write($"[asr] skipped quiet {(partial ? "partial" : "run")} {samples.Length / 16000.0:0.00}s rms={rms:0.00000}");
            return (null, false);
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
            return (null, true);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"识别失败: {ex.Message}");
            return (null, true);
        }
        finally
        {
            stopwatch.Stop();
        }

        _lastDecodeMs = stopwatch.ElapsedMilliseconds;
        text = TextGuards.TruncateRepetition(TextGuards.Normalize(text));
        if (!IsMeaningful(text)) return (null, false);

        if (TextGuards.IsDegenerate(text))
        {
            Log.Write($"[asr] dropped repetitive output: {text[..Math.Min(80, text.Length)]}");
            return (null, false);
        }

        Log.Write($"[asr] {(partial ? "partial" : "run")} {samples.Length / 16000.0:0.00}s in {stopwatch.ElapsedMilliseconds}ms: {text}");
        return (text, false);
    }

    /// <summary>
    /// Keeps tuning itself while running: learns the speaking rate to size captions
    /// and nudges the commit gap when captions keep ending mid-sentence.
    /// </summary>
    private void UpdateAdaptiveStats(string text, double seconds)
    {
        if (seconds >= 0.8 && text.Length >= 2)
        {
            var rate = text.Length / seconds;
            _charsPerSecond = _charsPerSecond <= 0 ? rate : _charsPerSecond * 0.8 + rate * 0.2;
            _maxChars = Math.Clamp(_charsPerSecond * 7.0, 24, 72);
        }

        if (EndsWithSentencePunctuation(text) || text.Length <= 6) _cleanCuts++;
        else _midSentenceCuts++;
        _finalsSinceTune++;

        if (_finalsSinceTune < 6) return;

        var before = _gapAdaptFactor;
        var cutRatio = _midSentenceCuts / (double)_finalsSinceTune;
        if (cutRatio > 0.5) _gapAdaptFactor = Math.Min(1.5, _gapAdaptFactor * 1.12);
        else if (cutRatio < 0.2) _gapAdaptFactor = Math.Max(0.7, _gapAdaptFactor * 0.94);

        Log.Write($"[adapt] captions={_finalsSinceTune} midSentence={_midSentenceCuts} clean={_cleanCuts} chars/s={_charsPerSecond:0.0} maxChars={_maxChars:0} commitGap={_gapBaseMs * before:0}->{EffectiveGapMs:0}ms");

        _finalsSinceTune = 0;
        _midSentenceCuts = 0;
        _cleanCuts = 0;
    }

    private static string Combine(string prefix, string text)
    {
        if (prefix.Length == 0) return text;
        if (text.Length == 0) return prefix;
        var endsWithPunct = prefix[^1] is '。' or '！' or '？' or '!' or '?' or '、' or '，' or ',';
        return endsWithPunct ? prefix + text : prefix + "、" + text;
    }

    /// <summary>True for bare interjections ("あ。", "はい!", "えっ?") that read badly alone.</summary>
    private static bool IsInterjection(string text)
    {
        var content = 0;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) content++;
        }

        return content is > 0 and <= 4 && text.Length <= 8;
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
        // Cancel only: disposing the previous CTS here can race its Task.Delay and
        // throw ObjectDisposedException inside the fire-and-forget task.
        _partialDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _partialDebounce = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_settings.PartialTranslateDelayMs, cts.Token).ConfigureAwait(false);
                if (!cts.IsCancellationRequested && ReferenceEquals(_liveItem, item) && item.Original == text)
                {
                    EnqueueTranslation(item, text, final: false, context: _lastFinalText);
                }
            }
            catch (OperationCanceledException)
            {
                // superseded
            }
            finally
            {
                cts.Dispose();
            }
        }, CancellationToken.None);
    }

    private void EnqueueTranslation(CaptionItem item, string text, bool final, string? context = null)
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

        var job = new TranslationJob(item, text, final, target, context);
        if (!_translationChannel.Writer.TryWrite(job) && _translationChannel.Reader.TryRead(out _))
        {
            // Queue full: the backend is slower than the speaker. Drop the oldest
            // job (its caption stays on screen untranslated) and keep the newest.
            var dropped = Interlocked.Increment(ref _droppedTranslations);
            if (dropped % 20 == 1)
            {
                Log.Write($"[mt] translation queue full; dropped {dropped} stale jobs");
            }

            _translationChannel.Writer.TryWrite(job);
        }
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
                    job.Context,
                    ct).ConfigureAwait(false);

                await UiAsync(() => job.Item.Translation = translated).ConfigureAwait(false);
                ItemUpdated?.Invoke(job.Item);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // A single backend timeout must not stop translation for the rest
                // of the session: log it and move on to the next caption.
                Log.Write("[mt] translation request timed out; continuing");
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"翻译失败: {ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _vad?.Dispose();
    }

    private readonly record struct TranslationJob(CaptionItem Item, string Text, bool Final, LanguageOption Target, string? Context);
}
