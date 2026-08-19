using System.IO;
using SwarmUI.Core;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Builds and parses the browser-facing URLs for files this extension writes under SwarmUI's
/// output root (chat image uploads, assistant avatars, <c>file_write</c> results).
/// <para>Everything here emits paths relative to the <b>global</b> <c>Paths.OutputPath</c> — which
/// includes the <c>{userId}</c> segment whenever <c>AppendUserNameToOutputPath</c> is on (the default).
/// That is the <c>/View/{user}/{rest}</c> route's shape, not <c>/Output/</c>'s: the <c>/Output/</c> route
/// re-roots the request at the caller's own user folder, so an <c>Output/local/…</c> URL resolved to
/// <c>Output/local/local/…</c> and 404'd on every default install. <c>/View/</c> is also what SwarmUI's
/// own image history uses; <c>/Output/</c> is documented as legacy.</para></summary>
public static class OutputUrls
{
    /// <summary>Route prefix for URLs handed to the browser.</summary>
    public const string ViewPrefix = "View/";

    /// <summary>Legacy prefix still present in threads/assistants saved by older builds.</summary>
    public const string LegacyOutputPrefix = "Output/";

    /// <summary>The absolute, normalized global output root.</summary>
    public static string Root => Path.GetFullPath(Program.ServerSettings.Paths.OutputPath);

    /// <summary>Builds the served URL for a file already written somewhere under <see cref="Root"/>.</summary>
    public static string ForFullPath(string fullPath)
        => ViewPrefix + Path.GetRelativePath(Root, Path.GetFullPath(fullPath)).Replace('\\', '/');

    /// <summary>Whether <paramref name="url"/> is one of this extension's local output URLs.</summary>
    public static bool IsLocal(string url)
        => !string.IsNullOrEmpty(url)
        && (url.StartsWith(ViewPrefix, StringComparison.OrdinalIgnoreCase)
            || url.StartsWith(LegacyOutputPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Strips the route prefix, returning the path relative to <see cref="Root"/>. Returns null
    /// when <paramref name="url"/> isn't a local output URL.</summary>
    public static string ToRelativePath(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }
        if (url.StartsWith(ViewPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return url[ViewPrefix.Length..];
        }
        if (url.StartsWith(LegacyOutputPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return url[LegacyOutputPrefix.Length..];
        }
        return null;
    }

    /// <summary>Resolves a local output URL to a full path, or null if it isn't one / escapes the root.</summary>
    public static string ToFullPath(string url)
    {
        string rel = ToRelativePath(url);
        if (rel is null)
        {
            return null;
        }
        string root = Root;
        string full = Path.GetFullPath(Path.Combine(root, rel));
        // Compare against root + separator: a bare StartsWith would also accept a sibling folder
        // whose name merely begins with the root's (eg "Output" vs "Output-archive").
        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
