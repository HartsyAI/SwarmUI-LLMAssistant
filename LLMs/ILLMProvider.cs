using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;

namespace SwarmUI.Extensions.LLMAssistant.LLMs;

/// <summary>The single seam between the extension and whatever actually runs an LLM.
/// <para>Everything in the stable extension core (chat, threads, tools, companion, T2I) talks only
/// to this interface and the extension's own DTOs — it never names a concrete backend or SwarmUI's
/// <c>AbstractLLMBackend</c>. The concrete implementations live in the removable <c>Backends/</c>
/// pack and register themselves via <see cref="LLMProviderRegistry"/>.</para>
/// <para>When SwarmUI ships a rich native LLM API, delete the pack and add one thin
/// <c>ILLMProvider</c> that adapts the native backend — nothing else changes.</para></summary>
public interface ILLMProvider
{
    /// <summary>Stable provider id (eg <c>"hartsy-local"</c>, <c>"anthropic"</c>).</summary>
    string Id { get; }

    /// <summary>Human-facing provider name.</summary>
    string DisplayName { get; }

    /// <summary>Lists every model this provider can currently serve. Implementations that make a
    /// network call must honor <paramref name="ct"/> so callers can bound an unresponsive endpoint
    /// (eg the model dropdown) instead of waiting out the full connection timeout.</summary>
    Task<List<LLMModelInfo>> ListModels(CancellationToken ct = default);

    /// <summary>Generates a full (non-streamed) response.</summary>
    Task<string> Generate(ExtendedLLMInput input);

    /// <summary>Generates with live chunks. <paramref name="onChunk"/> receives streaming output
    /// objects; <paramref name="ct"/> must be honored so the chat UI's Stop button cancels a single
    /// generation mid-stream.</summary>
    Task GenerateLive(ExtendedLLMInput input, string batchId, Action<JObject> onChunk, CancellationToken ct);

    /// <summary>Exact token count for the given text, or null if this provider can't tokenize cheaply
    /// (the caller then falls back to a chars/4 heuristic). Implementations must never block on a
    /// model load — return null instead.</summary>
    int? CountTokens(string text);

    /// <summary>Unloads any resident model(s) to free VRAM/RAM (eg to make room for an image model).
    /// Returns true if something was actually freed. A no-op for providers that hold nothing locally
    /// (remote/cloud). The next request lazily reloads.</summary>
    Task<bool> Unload();
}

/// <summary>In-memory registry of the LLM providers supplied by the removable backend pack.
/// <para>Empty until the pack calls <see cref="Register"/> from extension init. With an empty
/// registry the dispatcher simply reports "no LLM backend running", exactly as before.</para></summary>
public static class LLMProviderRegistry
{
    private static readonly ConcurrentDictionary<string, ILLMProvider> _providers = new();

    /// <summary>Registers (or replaces) a provider by its <see cref="ILLMProvider.Id"/>.</summary>
    public static void Register(ILLMProvider provider)
    {
        if (provider is not null && !string.IsNullOrEmpty(provider.Id))
        {
            _providers[provider.Id] = provider;
        }
    }

    /// <summary>Removes a provider by id (used when a pack is torn down).</summary>
    public static void Unregister(string id)
    {
        if (!string.IsNullOrEmpty(id))
        {
            _providers.TryRemove(id, out _);
        }
    }

    /// <summary>All registered providers.</summary>
    public static IReadOnlyCollection<ILLMProvider> All => _providers.Values.ToList();

    /// <summary>Gets a provider by id, or null.</summary>
    public static ILLMProvider Get(string id) => id is not null && _providers.TryGetValue(id, out ILLMProvider p) ? p : null;
}
