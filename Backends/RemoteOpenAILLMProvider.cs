using System.IO;
using System.Net.Http;
using System.Text;
using FreneticUtilities.FreneticDataSyntax;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using Hartsy.Extensions.LLMAssistant.LLMs;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Backends;

/// <summary>LLM backend for any OpenAI API-compatible provider (OpenAI, Ollama, LM Studio, vLLM,
/// OpenRouter, …). A per-user <c>openai_api</c> key overrides the configured Authorization header.</summary>
public class RemoteOpenAILLMProvider : LLMProviderBackend
{
    public class RemoteOpenAILLMProviderSettings : AutoConfiguration
    {
        [ConfigComment("The network address of the OpenAI API compatible LLM provider.\nUsually starts with 'http://' or 'https://'.\nFor example: 'http://localhost:11434' for Ollama, or 'https://api.openai.com' for OpenAI.")]
        public string Address = "";

        [ConfigComment("Whether the backend is allowed to revert to an 'idle' state if the API address is unresponsive.\nAn idle state is not considered an error, but cannot generate.\nIt will automatically return to 'running' if the API becomes available.")]
        public bool AllowIdle = false;

        [ConfigComment("If the remote instance has an 'Authorization:' header required, specify it here.\nFor example, 'Bearer sk-abc123'.\nIf you don't know what this is, you don't need it.")]
        [ValueIsSecret]
        public string AuthorizationHeader = "";

        [ConfigComment("Any other headers here, newline separated, for example:\nMyHeader: MyVal\nSecondHeader: secondVal")]
        public string OtherHeaders = "";

        [ConfigComment("The default model name to request if none is specified in the generation request.\nFor example: 'gpt-4o' for OpenAI, or 'llama3.2' for Ollama.")]
        public string DefaultModel = "";

        [ConfigComment("Which JSON field carries the response-length limit.\n 'auto' picks 'max_completion_tokens' for api.openai.com (required by o1/o3/GPT-5, accepted by 4o/4.1) and 'max_tokens' everywhere else (Ollama, vLLM, LM Studio, Azure, OpenRouter, …).\n"
            + "Override only if your endpoint rejects the auto choice.")]
        [ManualSettingsOptions(Vals = ["auto", "max_tokens", "max_completion_tokens"], ManualNames = ["Auto (detect from address)", "max_tokens (legacy / local servers)", "max_completion_tokens (OpenAI o1+ / GPT-5)"])]
        public string TokenLimitParameter = "auto";

        [ConfigComment("When attempting to connect to the backend, this is the maximum time Swarm will wait before considering the connection to be failed.\nNote that depending on other configurations, it may fail faster than this.\nFor local network machines, set this to a low value (eg 5) to avoid 'Loading...' delays.")]
        public int ConnectionAttemptTimeoutSeconds = 30;

        [ConfigComment("Whether to use this endpoint's native `tools`/`tool_calls` wire mechanism instead of the "
            + "text-based <tool_call> tag convention. 'Auto' only enables it for api.openai.com (the one dialect "
            + "guaranteed to support it correctly) — arbitrary self-hosted OpenAI-compatible servers (Ollama, "
            + "LM Studio, vLLM, older llama.cpp builds) vary in tool_calls support and quality, so they stay on "
            + "the safer tag convention unless you confirm your endpoint supports it and turn this on manually.")]
        [ManualSettingsOptions(Vals = ["auto", "on", "off"], ManualNames = ["Auto (only api.openai.com)", "On (this endpoint supports tool_calls)", "Off (always use the <tool_call> tag convention)"])]
        public string NativeToolCalling = "auto";
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
    public bool SupportsNativeToolCalling => Settings.NativeToolCalling switch
    {
        "on" => true,
        "off" => false,
        _ => (Settings.Address ?? "").Contains("openai.com", StringComparison.OrdinalIgnoreCase),
    };

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
            HttpRequestMessage request = new(HttpMethod.Get, $"{address}/v1/models");
            ApplyHeaders(request);
            HttpResponseMessage response = await HttpClient.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
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

