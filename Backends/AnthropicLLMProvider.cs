using System.IO;
using System.Net.Http;
using System.Text;
using FreneticUtilities.FreneticDataSyntax;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Extensions.LLMAssistant.LLMs;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Backends;

/// <summary>LLM backend for the Anthropic Messages API (Claude models). Uses the calling user's
/// per-user <c>anthropic_api</c> key (set in the User tab).</summary>
public class AnthropicLLMProvider : LLMProviderBackend
{
    public class AnthropicLLMProviderSettings : AutoConfiguration
    {
        [ConfigComment("The default model to use when a request doesn't specify one.\nFor example: claude-opus-4-8")]
        public string DefaultModel = "claude-opus-4-8";

        [ConfigComment("Maximum timeout in seconds for API requests.")]
        public int TimeoutSeconds = 120;

        [ConfigComment("The API base URL.\nOnly change this if using a proxy or compatible endpoint.")]
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

    /// <summary>Builds an Anthropic Messages API request body from the given LLM input.</summary>
    public JObject BuildRequestBody(ExtendedLLMInput input, bool stream)
    {
        string model = !string.IsNullOrEmpty(input.Model) ? input.Model : Settings.DefaultModel;
        JArray messages = [];
        foreach (LLMMessage msg in input.Messages)
        {
            if (msg.Role == LLMRoles.System)
            {
                continue;
            }
            messages.Add(new JObject() { ["role"] = msg.Role, ["content"] = BuildAnthropicContent(msg) });
        }
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
        // temperature / top_p were removed on the current flagship generation (Opus 4.7+, Sonnet 5, Fable 5)
        // and return a 400 if sent — only forward them for models that still accept them. Even where accepted,
        // Claude 4+ rejects specifying BOTH ("temperature and top_p cannot both be specified") — send only one.
        if (AcceptsSamplingParams(model))
        {
            if (input.Temperature >= 0)
            {
                // Anthropic's temperature range is 0–1.0 (the UI slider goes to 2, OpenAI-style) — clamp.
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
    /// removed them on the current flagship generation (Opus 4.7/4.8, Sonnet 5, Fable 5) — sending them
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

    /// <summary>Builds the Anthropic-shaped <c>content</c> for one message: a plain string when
    /// there's no media, or text + image blocks otherwise. HTTPS URLs use the <c>url</c> source
    /// form; everything else uses <c>base64</c> (local paths must already be resolved to base64 by
    /// the caller — this never touches the filesystem).</summary>
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

    /// <inheritdoc/>
    public override async Task GenerateLive(ExtendedLLMInput input, string batchId, Action<JObject> onChunk, CancellationToken ct)
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
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
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
                if (currentEvent == "content_block_delta")
                {
                    JObject delta = parsed.Value<JObject>("delta");
                    string text = delta?.Value<string>("text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        onChunk(new JObject() { ["chunk"] = text });
                    }
                }
            }
            catch (Exception ex)
            {
                Logs.Debug($"[AnthropicLLMProvider] Failed to parse SSE chunk: {ex.Message}");
            }
        }
    }

    /// <summary>Well-known Anthropic model definitions (current as of 2026-07). Uses bare model aliases —
    /// never date-suffixed IDs. Older Claude 3.x / 4.0 models were retired and 404 if requested.</summary>
    public static readonly List<(string Id, string Name, int ContextLength)> KnownModels =
    [
        ("claude-opus-4-8", "Claude Opus 4.8", 1000000),
        ("claude-opus-4-7", "Claude Opus 4.7", 1000000),
        ("claude-opus-4-6", "Claude Opus 4.6", 1000000),
        ("claude-sonnet-5", "Claude Sonnet 5", 1000000),
        ("claude-sonnet-4-6", "Claude Sonnet 4.6", 1000000),
        ("claude-haiku-4-5", "Claude Haiku 4.5", 200000)
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
}
