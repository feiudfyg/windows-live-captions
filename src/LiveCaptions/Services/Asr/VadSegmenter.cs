using SherpaOnnx;

namespace LiveCaptions.Services.Asr;

/// <summary>
/// Voice activity detector (sherpa-onnx). Replaces the hard energy threshold
/// with a learned speech detector so segmentation adapts to background music,
/// volume and speaking rate. TEN VAD is supported as well (quicker onsets, so
/// fewer clipped first syllables). The caller feeds 16 kHz mono blocks;
/// <see cref="SpeechActive"/> exposes the per-window decision.
/// </summary>
public sealed class VadSegmenter : IVad
{
    private readonly VoiceActivityDetector? _vad;
    private long _fedSamples;
    private float[] _scratch = [];

    public VadSegmenter(string? modelPath, float threshold = 0.5f, float minSilence = 0.15f)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) return;

        try
        {
            var config = new VadModelConfig();
            if (IsTenVad(modelPath!))
            {
                config.TenVad = new TenVadModelConfig
                {
                    Model = modelPath!,
                    Threshold = threshold,
                    MinSilenceDuration = minSilence,
                    MinSpeechDuration = 0.1f,
                    WindowSize = 256,
                    MaxSpeechDuration = 30f,
                };
                Engine = "TEN VAD";
            }
            else
            {
                config.SileroVad = new SileroVadModelConfig
                {
                    Model = modelPath!,
                    Threshold = threshold,
                    MinSilenceDuration = minSilence,
                    MinSpeechDuration = 0.2f,
                    WindowSize = 512,
                    MaxSpeechDuration = 30f,
                };
                Engine = "Silero VAD";
            }

            config.SampleRate = 16000;
            config.NumThreads = 1;
            config.Provider = "cpu";
            _vad = new VoiceActivityDetector(config, 120f);
        }
        catch (Exception ex)
        {
            Log.Write($"[vad] unavailable: {ex.Message}");
            _vad = null;
        }
    }

    /// <summary>Detector in use ("TEN VAD", "Silero VAD" or "none").</summary>
    public string Engine { get; private set; } = "none";

    public static bool IsTenVad(string modelPath)
        => Path.GetFileName(modelPath).Contains("ten-vad", StringComparison.OrdinalIgnoreCase)
           || Path.GetFileName(modelPath).Contains("ten_vad", StringComparison.OrdinalIgnoreCase);

    public bool IsAvailable => _vad is not null;

    /// <summary>Total samples accepted so far (absolute timeline).</summary>
    public long FedSamples => _fedSamples;

    /// <summary>True when the current analysis window contains speech.</summary>
    public bool SpeechActive { get; private set; }

    public void Accept(float[] samples, int count)
    {
        if (count <= 0) return;
        _fedSamples += count;
        if (_vad is null) return;

        try
        {
            if (count == samples.Length)
            {
                _vad.AcceptWaveform(samples);
            }
            else
            {
                if (_scratch.Length < count) _scratch = new float[count];
                Array.Copy(samples, _scratch, count);
                _vad.AcceptWaveform(_scratch);
            }

            SpeechActive = _vad.IsSpeechDetected();

            // Segments are only needed for boundary bookkeeping - drain the queue
            // so it cannot grow unbounded.
            while (!_vad.IsEmpty()) _vad.Pop();
        }
        catch
        {
            // VAD failures should never break capture; fall back to "speech" so
            // the pipeline keeps working (it degrades to the energy gate).
            SpeechActive = true;
        }
    }

    public void Reset()
    {
        _vad?.Reset();
        _fedSamples = 0;
        SpeechActive = false;
    }

    public void Dispose() => _vad?.Dispose();
}
