using System.Text;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>A line diff of two texts, in the unified form (<c>@@ -a,b +c,d @@</c> headers, then <c> </c>, <c>-</c> and <c>+</c> lines) that the interface draws as
/// coloured rows.</summary>
public static class TextDiff
{
    /// <summary>The most changed lines shown (and counted as lines of the diff) before it is cut, so one huge change cannot flood the browser.</summary>
    public const int MaxDiffLines = 4000;

    /// <summary>Lines of unchanged context around each change.</summary>
    public const int Context = 3;

    /// <summary>The largest middle section (lines before times lines after) compared line by line; beyond it the whole section is shown as replaced.</summary>
    private const long MaxCells = 4_000_000;

    /// <summary>What a diff found.</summary>
    /// <param name="Text">The unified diff, empty when the texts are equal.</param>
    /// <param name="Added">Lines only in the new text.</param>
    /// <param name="Removed">Lines only in the old text.</param>
    /// <param name="Truncated">The diff text was cut at <see cref="MaxDiffLines"/> lines (the counts are still complete).</param>
    public sealed record Result(string Text, int Added, int Removed, bool Truncated);

    /// <summary>Compares <paramref name="before"/> with <paramref name="after"/> line by line (an ending difference alone is not a change).</summary>
    public static Result Compare(string before, string after)
    {
        string[] a = SplitLines(before);
        string[] b = SplitLines(after);
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }
        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
        {
            suffix++;
        }
        int midA = a.Length - prefix - suffix;
        int midB = b.Length - prefix - suffix;
        if (midA == 0 && midB == 0)
        {
            return new Result("", 0, 0, false);
        }
        // Ops over the whole file: ' ' same, '-' removed, '+' added. The unchanged ends are not stored line by line, only counted.
        List<(char Op, string Line)> ops = [];
        for (int i = 0; i < prefix; i++)
        {
            ops.Add((' ', a[i]));
        }
        ops.AddRange(MiddleOps(a, b, prefix, midA, midB));
        for (int i = a.Length - suffix; i < a.Length; i++)
        {
            ops.Add((' ', a[i]));
        }
        int added = ops.Count(o => o.Op == '+');
        int removed = ops.Count(o => o.Op == '-');
        return new Result(Format(ops, out bool truncated), added, removed, truncated);
    }

    /// <summary>The text split into lines, the line endings dropped (so a file saved with the other ending is not all changed), and no empty last line for a final newline.</summary>
    private static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>The ops for the lines that differ: a longest-common-subsequence walk, or a plain replacement when the section is too large to compare.</summary>
    private static IEnumerable<(char Op, string Line)> MiddleOps(string[] a, string[] b, int start, int lengthA, int lengthB)
    {
        if ((long)lengthA * lengthB > MaxCells)
        {
            for (int i = 0; i < lengthA; i++)
            {
                yield return ('-', a[start + i]);
            }
            for (int j = 0; j < lengthB; j++)
            {
                yield return ('+', b[start + j]);
            }
            yield break;
        }
        // lcs[i, j]: the longest common run of a[start + i ..] and b[start + j ..].
        int[,] lcs = new int[lengthA + 1, lengthB + 1];
        for (int i = lengthA - 1; i >= 0; i--)
        {
            for (int j = lengthB - 1; j >= 0; j--)
            {
                lcs[i, j] = a[start + i] == b[start + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }
        int x = 0;
        int y = 0;
        while (x < lengthA && y < lengthB)
        {
            if (a[start + x] == b[start + y])
            {
                yield return (' ', a[start + x]);
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                yield return ('-', a[start + x++]);
            }
            else
            {
                yield return ('+', b[start + y++]);
            }
        }
        while (x < lengthA)
        {
            yield return ('-', a[start + x++]);
        }
        while (y < lengthB)
        {
            yield return ('+', b[start + y++]);
        }
    }

    /// <summary>Lays the ops out as hunks with <see cref="Context"/> lines around each change.</summary>
    private static string Format(List<(char Op, string Line)> ops, out bool truncated)
    {
        truncated = false;
        // Old and new line numbers at each op, so a hunk header can say where it starts.
        int[] oldAt = new int[ops.Count + 1];
        int[] newAt = new int[ops.Count + 1];
        for (int i = 0; i < ops.Count; i++)
        {
            oldAt[i + 1] = oldAt[i] + (ops[i].Op == '+' ? 0 : 1);
            newAt[i + 1] = newAt[i] + (ops[i].Op == '-' ? 0 : 1);
        }
        StringBuilder sb = new();
        int lines = 0;
        int index = 0;
        while (index < ops.Count)
        {
            if (ops[index].Op == ' ')
            {
                index++;
                continue;
            }
            // A hunk runs from Context lines before a change to Context lines after the last change that is within 2 * Context of the next.
            int first = Math.Max(0, index - Context);
            int last = index;
            int scan = index;
            while (scan < ops.Count)
            {
                if (ops[scan].Op != ' ')
                {
                    last = scan;
                }
                else if (scan - last > 2 * Context)
                {
                    break;
                }
                scan++;
            }
            int end = Math.Min(ops.Count, last + Context + 1);
            int oldCount = oldAt[end] - oldAt[first];
            int newCount = newAt[end] - newAt[first];
            sb.Append($"@@ -{oldAt[first] + (oldCount > 0 ? 1 : 0)},{oldCount} +{newAt[first] + (newCount > 0 ? 1 : 0)},{newCount} @@\n");
            lines++;
            for (int i = first; i < end; i++)
            {
                if (lines >= MaxDiffLines)
                {
                    truncated = true;
                    return sb.ToString().TrimEnd('\n');
                }
                sb.Append(ops[i].Op).Append(ops[i].Line).Append('\n');
                lines++;
            }
            index = end;
        }
        return sb.ToString().TrimEnd('\n');
    }
}
