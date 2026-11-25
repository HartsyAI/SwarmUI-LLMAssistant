using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.Onboarding;

/// <summary>The kinds of starter item.</summary>
public static class StarterKinds
{
    /// <summary>The kinds.</summary>
    public const string Assistant = "assistant", Snippet = "snippet", Chat = "chat";
}

/// <summary>Why an item was not added.</summary>
public static class StarterSkips
{
    /// <summary>The codes: the user already has one by that name, may not make that kind, or saving it failed.</summary>
    public const string Exists = "exists", Permission = "permission", Error = "error";
}

/// <summary>One user's own items, as the starter set sees them. There is no way to replace or remove anything through it.</summary>
public interface IStarterTarget
{
    /// <summary>The user's id, which their example chats' ids are made from.</summary>
    string UserId { get; }

    /// <summary>Why this user may not be given items of <paramref name="kind"/>, or null when they may.</summary>
    string Refusal(string kind);

    /// <summary>Whether the user can already see an item of this kind and id, their own or one shared with them.</summary>
    bool Has(string kind, string id);

    /// <summary>Adds a personal assistant. Null, or why it could not be saved.</summary>
    string AddAssistant(StarterAssistant assistant);

    /// <summary>Adds a personal slash command. Null, or why it could not be saved.</summary>
    string AddSnippet(StarterSnippet snippet);

    /// <summary>Adds a chat that uses <paramref name="assistantId"/>, under the id <see cref="StarterChat.IdFor"/> gives for this user. Null, or why it could not be saved.</summary>
    string AddChat(StarterChat chat, string assistantId);
}

/// <summary>Installs the starter set: each item the user does not have is added as an ordinary personal item they can edit or delete. An item that is there, edited or not, is never touched, so
/// running it again adds only what is missing (a deleted item included). It makes no network call and asks no model.</summary>
public static class StarterInstaller
{
    /// <summary>Held for a whole install, so two at once cannot both find an item missing.</summary>
    private static readonly object InstallLock = new();

    /// <summary>Every item of the set, in the order installed (assistants first, as the example chats belong to them).</summary>
    private static IEnumerable<(string Kind, string Id, string Title, Func<IStarterTarget, string> Add)> Items(string userId)
    {
        foreach (StarterAssistant assistant in StarterContent.Assistants)
        {
            yield return (StarterKinds.Assistant, assistant.Id, assistant.Name, target => target.AddAssistant(assistant));
        }
        foreach (StarterSnippet snippet in StarterContent.Snippets)
        {
            yield return (StarterKinds.Snippet, snippet.Name, $"/{snippet.Name}", target => target.AddSnippet(snippet));
        }
        foreach (StarterChat chat in StarterContent.Chats)
        {
            yield return (StarterKinds.Chat, chat.IdFor(userId), chat.Title,
                target => target.AddChat(chat, target.Has(StarterKinds.Assistant, chat.AssistantId) ? chat.AssistantId : AssistantConstants.DefaultId));
        }
    }

    /// <summary>What the set holds and which of it the user has: <c>{items[{kind, id, title, installed}], total, installed, missing}</c>.</summary>
    public static JObject Describe(IStarterTarget target)
    {
        JArray items = [];
        int installed = 0;
        foreach ((string kind, string id, string title, _) in Items(target.UserId))
        {
            bool has = target.Has(kind, id);
            installed += has ? 1 : 0;
            items.Add(new JObject { ["kind"] = kind, ["id"] = id, ["title"] = title, ["installed"] = has });
        }
        return new JObject { ["items"] = items, ["total"] = items.Count, ["installed"] = installed, ["missing"] = items.Count - installed };
    }

    /// <summary>Adds what is missing: <c>{added[{kind, id, title}], skipped[{kind, id, title, code, reason}]}</c>. One item that cannot be saved does not stop the others.</summary>
    public static (JArray Added, JArray Skipped) Install(IStarterTarget target)
    {
        JArray added = [], skipped = [];
        lock (InstallLock)
        {
            foreach ((string kind, string id, string title, Func<IStarterTarget, string> add) in Items(target.UserId))
            {
                string code = null, reason = null;
                try
                {
                    if (target.Has(kind, id))
                    {
                        (code, reason) = (StarterSkips.Exists, "You already have it.");
                    }
                    else if (target.Refusal(kind) is { } refusal)
                    {
                        (code, reason) = (StarterSkips.Permission, refusal);
                    }
                    else if (add(target) is { } error)
                    {
                        (code, reason) = (StarterSkips.Error, error);
                    }
                }
                catch (Exception ex)
                {
                    Logs.Warning($"[LLMAssistant] The starter {kind} '{id}' could not be added: {ex.Message}");
                    (code, reason) = (StarterSkips.Error, "It could not be saved (the server log says why).");
                }
                JObject entry = new() { ["kind"] = kind, ["id"] = id, ["title"] = title };
                if (code is null)
                {
                    added.Add(entry);
                }
                else
                {
                    entry["code"] = code;
                    entry["reason"] = reason;
                    skipped.Add(entry);
                }
            }
        }
        return (added, skipped);
    }
}
