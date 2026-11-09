using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using FreneticUtilities.FreneticDataSyntax;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.DataHolders;
using Hartsy.Extensions.LLMAssistant.LLMs;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Backends;

/// <summary>LLM backend for any OpenAI API-compatible provider (OpenAI, Anthropic Claude, Ollama, LM Studio, vLLM,
/// OpenRouter, …). A per-user key overrides the configured Authorization header: <c>anthropic_api</c> when the Address is
/// Anthropic's API, <c>openai_api</c> otherwise.</summary>
public class RemoteOpenAILLMProvider : LLMProviderBackend
{
    /// <summary>Settings saved to Data/Backends.fds by field name, so renaming or retyping a field breaks every saved backend.</summary>
    /// <remarks>Assets/llm-backend-cards.js groups these fields into the card's sections.</remarks>
    public class RemoteOpenAILLMProviderSettings : AutoConfiguration
    {
        [ConfigComment("The server's web address. For example http://localhost:11434 for Ollama,\nhttp://localhost:1234 for LM Studio, https://api.openai.com for OpenAI,\nor https://api.anthropic.com for Claude.")]
        [SuggestionPlaceholder(Text = "for example http://localhost:11434")]
        public string Address = "";

        [ConfigComment("Lets the backend wait quietly (idle) instead of showing an error when the server is offline at startup.\nIt switches back to running after its next successful reply.")]
        public bool AllowIdle = false;

        [ConfigComment("Only if the server needs a key. For example: Bearer sk-abc123\nA user's own key from the User tab is used instead when they have one\n(their Anthropic key for https://api.anthropic.com, their OpenAI key otherwise).")]
        [ValueIsSecret]
        public string AuthorizationHeader = "";

        [ConfigComment("Extra headers some servers need, one per line. For example:\nMyHeader: MyValue")]
        public string OtherHeaders = "";

        [ConfigComment("The model used when a request doesn't name one.\nAfter you save a working Address, this lists the server's models. You can still type any name.")]
        [SuggestionPlaceholder(Text = "for example llama3.2 or gpt-4o")]
        public string DefaultModel = "";

        [ConfigComment("Which field sends the reply length limit. Auto is right unless the server rejects it:\nit uses max_completion_tokens for api.openai.com and max_tokens everywhere else.")]
        [ManualSettingsOptions(Vals = ["auto", "max_tokens", "max_completion_tokens"], ManualNames = ["Auto (recommended)", "max_tokens (most servers)", "max_completion_tokens (OpenAI o1, o3, GPT-5)"])]
        public string TokenLimitParameter = "auto";

        [ConfigComment("How many seconds to wait for the server when connecting and listing models.\nUse about 5 for a server on your own network.")]
        public int ConnectionAttemptTimeoutSeconds = 30;

    }

    /// <summary>Shared HTTP client for all instances of this backend type.</summary>
    public static HttpClient HttpClient = NetworkBackendUtils.MakeHttpClient();

    /// <summary>The settings for this backend.</summary>
    public RemoteOpenAILLMProviderSettings Settings => SettingsRaw as RemoteOpenAILLMProviderSettings;

    /// <inheritdoc/>
    public override string ProviderKind => "openai";

    /// <inheritdoc/>
    public override string DisplayName => "Remote LLM (OpenAI API)";

    /// <inheritdoc/>
    public override IEnumerable<string> SupportedFeatures => ["llm", "remote_llm"];

    /// <inheritdoc/>
    public override bool ModelsVaryByUser => true;

    /// <inheritdoc/>
    public override Task<List<LLMModelInfo>> ListModelsFor(Session session, CancellationToken ct = default) => ListModelsAs(new ExtendedLLMInput { RequestSession = session }, ct);

    /// <summary>The <c>anthropic-version</c> Anthropic's own (non-OpenAI-shaped) routes require, here the Models API.</summary>
    public const string AnthropicVersion = "2023-06-01";

    /// <summary>The header that picks the workspace for an Anthropic key not tied to one.</summary>
    public const string AnthropicWorkspaceHeader = "anthropic-workspace-id";

    /// <summary>Whether Address is Anthropic's API, reached through its OpenAI-compatible layer.</summary>
    public bool IsAnthropic => IsAnthropicAddress(Settings.Address);

