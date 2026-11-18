using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.Hooks;

/// <summary>Decides whether a rule hook applies to a tool call. Pure: no settings, no I/O.</summary>
public static class HookMatcher
{
    /// <summary>The longest a regular expression may take on one argument, so a pattern that backtracks badly cannot stall a turn.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Whether a tool pattern (<c>*</c> any run of characters, <c>?</c> any one) matches a tool id, ignoring case.</summary>
    public static bool ToolMatches(string pattern, string toolId) => Glob(pattern, toolId);

    /// <summary>The compiled form of each glob seen, so a pattern is built once rather than on every call of every tool. Bounded: when it fills it is
    /// emptied, since a hook layer holds few patterns and the next calls rebuild the ones in use.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> GlobCache = new();

    /// <summary>Most globs kept at once.</summary>
    private const int MaxCachedGlobs = 512;

    /// <summary>Whether a glob (<c>*</c> any run of characters, <c>?</c> any one) matches the whole text, ignoring case. A pattern that takes
    /// longer than the time limit on this text does not match, rather than failing the call it was checking.</summary>
    public static bool Glob(string pattern, string text)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }
        if (!GlobCache.TryGetValue(pattern, out Regex regex))
        {
            if (GlobCache.Count >= MaxCachedGlobs)
            {
                GlobCache.Clear();
            }
            regex = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
            GlobCache[pattern] = regex;
        }
        try
        {
            return regex.IsMatch(text ?? "");
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Compiles a regular expression the way hooks run it; false when it is not valid.</summary>
    public static bool TryCompile(string pattern, out Regex regex)
    {
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            regex = null;
            return false;
        }
    }

    /// <summary>Whether a rule hook applies to a call: its tool pattern matches and, when it names an argument, that argument is present and
    /// matches its value. A hook that names no argument applies to every call of the tool.</summary>
    public static bool Applies(ToolHook hook, string toolId, JObject args)
    {
        if (!ToolMatches(hook.Tool, toolId))
        {
            return false;
        }
        if (string.IsNullOrEmpty(hook.Argument))
        {
            return true;
        }
        string text = ArgumentText(hook.Argument, args);
        if (text is null)
        {
            return false;
        }
        try
        {
            return hook.MatchType switch
            {
                ToolHook.MatchGlob => Glob(hook.Value, text),
                ToolHook.MatchContains => text.Contains(hook.Value, StringComparison.OrdinalIgnoreCase),
                ToolHook.MatchRegex => TryCompile(hook.Value, out Regex regex) && regex.IsMatch(text),
                _ => true,
            };
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>The text of one argument (or of all of them for <c>*</c>), or null when the call does not have it.</summary>
    public static string ArgumentText(string argument, JObject args)
    {
        if (args is null)
        {
            return null;
        }
        if (argument == "*")
        {
            return args.ToString(Newtonsoft.Json.Formatting.None);
        }
        JToken token = args[argument];
        if (token is null || token.Type == JTokenType.Null)
        {
            return null;
        }
        return token.Type == JTokenType.String ? token.ToString() : token.ToString(Newtonsoft.Json.Formatting.None);
    }
}
