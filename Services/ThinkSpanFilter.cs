using System.Text;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Removes inline <c>&lt;think&gt;</c> spans from streamed text, for speech. A tag may be split across deltas, so a possible
/// partial tag at the end of a delta is held until the next delta decides it.</summary>
public sealed class ThinkSpanFilter
{
    /// <summary>The tag that opens a span.</summary>
    private const string Open = "<think>";

    /// <summary>The tag that closes a span.</summary>
    private const string Close = "</think>";

    /// <summary>Whether the feed is inside a span, so its text is dropped.</summary>
    private bool _inside;

    /// <summary>A possible partial tag at the end of the last delta.</summary>
    private string _held = "";

    /// <summary>Returns the part of <paramref name="delta"/> that is outside think spans.</summary>
    public string Feed(string delta)
    {
        string text = _held + (delta ?? "");
        _held = "";
        StringBuilder visible = new();
        int pos = 0;
        while (pos < text.Length)
        {
            string tag = _inside ? Close : Open;
            int at = text.IndexOf(tag, pos, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                if (!_inside)
                {
                    visible.Append(text, pos, at - pos);
                }
                _inside = !_inside;
                pos = at + tag.Length;
                continue;
            }
            int keep = PartialTagLength(text, pos, tag);
            int end = text.Length - keep;
            if (!_inside)
            {
                visible.Append(text, pos, end - pos);
            }
            _held = text[end..];
            break;
        }
        return visible.ToString();
    }

    /// <summary>Ends the stream: a held partial tag is text, unless a span is still open, whose text is dropped.</summary>
    public string Flush()
    {
        string rest = _inside ? "" : _held;
        _held = "";
        _inside = false;
        return rest;
    }

    /// <summary>The length of the longest end of <paramref name="text"/> from <paramref name="pos"/> that starts <paramref name="tag"/>.</summary>
    private static int PartialTagLength(string text, int pos, string tag)
    {
        for (int length = Math.Min(tag.Length - 1, text.Length - pos); length > 0; length--)
        {
            if (text.EndsWith(tag[..length], StringComparison.OrdinalIgnoreCase))
            {
                return length;
            }
        }
        return 0;
    }
}