    /// <summary>Whether <paramref name="address"/>'s host is <c>anthropic.com</c> or one of its subdomains.</summary>
    internal static bool IsAnthropicAddress(string address)
    {
        if (!Uri.TryCreate((address ?? "").Trim(), UriKind.Absolute, out Uri uri))
        {
            return false;
        }
        return uri.Host.Equals("anthropic.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".anthropic.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The User tab key type this backend reads: the user's Anthropic key for Anthropic's API, their OpenAI key otherwise.</summary>
    public string UserKeyType => IsAnthropic ? "anthropic_api" : "openai_api";

    /// <summary>The calling user's own key for this backend, or null when they have none.</summary>
    public string GetUserKey(ExtendedLLMInput input) => input?.RequestSession?.User?.GetGenericData(UserKeyType, "key");

    /// <inheritdoc/>
    protected override async Task OnProviderInit()
    {
        if (string.IsNullOrWhiteSpace(Settings.Address))
        {
            Status = BackendStatus.DISABLED;
            return;
        }
        string address = Settings.Address.TrimEnd('/');
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(Settings.ConnectionAttemptTimeoutSeconds));
            HttpRequestMessage request = new(HttpMethod.Get, ModelsUrl(address));
            ApplyHeaders(request, null, true);
            HttpResponseMessage response = await HttpClient.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                Status = BackendStatus.RUNNING;
                return;
            }
            // With no server-wide key, each user brings their own, so a server that answers "no key" is up and ready for them.
            if ((response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) && string.IsNullOrWhiteSpace(Settings.AuthorizationHeader))
            {
                Logs.Debug($"[RemoteOpenAILLMProvider] {address} needs a key; none is set on the backend, so each user's own key from the User tab is used.");
                Status = BackendStatus.RUNNING;
                return;
            }
            Logs.Warning($"[RemoteOpenAILLMProvider] Connection to {address} returned status {response.StatusCode}.");
        }
        catch (Exception ex)
        {
            Logs.Warning($"[RemoteOpenAILLMProvider] Failed to connect to {address}: {ex.Message}");
        }
        Status = Settings.AllowIdle ? BackendStatus.IDLE : BackendStatus.ERRORED;
    }

    /// <inheritdoc/>
    protected override async Task OnProviderShutdown() => Status = BackendStatus.DISABLED;

    /// <summary>The model list URL. Anthropic's pages at 20 models unless asked for more.</summary>
    private string ModelsUrl(string address) => IsAnthropic ? $"{address}/v1/models?limit=1000" : $"{address}/v1/models";

    /// <summary>Applies configured headers to an HTTP request, with optional per-user API key override. <paramref name="modelsList"/>
    /// marks a model list request, which Anthropic serves from its own Models API: that takes the key as <c>x-api-key</c> with an
    /// <c>anthropic-version</c>, not as a Bearer token.</summary>
    public void ApplyHeaders(HttpRequestMessage request, ExtendedLLMInput input = null, bool modelsList = false)
    {
        string authHeader = Settings.AuthorizationHeader;
        string userKey = GetUserKey(input);
        if (!string.IsNullOrEmpty(userKey))
        {
            authHeader = $"Bearer {userKey}";
        }
        bool anthropicModels = modelsList && IsAnthropic;
        if (!string.IsNullOrWhiteSpace(authHeader))
        {
            if (anthropicModels)
            {
                string key = authHeader.Trim();
                request.Headers.TryAddWithoutValidation("x-api-key", key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? key["Bearer ".Length..].Trim() : key);
            }
            else
            {
                request.Headers.TryAddWithoutValidation("Authorization", authHeader);
            }
        }
        if (!string.IsNullOrWhiteSpace(Settings.OtherHeaders))
        {
            foreach (string line in Settings.OtherHeaders.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int colonIndex = line.IndexOf(':');
                if (colonIndex > 0)
                {
                    string headerName = line[..colonIndex].Trim();
                    string headerValue = line[(colonIndex + 1)..].Trim();
                    request.Headers.TryAddWithoutValidation(headerName, headerValue);
                }
            }
        }
        if (anthropicModels && !request.Headers.Contains("anthropic-version"))
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        }
        // A user's own workspace, set with their Anthropic key, replaces the backend's one from Other Headers.
        string userWorkspace = IsAnthropic ? input?.RequestSession?.User?.GetGenericData("anthropic_api", "workspace_id") : null;
        if (!string.IsNullOrWhiteSpace(userWorkspace))
        {
            request.Headers.Remove(AnthropicWorkspaceHeader);
            request.Headers.TryAddWithoutValidation(AnthropicWorkspaceHeader, userWorkspace.Trim());
        }
    }

    /// <summary>Resolves the field name carrying the response-length limit, honoring the configured
    /// override and otherwise inferring from the endpoint: OpenAI's API requires
    /// <c>max_completion_tokens</c> for o1/o3/GPT-5 (and accepts it for 4o/4.1), while local /
    /// non-OpenAI servers (Ollama, vLLM, LM Studio) and Azure expect the legacy <c>max_tokens</c>.</summary>
    private string TokenLimitField()
    {
        string choice = Settings.TokenLimitParameter;
        if (choice is "max_tokens" or "max_completion_tokens")
        {
            return choice;
        }
        return (Settings.Address ?? "").Contains("openai.com", StringComparison.OrdinalIgnoreCase) ? "max_completion_tokens" : "max_tokens";
    }

    /// <summary>Builds an OpenAI chat completions request body from the given LLM input.</summary>
    public JObject BuildRequestBody(ExtendedLLMInput input, bool stream)
    {
        string model = !string.IsNullOrEmpty(input.Model) ? input.Model : Settings.DefaultModel;
        JArray messages = [];
        if (input.Messages.Count > 0)
        {
            messages = BuildOpenAIMessages(input.Messages);
        }
        else
        {
            if (!string.IsNullOrEmpty(input.SystemPrompt))
            {
                messages.Add(new JObject() { ["role"] = LLMRoles.System, ["content"] = input.SystemPrompt });
            }
            if (!string.IsNullOrEmpty(input.UserMessage))
            {
                messages.Add(new JObject() { ["role"] = LLMRoles.User, ["content"] = input.UserMessage });
            }
        }
        JObject body = new()
        {
            ["model"] = model,
            ["messages"] = messages,
            [TokenLimitField()] = input.MaxTokens,
            ["stream"] = stream
        };
        if (IsAnthropic)
        {
            AddAnthropicSampling(body, model, input);
        }
        else
        {
            body["temperature"] = input.Temperature;
            body["top_p"] = input.TopP;
        }
        if (input.Seed >= 0)
        {
            body["seed"] = input.Seed;
        }
        // The endpoint must support tools; one that does not answers with an error, which surfaces to the user.
        if (input.Tools is { Count: > 0 })
        {
            body["tools"] = BuildOpenAITools(input.Tools);
            body["tool_choice"] = !string.IsNullOrEmpty(input.ForceToolId)
                ? new JObject { ["type"] = "function", ["function"] = new JObject { ["name"] = input.ForceToolId } }
                : "auto";
        }
        return body;
    }

    /// <summary>Adds the one sampling setting Claude takes. Claude 4.7 and later reject <c>temperature</c> and <c>top_p</c> outright
    /// (400), so they get neither; older models reject both together, so they get <c>temperature</c> (Claude's range ends at 1),
    /// or <c>top_p</c> when temperature is unset.</summary>
    internal static void AddAnthropicSampling(JObject body, string model, ExtendedLLMInput input)
    {
        if (!AnthropicAcceptsSampling(model))
        {
            return;
        }
        if (input.Temperature >= 0)
        {
            body["temperature"] = Math.Min(input.Temperature, 1.0);
        }
        else if (input.TopP >= 0 && input.TopP < 1.0)
        {
            body["top_p"] = input.TopP;
        }
    }

    /// <summary>Whether a Claude model still accepts <c>temperature</c>/<c>top_p</c>: an allowlist of the older families, so an
    /// unknown or new model never fails on them.</summary>
    internal static bool AnthropicAcceptsSampling(string model)
    {
        string m = (model ?? "").ToLowerFast();
        return m.Contains("claude-3")
            || m.Contains("opus-4-0") || m.Contains("opus-4-1") || m.Contains("opus-4-5") || m.Contains("opus-4-6")
            || m.Contains("sonnet-4-0") || m.Contains("sonnet-4-5") || m.Contains("sonnet-4-6")
            || m.Contains("haiku-4-5");
    }

    /// <summary>The OpenAI <c>messages</c> array for a conversation. An assistant turn that made tool calls carries
    /// <c>tool_calls</c> (arguments as JSON text); a tool result is a <c>tool</c> message keyed by its call id.</summary>
    internal static JArray BuildOpenAIMessages(IReadOnlyList<LLMMessage> source)
    {
        JArray messages = [];
        foreach (LLMMessage msg in source)
        {
            if (msg.Role == LLMRoles.Tool)
            {
                messages.Add(new JObject() { ["role"] = LLMRoles.Tool, ["tool_call_id"] = msg.ToolCallId ?? "", ["content"] = msg.Content ?? "" });
                continue;
            }
            if (msg.Role == LLMRoles.Assistant && msg.ToolCalls is { Count: > 0 } calls)
            {
                JArray toolCalls = [];
                foreach (JObject call in calls)
                {
                    toolCalls.Add(new JObject()
                    {
                        ["id"] = call["id"]?.ToString() ?? "",
                        ["type"] = "function",
                        ["function"] = new JObject()
                        {
                            ["name"] = call["name"]?.ToString() ?? "",
                            ["arguments"] = LLMMessageMapping.ArgumentsText(call["arguments"]),
                        },
                    });
                }
                messages.Add(new JObject()
                {
                    ["role"] = LLMRoles.Assistant,
                    ["content"] = string.IsNullOrWhiteSpace(msg.Content) ? null : msg.Content,
                    ["tool_calls"] = toolCalls,
                });
                continue;
            }
            messages.Add(new JObject() { ["role"] = msg.Role, ["content"] = BuildOpenAIContent(msg) });
        }
        return messages;
    }

    /// <summary>Maps this extension's tool JObject shape (<c>{id, name, description, parameters}</c>) onto
    /// the OpenAI function-tool definition (<c>{type:"function", function:{name, description, parameters}}</c>).
    /// <c>id</c> (snake_case, eg <c>generate_image</c>) is used as the function name, matching
    /// <c>ToolExecutorService.ExecuteTool</c>'s dispatch key; same reasoning as Anthropic's mapping.</summary>
    private static JArray BuildOpenAITools(List<JObject> tools)
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
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = id,
                    ["description"] = tool["description"]?.ToString() ?? "",
                    ["parameters"] = tool["parameters"] as JObject ?? new JObject { ["type"] = "object", ["properties"] = new JObject() },
                },
            });
        }
        return result;
    }

    /// <summary>Builds the OpenAI-shaped <c>content</c> for one message: a plain string when there's
    /// no media, or text + <c>image_url</c> parts otherwise. HTTPS URLs pass through; base64
    /// attachments are wrapped in a <c>data:</c> URI (local paths must already be resolved to base64
    /// by the caller; this never touches the filesystem).</summary>
    private static JToken BuildOpenAIContent(LLMMessage msg)
    {
        if (msg.Media is null || msg.Media.Count == 0)
        {
            return msg.Content ?? "";
        }
        JArray parts = [];
        if (!string.IsNullOrEmpty(msg.Content))
        {
            parts.Add(new JObject { ["type"] = "text", ["text"] = msg.Content });
        }
        foreach (LLMMediaAttachment att in msg.Media)
        {
            if (att is null || string.IsNullOrEmpty(att.Data))
            {
                continue;
            }
            bool isHttpUrl = att.Type == "url" && (att.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || att.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            string url = isHttpUrl
                ? att.Data
                : $"data:{(string.IsNullOrEmpty(att.MediaType) ? "image/png" : att.MediaType)};base64,{att.Data}";
            parts.Add(new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = url } });
        }
        return parts;
    }

    /// <inheritdoc/>
    public override async Task GenerateLive(ExtendedLLMInput input, string batchId, Func<JObject, Task> onChunk, CancellationToken ct)
    {
        if (IsAnthropic && string.IsNullOrWhiteSpace(GetUserKey(input)) && string.IsNullOrWhiteSpace(Settings.AuthorizationHeader))
        {
            throw new SwarmReadableErrorException("No Anthropic API key set. Go to the User tab to set your Anthropic API key.");
        }
        string address = Settings.Address.TrimEnd('/');
        JObject body = BuildRequestBody(input, true);
        HttpRequestMessage request = new(HttpMethod.Post, $"{address}/v1/chat/completions")
        {
            Content = Utilities.JSONContent(body)
        };
        ApplyHeaders(request, input);
        HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new SwarmReadableErrorException($"Remote LLM API returned error {response.StatusCode}: {errorBody}");
        }
        using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(stream, Encoding.UTF8);
        // Tracks in-progress native tool_calls by their `index`: OpenAI streams id/name once on the first
        // delta for an index and `arguments` as incremental string fragments across subsequent deltas for
        // the same index; nothing is complete/parseable until `finish_reason == "tool_calls"` arrives.
        Dictionary<int, (string Id, string Name, StringBuilder Args)> pendingToolCalls = [];
        while (true)
        {
            if (ct.IsCancellationRequested) { break; }
            string line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            if (!line.StartsWith("data: "))
            {
                continue;
            }
            string data = line["data: ".Length..];
            if (data == "[DONE]")
            {
                break;
            }
            try
            {
                JObject parsed = JObject.Parse(data);
                if (parsed["error"] is JToken serverError && serverError.Type != JTokenType.Null)
                {
                    // An error event ends the reply with the server's message; the text before it is kept.
                    string message = serverError is JObject detail ? detail.Value<string>("message") : serverError.ToString();
                    await onChunk(new JObject() { ["stopReason"] = "error", ["error"] = string.IsNullOrWhiteSpace(message) ? "The server reported an error." : message });
                    break;
                }
                JArray choices = parsed.Value<JArray>("choices");
                if (choices is not null && choices.Count > 0)
                {
                    JObject firstChoice = choices[0] as JObject;
                    JObject delta = firstChoice?.Value<JObject>("delta");
                    string content = delta?.Value<string>("content");
                    if (!string.IsNullOrEmpty(content))
                    {
                        await onChunk(new JObject() { ["chunk"] = content });
                    }
                    if (delta?.Value<JArray>("tool_calls") is JArray toolCallDeltas)
                    {
                        foreach (JObject tcDelta in toolCallDeltas.OfType<JObject>())
                        {
                            int index = tcDelta.Value<int?>("index") ?? 0;
                            if (!pendingToolCalls.TryGetValue(index, out (string Id, string Name, StringBuilder Args) call))
                            {
                                call = (null, null, new StringBuilder());
                            }
                            string id = tcDelta.Value<string>("id") ?? call.Id;
                            JObject function = tcDelta.Value<JObject>("function");
                            string name = function?.Value<string>("name") ?? call.Name;
                            call.Args.Append(function?.Value<string>("arguments"));
                            pendingToolCalls[index] = (id, name, call.Args);
                        }
                    }
                    string finishReason = firstChoice?.Value<string>("finish_reason");
                    // finish_reason "length" means the server truncated the reply at the token cap, not
                    // a natural stop, so surface that and the UI can tell the user why.
                    if (finishReason == "length")
                    {
                        await onChunk(new JObject() { ["stopReason"] = "length" });
                    }
                    else if (finishReason == "tool_calls" && pendingToolCalls.Count > 0)
                    {
                        foreach ((string id, string name, StringBuilder argsBuilder) in pendingToolCalls.Values)
                        {
                            // Malformed JSON stays raw text, so the turn answers the model with an error instead of running the tool on {}.
                            JToken args = LLMMessageMapping.StreamedArguments(argsBuilder.ToString());
                            if (args is not JObject)
                            {
                                Logs.Debug($"[RemoteOpenAILLMProvider] Malformed tool_calls arguments JSON for {name}.");
                            }
                            await onChunk(new JObject()
                            {
                                ["native_tool_call"] = new JObject { ["id"] = id, ["name"] = name, ["arguments"] = args }
                            });
                        }
                        pendingToolCalls.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                Logs.Debug($"[RemoteOpenAILLMProvider] Failed to parse SSE chunk: {ex.Message}");
            }
        }
        if (Status == BackendStatus.IDLE)
        {
            Status = BackendStatus.RUNNING;
        }
    }

    /// <inheritdoc/>
    public override Task<List<LLMModelInfo>> ListModels(CancellationToken ct = default) => ListModelsAs(null, ct);

    /// <summary>Lists the endpoint's models with the headers a request from <paramref name="asUser"/>'s session would carry
    /// (that user's own key, when set), or the configured headers alone when null.</summary>
    public async Task<List<LLMModelInfo>> ListModelsAs(ExtendedLLMInput asUser, CancellationToken ct = default)
    {
        List<LLMModelInfo> models = [];
        if (string.IsNullOrWhiteSpace(Settings.Address))
        {
            return models;
        }
        // Anthropic lists nothing without a key, so a user who has none is not worth a request (and a warning) each time.
        if (IsAnthropic && string.IsNullOrWhiteSpace(GetUserKey(asUser)) && string.IsNullOrWhiteSpace(Settings.AuthorizationHeader))
        {
            return models;
        }
        string address = Settings.Address.TrimEnd('/');
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(Settings.ConnectionAttemptTimeoutSeconds));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
        try
        {
            HttpRequestMessage request = new(HttpMethod.Get, ModelsUrl(address));
            ApplyHeaders(request, asUser, true);
            HttpResponseMessage response = await HttpClient.SendAsync(request, linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                Logs.Warning($"[RemoteOpenAILLMProvider] Failed to list models from {address}: HTTP {response.StatusCode}");
                return models;
            }
            string body = await response.Content.ReadAsStringAsync(linked.Token);
            models = ParseModelList(JObject.Parse(body), AbstractBackendData?.ID ?? -1, IsAnthropic);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[RemoteOpenAILLMProvider] Error listing models from {address}: {ex.Message}");
        }
        return models;
    }

    /// <summary>The models in a <c>/v1/models</c> answer's <c>data</c>, in its order. Reads OpenAI's and Ollama's fields, and
    /// Anthropic's <c>display_name</c> and <c>max_input_tokens</c>. An Anthropic model's provider is <c>anthropic</c> (as it was under
    /// the removed Anthropic backend, so the model picker's group and <c>provider:anthropic</c> instruction variants still apply) and
    /// its family is <c>claude</c>.</summary>
    internal static List<LLMModelInfo> ParseModelList(JObject parsed, int backendId, bool anthropic)
    {
        List<LLMModelInfo> models = [];
        if (parsed?.Value<JArray>("data") is not JArray data)
        {
            return models;
        }
        foreach (JObject model in data.OfType<JObject>())
        {
            string id = model.Value<string>("id") ?? "";
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }
            LLMModelInfo info = new()
            {
                Id = id,
                Name = new[] { model.Value<string>("name"), model.Value<string>("display_name") }.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? id,
                Provider = anthropic ? "anthropic" : "openai_api",
                BackendId = backendId,
                IsLoaded = true
            };
            string family = model.Value<string>("family") ?? model["details"]?.Value<string>("family") ?? (anthropic ? "claude" : null);
            if (!string.IsNullOrEmpty(family))
            {
                info.Family = family;
            }
            int contextLength = model.Value<int?>("max_input_tokens") ?? -1;
            if (contextLength > 0)
            {
                info.ContextLength = contextLength;
            }
            string quant = model["details"]?.Value<string>("quantization_level");
            if (!string.IsNullOrEmpty(quant))
            {
                info.Quantization = quant;
            }
            long size = model.Value<long?>("size") ?? -1;
            if (size > 0)
            {
                info.SizeBytes = size;
            }
            foreach (KeyValuePair<string, JToken> prop in model)
            {
                if (prop.Key is "id" or "name" or "display_name" or "family" or "size" or "details" or "object")
                {
                    continue;
                }
                if (prop.Value.Type == JTokenType.String)
                {
                    info.Metadata[prop.Key] = prop.Value.ToString();
                }
            }
            models.Add(info);
        }
        return models;
    }
}
