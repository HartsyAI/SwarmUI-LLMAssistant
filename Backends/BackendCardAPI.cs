using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using Hartsy.Extensions.LLMAssistant.LLMs;

namespace Hartsy.Extensions.LLMAssistant.Backends;

/// <summary>Routes the LLM backend cards under Server &gt; Backends use to fill their dropdowns (see Assets/llm-backend-cards.js).</summary>
public static class BackendCardAPI
{
    /// <summary>Longest a card waits for a model list, so an offline server cannot hold it for the server's full connect timeout.</summary>
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Registers the card routes. Viewing the backends list is enough, since they change nothing.</summary>
    public static void Register()
    {
        API.RegisterAPICall(LLMAssistantListGpus, false, Permissions.ViewBackendsList);
        API.RegisterAPICall(LLMAssistantListBackendModels, false, Permissions.ViewBackendsList);
    }

    /// <summary>The GPU choices for one Device value of the local LLM card, so the card can relist GPU when Device changes.</summary>
    public static Task<JObject> LLMAssistantListGpus(Session session, string device = "auto")
    {
        (string kind, IReadOnlyList<LocalDeviceChoices.Choice> choices) = LocalDeviceChoices.ForDevice(device);
        return Task.FromResult(new JObject
        {
            ["kind"] = kind,
            ["values"] = new JArray(choices.Select(c => c.Value)),
            ["names"] = new JArray(choices.Select(c => c.Name))
        });
    }

    /// <summary>Default Model choices for one remote LLM backend, asked with the viewer's own key: the server's list (sorted) or Anthropic's (newest first).</summary>
    /// <remarks>An empty list carries its cause in <c>reason</c>, not <c>error</c>, which core would log on every Backends tab visit.</remarks>
    public static async Task<JObject> LLMAssistantListBackendModels(Session session, int backend_id)
    {
        if (!Program.Backends.AllBackends.TryGetValue(backend_id, out BackendHandler.BackendData data))
        {
            return new JObject { ["error"] = $"No backend with ID {backend_id}." };
        }
        using CancellationTokenSource cts = new(ListTimeout);
        try
        {
            if (data.AbstractBackend is RemoteOpenAILLMProvider openai)
            {
                List<LLMModelInfo> listed = await openai.ListModelsAs(new ExtendedLLMInput { RequestSession = session }, cts.Token);
                return ModelChoices(listed.Select(m => (m.Id, m.Name)).OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase));
            }
            if (data.AbstractBackend is AnthropicLLMProvider anthropic)
            {
                List<(string Id, string Name)> models = await anthropic.ListApiModelsAs(session, cts.Token);
                return models is null ? ModelChoices([], "Set your own Anthropic key in the User tab to list Anthropic's models.") : ModelChoices(models);
            }
            return ModelChoices([]);
        }
        catch (OperationCanceledException)
        {
            return ModelChoices([], "The server did not answer in time.");
        }
        catch (Exception ex)
        {
            // Best effort: any failure leaves the card's own list or text box in place.
            Logs.Debug($"[LLMAssistant] Could not list models for backend {backend_id}: {ex.Message}");
            return ModelChoices([], ex.Message);
        }
    }

    /// <summary>The <c>{values, names}</c> a card dropdown takes: each model once, labeled "Name (id)" when the name differs.</summary>
    internal static JObject ModelChoices(IEnumerable<(string Id, string Name)> models, string reason = null)
    {
        JArray values = [];
        JArray names = [];
        HashSet<string> seen = [];
        foreach ((string id, string name) in models)
        {
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                continue;
            }
            values.Add(id);
            names.Add(string.IsNullOrWhiteSpace(name) || name == id ? id : $"{name} ({id})");
        }
        JObject result = new() { ["values"] = values, ["names"] = names };
        if (reason is not null)
        {
            result["reason"] = reason;
        }
        return result;
    }
}
