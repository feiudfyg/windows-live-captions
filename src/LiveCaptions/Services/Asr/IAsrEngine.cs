namespace LiveCaptions.Services.Asr;

/// <summary>
/// Speech recognition backend. Implementations produce plain text for a
/// 16 kHz mono float PCM buffer.
/// </summary>
public interface IAsrEngine : IDisposable
{
    string Name { get; }

    /// <summary>Compute backend actually in use (e.g. "Vulkan", "CUDA", "CPU").</summary>
    string Backend { get; }

    bool IsLoaded { get; }

    Task LoadAsync(CancellationToken ct = default);

    /// <summary>Transcribe an utterance. <paramref name="context"/> is the tail of previous speech used as a hint.</summary>
    Task<string> TranscribeAsync(float[] samples, Models.LanguageOption? language, string? context, CancellationToken ct = default);
}

internal static class AudioUtil
{
    /// <summary>Simple linear resampler used only when a model requests a non-16 kHz rate.</summary>
    public static float[] Resample(float[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0) return input;

        var ratio = (double)toRate / fromRate;
        var output = new float[(int)(input.Length * ratio)];
        for (var i = 0; i < output.Length; i++)
        {
            var src = i / ratio;
            var i0 = (int)src;
            var i1 = Math.Min(i0 + 1, input.Length - 1);
            var frac = (float)(src - i0);
            output[i] = input[i0] * (1 - frac) + input[i1] * frac;
        }

        return output;
    }

    /// <summary>Wrap PCM samples in a minimal 16-bit WAV container (mtmd loads audio from containers).</summary>
    public static byte[] ToWav(float[] samples, int sampleRate)
    {
        using var ms = new MemoryStream(samples.Length * 2 + 44);
        using var bw = new BinaryWriter(ms);
        var dataSize = samples.Length * 2;

        bw.Write("RIFF"u8);
        bw.Write(36 + dataSize);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);           // PCM
        bw.Write((short)1);           // mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);     // byte rate
        bw.Write((short)2);           // block align
        bw.Write((short)16);          // bits per sample
        bw.Write("data"u8);
        bw.Write(dataSize);

        foreach (var sample in samples)
        {
            var value = (short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue);
            bw.Write(value);
        }

        bw.Flush();
        return ms.ToArray();
    }

    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return 0;
        double sum = 0;
        foreach (var s in samples) sum += (double)s * s;
        return Math.Sqrt(sum / samples.Length);
    }
}
