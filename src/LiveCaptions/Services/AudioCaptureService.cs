using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LiveCaptions.Services;

/// <summary>Where the captions listen: the playback loopback, a microphone, or both mixed.</summary>
public enum AudioInputMode
{
    System,
    Microphone,
    Both,
}

/// <summary>
/// Captures one or more audio endpoints (system loopback and/or microphone) and
/// produces a single 16 kHz mono float stream for speech recognition.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    public const int TargetSampleRate = 16000;
    private const int ChunkSamples = TargetSampleRate / 10; // 100 ms

    private sealed class Source
    {
        public required IWaveIn Capture { get; init; }
        public required BufferedWaveProvider Buffer { get; init; }
        public required ISampleProvider Chain { get; init; }
        public required string DeviceId { get; init; }
        public required string DeviceName { get; init; }
        public required bool IsLoopback { get; init; }
    }

    private readonly AudioInputMode _mode;
    private readonly List<Source> _sources = [];
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _pump;
    private DateTime _nextDeviceCheck = DateTime.UtcNow.AddSeconds(5);
    private int _restarting;

    public AudioCaptureService(AudioInputMode mode = AudioInputMode.System)
    {
        _mode = mode;
    }

    public event Action<float[], int>? SamplesAvailable;
    public event Action<string>? Failed;
    public event Action<string>? DeviceChanged;

    public AudioInputMode Mode => _mode;

    public bool IsRunning
    {
        get
        {
            lock (_sync) return _sources.Count > 0;
        }
    }

    public string DeviceName { get; private set; } = ModeLabel(AudioInputMode.System);

    public static AudioInputMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "mic" or "microphone" or "capture" => AudioInputMode.Microphone,
        "both" or "mixed" => AudioInputMode.Both,
        _ => AudioInputMode.System,
    };

    public static string ModeLabel(AudioInputMode mode) => mode switch
    {
        AudioInputMode.Microphone => "麦克风",
        AudioInputMode.Both => "系统音频 + 麦克风",
        _ => "系统音频",
    };

    public void Start()
    {
        if (IsRunning) return;

        var failures = new List<string>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (_mode is AudioInputMode.System or AudioInputMode.Both)
            {
                try
                {
                    // The capture keeps the device alive; do not dispose it here.
                    var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    AddSource(new WasapiLoopbackCapture(device), device, loopback: true);
                }
                catch (Exception ex)
                {
                    failures.Add($"系统音频: {ex.Message}");
                }
            }

            if (_mode is AudioInputMode.Microphone or AudioInputMode.Both)
            {
                try
                {
                    var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                    AddSource(new WasapiCapture(device), device, loopback: false);
                }
                catch (Exception ex)
                {
                    failures.Add($"麦克风: {ex.Message}");
                }
            }

            if (!IsRunning)
            {
                throw new InvalidOperationException(failures.Count > 0
                    ? string.Join("; ", failures)
                    : "没有可用的音频输入设备");
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _pump = Task.Run(() => PumpLoop(token));

            foreach (var source in Snapshot())
            {
                try
                {
                    source.Capture.StartRecording();
                }
                catch (Exception ex)
                {
                    failures.Add($"{source.DeviceName}: {ex.Message}");
                    RemoveSource(source);
                }
            }

            if (!IsRunning)
            {
                throw new InvalidOperationException(failures.Count > 0
                    ? string.Join("; ", failures)
                    : "音频输入设备启动失败");
            }

            DeviceName = DescribeSources();
            foreach (var failure in failures)
            {
                Log.Write($"[audio] input unavailable: {failure}");
            }

            DeviceChanged?.Invoke($"{DeviceName} ({Snapshot()[0].Capture.WaveFormat.SampleRate} Hz, {Snapshot()[0].Capture.WaveFormat.Channels} ch)");
        }
        catch (Exception ex)
        {
            Stop();
            Failed?.Invoke($"无法开始音频捕获: {ex.Message}");
        }
    }

    private void AddSource(IWaveIn capture, MMDevice device, bool loopback)
    {
        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(8),
            ReadFully = false,
        };

        ISampleProvider chain = buffer.ToSampleProvider();
        if (chain.WaveFormat.Channels >= 2)
        {
            chain = new StereoToMonoSampleProvider(chain) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }

        if (chain.WaveFormat.SampleRate != TargetSampleRate)
        {
            chain = new WdlResamplingSampleProvider(chain, TargetSampleRate);
        }

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;

        lock (_sync)
        {
            _sources.Add(new Source
            {
                Capture = capture,
                Buffer = buffer,
                Chain = chain,
                DeviceId = device.ID,
                DeviceName = device.FriendlyName,
                IsLoopback = loopback,
            });
        }
    }

    private void RemoveSource(Source source)
    {
        lock (_sync) _sources.Remove(source);

        try
        {
            source.Capture.StopRecording();
        }
        catch
        {
            // ignore
        }

        try
        {
            source.Capture.DataAvailable -= OnDataAvailable;
            source.Capture.RecordingStopped -= OnRecordingStopped;
            source.Capture.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    private Source[] Snapshot()
    {
        lock (_sync) return [.. _sources];
    }

    private string DescribeSources()
    {
        var parts = Snapshot().Select(s => s.IsLoopback ? $"系统音频: {s.DeviceName}" : $"麦克风: {s.DeviceName}");
        return string.Join(" + ", parts);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || sender is not IWaveIn wave) return;

        try
        {
            Source? source;
            lock (_sync)
            {
                source = _sources.FirstOrDefault(s => ReferenceEquals(s.Capture, wave));
            }

            source?.Buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }
        catch
        {
            // Ignore buffer races during shutdown.
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;

        // Common case: the audio service restarted or the endpoint was invalidated
        // (0x88890004 AUDCLNT_E_DEVICE_INVALIDATED). Rebuild the capture instead of
        // leaving the app deaf.
        Log.Write($"[audio] capture stopped: 0x{e.Exception.HResult:X8} {e.Exception.Message} - reconnecting");
        Restart();
    }

    /// <summary>Rebuilds all captures on a background thread (safe from capture callbacks).
    /// Retries a few times because an audio service restart makes the endpoint
    /// unavailable for a moment.</summary>
    private void Restart()
    {
        if (Interlocked.CompareExchange(ref _restarting, 1, 0) != 0) return;

        Task.Run(async () =>
        {
            try
            {
                for (var attempt = 1; attempt <= 4; attempt++)
                {
                    try
                    {
                        Stop();
                        Start();
                        if (IsRunning)
                        {
                            Log.Write($"[audio] reconnected on attempt {attempt}");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"[audio] reconnect attempt {attempt} failed: {ex.Message}");
                    }

                    await Task.Delay(1500).ConfigureAwait(false);
                }

                Failed?.Invoke("音频重新连接失败，请点击开始重新监听");
            }
            finally
            {
                Interlocked.Exchange(ref _restarting, 0);
            }
        });
    }

    private void PumpLoop(CancellationToken ct)
    {
        var mix = new float[ChunkSamples];
        var temp = new float[ChunkSamples];
        long chunks = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow >= _nextDeviceCheck)
                {
                    _nextDeviceCheck = DateTime.UtcNow.AddSeconds(5);
                    if (CheckDeviceChanged())
                    {
                        break; // a restart is in flight; this pump instance is done
                    }
                }

                var sources = Snapshot();
                if (sources.Length == 0)
                {
                    Thread.Sleep(10);
                    continue;
                }

                Array.Clear(mix, 0, mix.Length);
                var read = 0;
                foreach (var source in sources)
                {
                    int n;
                    try
                    {
                        n = source.Chain.Read(temp, 0, temp.Length);
                    }
                    catch (ObjectDisposedException)
                    {
                        n = 0;
                    }

                    for (var i = 0; i < n; i++) mix[i] += temp[i];
                    if (n > read) read = n;
                }

                if (read > 0)
                {
                    for (var i = 0; i < read; i++)
                    {
                        mix[i] = Math.Clamp(mix[i], -1f, 1f);
                    }

                    chunks++;
                    if (chunks == 1 || chunks % 200 == 0)
                    {
                        Log.Write($"[audio] pump chunks={chunks} lastRead={read} sources={sources.Length}");
                    }

                    SamplesAvailable?.Invoke(mix, read);
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Failed?.Invoke($"音频读取失败: {ex.Message}");
                break;
            }
        }
    }

    /// <summary>
    /// Capture is bound to a device instance; when the user switches speakers or
    /// microphones the stream goes silent forever. Poll the default endpoints and
    /// rebuild the capture when one changes.
    /// </summary>
    private bool CheckDeviceChanged()
    {
        if (Interlocked.CompareExchange(ref _restarting, 1, 0) != 0) return false;

        var changed = false;
        try
        {
            var sources = Snapshot();
            using var enumerator = new MMDeviceEnumerator();

            var loopback = sources.FirstOrDefault(s => s.IsLoopback);
            if (loopback is not null)
            {
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                changed |= !string.Equals(device.ID, loopback.DeviceId, StringComparison.OrdinalIgnoreCase);
            }

            var microphone = sources.FirstOrDefault(s => !s.IsLoopback);
            if (microphone is not null)
            {
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                changed |= !string.Equals(device.ID, microphone.DeviceId, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Device enumeration is best-effort.
        }

        if (!changed)
        {
            Interlocked.Exchange(ref _restarting, 0);
            return false;
        }

        Log.Write("[audio] default input device changed, restarting capture");
        Interlocked.Exchange(ref _restarting, 0);
        Restart();
        return true;
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        foreach (var source in Snapshot())
        {
            RemoveSource(source);
        }

        try
        {
            _pump?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore
        }

        _pump = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();
}