    /// <summary>Applies configured headers to an HTTP request, with optional per-user API key override.</summary>
    public void ApplyHeaders(HttpRequestMessage request, ExtendedLLMInput input = null)
    {
        string authHeader = Settings.AuthorizationHeader;
        string userKey = input?.RequestSession?.User?.GetGenericData("openai_api", "key");
        if (!string.IsNullOrEmpty(userKey))
        {
            authHeader = $"Bearer {userKey}";
        }
        if (!string.IsNullOrWhiteSpace(authHeader))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authHeader);
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
            foreach (LLMMessage msg in input.Messages)
            {
                messages.Add(new JObject() { ["role"] = msg.Role, ["content"] = BuildOpenAIContent(msg) });
            }
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
            ["temperature"] = input.Temperature,
            [TokenLimitField()] = input.MaxTokens,
            ["top_p"] = input.TopP,
            ["stream"] = stream
        };
        if (input.Seed >= 0)
        {
            body["seed"] = input.Seed;
        }
        // Re-check SupportsNativeToolCalling here (not just trust the caller already gated it) — input.Tools
        // is populated unconditionally by ChatEndpoints.ApplyToolsToInput regardless of native/legacy mode
        // (only the tag-convention system-prompt injection is conditional), so this is the single place that
        // actually decides whether THIS request emits a native tools field.
        if (input.Tools is { Count: > 0 } && SupportsNativeToolCalling)
        {
            body["tools"] = BuildOpenAITools(input.Tools);
            body["tool_choice"] = !string.IsNullOrEmpty(input.ForceToolId)
                ? new JObject { ["type"] = "function", ["function"] = new JObject { ["name"] = input.ForceToolId } }
                : "auto";
        }
        return body;
    }

    /// <summary>Maps this extension's tool JObject shape (<c>{id, name, description, parameters}</c>) onto
    /// the OpenAI function-tool definition (<c>{type:"function", function:{name, description, parameters}}</c>).
    /// <c>id</c> (snake_case, eg <c>generate_image</c>) is used as the function name, matching
    /// <c>ToolExecutorService.ExecuteTool</c>'s dispatch key — same reasoning as Anthropic's mapping.</summary>
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
    /// by the caller — this never touches the filesystem).</summary>
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
    public override async Task GenerateLive(ExtendedLLMInput input, string batchId, Action<JObject> onChunk, CancellationToken ct)
    {
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
        // Tracks in-progress native tool_calls by their `index` — OpenAI streams id/name once on the first
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
                JArray choices = parsed.Value<JArray>("choices");
                if (choices is not null && choices.Count > 0)
                {
                    JObject firstChoice = choices[0] as JObject;
                    JObject delta = firstChoice?.Value<JObject>("delta");
                    string content = delta?.Value<string>("content");
                    if (!string.IsNullOrEmpty(content))
                    {
                        onChunk(new JObject() { ["chunk"] = content });
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
                    // a natural stop — surface that so the UI can tell the user why.
                    if (finishReason == "length")
                    {
                        onChunk(new JObject() { ["stopReason"] = "length" });
                    }
                    else if (finishReason == "tool_calls" && pendingToolCalls.Count > 0)
                    {
                        foreach ((string id, string name, StringBuilder argsBuilder) in pendingToolCalls.Values)
                        {
                            JObject args;
                            try
                            {
                                string json = argsBuilder.ToString();
                                args = string.IsNullOrWhiteSpace(json) ? new JObject() : JObject.Parse(json);
                            }
                            catch (Exception ex)
                            {
                                Logs.Debug($"[RemoteOpenAILLMProvider] Malformed tool_calls arguments JSON for {name}: {ex.Message}");
                                args = new JObject();
                            }
                            onChunk(new JObject()
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
    public override async Task<List<LLMModelInfo>> ListModels(CancellationToken ct = default)
    {
        List<LLMModelInfo> models = [];
        if (string.IsNullOrWhiteSpace(Settings.Address))
        {
            return models;
        }
        string address = Settings.Address.TrimEnd('/');
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(Settings.ConnectionAttemptTimeoutSeconds));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
        try
        {
            HttpRequestMessage request = new(HttpMethod.Get, $"{address}/v1/models");
            ApplyHeaders(request);
            HttpResponseMessage response = await HttpClient.SendAsync(request, linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                Logs.Warning($"[RemoteOpenAILLMProvider] Failed to list models from {address}: HTTP {response.StatusCode}");
                return models;
            }
            string body = await response.Content.ReadAsStringAsync(linked.Token);
            JObject parsed = JObject.Parse(body);
            JArray data = parsed.Value<JArray>("data");
            if (data is null)
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
                    Name = model.Value<string>("name") ?? id,
                    Provider = "openai_api",
                    BackendId = AbstractBackendData?.ID ?? -1,
                    IsLoaded = true
                };
                string family = model.Value<string>("family") ?? model["details"]?.Value<string>("family");
                if (!string.IsNullOrEmpty(family))
                {
                    info.Family = family;
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
                    if (prop.Key is "id" or "name" or "family" or "size" or "details" or "object")
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
}
