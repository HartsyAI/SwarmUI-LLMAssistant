namespace Hartsy.Extensions.LLMAssistant.Services.ImageTools;

/// <summary>What <see cref="ModelNameResolver.Resolve"/> made of a requested model name.</summary>
/// <param name="Name">The installed model the request means, or null when none (or more than one) does.</param>
/// <param name="Suggestions">The closest installed names, best first, when <paramref name="Name"/> is null (up to <see cref="ModelNameResolver.MaxSuggestions"/>).</param>
/// <param name="Ambiguous">True when several installed models fit the request equally well, so none was picked.</param>
public sealed record ModelResolution(string Name, IReadOnlyList<string> Suggestions, bool Ambiguous)
{
    /// <summary>Whether a model was found.</summary>
    public bool Found => Name is not null;
}

/// <summary>Matches a model name a person or an LLM typed against the installed names, and when nothing fits well enough offers the nearest ones ("did you mean"). Pure: it knows nothing of SwarmUI.
/// <para>It accepts, in order: the exact name; the name without regard to case; the name without its extension or its folders (<c>sd_xl_base_1.0</c> for <c>OfficialStableDiffusion/sd_xl_base_1.0.safetensors</c>);
/// and a name that is the only one containing the request once punctuation is ignored. It never guesses between several fits, and never accepts a spelling mistake: a mistake gets suggestions.</para></summary>
public static class ModelNameResolver
{
    /// <summary>The most suggestions returned.</summary>
    public const int MaxSuggestions = 5;

    /// <summary>The lowest similarity (0 to 1) a name needs to be suggested.</summary>
    private const double MinSimilarity = 0.45;

    /// <summary>The extensions that are dropped when comparing names.</summary>
    private static readonly string[] Extensions = [".safetensors", ".sft", ".gguf", ".ckpt", ".pt", ".pth", ".bin", ".engine"];

    /// <summary>Finds the installed model that <paramref name="requested"/> means.</summary>
    public static ModelResolution Resolve(string requested, IReadOnlyCollection<string> installed)
    {
        string wanted = requested?.Trim() ?? "";
        if (wanted.Length == 0 || installed is null || installed.Count == 0)
        {
            return new ModelResolution(null, [], false);
        }
        string exact = installed.FirstOrDefault(n => n == wanted);
        if (exact is not null)
        {
            return new ModelResolution(exact, [], false);
        }
        // The same name in another case, then with the extension dropped, then with the folders dropped too: each step may fit several models, and then nothing is picked.
        foreach (Func<string, string> view in new Func<string, string>[] { n => n.ToLowerInvariant(), n => StripExtension(n).ToLowerInvariant(), n => Leaf(StripExtension(n)).ToLowerInvariant() })
        {
            string target = view(wanted.Replace('\\', '/'));
            List<string> fits = [.. installed.Where(n => view(n) == target).OrderBy(n => n, StringComparer.Ordinal)];
            if (fits.Count == 1)
            {
                return new ModelResolution(fits[0], [], false);
            }
            if (fits.Count > 1)
            {
                return new ModelResolution(null, [.. fits.Take(MaxSuggestions)], true);
            }
        }
        string key = Squash(Leaf(StripExtension(wanted.Replace('\\', '/'))));
        if (key.Length >= 3)
        {
            List<string> containing = [.. installed.Where(n => Squash(Leaf(StripExtension(n))).Contains(key)).OrderBy(n => n, StringComparer.Ordinal)];
            if (containing.Count == 1)
            {
                return new ModelResolution(containing[0], [], false);
            }
            if (containing.Count > 1)
            {
                return new ModelResolution(null, [.. containing.Take(MaxSuggestions)], true);
            }
        }
        return new ModelResolution(null, Suggest(wanted, installed), false);
    }

    /// <summary>The installed names closest to <paramref name="requested"/>, best first.</summary>
    public static List<string> Suggest(string requested, IEnumerable<string> installed)
    {
        string key = Squash(Leaf(StripExtension((requested ?? "").Replace('\\', '/'))));
        if (key.Length == 0)
        {
            return [];
        }
        return [.. installed
            .Select(n => (Name: n, Score: Similarity(key, Squash(Leaf(StripExtension(n))))))
            .Where(x => x.Score >= MinSimilarity)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(MaxSuggestions)
            .Select(x => x.Name)];
    }

    /// <summary>How alike two squashed names are, 0 to 1: edit distance, or a full score when one holds the other with little left over.</summary>
    internal static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }
        int longest = Math.Max(a.Length, b.Length);
        double score = 1.0 - (double)Distance(a, b) / longest;
        if (a.Length >= 3 && b.Contains(a) || b.Length >= 3 && a.Contains(b))
        {
            score = Math.Max(score, 0.5 + 0.5 * Math.Min(a.Length, b.Length) / longest);
        }
        return score;
    }

    /// <summary>The Levenshtein distance between two strings.</summary>
    internal static int Distance(string a, string b)
    {
        int[] previous = new int[b.Length + 1], current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>The name without a known model-file extension.</summary>
    private static string StripExtension(string name)
    {
        foreach (string extension in Extensions)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^extension.Length];
            }
        }
        return name;
    }

    /// <summary>The last path part of a name.</summary>
    private static string Leaf(string name) => name[(name.LastIndexOf('/') + 1)..];

    /// <summary>Lower case letters and digits only, so <c>SD-XL base_1.0</c> and <c>sdxlbase10</c> compare equal.</summary>
    private static string Squash(string name) => new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}
