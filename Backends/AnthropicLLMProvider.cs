using System.IO;
using System.Net.Http;
using System.Text;
using FreneticUtilities.FreneticDataSyntax;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.DataHolders;
using Hartsy.Extensions.LLMAssistant.LLMs;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Backends;

/// <summary>LLM backend for the Anthropic Messages API (Claude models). Uses the calling user's
/// per-user <c>anthropic_api</c> key (set in the User tab).</summary>
public class AnthropicLLMProvider : LLMProviderBackend
{
    /// <summary>Settings saved to Data/Backends.fds by field name, so renaming or retyping a field breaks every saved backend.</summary>
    /// <remarks>Assets/llm-backend-cards.js groups these fields into the card's sections.</remarks>
    public class AnthropicLLMProviderSettings : AutoConfiguration
    {
        [ConfigComment("The Claude model used when a request doesn't name one.\nWith your own Anthropic key set in the User tab, this list comes from Anthropic, so new models show up.")]
        [ManualSettingsOptions(Vals = ["claude-fable-5-1", "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-opus-5", "claude-fable-5", "claude-sonnet-5", "claude-opus-4-8", "claude-opus-4-7", "claude-opus-4-6", "claude-sonnet-4-6", "claude-haiku-4-5", "claude-opus-4-5-20251101", "claude-sonnet-4-5-20250929", "claude-haiku-4-5-20251001"],
            ManualNames = ["Claude Fable 5.1", "Claude Opus 5.5", "Claude Sonnet 5.5", "Claude Haiku 5.5", "Claude Opus 5", "Claude Fable 5", "Claude Sonnet 5", "Claude Opus 4.8", "Claude Opus 4.7", "Claude Opus 4.6", "Claude Sonnet 4.6", "Claude Haiku 4.5", "Claude Opus 4.5", "Claude Sonnet 4.5", "Claude Haiku 4.5 (2025-10-01)"])]
        public string DefaultModel = "claude-opus-4-8";

        [ConfigComment("Longest a whole reply may take, in seconds, before it is stopped.\nRaise it if long replies get cut off.")]
        public int TimeoutSeconds = 120;

        [ConfigComment("Only needed when the API key is not tied to one workspace. Leave it empty otherwise.\nA user's own workspace ID, set with their key in the User tab, is used instead.")]
        public string WorkspaceId = "";

        [ConfigComment("Only change this to use a proxy or an Anthropic-compatible server.")]
        [SuggestionPlaceholder(Text = "https://api.anthropic.com")]
        public string BaseUrl = "https://api.anthropic.com";
    }

    /// <summary>Shared HTTP client for Anthropic API requests.</summary>
    public static HttpClient HttpClient = NetworkBackendUtils.MakeHttpClient();

    /// <summary>The settings for this backend.</summary>
    public AnthropicLLMProviderSettings Settings => SettingsRaw as AnthropicLLMProviderSettings;

    /// <inheritdoc/>
    public override string ProviderKind => "anthropic";

    /// <inheritdoc/>
    public override string DisplayName => "Anthropic Claude";

    /// <inheritdoc/>
    public override IEnumerable<string> SupportedFeatures => ["llm", "remote_llm", "anthropic"];

    /// <inheritdoc/>
    protected override async Task OnProviderInit() => Status = BackendStatus.RUNNING;

    /// <inheritdoc/>
    protected override async Task OnProviderShutdown() => Status = BackendStatus.DISABLED;

    /// <summary>Returns the per-user Anthropic API key, or null if not set.</summary>
    public static string GetApiKey(ExtendedLLMInput input)
    {
        return input?.RequestSession?.User?.GetGenericData("anthropic_api", "key");
    }

    /// <summary>Returns the workspace ID to send with a request: the user's own, else the server's setting, else null.</summary>
    public string GetWorkspaceId(ExtendedLLMInput input)
    {
        string userWorkspace = input?.RequestSession?.User?.GetGenericData("anthropic_api", "workspace_id");
        if (!string.IsNullOrWhiteSpace(userWorkspace))
        {
            return userWorkspace.Trim();
        }
        return string.IsNullOrWhiteSpace(Settings.WorkspaceId) ? null : Settings.WorkspaceId.Trim();
    }

