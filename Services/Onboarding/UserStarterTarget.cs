using Hartsy.Extensions.LLMAssistant.Services.PromptAssets;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services.Onboarding;

/// <summary>The starter set's view of a real user. It saves through the same services as the settings screen and the chat, into the personal layer only, and needs the same permission each
/// kind needs there.</summary>
public sealed class UserStarterTarget(User user) : IStarterTarget
{
    /// <summary>What the user's trusted workspaces hold, read once for this target when first asked about.</summary>
    private List<PromptAsset> _inWorkspaces;

    /// <inheritdoc/>
    public string UserId => user.UserID;

    /// <inheritdoc/>
    public string Refusal(string kind)
    {
        if (kind == StarterKinds.Chat)
        {
            return user.HasPermission(LLMAssistantAPI.PermThreads) ? null : "Adding chats needs the LLM Threads permission.";
        }
        return user.HasPermission(LLMAssistantAPI.PermSettings) ? null : "Adding assistants and snippets needs the LLM Settings permission.";
    }

    /// <inheritdoc/>
    public bool Has(string kind, string id)
    {
        if (kind == StarterKinds.Assistant)
        {
            return StarterContent.Assistants.FirstOrDefault(a => a.Id == id) is { } starter && StarterMatching.HasAssistant(Assistants(), starter);
        }
        if (kind == StarterKinds.Snippet)
        {
            return StarterMatching.HasSnippet(PromptAssetService.For(user, null), PromptAssetService.RootFor(user, PromptAsset.ScopePersonal), id);
        }
        // The stored text itself, so a chat that cannot be read is left alone too.
        return user.GetGenericData(ThreadStorageService.DataName, ThreadStorageService.ThreadPrefix + id) is not null;
    }

    /// <inheritdoc/>
    public bool InWorkspace(string kind, string id)
    {
        if (kind != StarterKinds.Snippet)
        {
            return false;
        }
        _inWorkspaces ??= PromptAssetService.InTrustedWorkspaces(user);
        return StarterMatching.InWorkspace(_inWorkspaces, id);
    }

    /// <inheritdoc/>
    public bool HasAssistant(string assistantId) => Assistants()?[assistantId] is not null;

    /// <inheritdoc/>
    public string AddAssistant(StarterAssistant assistant)
    {
        lock (SettingsService.SettingsLock)
        {
            // Asked again under the lock every assistant save takes: a save made since the first look is not written over.
            return Has(StarterKinds.Assistant, assistant.Id) ? StarterSkips.AlreadyThere : AssistantService.SaveAssistant(assistant.ToJson(), user, SettingsService.ScopePersonal).Error;
        }
    }

    /// <inheritdoc/>
    public string AddSnippet(StarterSnippet snippet)
    {
        if (PromptAssetService.Save(user, PromptAsset.KindCommand, snippet.Name, snippet.Description, snippet.ArgumentHint, snippet.Body, false, PromptAsset.ScopePersonal, out string error, createOnly: true))
        {
            return null;
        }
        return error == PromptAssetService.AlreadyExists ? StarterSkips.AlreadyThere : error;
    }

    /// <summary>The assistants the user can see, by id.</summary>
    private JObject Assistants() => SettingsService.GetMergedSettings(user)["assistants"] as JObject;

    /// <inheritdoc/>
    public string AddChat(StarterChat chat, string assistantId)
    {
        JObject thread = chat.ToThread(UserId, assistantId, DateTime.UtcNow);
        // Its title is the one given; the automatic title never replaces it.
        ThreadStorageService.MarkTitleClaimed(thread);
        ThreadStorageService.SaveThread(user, thread);
        return null;
    }
}
