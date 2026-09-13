using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LiveCaptions.Services;

/// <summary>
/// Captures the default playback device via WASAPI loopback and produces
/// 16 kHz mono float samples for speech recognition.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    public const int TargetSampleRate = 16000;
    private const int ChunkSamples = TargetSampleRate / 10; // 100 ms

    private WasapiLoopbackCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private ISampleProvider? _chain;
    private CancellationTokenSource? _cts;
    private Task? _pump;

    public event Action<float[], int>? SamplesAvailable;
    public event Action<string>? Failed;
    public event Action<string>? DeviceChanged;

    public bool IsRunning => _capture is not null;
    public string DeviceName { get; private set; } = "(默认播放设备)";

    public void Start()
    {
        if (_capture is not null) return;

        try
        {
            var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            DeviceName = device.FriendlyName;

            _capture = new WasapiLoopbackCapture(device);

            _buffer = new BufferedWaveProvider(_capture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(8),
                ReadFully = false,
            };

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            ISampleProvider chain = _buffer.ToSampleProvider();
            if (chain.WaveFormat.Channels >= 2)
            {
                chain = new StereoToMonoSampleProvider(chain) { LeftVolume = 0.5f, RightVolume = 0.5f };
            }

            if (chain.WaveFormat.SampleRate != TargetSampleRate)
            {
                chain = new WdlResamplingSampleProvider(chain, TargetSampleRate);
            }

            _chain = chain;
            _cts = new CancellationTokenSource();
            _pump = Task.Run(() => PumpLoop(_cts.Token));

            _capture.StartRecording();
            DeviceChanged?.Invoke($"{DeviceName} ({_capture.WaveFormat.SampleRate} Hz, {_capture.WaveFormat.Channels} ch)");
        }
        catch (Exception ex)
        {
            Stop();
            Failed?.Invoke($"无法开始音频捕获: {ex.Message}");
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded > 0)
        {
            try
            {
                _buffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
            }
            catch
            {
                // Ignore buffer races during shutdown.
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            Failed?.Invoke($"音频捕获停止: {e.Exception.Message}");
        }
    }

    private void PumpLoop(CancellationToken ct)
    {
        var buffer = new float[ChunkSamples];
        var chain = _chain!;
        long chunks = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var read = chain.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    chunks++;
                    if (chunks == 1 || chunks % 200 == 0)
                    {
                        Log.Write($"[audio] pump chunks={chunks} lastRead={read}");
                    }

                    SamplesAvailable?.Invoke(buffer, read);
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

        try
        {
            _capture?.StopRecording();
        }
        catch
        {
            // ignore
        }

        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
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
        _buffer = null;
        _chain = null;
    }

    public void Dispose() => Stop();
}
