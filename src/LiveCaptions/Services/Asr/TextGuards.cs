using System.Text;
using System.Text.RegularExpressions;

namespace LiveCaptions.Services.Asr;

/// <summary>
/// Guards against the repetition loops that autoregressive ASR/LLM models fall
/// into on noisy or hard audio ("Let's cheer up. Let's cheer up. ...").
/// </summary>
internal static partial class TextGuards
{
    /// <summary>
    /// Truncate a degenerate textual tail where a unit (sentence/word/phrase)
    /// repeats over and over. Returns the cleaned text.
    /// </summary>
    public static string TruncateRepetition(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var result = TruncateRepeatedSentences(text);
        result = TruncateRepeatedWords(result);
        return result.Trim();
    }

    /// <summary>True when the text is so repetitive that it should not be shown at all.</summary>
    public static bool IsDegenerate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var sentences = SentenceSplitRegex().Matches(text)
            .Select(m => m.Value.Trim())
            .Where(s => s.Length > 1)
            .ToList();

        if (sentences.Count >= 4)
        {
            var distinct = sentences.Select(s => s.ToLowerInvariant()).Distinct().Count();
            if (distinct <= Math.Max(1, sentences.Count / 4))
            {
                return true; // almost everything is the same sentence
            }
        }

        // Same word repeated many times in a row.
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 6)
        {
            var distinctWords = words.Select(w => w.ToLowerInvariant()).Distinct().Count();
            if (distinctWords <= Math.Max(1, words.Length / 6))
            {
                return true;
            }
        }

        return false;
    }

    private static string TruncateRepeatedSentences(string text)
    {
        var matches = SentenceSplitRegex().Matches(text);
        if (matches.Count < 3) return text;

        var units = matches.Select(m => m.Value.Trim()).ToList();
        var repeated = TrailingRepeatCount(units);
        if (repeated < 3) return text;

        // Keep everything up to (and including) the second occurrence.
        var keepCount = units.Count - repeated + 2;
        var last = matches[keepCount - 1];
        return text[..(last.Index + last.Length)].Trim();
    }

    private static string TruncateRepeatedWords(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 6) return text;

        var repeated = TrailingRepeatCount(words.ToList());
        if (repeated < 4) return text;

        var keepCount = words.Length - repeated + 2;
        return string.Join(' ', words.Take(keepCount)).Trim();
    }

    /// <summary>How many times the final unit repeats consecutively at the end.</summary>
    private static int TrailingRepeatCount(List<string> units)
    {
        if (units.Count == 0) return 0;

        var last = units[^1].ToLowerInvariant();
        var count = 1;
        for (var i = units.Count - 2; i >= 0; i--)
        {
            if (!string.Equals(units[i].Trim().ToLowerInvariant(), last, StringComparison.Ordinal)) break;
            count++;
        }

        return count;
    }

    [GeneratedRegex(@"[^。．.!?！？\r\n]+[。．.!?！？]*")]
    private static partial Regex SentenceSplitRegex();

    /// <summary>Collapse runs of whitespace and strip control characters for display.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            if (char.IsControl(ch) && ch != '\n')
            {
                continue;
            }

            if (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r')
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            lastWasSpace = false;
            sb.Append(ch);
        }

        return sb.ToString().Trim().TrimStart(LeadingJunk).TrimEnd(TrailingJunk).Trim();
    }

    private static readonly char[] LeadingJunk = ['・', '·', '-', '—', '–', '，', ',', '、', '。', '.', ' ', '\u3000'];
    private static readonly char[] TrailingJunk = ['・', '·', ' ', '\u3000'];
}
