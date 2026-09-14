using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveCaptions.Models;

namespace LiveCaptions.Services;

/// <summary>
/// Translation through an OpenAI-compatible HTTP endpoint (e.g. LM Studio or
/// llama.cpp server). Lets the app use models that the bundled llama.cpp
/// runtime cannot load (for example brand-new architectures).
/// </summary>
public sealed class HttpTranslationService : ITranslator
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    private readonly AppSettings _settings;
    private string _model = "";

    public HttpTranslationService(AppSettings settings) => _settings = settings;

    public bool IsLoaded { get; private set; }
    public string Backend { get; private set; } = "HTTP";
    public string ModelName => string.IsNullOrEmpty(_model) ? "(服务端模型)" : _model;

    private string BaseUrl => _settings.LlmEndpoint.TrimEnd('/');

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (IsLoaded) return;

        JsonNode? models;
        try
        {
            models = await Http.GetFromJsonAsync<JsonNode>($"{BaseUrl}/models", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法连接翻译服务 {BaseUrl}: {ex.Message}", ex);
        }

        var first = models?["data"]?.AsArray().FirstOrDefault()?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(_settings.LlmEndpointModel))
        {
            _model = first ?? "";
        }
        else
        {
            _model = _settings.LlmEndpointModel;
        }

        if (string.IsNullOrEmpty(_model))
        {
            throw new InvalidOperationException("翻译服务未提供任何模型（请在 LM Studio 中加载模型）");
        }

        IsLoaded = true;
        Backend = $"HTTP {BaseUrl}";
        Log.Write($"[mt] http backend ready: {_model} @ {BaseUrl}");
    }

    public async Task<string> TranslateAsync(string text, LanguageOption target, Action<string>? onToken, CancellationToken ct = default)
    {
        if (!IsLoaded) throw new InvalidOperationException("翻译服务尚未就绪");
        if (string.IsNullOrWhiteSpace(text)) return "";

        var body = new JsonObject
        {
            ["model"] = _model,
            ["temperature"] = _settings.TranslateTemperature,
            ["max_tokens"] = Math.Clamp(text.Length * 3, 64, _settings.MaxTranslateTokens),
            ["stream"] = false,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = TranslationText.SystemPrompt(target) },
                new JsonObject { ["role"] = "user", ["content"] = text.Trim() },
            },
        };

        if (_settings.LlmHttpDisableThinking)
        {
            body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
        }

        using var response = await Http.PostAsJsonAsync($"{BaseUrl}/chat/completions", body, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"翻译请求失败 ({(int)response.StatusCode}): {Trim(payload)}");
        }

        var json = JsonNode.Parse(payload);
        var content = json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        var result = TranslationText.Clean(content);
        onToken?.Invoke(result);
        return result;
    }

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200];

    public void Dispose()
    {
        IsLoaded = false;
    }
}
