using System.IO;
using Hartsy.Extensions.LLMAssistant.Services.PromptAssets;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services.Onboarding;

/// <summary>The starter set's view of a real user. It saves through the same services as the settings screen and the chat, into the personal layer only, and needs the same permission each
/// kind needs there.</summary>
public sealed class UserStarterTarget(User user) : IStarterTarget
{
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
            return (SettingsService.GetMergedSettings(user)["assistants"] as JObject)?[id] is not null;
        }
        if (kind == StarterKinds.Snippet)
        {
            // The file is looked for as well as the loaded list, which leaves out a file too large to read: that one must not be written over either.
            string root = PromptAssetService.RootFor(user, PromptAsset.ScopePersonal);
            return PromptAssetService.For(user, null).Get(PromptAsset.KindCommand, id) is not null
                || (root is not null && File.Exists(Path.Combine(root, "commands", $"{id}.md")));
        }
        // The stored text itself, so a chat that cannot be read is left alone too.
        return user.GetGenericData(ThreadStorageService.DataName, ThreadStorageService.ThreadPrefix + id) is not null;
    }

    /// <inheritdoc/>
    public string AddAssistant(StarterAssistant assistant)
    {
        lock (SettingsService.SettingsLock)
        {
            // Asked again under the lock every assistant save takes: a save made since the first look is not written over.
            return Has(StarterKinds.Assistant, assistant.Id) ? null : AssistantService.SaveAssistant(assistant.ToJson(), user, SettingsService.ScopePersonal).Error;
        }
    }

    /// <inheritdoc/>
    public string AddSnippet(StarterSnippet snippet)
        => PromptAssetService.Save(user, PromptAsset.KindCommand, snippet.Name, snippet.Description, snippet.ArgumentHint, snippet.Body, false, PromptAsset.ScopePersonal, out string error) ? null : error;

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
