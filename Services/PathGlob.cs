using System.Text;
using System.Text.RegularExpressions;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Matches file paths against glob patterns the way a shell user expects: <c>*</c> is any run of characters within one folder name,
/// <c>**</c> crosses folders (<c>src/**/*.cs</c> also matches <c>src/a.cs</c>), <c>?</c> is one character, and a pattern with no <c>/</c> is matched against
/// the file name wherever it is. Paths use forward slashes and are relative to the folder being searched. (Hook tool patterns are different:
/// there <c>*</c> crosses everything, see <see cref="Hooks.HookMatcher.Glob"/>.)</summary>
public static class PathGlob
{
    /// <summary>The longest a pattern may be.</summary>
    public const int MaxPatternLength = 300;

    /// <summary>Longest a match may take, so a pattern that backtracks badly cannot stall a search.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Compiles <paramref name="pattern"/>, or returns null (with the reason in <paramref name="error"/>) when it is empty or too long.</summary>
    public static Regex Compile(string pattern, out string error)
    {
        error = null;
        pattern = (pattern ?? "").Trim().Replace('\\', '/');
        if (pattern.Length == 0 || pattern.Length > MaxPatternLength)
        {
            error = $"The pattern must be 1 to {MaxPatternLength} characters.";
            return null;
        }
        while (pattern.StartsWith("./", StringComparison.Ordinal))
        {
            pattern = pattern[2..];
        }
        StringBuilder sb = new("^");
        // A pattern with no folder part matches by file name anywhere, as `*.cs` does in a shell search.
        if (!pattern.Contains('/'))
        {
            sb.Append("(?:.*/)?");
        }
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    // "**/" is any number of folders, including none; a bare "**" is anything.
                    if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                    {
                        sb.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        sb.Append(".*");
                        i++;
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, MatchTimeout);
    }

    /// <summary>Whether a compiled pattern matches <paramref name="relativePath"/>. A pattern that takes too long does not match.</summary>
    public static bool IsMatch(Regex compiled, string relativePath)
    {
        try
        {
            return compiled.IsMatch(relativePath);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
