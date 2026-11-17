using System.IO;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>The one place this extension touches SwarmUI core through something that is not a documented extension point.
/// <para>Core offers no registration hook for these three things, so each is a small mutation of a public static, kept here with
/// the reason, so a core change has one file to fix and one proposal to track. See <c>docs/UPSTREAM-PROPOSALS.md</c> for the
/// core change that would remove each one. Everything else the extension does goes through documented hooks (API routes,
/// permissions, tabs, assets, user generic data, backend and parameter registration).</para></summary>
public static class CoreBridge
{
    /// <summary>Model types registered through <see cref="RegisterModelType"/>, by type name, with their model-root subfolder.</summary>
    private static readonly Dictionary<string, string> ModelTypes = [];

    /// <summary>Guards <see cref="ModelTypes"/> and the event subscription.</summary>
    private static readonly object ModelTypeLock = new();

    /// <summary>Whether <see cref="OnModelPathsChanged"/> is subscribed to core's event.</summary>
    private static bool Subscribed = false;

    /// <summary>Lets users store a per-user API key of this type.
    /// <para>Core's key registry (<c>UserUpstreamApiKeys.Register</c>, a documented hook) draws the User tab field, but the route that
    /// saves a key checks a second, separate set, so a registered type is refused ("Invalid key type") until it is added there too.</para></summary>
    public static void AcceptApiKeyType(string keyType)
    {
        BasicAPIFeatures.AcceptedAPIKeyTypes.Add(keyType);
    }

    /// <summary>Runs <paramref name="handler"/> on a generation's parameters before core parses prompt tags and wildcards.
    /// <para>Core's own tag pass is the first entry of its late-handler list and consumes the <c>&lt;llmprompt&gt;</c> tags, so a handler
    /// added the documented way (at the end) would never see them. Inserting first is the only ordering the list offers.</para></summary>
    public static void RunBeforeCorePromptParsing(Action<T2IParamInput> handler)
    {
        T2IParamInput.LateSpecialParameterHandlers.Insert(0, handler);
    }

    /// <summary>Registers a model type (a folder of model files under each model root) in core's model registry, and re-registers it
    /// whenever core rebuilds its model sets.
    /// <para>Core has no model-type registration call. Its set is a public dictionary that core clears on any model-path settings
    /// save, so the type has to be put back after each change.</para></summary>
    /// <param name="modelType">The type name shown to core, for example <c>LLM</c>.</param>
    /// <param name="subfolder">The folder under each model root that holds these models, for example <c>llm</c>.</param>
    public static void RegisterModelType(string modelType, string subfolder)
    {
        lock (ModelTypeLock)
        {
            ModelTypes[modelType] = subfolder;
            if (!Subscribed)
            {
                Program.ModelPathsChangedEvent += OnModelPathsChanged;
                Subscribed = true;
            }
        }
        EnsureModelType(modelType, subfolder);
    }

    /// <summary>Undoes the event subscription. The registered model sets stay, as core owns them.</summary>
    public static void Shutdown()
    {
        lock (ModelTypeLock)
        {
            if (Subscribed)
            {
                Program.ModelPathsChangedEvent -= OnModelPathsChanged;
                Subscribed = false;
            }
        }
    }

    /// <summary>Puts every registered type back after core cleared its sets. The event fires after core's own refresh pass, so each
    /// re-registered set has to refresh itself.</summary>
    private static void OnModelPathsChanged()
    {
        KeyValuePair<string, string>[] types;
        lock (ModelTypeLock)
        {
            types = [.. ModelTypes];
        }
        foreach (KeyValuePair<string, string> type in types)
        {
            EnsureModelType(type.Key, type.Value);
            if (Program.T2IModelSets.TryGetValue(type.Key, out T2IModelHandler handler))
            {
                handler.Refresh();
            }
        }
    }

    /// <summary>Creates the model set for a type if core does not currently have one.</summary>
    private static void EnsureModelType(string modelType, string subfolder)
    {
        if (Program.T2IModelSets.ContainsKey(modelType))
        {
            return;
        }
        T2IModelHandler handler = new() { ModelType = modelType };
        List<string> paths = [];
        foreach (string root in Program.ServerSettings.Paths.ActualModelRoots)
        {
            string path = Path.Combine(root, subfolder);
            Directory.CreateDirectory(path);
            paths.Add(path);
        }
        if (paths.Count > 0)
        {
            handler.FolderPaths = [.. paths];
            handler.DownloadFolderPath = paths[0];
        }
        Program.T2IModelSets[modelType] = handler;
        Logs.Info($"[LLMAssistant] Registered {modelType} model type with {paths.Count} folder(s).");
    }
}
