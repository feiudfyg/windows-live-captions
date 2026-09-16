using System.Net.Http.Json;
using System.Text.Json;

namespace LiveCaptions.Services.Asr;

/// <summary>
/// FireRedVAD streaming detector served by the PyTorch sidecar (/vad endpoint).
/// Blocks are buffered to 100 ms and resolved synchronously; the returned frame
/// flags (10 ms each) are reduced to a single per-block decision.
/// </summary>
public sealed class RemoteVadSegmenter : IVad
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _baseAddress;
    private readonly float[] _pending = new float[1600];
    private int _pendingCount;
    private bool _firstBlock = true;
    private bool _broken;

    public RemoteVadSegmenter(string baseAddress)
    {
        _baseAddress = baseAddress.TrimEnd('/');
    }

    public bool IsAvailable => !_broken;

    public string Engine => "FireRedVAD (流式)";

    public long FedSamples { get; private set; }

    public bool SpeechActive { get; private set; }

    public void Accept(float[] samples, int count)
    {
        if (count <= 0) return;
        FedSamples += count;

        var offset = 0;
        while (offset < count)
        {
            var take = Math.Min(_pending.Length - _pendingCount, count - offset);
            Array.Copy(samples, offset, _pending, _pendingCount, take);
            _pendingCount += take;
            offset += take;

            if (_pendingCount == _pending.Length)
            {
                Flush();
                _pendingCount = 0;
            }
        }
    }

    private void Flush()
    {
        if (_broken) return;

        try
        {
            var bytes = new byte[_pendingCount * sizeof(float)];
            Buffer.BlockCopy(_pending, 0, bytes, 0, bytes.Length);

            var url = _firstBlock ? $"{_baseAddress}/vad?reset=1" : $"{_baseAddress}/vad";
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var response = _http.Send(new HttpRequestMessage(HttpMethod.Post, url) { Content = content });
            _firstBlock = false;

            if (!response.IsSuccessStatusCode)
            {
                _broken = true;
                Log.Write($"[vad] FireRed endpoint returned {(int)response.StatusCode}; falling back to silence");
                return;
            }

            using var stream = response.Content.ReadAsStream();
            using var document = JsonDocument.Parse(stream);
            SpeechActive = document.RootElement.TryGetProperty("speech", out var speech) && speech.GetBoolean();
        }
        catch (Exception ex)
        {
            // A broken VAD must not stop capture: treat everything as speech so the
            // pipeline degrades to the plain energy gate behaviour.
            _broken = true;
            SpeechActive = true;
            Log.Write($"[vad] FireRed unavailable: {ex.Message}");
        }
    }

    public void Reset()
    {
        _pendingCount = 0;
        _firstBlock = true;
        SpeechActive = false;
        FedSamples = 0;
    }

    public void Dispose() => _http.Dispose();
}
