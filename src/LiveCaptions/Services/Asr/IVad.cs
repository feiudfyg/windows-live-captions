namespace LiveCaptions.Services.Asr;

/// <summary>
/// Voice activity detector fed with 16 kHz mono blocks. Implementations decide
/// per block whether the window contains speech; the pipeline uses that to cut
/// utterances at natural pauses.
/// </summary>
public interface IVad : IDisposable
{
    bool IsAvailable { get; }

    /// <summary>Detector in use (for logs): "TEN VAD", "FireRedVAD", ...</summary>
    string Engine { get; }

    /// <summary>Total samples accepted so far (absolute timeline).</summary>
    long FedSamples { get; }

    /// <summary>True when the most recent analysis window contains speech.</summary>
    bool SpeechActive { get; }

    void Accept(float[] samples, int count);

    void Reset();
}
