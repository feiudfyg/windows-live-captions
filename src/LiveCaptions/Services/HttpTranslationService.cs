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

    /// <summary>Three or more consecutive English words - a sign of mixed-language drift.</summary>
    private static readonly System.Text.RegularExpressions.Regex EnglishRun =
        new(@"[A-Za-z']{2,}(?:[ ,.!?'’\x22-]+[A-Za-z']{2,}){2,}",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly AppSettings _settings;
    private readonly string? _endpointOverride;
    private string _model = "";
    private string _hardening = "";
    private int _consecutiveFailures;
    private long _totalMs;
    private int _count;

    public HttpTranslationService(AppSettings settings, string? endpointOverride = null)
    {
        _settings = settings;
        _endpointOverride = endpointOverride;
    }

    public bool IsLoaded { get; private set; }
    public string Backend { get; private set; } = "HTTP";
    public string ModelName => string.IsNullOrEmpty(_model) ? "(服务端模型)" : _model;
    public double AverageLatencyMs => _count == 0 ? 0 : _totalMs / (double)_count;

    private string BaseUrl => (_endpointOverride ?? _settings.LlmEndpoint).TrimEnd('/');

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

    public async Task<string> TranslateAsync(string text, LanguageOption target, Action<string>? onToken, string? context = null, CancellationToken ct = default)
    {
        if (!IsLoaded) throw new InvalidOperationException("翻译服务尚未就绪");
        if (string.IsNullOrWhiteSpace(text)) return "";

        var source = text.Trim();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await RequestAsync(source, target, _hardening, null, context, ct).ConfigureAwait(false);

        // Self-repair: models occasionally echo the input, loop or answer in the
        // wrong language. Retry once with a hardened prompt and remember what
        // fixed it for the rest of the session.
        var problem = Problem(source, result, target);
        if (problem is not null)
        {
            Log.Write($"[mt] retry ({problem})");
            var hardening = _hardening.Length > 0
                ? _hardening
                : "\n\nIMPORTANT: Output ONLY the translation in " + target.DisplayName
                  + ". Never answer in another language, never repeat the user's text.";
            var retry = await RequestAsync(source, target, hardening, 0.1, context, ct).ConfigureAwait(false);

            if (Problem(source, retry, target) is null)
            {
                _hardening = hardening;
                _consecutiveFailures = 0;
                result = retry;
                Log.Write("[mt] retry fixed the output - prompt hardening kept");
            }
            else
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= 2 && _hardening.Length > 0)
                {
                    _hardening = "";
                    _consecutiveFailures = 0;
                    Log.Write("[mt] prompt hardening disabled (not helping)");
                }

                if (retry.Length > 0) result = retry;

                // The hardened retry sometimes still drifts (e.g. half English
                // output). Try one direct machine-translation request and keep
                // whichever candidate looks more like the target language.
                var repaired = await RequestRepairAsync(source, target, ct).ConfigureAwait(false);
                if (repaired.Length > 0 && Problem(source, repaired, target) is null)
                {
                    result = repaired;
                    Log.Write($"[mt] repair fixed the output ({problem})");
                }
                else if (repaired.Length > 0 && Better(repaired, result, target))
                {
                    result = repaired;
                    Log.Write($"[mt] repair improved the output ({problem})");
                }

                // Last resort for drifted output: treat the bad translation itself as
                // the source. A plain "translate this into <target>" request lands in
                // the target language even when the original request did not.
                if (result.Length > 0)
                {
                    var rewritten = await RequestRepairAsync(result, target, ct).ConfigureAwait(false);
                    if (rewritten.Length > 0 && Problem(source, rewritten, target) is null)
                    {
                        result = rewritten;
                        Log.Write($"[mt] rewrite fixed the output ({problem})");
                    }
                    else if (rewritten.Length > 0 && Better(rewritten, result, target))
                    {
                        result = rewritten;
                    }
                }
            }
        }
        else
        {
            _consecutiveFailures = 0;
        }

        watch.Stop();
        _totalMs += watch.ElapsedMilliseconds;
        _count++;
        if (_count % 10 == 0)
        {
            Log.Write($"[mt] latency avg={AverageLatencyMs:0}ms over {_count} translations");
        }

        onToken?.Invoke(result);
        return result;
    }

    private Task<string> RequestAsync(string text, LanguageOption target, string hardening, double? temperature, string? context, CancellationToken ct)
        => RequestWithSystemAsync(
            text,
            TranslationText.SystemPrompt(target, LanguageCatalog.FindSource(_settings.SourceLanguage), context) + hardening,
            temperature,
            ct);

    /// <summary>Last-resort request: a direct machine-translation instruction without
    /// the elaborate system prompt, which some models turn into chit-chat.</summary>
    private Task<string> RequestRepairAsync(string text, LanguageOption target, CancellationToken ct)
    {
        var source = LanguageCatalog.FindSource(_settings.SourceLanguage);
        var described = string.Equals(source.Code, "auto", StringComparison.OrdinalIgnoreCase)
            ? "text"
            : $"{source.EnglishName} text";

        return RequestWithSystemAsync(
            text,
            $"You are a machine translation engine. Translate the user's {described} into {target.DisplayName}. "
            + $"Reply with the translation only, written entirely in {target.DisplayName}. No notes, no original text.",
            0,
            ct);
    }

    private async Task<string> RequestWithSystemAsync(string text, string system, double? temperature, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            // null = caller did not care; 0 from the repair prompt is deliberate
            // (deterministic fallback) and must not be replaced by the setting.
            ["temperature"] = temperature ?? _settings.TranslateTemperature,
            ["max_tokens"] = Math.Clamp(text.Length * 3, 64, _settings.MaxTranslateTokens),
            ["stream"] = false,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = text },
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

        // A 200 with a proxy error page or an unexpected shape must be a failed
        // request, not an exception that escapes the repair ladder.
        try
        {
            var json = JsonNode.Parse(payload);
            if (json?["choices"] is not JsonArray { Count: > 0 } choices ||
                choices[0]?["message"]?["content"] is not JsonValue content)
            {
                throw new InvalidDataException($"响应格式无法识别: {Trim(payload)}");
            }

            return TranslationText.Clean(content.GetValue<string>());
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"响应不是 JSON: {Trim(payload)}", ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"响应内容无法读取: {Trim(payload)}", ex);
        }
    }

    /// <summary>Which of two candidate translations looks more like the target language.</summary>
    private static bool Better(string candidate, string current, LanguageOption target)
    {
        if (target.Code.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
            target.Code.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return Score(candidate) > Score(current);

            static int Score(string text)
            {
                var score = 0;
                foreach (var ch in text)
                {
                    if (ch is >= '\u4e00' and <= '\u9fff' or >= '\u3040' and <= '\u30ff') score++;
                    else if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z') score -= 2;
                }

                return score;
            }
        }

        return false;
    }

    /// <summary>Detects outputs that suggest the model echoed, looped, ignored the
    /// task or answered in the wrong language.</summary>
    private static string? Problem(string source, string result, LanguageOption target)
    {
        if (string.IsNullOrWhiteSpace(result)) return "empty";

        static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());
        var src = Normalize(source);
        var res = Normalize(result);

        if (res.Length == 0) return "empty";
        if (string.Equals(src, res, StringComparison.OrdinalIgnoreCase)) return "echo";
        if (src.Length >= 6 && res.Contains(src, StringComparison.OrdinalIgnoreCase)) return "echo-mixed";
        if (result.Length >= 40 && result.Distinct().Count() <= 6) return "repetition";

        // Wrong script: a Chinese target must not come back as plain English or kana.
        if (target.Code.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            var cjk = 0;
            var latin = 0;
            var kana = 0;
            foreach (var ch in result)
            {
                if (ch is >= '\u4e00' and <= '\u9fff') cjk++;
                else if (ch is >= '\u3040' and <= '\u30ff') kana++;
                else if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z') latin++;
            }

            if (latin > cjk && latin >= 8) return "wrong-language(en)";
            if (kana > cjk && kana >= 8) return "wrong-language(kana)";

            // Mostly Chinese but with an English sentence left inside.
            if (latin >= 8 && EnglishRun.IsMatch(result)) return "mixed-language(en)";
        }

        return null;
    }

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200];

    public void Dispose()
    {
        IsLoaded = false;
    }
}
