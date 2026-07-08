using System.IO;
using System.Net.Http;
using System.Text;
using FreneticUtilities.FreneticDataSyntax;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Extensions.LLMAssistant.LLMs;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Backends;

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
        return body;
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
