using System.Collections.Concurrent;
using System.IO;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Text2Image;

namespace Hartsy.Extensions.LLMAssistant.Services.PromptStyles;

/// <summary>Reads what core knows about an image or video model from its model set (the one the Generate tab lists), without changing anything. Core works out each model's class from the file's header
/// when it lists the model, so this needs no backend and no loaded model.</summary>
public static class CoreModelFacts
{
    /// <summary>The name of core's main model set, the checkpoints and diffusion models the Generate tab picks from.</summary>
    public const string ModelSetName = "Stable-Diffusion";

    /// <summary>The most models listed to the browser.</summary>
    public const int MaxListed = 3000;

    /// <summary>The facts for the model called <paramref name="name"/>, or null when core has no such model or <paramref name="user"/> is not allowed to see it (the same per-role model
    /// restrictions core applies to its own model list, so this never tells a user a model exists that core would hide from them).</summary>
    public static ImageModelFacts For(string name, User user)
    {
        if (string.IsNullOrWhiteSpace(name) || !Program.T2IModelSets.TryGetValue(ModelSetName, out T2IModelHandler handler))
        {
            return null;
        }
        T2IModel model = handler.GetModel(name.Trim().Replace('\\', '/'));
        return model is null || !CanSee(user, model) ? null : Describe(model, withHeader: true);
    }

    /// <summary>The facts for every model core lists to <paramref name="user"/>, in name order (up to <see cref="MaxListed"/>).</summary>
    public static List<ImageModelFacts> ListAll(User user)
    {
        if (!Program.T2IModelSets.TryGetValue(ModelSetName, out T2IModelHandler handler))
        {
            return [];
        }
        return [.. handler.Models.Values.Where(m => CanSee(user, m)).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Take(MaxListed).Select(m => Describe(m, withHeader: false))];
    }

    /// <summary>Whether <paramref name="user"/> may see <paramref name="model"/>. No user means the server's own view, which sees everything.</summary>
    private static bool CanSee(User user, T2IModel model) => user is null || user.IsAllowedModel(model.Name);

    /// <summary>Prompt facts read from model files, by file path, kept while the file's size and time are unchanged so a header is read once.</summary>
    private static readonly ConcurrentDictionary<string, (long Length, long Ticks, ModelPromptFacts Facts)> HeaderCache = new();

    /// <summary>The facts for one of core's models. A model whose class core could not work out has no class or compat class id, so only a style matched by name can pick it. The file's own prompt keys are read
    /// only when <paramref name="withHeader"/> (one model asked for), not for the whole list, which would open every file.</summary>
    private static ImageModelFacts Describe(T2IModel model, bool withHeader)
    {
        T2IModelClass clazz = model.ModelClass;
        T2IModelCompatClass compat = clazz?.CompatClass;
        ModelPromptFacts prompt = withHeader ? ReadPromptFacts(model) : null;
        return new ImageModelFacts(model.Name, clazz?.ID, clazz?.Name, compat?.ID, compat is not null && (compat.IsText2Video || compat.IsImage2Video), prompt);
    }

    /// <summary>What the model file's header says about prompting, through core's own header reader. Null when it says nothing or the file cannot be read (a missing or odd file is not an error here).</summary>
    private static ModelPromptFacts ReadPromptFacts(T2IModel model)
    {
        string path = model.RawFilePath;
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }
            FileInfo info = new(path);
            if (HeaderCache.TryGetValue(path, out (long Length, long Ticks, ModelPromptFacts Facts) cached) && cached.Length == info.Length && cached.Ticks == info.LastWriteTimeUtc.Ticks)
            {
                return cached.Facts;
            }
            JObject header = T2IModel.GetMetadataHeaderFrom(path);
            ModelPromptFacts facts = ModelPromptFacts.FromMetadata(header?["__metadata__"] as JObject);
            HeaderCache[path] = (info.Length, info.LastWriteTimeUtc.Ticks, facts);
            return facts;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
