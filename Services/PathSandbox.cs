using System.IO;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Keeps a path inside a root folder. One check, used by everything that takes a path from a model: it refuses a path that climbs out
/// with <c>..</c>, an absolute path that is not inside the root, names the host reserves (Windows device names, alternate data streams,
/// UNC shares), and, unlike a purely textual check, follows symbolic links and junctions, so a link inside the root that points outside it
/// cannot be used to reach what lies beyond.</summary>
public static class PathSandbox
{
    /// <summary>Windows device names, which open a device rather than a file whatever their extension.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>How path text compares on this host: names are not case-sensitive on Windows.</summary>
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Resolves <paramref name="requested"/> (relative to <paramref name="root"/>, or absolute and inside it; empty means the root itself) to a full
    /// path inside the root. Returns false with the reason in <paramref name="error"/> (written for the model, never naming host paths) when it cannot.</summary>
    public static bool TryResolve(string root, string requested, out string fullPath, out string error)
    {
        fullPath = null;
        error = null;
        string rootFull = TrimEnd(Path.GetFullPath(root));
        requested = (requested ?? "").Trim().Replace('\\', '/');
        if (requested.Length == 0 || requested == ".")
        {
            fullPath = RealPath(rootFull);
            return Verify(rootFull, ref fullPath, out error);
        }
        if (requested.StartsWith("//", StringComparison.Ordinal))
        {
            error = "Network paths are not allowed.";
            return false;
        }
        if (requested.Contains('\0'))
        {
            error = "The path contains an invalid character.";
            return false;
        }
        bool rooted = Path.IsPathRooted(requested);
        string relative = rooted ? "" : requested;
        foreach (string segment in requested.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                error = "The path may not contain '..'. Use a path inside the workspace.";
                return false;
            }
            // A drive letter opens an absolute path; any other colon names an alternate data stream (or is not valid on Windows).
            bool driveLetter = rooted && segment.Length == 2 && segment[1] == ':' && char.IsLetter(segment[0]);
            if (!driveLetter && segment.Contains(':'))
            {
                error = "The path contains an invalid character (':').";
                return false;
            }
            if (!driveLetter && !IsAcceptableName(segment, out error))
            {
                return false;
            }
        }
        string candidate;
        try
        {
            candidate = rooted ? Path.GetFullPath(requested) : Path.GetFullPath(Path.Combine(rootFull, relative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "The path is not valid.";
            return false;
        }
        if (!IsInside(rootFull, candidate))
        {
            error = "The path is outside the workspace.";
            return false;
        }
        fullPath = RealPath(candidate);
        return Verify(rootFull, ref fullPath, out error);
    }

    /// <summary>Whether <paramref name="fullPath"/> is <paramref name="root"/> or something under it, by path text alone.</summary>
    public static bool IsInside(string root, string fullPath)
    {
        string rootFull = TrimEnd(Path.GetFullPath(root));
        string path = TrimEnd(Path.GetFullPath(fullPath));
        return path.Equals(rootFull, Comparison)
            || path.StartsWith(rootFull + Path.DirectorySeparatorChar, Comparison)
            || path.StartsWith(rootFull + Path.AltDirectorySeparatorChar, Comparison);
    }

    /// <summary>The path relative to the root, with forward slashes, for showing to the model. Empty for the root itself.</summary>
    public static string Relative(string root, string fullPath)
    {
        string relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        return relative == "." ? "" : relative;
    }

    /// <summary>After links are followed, the path must still be inside the root's own real location.</summary>
    private static bool Verify(string rootFull, ref string fullPath, out string error)
    {
        error = null;
        string realRoot = RealPath(rootFull);
        if (!IsInside(realRoot, fullPath))
        {
            fullPath = null;
            error = "The path leads outside the workspace (through a link).";
            return false;
        }
        return true;
    }

    /// <summary>The path with every symbolic link and junction on the way to it replaced by what it points at. A part that does not exist yet
    /// (a file about to be written) is kept as it is.</summary>
    internal static string RealPath(string fullPath)
    {
        string root = Path.GetPathRoot(fullPath) ?? "";
        string current = root;
        string[] segments = fullPath[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0)
            {
                continue;
            }
            current = Path.Combine(current, segments[i]);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            FileSystemInfo target = null;
            try
            {
                // Read before the existence check: a link whose target is missing does not "exist", but writing through it would create the target.
                target = info.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (IOException)
            {
                // A link that cannot be read is kept as its own path, which the caller then finds is not where it was asked to be.
            }
            if (target is not null)
            {
                current = target.FullName;
            }
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                // Nothing here yet, so nothing below it can be a link either.
                return Path.Combine([current, .. segments[(i + 1)..].Where(s => s.Length > 0)]);
            }
        }
        return TrimEnd(current);
    }

    /// <summary>Whether one path segment is an acceptable file or folder name.</summary>
    private static bool IsAcceptableName(string name, out string error)
    {
        error = null;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "The path contains an invalid character.";
            return false;
        }
        if (OperatingSystem.IsWindows())
        {
            string stem = name.Contains('.') ? name[..name.IndexOf('.')] : name;
            if (ReservedNames.Contains(stem.TrimEnd()))
            {
                error = $"'{name}' is a reserved name.";
                return false;
            }
            if (name.EndsWith('.') || name.EndsWith(' '))
            {
                error = "A name cannot end with a dot or a space.";
                return false;
            }
        }
        return true;
    }

    /// <summary>The path without a trailing separator (a drive or filesystem root keeps its own).</summary>
    private static string TrimEnd(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 || (OperatingSystem.IsWindows() && trimmed.Length == 2 && trimmed[1] == ':') ? path : trimmed;
    }
}
