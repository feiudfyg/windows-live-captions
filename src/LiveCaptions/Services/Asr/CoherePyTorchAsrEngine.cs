using System.Net.Http.Headers;
using System.Text.Json;
using LiveCaptions.Models;

namespace LiveCaptions.Services.Asr;

/// <summary>
/// Full precision Cohere Transcribe (official bf16 weights, PyTorch CUDA) served
/// by a local Python sidecar. Measured on an RTX 5090: ~100 ms per 8 s window,
/// 157x realtime on a 60 s clip.
/// </summary>
public sealed class CoherePyTorchAsrEngine : IAsrEngine
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private readonly PyTorchAsrServer _server;
    private readonly string _defaultLanguage;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CoherePyTorchAsrEngine(PyTorchAsrServer server, string? defaultLanguage = null)
    {
        _server = server;
        _defaultLanguage = string.IsNullOrWhiteSpace(defaultLanguage) || defaultLanguage == "auto"
            ? "ja"
            : defaultLanguage;
    }

    public string Name => "Cohere Transcribe（PyTorch 全精度）";

    public string Backend => IsLoaded ? $"CUDA · {_server.ActualPort} 端口" : "启动中";

    public bool IsLoaded { get; private set; }

    /// <summary>Base address of the sidecar once it is running (also serves /vad).</summary>
    public string? ServerAddress => _server.IsRunning ? _server.BaseAddress : null;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _server.StartAsync(ct).ConfigureAwait(false);
        IsLoaded = true;
    }

    public async Task<string> TranscribeAsync(float[] samples, LanguageOption? language, string? context, CancellationToken ct = default)
    {
        if (samples.Length == 0) return string.Empty;

        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var code = language?.Code ?? _defaultLanguage;
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var response = await Http
                .PostAsync($"{_server.BaseAddress}/transcribe?language={Uri.EscapeDataString(code)}", content, ct)
                .ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"识别服务返回 {(int)response.StatusCode}: {payload}");
            }

            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("text", out var text)
                ? text.GetString()?.Trim() ?? ""
                : "";
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        IsLoaded = false;
        _gate.Dispose();
        _server.Dispose();
    }
}
