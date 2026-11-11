using System.Net.WebSockets;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.Backends;
using Hartsy.Extensions.LLMAssistant.LLMs;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>Model listing endpoints for LLM models.
/// Delegates to the registered <see cref="ILLMProvider"/>s (supplied by the removable backend pack)
/// so all available LLM models are included.</summary>
public static class ModelEndpoints
{
    /// <summary>Per-provider listing bound for the interactive dropdown — a single unresponsive backend
    /// shouldn't hold up the whole model list (which it would under its full connection timeout).</summary>
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Returns available LLM models from all registered LLM providers. Each provider is queried
    /// in parallel under a bounded timeout; whatever resolves is returned, and any provider that timed
    /// out or errored is reported in <c>warnings</c> so the UI can degrade gracefully instead of hanging.</summary>
    public static async Task<JObject> LLMAssistantGetModels(Session session)
    {
        List<ILLMProvider> providers = LLMProviderRegistry.All.ToList();
        (ILLMProvider Provider, List<LLMModelInfo> Models, string Error)[] results =
            await Task.WhenAll(providers.Select(ListWithTimeout));
        JArray models = [];
        JArray warnings = [];
        foreach ((ILLMProvider provider, List<LLMModelInfo> providerModels, string error) in results)
        {
            if (error is not null)
            {
                warnings.Add($"{provider.DisplayName}: {error}");
                continue;
            }
            foreach (LLMModelInfo model in providerModels)
            {
                JObject entry = model.ToJson();
                // Extension compat: add "title" as alias for "name" for the UI dropdown
                entry["title"] = entry["name"];
                models.Add(entry);
            }
        }
        return new JObject
        {
            ["success"] = true,
            ["models"] = models,
            ["warnings"] = warnings
        };
    }

    /// <summary>Answers a typed-decision request (state plus choice / score / true-false questions) with Cloudflare Clef
    /// through the local HartsyInference backend. The body is the Jev / SystemOne request; the response is its answer body.</summary>
    public static async Task<JObject> LLMAssistantDecide(Session session, JObject input)
    {
        HartsyLocalLLMProvider provider = LLMProviderRegistry.All.OfType<HartsyLocalLLMProvider>().FirstOrDefault();
        if (provider is null)
        {
            return new JObject { ["success"] = false, ["error"] = "The local HartsyInference LLM backend is not available." };
        }
        try
        {
            string answer = await provider.DecideAsync(input.ToString(), CancellationToken.None);
            JObject result = JObject.Parse(answer);
            result["success"] = true;
            return result;
        }
        catch (ArgumentException ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
        catch (SwarmReadableErrorException ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Downloads a variant of a downloadable local model (today <c>moss-vl-realtime</c>: <c>gguf-q4k</c>, <c>bf16</c>, <c>nf4</c>,
    /// <c>fp8</c>) into the Swarm LLM model folder, streaming progress. Request: <c>{"model":"moss-vl-realtime","variant":"nf4"}</c>.
    /// Frames out: <c>{"progress":{"file":"…","fraction":0.42}}</c> per file, then the returned <c>{"success":true,"path":"…"}</c> (or
    /// <c>{"success":false,"error":"…"}</c>). The client closing the socket cancels the download; files already finished are kept.
    /// Admin only: a download is gigabytes of disk and bandwidth.</summary>
    public static async Task<JObject> LLMAssistantDownloadModel(WebSocket socket, Session session, JObject input)
    {
        string model = input.Value<string>("model");
        string variant = input.Value<string>("variant");
        if (string.IsNullOrWhiteSpace(variant))
        {
            variant = "gguf-q4k";
        }
        if (!HartsyLocalLLMProvider.IsDownloadableModel(model, variant, out _, out string refusal))
        {
            return new JObject { ["success"] = false, ["error"] = refusal };
        }
        using CancellationTokenSource cancel = new();
        // The client closing its socket stops the download; the receive loop ends when it does.
        _ = ChatEndpoints.WatchForDisconnectAsync(socket, () => cancel.Cancel());
        // Progress callbacks run as the download streams; one reader sends them in order so frames never interleave on the socket.
        System.Threading.Channels.Channel<JObject> frames = System.Threading.Channels.Channel.CreateUnbounded<JObject>();
        Task sender = Task.Run(async () =>
        {
            await foreach (JObject frame in frames.Reader.ReadAllAsync())
            {
                if (socket.State != WebSocketState.Open)
                {
                    continue;
                }
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(frame.ToString(Newtonsoft.Json.Formatting.None));
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        });
        Dictionary<string, double> lastSent = [];
        try
        {
            string path = await HartsyLocalLLMProvider.DownloadModelAsync(model, variant, (asset, fraction) =>
            {
                // At most a frame per percent per file, so a multi-gigabyte shard does not flood the socket.
                lock (lastSent)
                {
                    if (lastSent.TryGetValue(asset.FileName, out double last) && fraction < 1.0 && fraction - last < 0.01)
                    {
                        return;
                    }
                    lastSent[asset.FileName] = fraction;
                }
                frames.Writer.TryWrite(new JObject { ["progress"] = new JObject { ["file"] = asset.FileName, ["fraction"] = fraction } });
            }, cancel.Token);
            return new JObject { ["success"] = true, ["path"] = path };
        }
        catch (OperationCanceledException)
        {
            return new JObject { ["success"] = false, ["error"] = "The download was cancelled. Files that finished are kept." };
        }
        catch (SwarmReadableErrorException ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
        finally
        {
            frames.Writer.TryComplete();
            await sender;
        }
    }

    /// <summary>Unloads every registered LLM provider's resident model to free VRAM/RAM (eg to make room for
    /// an image model). Remote/cloud providers are no-ops. Returns how many providers actually freed something
    /// so the UI can tell the user whether anything was loaded.</summary>
    public static async Task<JObject> LLMAssistantUnloadModels(Session session)
    {
        bool[] freed = await Task.WhenAll(LLMProviderRegistry.All.Select(async p =>
        {
            try { return await p.Unload(); }
            catch { return false; }
        }));
        return new JObject
        {
            ["success"] = true,
            ["freed"] = freed.Count(f => f),
            ["providers"] = freed.Length
        };
    }

    /// <summary>Lists one provider's models under <see cref="ListTimeout"/>, turning a timeout/error into a
    /// human-readable message instead of a thrown exception so one bad backend can't fail the whole call.</summary>
    private static async Task<(ILLMProvider, List<LLMModelInfo>, string)> ListWithTimeout(ILLMProvider provider)
    {
        using CancellationTokenSource cts = new(ListTimeout);
        try
        {
            return (provider, await provider.ListModels(cts.Token) ?? [], null);
        }
        catch (OperationCanceledException)
        {
            return (provider, null, $"did not respond within {ListTimeout.TotalSeconds:0}s (backend unreachable?)");
        }
        catch (Exception ex)
        {
            return (provider, null, ex.Message);
        }
    }
}
