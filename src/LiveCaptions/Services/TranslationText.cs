using System.Text;
using System.Text.RegularExpressions;
using LiveCaptions.Models;

namespace LiveCaptions.Services;

/// <summary>Shared prompt construction and output cleanup for translation.</summary>
internal static partial class TranslationText
{
    public static string SystemPrompt(LanguageOption target, LanguageOption? source = null, string? context = null)
    {
        var sourceKnown = source is not null &&
                          !string.Equals(source.Code, "auto", StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.Append("You are a professional real-time subtitle translator. ");
        sb.Append(sourceKnown
            ? $"Translate the {source!.EnglishName} text into {target.EnglishName} ({target.DisplayName}). "
            : $"Translate the user's text into {target.EnglishName} ({target.DisplayName}). ");

        if (sourceKnown)
        {
            // Live ASR output is noisy; naming the source language stops the model
            // from reading Japanese kanji as their Chinese look-alikes.
            sb.Append($"The input is live {source!.EnglishName} speech recognition output: it may contain ")
              .Append("homophone errors, missing particles, or words cut off mid-sentence, and some characters ")
              .Append($"are {source.EnglishName} usages rather than look-alike words of another language. ")
              .Append("Infer the speaker's intended meaning from context and translate that meaning naturally; ")
              .Append("do not render broken fragments literally. ")
              .Append("Loanwords often have several possible readings: choose the one the situation implies ")
              .Append("(e.g. ライブ at a music event is a concert or live performance, not a live stream). ");
        }

        sb.Append("Output only the translation itself: no explanations, no notes, no quotes, no original text. ");
        sb.Append("Never drop the end of a sentence, and never add facts that are not implied by the input. ");
        sb.Append("Keep names, numbers, technical terms and the original tone.");

        if (!string.IsNullOrWhiteSpace(context))
        {
            // Carry-over so names and pronouns stay consistent between captions.
            sb.Append(" For context, the previous subtitle was: \"").Append(context.Trim())
              .Append("\" - do not translate it, but keep names and pronouns consistent with it.");
        }

        return sb.ToString();
    }

    /// <summary>Raw ChatML prompt used with the local GGUF path.</summary>
    public static string ChatMlPrompt(string text, LanguageOption target, LanguageOption? source = null, string? context = null)
    {
        var sb = new StringBuilder();
        sb.Append("<|im_start|>system\n");
        sb.Append(SystemPrompt(target, source, context));
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
