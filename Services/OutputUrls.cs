using System.IO;
using SwarmUI.Core;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Builds/parses served URLs for files under the output root. Emits <c>/View/{user}/{rest}</c>
/// (the <c>/Output/</c> route re-roots at the caller's user folder and 404s on default installs).</summary>
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

    /// <summary>The characters that end or break a markdown link's address, or mean something else in a URL. Spaces and other white space are handled apart.</summary>
    private const string LinkUnsafe = "%#?()<>[]\"'`\\";

    /// <summary><paramref name="url"/>, a local output URL as core gives it, with the characters that stop it working as a markdown link percent-encoded (spaces and brackets among them). Core's
    /// <c>/View/</c> route reads the encoding back, as does <see cref="LocalPath"/>. Anything else, a data URI for one, is returned as it is.</summary>
    public static string ForMarkdown(string url)
    {
        if (!IsLocal(url))
        {
            return url;
        }
        return string.Concat(url.Select(c => char.IsWhiteSpace(c) || char.IsControl(c) || LinkUnsafe.Contains(c) ? Uri.EscapeDataString(c.ToString()) : c.ToString()));
    }

    /// <summary>The file a local output URL names: the URL as written when a file is there, else with its percent-encoding read (the <see cref="ForMarkdown"/> form). When neither is a file, the answer
    /// for the URL as written, which is null when it leaves the output root. <paramref name="toFullPath"/> and <paramref name="exists"/> are <see cref="ToFullPath"/> and <see cref="File.Exists"/>
    /// outside tests.</summary>
    internal static string LocalPath(string url, Func<string, string> toFullPath, Func<string, bool> exists)
    {
        string written = toFullPath(url);
        if (written is not null && exists(written))
        {
            return written;
        }
        string decoded = Uri.UnescapeDataString(url);
        return decoded != url && toFullPath(decoded) is string read && exists(read) ? read : written;
    }

    /// <summary><see cref="LocalPath(string, Func{string, string}, Func{string, bool})"/> on this server's output folder, for reading a file a request names.</summary>
    public static string LocalPath(string url) => LocalPath(url, ToFullPath, File.Exists);

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
        // root + separator, so a sibling folder like "Output-archive" can't pass.
        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