    /// <summary>Sets the headers every Anthropic Messages request needs. The workspace header is added only when
    /// a workspace ID is known, so requests with a workspace-scoped key are unchanged.</summary>
    public static void ApplyHeaders(HttpRequestMessage request, string apiKey, string workspaceId)
    {
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            request.Headers.TryAddWithoutValidation("anthropic-workspace-id", workspaceId);
        }
    }

    /// <summary>Builds an Anthropic Messages API request body from the given LLM input.</summary>
    public JObject BuildRequestBody(ExtendedLLMInput input, bool stream)
    {
        string model = !string.IsNullOrEmpty(input.Model) ? input.Model : Settings.DefaultModel;
        JArray messages = BuildAnthropicMessages(input.Messages);
        if (messages.Count == 0 && !string.IsNullOrEmpty(input.UserMessage))
        {
            messages.Add(new JObject() { ["role"] = LLMRoles.User, ["content"] = input.UserMessage });
        }
        JObject body = new()
        {
            ["model"] = model,
            ["max_tokens"] = input.MaxTokens,
            ["messages"] = messages,
            ["stream"] = stream
        };
        string systemPrompt = input.SystemPrompt;
        if (string.IsNullOrEmpty(systemPrompt))
        {
            foreach (LLMMessage msg in input.Messages)
            {
                if (msg.Role == LLMRoles.System)
                {
                    systemPrompt = msg.Content;
                    break;
                }
            }
        }
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            body["system"] = systemPrompt;
        }
        if (input.Tools is { Count: > 0 })
        {
            body["tools"] = BuildAnthropicTools(input.Tools);
            // Forced tool_choice mirrors the legacy convention's "IMPORTANT: you MUST call X" directive,
            // but as a real API-level constraint instead of a prose instruction the model could ignore.
            body["tool_choice"] = !string.IsNullOrEmpty(input.ForceToolId)
                ? new JObject { ["type"] = "tool", ["name"] = input.ForceToolId }
                : new JObject { ["type"] = "auto" };
        }
        // temperature / top_p were removed on the current flagship generation (Opus 4.7+, Sonnet 5, Fable 5)
        // and return a 400 if sent, so only forward them for models that still accept them. Even where accepted,
        // Claude 4+ rejects specifying BOTH ("temperature and top_p cannot both be specified"), so send only one.
        if (AcceptsSamplingParams(model))
        {
            if (input.Temperature >= 0)
            {
                // Anthropic's temperature range is 0 to 1.0 (the UI slider goes to 2, OpenAI-style), so clamp.
                body["temperature"] = Math.Min(input.Temperature, 1.0);
            }
            else if (input.TopP >= 0 && input.TopP < 1.0)
            {
                body["top_p"] = input.TopP;
            }
        }
        return body;
    }

    /// <summary>Whether a model accepts the <c>temperature</c>/<c>top_p</c> sampling params. Anthropic
    /// removed them on the current flagship generation (Opus 4.7/4.8, Sonnet 5, Fable 5); sending them
    /// there returns a 400. Allowlist of older families that still accept them; unknown/new models default
    /// to OFF so a request never fails on an unrecognized model.</summary>
    private static bool AcceptsSamplingParams(string model)
    {
        string m = (model ?? "").ToLowerInvariant();
        return m.Contains("claude-3")       // Claude 3.x (opus 3, sonnet 3.5, haiku 3/3.5)
            || m.Contains("opus-4-0") || m.Contains("opus-4-1") || m.Contains("opus-4-5") || m.Contains("opus-4-6")
            || m.Contains("sonnet-4-0") || m.Contains("sonnet-4-5") || m.Contains("sonnet-4-6")
            || m.Contains("haiku-4-5");
    }

    /// <summary>The Anthropic <c>messages</c> array for a conversation. System turns are left out (they go in the top-level
    /// <c>system</c> field). An assistant turn that made tool calls becomes <c>text</c> and <c>tool_use</c> blocks; a tool
    /// result becomes a <c>tool_result</c> block inside a user turn. Blank assistant turns are dropped (Anthropic rejects empty
    /// text), and consecutive turns of one role are merged so tool results and the user's next message share one turn.</summary>
    internal static JArray BuildAnthropicMessages(IReadOnlyList<LLMMessage> source)
    {
        JArray messages = [];
        foreach (LLMMessage msg in source)
        {
            if (msg.Role == LLMRoles.System)
            {
                continue;
            }
            if (msg.Role == LLMRoles.Tool)
            {
                messages.Add(new JObject()
                {
                    ["role"] = LLMRoles.User,
                    ["content"] = new JArray(new JObject()
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = msg.ToolCallId ?? "",
                        ["content"] = msg.Content ?? "",
                    }),
                });
                continue;
            }
            if (msg.Role == LLMRoles.Assistant && msg.ToolCalls is { Count: > 0 } calls)
            {
                JArray blocks = [];
                if (!string.IsNullOrWhiteSpace(msg.Content))
                {
                    blocks.Add(new JObject() { ["type"] = "text", ["text"] = msg.Content });
                }
                foreach (JObject call in calls)
                {
                    blocks.Add(new JObject()
                    {
                        ["type"] = "tool_use",
                        ["id"] = call["id"]?.ToString() ?? "",
                        ["name"] = call["name"]?.ToString() ?? "",
                        ["input"] = LLMMessageMapping.ArgumentsObject(call["arguments"]),
                    });
                }
                messages.Add(new JObject() { ["role"] = LLMRoles.Assistant, ["content"] = blocks });
                continue;
            }
            if (msg.Role == LLMRoles.Assistant && string.IsNullOrWhiteSpace(msg.Content) && (msg.Media is null || msg.Media.Count == 0))
            {
                continue;
            }
            messages.Add(new JObject() { ["role"] = msg.Role, ["content"] = BuildAnthropicContent(msg) });
        }
        return MergeSameRole(messages);
    }

    /// <summary>Joins consecutive turns of one role into one turn, converting string content to text blocks, so the API sees
    /// strict user/assistant alternation.</summary>
    private static JArray MergeSameRole(JArray messages)
    {
        JArray merged = [];
        foreach (JToken token in messages)
        {
            JObject current = (JObject)token;
            if (merged.Count > 0 && merged[^1] is JObject previous && string.Equals(previous["role"]?.ToString(), current["role"]?.ToString(), StringComparison.Ordinal))
            {
                JArray blocks = ContentBlocks(previous["content"]);
                foreach (JToken block in ContentBlocks(current["content"]))
                {
                    blocks.Add(block);
                }
                previous["content"] = blocks;
                continue;
            }
            merged.Add(current.DeepClone());
        }
        return merged;
    }

    /// <summary>A message's content as content blocks: a copy of an array, or one text block for non-empty string content.</summary>
    private static JArray ContentBlocks(JToken content)
    {
        if (content is JArray array)
        {
            return [.. array.Select(b => b.DeepClone())];
        }
        string text = content?.ToString() ?? "";
        return string.IsNullOrEmpty(text) ? [] : [new JObject() { ["type"] = "text", ["text"] = text }];
    }

    /// <summary>Builds the Anthropic-shaped <c>content</c> for one message: a plain string when
    /// there's no media, or text + image blocks otherwise. HTTPS URLs use the <c>url</c> source
    /// form; everything else uses <c>base64</c> (local paths must already be resolved to base64 by
    /// the caller; this never touches the filesystem).</summary>
    private static JToken BuildAnthropicContent(LLMMessage msg)
    {
        if (msg.Media is null || msg.Media.Count == 0)
        {
            return msg.Content ?? "";
        }
        JArray blocks = [];
        if (!string.IsNullOrEmpty(msg.Content))
        {
            blocks.Add(new JObject { ["type"] = "text", ["text"] = msg.Content });
        }
        foreach (LLMMediaAttachment att in msg.Media)
        {
            if (att is null || string.IsNullOrEmpty(att.Data))
            {
                continue;
            }
            bool isHttpUrl = att.Type == "url" && (att.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || att.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            JObject source = isHttpUrl
                ? new JObject { ["type"] = "url", ["url"] = att.Data }
                : new JObject
                {
                    ["type"] = "base64",
                    ["media_type"] = string.IsNullOrEmpty(att.MediaType) ? "image/png" : att.MediaType,
                    ["data"] = att.Data
                };
            blocks.Add(new JObject { ["type"] = "image", ["source"] = source });
        }
        return blocks;
    }

    /// <summary>Maps this extension's tool JObject shape (<c>{id, name, description, parameters}</c>, the
    /// same shape the engine's tool definitions are built from) onto Anthropic's native tool definition (<c>{name, description, input_schema}</c>).
    /// <c>id</c> (the snake_case identifier, eg <c>generate_image</c>) is used as Anthropic's <c>name</c>,
    /// not the human display <c>name</c> field, since that's what <c>ToolExecutorService.ExecuteTool</c>
    /// dispatches on for the returned tool_use block.</summary>
    private static JArray BuildAnthropicTools(List<JObject> tools)
    {
        JArray result = [];
        foreach (JObject tool in tools)
        {
            string id = tool["id"]?.ToString() ?? tool["name"]?.ToString();
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }
            result.Add(new JObject
            {
                ["name"] = id,
                ["description"] = tool["description"]?.ToString() ?? "",
                ["input_schema"] = tool["parameters"] as JObject ?? new JObject { ["type"] = "object", ["properties"] = new JObject() },
            });
        }
        return result;
    }

    /// <inheritdoc/>
    public override async Task GenerateLive(ExtendedLLMInput input, string batchId, Func<JObject, Task> onChunk, CancellationToken ct)
    {
        string apiKey = GetApiKey(input);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new SwarmReadableErrorException("No Anthropic API key set. Go to the User tab to configure your Anthropic API key.");
        }
        string baseUrl = Settings.BaseUrl.TrimEnd('/');
        JObject body = BuildRequestBody(input, true);
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/v1/messages")
        {
            Content = Utilities.JSONContent(body)
        };
        ApplyHeaders(request, apiKey, GetWorkspaceId(input));
        // Link the per-request timeout with the caller's cancellation so Stop in the UI cancels
        // the HTTP request immediately, not just after the response stream ends.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(Settings.TimeoutSeconds));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
        HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(linked.Token);
            throw new SwarmReadableErrorException($"Anthropic API returned error {response.StatusCode}: {errorBody}");
        }
        using Stream stream = await response.Content.ReadAsStreamAsync(linked.Token);
        using StreamReader reader = new(stream, Encoding.UTF8);
        string currentEvent = "";
        // Tracks in-progress native tool_use blocks by their content-block index: Anthropic streams a
        // block's `input` as incremental `input_json_delta.partial_json` fragments (never mid-stream
        // parseable on their own), only complete/valid once the block's content_block_stop arrives.
        Dictionary<int, (string Id, string Name, StringBuilder Json)> pendingToolUse = [];
        while (true)
        {
            if (linked.IsCancellationRequested) { break; }
            string line = await reader.ReadLineAsync(linked.Token);
            if (line is null)
            {
                break;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                currentEvent = "";
                continue;
            }
            if (line.StartsWith("event: "))
            {
                currentEvent = line["event: ".Length..];
                if (currentEvent == "message_stop")
                {
                    break;
                }
                continue;
            }
            if (!line.StartsWith("data: "))
            {
                continue;
            }
            string data = line["data: ".Length..];
            try
            {
                JObject parsed = JObject.Parse(data);
                if (currentEvent == "content_block_start")
                {
                    JObject block = parsed.Value<JObject>("content_block");
                    if (block?.Value<string>("type") == "tool_use")
                    {
                        int index = parsed.Value<int?>("index") ?? 0;
                        pendingToolUse[index] = (block.Value<string>("id"), block.Value<string>("name"), new StringBuilder());
                    }
                }
                else if (currentEvent == "content_block_delta")
                {
                    JObject delta = parsed.Value<JObject>("delta");
                    string deltaType = delta?.Value<string>("type");
                    int index = parsed.Value<int?>("index") ?? 0;
                    if (deltaType == "input_json_delta" && pendingToolUse.TryGetValue(index, out (string Id, string Name, StringBuilder Json) call))
                    {
                        call.Json.Append(delta.Value<string>("partial_json"));
                    }
                    else
                    {
                        string text = delta?.Value<string>("text");
                        if (!string.IsNullOrEmpty(text))
                        {
                            await onChunk(new JObject() { ["chunk"] = text });
                        }
                    }
                }
                else if (currentEvent == "content_block_stop")
                {
                    int index = parsed.Value<int?>("index") ?? 0;
                    if (pendingToolUse.Remove(index, out (string Id, string Name, StringBuilder Json) call))
                    {
                        // Malformed JSON (not expected from Anthropic) stays raw text, so the turn answers the model with an error instead of running the tool on {}.
                        JToken args = LLMMessageMapping.StreamedArguments(call.Json.ToString());
                        if (args is not JObject)
                        {
                            Logs.Debug($"[AnthropicLLMProvider] Malformed tool_use input JSON for {call.Name}.");
                        }
                        await onChunk(new JObject()
                        {
                            ["native_tool_call"] = new JObject { ["id"] = call.Id, ["name"] = call.Name, ["arguments"] = args }
                        });
                    }
                }
                else if (currentEvent == "message_delta")
                {
                    // stop_reason "max_tokens" means Anthropic cut the reply off at the token cap, not
                    // because the model was done, so surface that and the UI can tell the user why.
                    string stopReason = parsed.Value<JObject>("delta")?.Value<string>("stop_reason");
                    if (stopReason == "max_tokens")
                    {
                        await onChunk(new JObject() { ["stopReason"] = "length" });
                    }
                }
            }
            catch (Exception ex)
            {
                Logs.Debug($"[AnthropicLLMProvider] Failed to parse SSE chunk: {ex.Message}");
            }
        }
    }

    /// <summary>Well-known Anthropic model definitions (current as of 2026-07). Uses bare model aliases,
    /// never date-suffixed IDs. Older Claude 3.x / 4.0 models were retired and 404 if requested.</summary>
    // TODO: Talk to mcmonkey about a better way to get the model list. The backend card already lists the Models API
    // with the viewing user's key (ListApiModelsAs), but ListModels gets no session, so the chat's list stays hardcoded.
    public static readonly List<(string Id, string Name, int ContextLength)> KnownModels =
    [
        ("claude-fable-5-1", "Claude Fable 5.1", 1000000),
        ("claude-opus-5-5", "Claude Opus 5.5", 1000000),
        ("claude-sonnet-5-5", "Claude Sonnet 5.5", 1000000),
        ("claude-haiku-5-5", "Claude Haiku 5.5", 1000000),
        ("claude-opus-5", "Claude Opus 5", 1000000),
        ("claude-fable-5", "Claude Fable 5", 1000000),
        ("claude-sonnet-5", "Claude Sonnet 5", 1000000),
        ("claude-opus-4-8", "Claude Opus 4.8", 1000000),
        ("claude-opus-4-7", "Claude Opus 4.7", 1000000),
        ("claude-opus-4-6", "Claude Opus 4.6", 1000000),
        ("claude-sonnet-4-6", "Claude Sonnet 4.6", 1000000),
        ("claude-haiku-4-5", "Claude Haiku 4.5", 200000),
        ("claude-opus-4-5-20251101", "Claude Opus 4.5", 200000),
        ("claude-sonnet-4-5-20250929", "Claude Sonnet 4.5", 200000),
        ("claude-haiku-4-5-20251001", "Claude Haiku 4.5 (2025-10-01)", 200000)
    ];

    /// <inheritdoc/>
    public override async Task<List<LLMModelInfo>> ListModels(CancellationToken ct = default)
    {
        List<LLMModelInfo> models = [];
        foreach ((string id, string name, int contextLength) in KnownModels)
        {
            models.Add(new LLMModelInfo()
            {
                Id = id,
                Name = name,
                Provider = "anthropic",
                BackendId = AbstractBackendData?.ID ?? -1,
                ContextLength = contextLength,
                Family = "claude",
                IsLoaded = true
            });
        }
        return models;
    }

    /// <summary>The models the Anthropic Models API lists for <paramref name="session"/>'s own key, newest first, or null when
    /// that user has no key set. For the backend card, which has the viewer's session where <see cref="ListModels"/> has none.</summary>
    public async Task<List<(string Id, string Name)>> ListApiModelsAs(Session session, CancellationToken ct)
    {
        ExtendedLLMInput asUser = new() { RequestSession = session };
        string apiKey = GetApiKey(asUser);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }
        using HttpRequestMessage request = new(HttpMethod.Get, $"{Settings.BaseUrl.TrimEnd('/')}/v1/models?limit=1000");
        ApplyHeaders(request, apiKey, GetWorkspaceId(asUser));
        using HttpResponseMessage response = await HttpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new SwarmReadableErrorException($"Anthropic returned HTTP {(int)response.StatusCode} when listing models.");
        }
        return ParseModelList(JObject.Parse(await response.Content.ReadAsStringAsync(ct)));
    }

    /// <summary>(id, display name) for each entry of a Models API response's <c>data</c>, in the API's order.</summary>
    internal static List<(string Id, string Name)> ParseModelList(JObject body)
    {
        List<(string Id, string Name)> models = [];
        foreach (JObject model in (body?["data"] as JArray ?? []).OfType<JObject>())
        {
            string id = model.Value<string>("id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }
            string name = model.Value<string>("display_name");
            models.Add((id, string.IsNullOrWhiteSpace(name) ? id : name));
        }
        return models;
    }
}
