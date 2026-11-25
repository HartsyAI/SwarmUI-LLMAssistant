using System.Text;
using System.Text.RegularExpressions;
using Hartsy.Extensions.LLMAssistant.Services.Knowledge;

namespace Hartsy.Extensions.LLMAssistant.Services.Documents;

/// <summary>What the extractors share: reading bytes as text, telling text from something else, and cutting text to a length. Works under invariant globalization, so only the
/// encodings built into the runtime are used.</summary>
public static class DocumentText
{
    /// <summary>The characters Windows-1252 puts where Latin-1 has control codes (0x80 to 0x9F), in order; a zero is a code it leaves undefined.</summary>
    private const string Windows1252 = "€\0‚ƒ„…†‡ˆ‰Š‹Œ\0Ž\0\0‘’“”•–—˜™š›œ\0žŸ";

    /// <summary>Reads bytes as text: the encoding a byte-order mark names (UTF-8 or UTF-16), else UTF-8 when the bytes are valid UTF-8, else Windows-1252 (the usual old single-byte
    /// encoding). Null when the bytes are not text.</summary>
    public static string Decode(byte[] data)
    {
        if (data is null)
        {
            return null;
        }
        string text;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            text = Encoding.UTF8.GetString(data, 3, data.Length - 3);
        }
        else if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            text = Encoding.Unicode.GetString(data, 2, data.Length - 2);
        }
        else if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            text = Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        }
        else
        {
            try
            {
                text = new UTF8Encoding(false, true).GetString(data);
            }
            catch (DecoderFallbackException)
            {
                char[] chars = new char[data.Length];
                for (int i = 0; i < data.Length; i++)
                {
                    byte value = data[i];
                    char mapped = value is >= 0x80 and <= 0x9F ? Windows1252[value - 0x80] : (char)value;
                    // An undefined code stays a control code, which counts against the bytes being text.
                    chars[i] = mapped == '\0' && value != 0 ? (char)value : mapped;
                }
                text = new string(chars);
            }
        }
        return IsUnreadable(text, 0.02) || KnowledgeText.LooksBinary(text) ? null : text;
    }

    /// <summary>Whether more than a share of the visible characters (at most the first 200,000 characters are looked at) are not text: control codes, replacement characters, private-use
    /// characters (what a font with no text mapping gives) or broken surrogate pairs.</summary>
    public static bool IsUnreadable(string text, double share)
    {
        int sample = Math.Min(text?.Length ?? 0, 200_000);
        int counted = 0;
        int bad = 0;
        for (int i = 0; i < sample; i++)
        {
            char c = text[i];
            // The escape code counts as ordinary: logs with coloured output are full of it.
            if (c is ' ' or '\t' or '\n' or '\r' or '\f' or '\u001B')
            {
                continue;
            }
            counted++;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    // Planes 15 and 16 are private use too.
                    if (c >= '\uDB80')
                    {
                        bad++;
                    }
                    i++;
                    continue;
                }
                bad++;
            }
            else if (char.IsControl(c) || c == '\uFFFD' || c is >= '\uE000' and <= '\uF8FF' || char.IsLowSurrogate(c))
            {
                bad++;
            }
        }
        return bad > counted * share;
    }

    /// <summary>The first <paramref name="max"/> characters of a text, never ending in half a surrogate pair.</summary>
    public static string Cut(string text, int max)
    {
        if (text is null || text.Length <= max)
        {
            return text ?? "";
        }
        int end = Math.Max(0, max);
        if (end > 0 && char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }
        return text[..end];
    }

    /// <summary>The result for a document that is one run of text (no pages): tidied the way the knowledge library stores text, cut to the limit, and refused when there is nothing in it.
    /// <paramref name="fileName"/> decides whether it is treated as a web page (tags dropped).</summary>
    public static ExtractedDocument Single(string kind, string raw, string fileName, DocumentLimits limits, bool alreadyTruncated = false)
    {
        string text;
        try
        {
            text = KnowledgeText.Normalize(raw, fileName);
        }
        catch (RegexMatchTimeoutException)
        {
            return ExtractedDocument.Failed(DocumentStatus.Malformed, kind, "That document could not be read as text in a reasonable time. Is it a very large or damaged web page?");
        }
        if (text.Length == 0)
        {
            return ExtractedDocument.Failed(DocumentStatus.NoTextLayer, kind, "There is no text in that document.");
        }
        bool cut = text.Length > limits.MaxChars;
        return new ExtractedDocument(DocumentStatus.Ok, kind, [cut ? Cut(text, limits.MaxChars) : text], 0, cut || alreadyTruncated, [], "");
    }
}
