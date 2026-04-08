using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.Services;

namespace SwarmUI.Extensions.LLMAssistant.WebAPI;

/// <summary>Thread CRUD endpoints.</summary>
public static class ThreadEndpoints
{
    public static async Task<JObject> LLMAssistantGetThreads(Session session)
    {
        JArray threads = ThreadStorageService.GetThreadIndex(session.User);
        return new JObject
        {
            ["success"] = true,
            ["threads"] = threads
        };
    }

    public static async Task<JObject> LLMAssistantGetThread(Session session, string threadId)
    {
        JObject thread = ThreadStorageService.GetThread(session.User, threadId);
        if (thread is null)
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = $"Thread '{threadId}' not found."
            };
        }
        return new JObject
        {
            ["success"] = true,
            ["thread"] = thread
        };
    }

    public static async Task<JObject> LLMAssistantSaveThread(Session session, JObject rawInput)
    {
        JObject thread = rawInput["thread"] as JObject;
        if (thread is null)
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = "No thread data provided."
            };
        }
        ThreadStorageService.SaveThread(session.User, thread);
        return new JObject
        {
            ["success"] = true,
            ["thread"] = thread
        };
    }

    public static async Task<JObject> LLMAssistantDeleteThread(Session session, string threadId)
    {
        bool deleted = ThreadStorageService.DeleteThread(session.User, threadId);
        return new JObject
        {
            ["success"] = deleted,
            ["error"] = deleted ? null : $"Thread '{threadId}' not found."
        };
    }
}
