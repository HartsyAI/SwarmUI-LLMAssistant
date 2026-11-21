using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Hartsy.Extensions.LLMAssistant.Services.PromptRewrite;

/// <summary>Hides Swarm's own angle-bracket tags (<c>&lt;lora:name:0.8&gt;</c>, <c>&lt;wildcard:x&gt;</c>, <c>&lt;random:a,b&gt;</c>, <c>&lt;segment:...&gt;</c>, a <c>&lt;bbox[...]:...&gt;</c> the user wrote) behind numbered placeholders
/// before a prompt goes to the rewriting model, and puts them back after. The model never sees tag text it could mangle, and the result can be checked in code: every placeholder must come back exactly once.</summary>
public sealed class SwarmTagMask
{
    /// <summary>The start and end of a placeholder, written so it cannot be taken for prompt text or for a Swarm tag.</summary>
    public const string Open = "{{SWARM_TAG_", Close = "}}";

    /// <summary>The most tags masked in one prompt; a prompt with more is left to the caller to refuse.</summary>
    public const int MaxTags = 200;

    /// <summary>A tag's inside starts with a name, an optional <c>[...]</c> and then <c>:</c> or the end (<c>lora:x</c>, <c>break</c>, <c>bbox[0.1,0.1]:x</c>); anything else with an angle bracket is plain text.</summary>
    private static readonly Regex TagStart = new(@"^[A-Za-z][A-Za-z0-9_]*(\[[^\]]*\])?(:|$)", RegexOptions.Compiled);

    /// <summary>Matches one placeholder and its number.</summary>
    private static readonly Regex Placeholder = new(Regex.Escape(Open) + @"(\d+)" + Regex.Escape(Close), RegexOptions.Compiled);

    /// <summary>The prompt with every tag replaced by its placeholder.</summary>
    public string Masked { get; }

    /// <summary>The hidden tags, in order; the tag at index <c>n</c> is behind <c>{{SWARM_TAG_n}}</c>.</summary>
    public IReadOnlyList<string> Tags { get; }

    private SwarmTagMask(string masked, List<string> tags)
    {
        Masked = masked;
        Tags = tags;
    }

    /// <summary>The placeholder for tag <paramref name="index"/>.</summary>
    public static string PlaceholderFor(int index) => $"{Open}{index}{Close}";

    /// <summary>Hides the tags in <paramref name="text"/>. A tag may hold other tags (<c>&lt;random:&lt;lora:a&gt;,b&gt;</c>): the whole outer tag is hidden as one. An angle bracket that opens no tag, or never closes, stays as text.</summary>
    public static SwarmTagMask Mask(string text)
    {
        text ??= "";
        StringBuilder masked = new(text.Length);
        List<string> tags = [];
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '<' && i + 1 < text.Length && char.IsAsciiLetter(text[i + 1]))
            {
                int end = FindClose(text, i);
                if (end > i && TagStart.IsMatch(text.AsSpan(i + 1, end - i - 1).ToString()))
                {
                    masked.Append(PlaceholderFor(tags.Count));
                    tags.Add(text[i..(end + 1)]);
                    i = end + 1;
                    continue;
                }
            }
            masked.Append(text[i]);
            i++;
        }
        return new SwarmTagMask(masked.ToString(), tags);
    }

    /// <summary>The index of the <c>&gt;</c> that closes the tag opened at <paramref name="start"/>, counting nested angle brackets, or -1 when it never closes.</summary>
    private static int FindClose(string text, int start)
    {
        int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            if (text[i] == '<')
            {
                depth++;
            }
            else if (text[i] == '>')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }
        return -1;
    }

    /// <summary>What is wrong with how <paramref name="output"/> carries the placeholders, or null when each one is there exactly once and none is invented. The text is checked before the tags are put back.</summary>
    public string Check(string output)
    {
        int[] seen = new int[Tags.Count];
        foreach (Match match in Placeholder.Matches(output ?? ""))
        {
            if (!int.TryParse(match.Groups[1].Value, out int index) || index < 0 || index >= Tags.Count)
            {
                return $"it contains {match.Value}, which is not one of the placeholders it was given";
            }
            seen[index]++;
        }
        for (int i = 0; i < seen.Length; i++)
        {
            if (seen[i] == 0)
            {
                return $"it dropped {PlaceholderFor(i)}; every placeholder must appear exactly once";
            }
            if (seen[i] > 1)
            {
                return $"it repeated {PlaceholderFor(i)}; every placeholder must appear exactly once";
            }
        }
        // Every real tag of the idea is a placeholder now, so any raw tag in the answer was written by the model (or planted by the idea): never let it through.
        SwarmTagMask invented = Mask(output);
        if (invented.Tags.Count > 0)
        {
            return $"it wrote the Swarm tag {invented.Tags[0]} itself; tags may only come from the placeholders it was given";
        }
        return null;
    }

    /// <summary>Puts the tags back into <paramref name="output"/>. With <paramref name="insideJsonString"/> each tag is escaped for a JSON string value (quotes and backslashes), so a tag with a quote in it
    /// cannot break the JSON around it.</summary>
    public string Restore(string output, bool insideJsonString = false)
    {
        return Placeholder.Replace(output ?? "", match =>
        {
            if (!int.TryParse(match.Groups[1].Value, out int index) || index < 0 || index >= Tags.Count)
            {
                return match.Value;
            }
            string tag = Tags[index];
            return insideJsonString ? JsonConvert.ToString(tag)[1..^1] : tag;
        });
    }
}
