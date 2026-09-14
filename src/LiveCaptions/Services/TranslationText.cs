using System.Text;
using System.Text.RegularExpressions;
using LiveCaptions.Models;

namespace LiveCaptions.Services;

/// <summary>Shared prompt construction and output cleanup for translation.</summary>
internal static partial class TranslationText
{
    public static string SystemPrompt(LanguageOption target)
    {
        var sb = new StringBuilder();
        sb.Append("You are a professional real-time subtitle translator. ");
        sb.Append($"Translate the user's text into {target.EnglishName} ({target.DisplayName}). ");
        sb.Append("Output only the translation itself: no explanations, no notes, no quotes, no original text. ");
        sb.Append("Keep names, numbers, technical terms and the original tone. ");
        sb.Append($"If the text is already in {target.EnglishName}, output it unchanged.");
        return sb.ToString();
    }

    /// <summary>Raw ChatML prompt used with the local GGUF path.</summary>
    public static string ChatMlPrompt(string text, LanguageOption target)
    {
        var sb = new StringBuilder();
        sb.Append("<|im_start|>system\n");
        sb.Append(SystemPrompt(target));
        sb.Append("<|im_end|>\n");
        sb.Append("<|im_start|>user\n").Append(text.Trim()).Append("<|im_end|>\n");
        sb.Append("<|im_start|>assistant\n");

        // Qwen3.x chat template: prefill an empty <think> block to disable thinking.
        sb.Append("<think>\n\n</think>\n\n");
        return sb.ToString();
    }

    public static string Clean(string raw)
    {
        var text = raw;

        var endThink = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (endThink >= 0)
        {
            text = text[(endThink + "</think>".Length)..];
        }
        else
        {
            var startThink = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (startThink == 0)
            {
                return ""; // reasoning in progress - nothing to display yet
            }

            if (startThink > 0)
            {
                text = text[..startThink];
            }
        }

        text = ThinkBlockRegex().Replace(text, "");
        text = text.Replace("<think>", " ").Replace("</think>", " ");
        text = text.Replace("<|im_end|>", " ").Replace("<|im_start|>", " ").Replace("<|endoftext|>", " ");
        text = text.Replace("\r", " ").Replace("\n", " ");
        text = text.Trim().Trim('"', '\'', '“', '”', '‘', '’').Trim();

        // Defensive: drop an echoed "Translation:" style prefix.
        var prefixIndex = text.IndexOf(':');
        if (prefixIndex is > 0 and < 24)
        {
            var prefix = text[..prefixIndex].ToLowerInvariant();
            if (prefix.Contains("translation") || prefix.Contains("译文") || prefix.Contains("翻译"))
            {
                text = text[(prefixIndex + 1)..].Trim();
            }
        }

        return Asr.TextGuards.TruncateRepetition(text);
    }

    [GeneratedRegex(@" thinking.*?<｜end▁of▁thinking｜>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlockRegex();
}
