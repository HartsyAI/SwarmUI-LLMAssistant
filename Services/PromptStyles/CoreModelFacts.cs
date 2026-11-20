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
        return model is null || !CanSee(user, model) ? null : Describe(model);
    }

    /// <summary>The facts for every model core lists to <paramref name="user"/>, in name order (up to <see cref="MaxListed"/>).</summary>
    public static List<ImageModelFacts> ListAll(User user)
    {
        if (!Program.T2IModelSets.TryGetValue(ModelSetName, out T2IModelHandler handler))
        {
            return [];
        }
        return [.. handler.Models.Values.Where(m => CanSee(user, m)).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Take(MaxListed).Select(Describe)];
    }

    /// <summary>Whether <paramref name="user"/> may see <paramref name="model"/>. No user means the server's own view, which sees everything.</summary>
    private static bool CanSee(User user, T2IModel model) => user is null || user.IsAllowedModel(model.Name);

    /// <summary>The facts for one of core's models. A model whose class core could not work out has no class or compat class id, so only a style matched by name can pick it.</summary>
    private static ImageModelFacts Describe(T2IModel model)
    {
        T2IModelClass clazz = model.ModelClass;
        T2IModelCompatClass compat = clazz?.CompatClass;
        return new ImageModelFacts(model.Name, clazz?.ID, clazz?.Name, compat?.ID, compat is not null && (compat.IsText2Video || compat.IsImage2Video));
    }
}
