using System.Net.WebSockets;
using LLama.Common;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Extensions.LLMAssistant.LLMs;
using SwarmUI.Extensions.LLMAssistant.Services;

namespace SwarmUI.Extensions.LLMAssistant.WebAPI;

/// <summary>Chat message endpoints (HTTP and WebSocket streaming).</summary>
public static class ChatEndpoints
{
    private static readonly PromptCacheService _cache = new(500);

    /// <summary>Sends a message to an LLM and returns the full response.</summary>
    public static async Task<JObject> LLMAssistantSendMessage(Session session,
        string message, string instructionId = null, string model = null,
        double temperature = -1, int maxTokens = -1, bool noCache = false,
        string assistantId = null)
    {
        try
        {
            JObject settings = SettingsService.GetMergedSettings(session.User);
            assistantId ??= AssistantService.GetActiveAssistantId(settings, session.User);
            string systemPrompt = ResolveInstructionForRequest(instructionId, assistantId, settings, session.User);
            ExtendedLLMInput input = ExtendedLLMInput.Create(message, systemPrompt, model);
            JObject resolvedParams = AssistantService.ResolveParameters(assistantId, settings, session.User);
            ApplyParameters(input, resolvedParams, temperature, maxTokens);
            string response;
            if (noCache)
            {
                response = await LLMDispatcher.Generate(input);
            }
            else
            {
                response = await _cache.GetOrCreate(message, instructionId, async () =>
                {
                    return await LLMDispatcher.Generate(input);
                });
            }
            return new JObject
            {
                ["success"] = true,
                ["response"] = response
            };
        }
        catch (Exception ex)
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    /// <summary>Sends a message with streaming response over WebSocket.</summary>
    public static async Task<JObject> LLMAssistantSendMessageWS(WebSocket socket, Session session, JObject rawInput)
    {
        try
        {
            string message = rawInput["message"]?.ToString();
            string instructionId = rawInput["instructionId"]?.ToString();
            string model = rawInput["model"]?.ToString();
            double temperature = rawInput["temperature"]?.Value<double>() ?? -1;
            int maxTokens = rawInput["maxTokens"]?.Value<int>() ?? -1;
            string assistantId = rawInput["assistantId"]?.ToString();
            JArray historyArray = rawInput["history"] as JArray;
            JObject settings = SettingsService.GetMergedSettings(session.User);
            assistantId ??= AssistantService.GetActiveAssistantId(settings, session.User);
            string systemPrompt = ResolveInstructionForRequest(instructionId, assistantId, settings, session.User);
            ExtendedLLMInput input;
            if (historyArray is not null && historyArray.Count > 0)
            {
                List<ChatMessageData> history = [];
                foreach (JToken msg in historyArray)
                {
                    history.Add(new ChatMessageData
                    {
                        Role = msg["role"]?.ToString() ?? Roles.User,
                        Content = msg["content"]?.ToString() ?? ""
                    });
                }
                history = TruncateHistory(history, settings, rawInput);
                input = ExtendedLLMInput.CreateFromHistory(history, systemPrompt, model);
            }
            else
            {
                input = ExtendedLLMInput.Create(message, systemPrompt, model);
            }
            JObject resolvedParams = AssistantService.ResolveParameters(assistantId, settings, session.User);
            ApplyParameters(input, resolvedParams, temperature, maxTokens);
            // Load tools enabled for this assistant and inject their descriptions into the system prompt
            List<JObject> enabledTools = ToolRegistryService.GetEnabledTools(assistantId, settings, session.User);
            if (enabledTools.Count > 0)
            {
                input.Tools = enabledTools;
                string toolPrompt = ToolPromptService.BuildToolSystemPrompt(enabledTools);
                input.SystemPrompt = (input.SystemPrompt ?? "") + toolPrompt;
                // Replace the system message at position 0 of the ChatHistory if present
                if (input.ChatHistory.Messages.Count > 0 && input.ChatHistory.Messages[0].AuthorRole == LLama.Common.AuthorRole.System)
                {
                    input.ChatHistory.Messages[0] = new LLama.Common.ChatHistory.Message(LLama.Common.AuthorRole.System, input.SystemPrompt);
                }
                else
                {
                    LLama.Common.ChatHistory newHistory = new();
                    newHistory.AddMessage(LLama.Common.AuthorRole.System, input.SystemPrompt);
                    foreach (LLama.Common.ChatHistory.Message msg in input.ChatHistory.Messages)
                    {
                        newHistory.AddMessage(msg.AuthorRole, msg.Content);
                    }
                    input.ChatHistory = newHistory;
                }
            }
            await LLMStreamHelper.StreamToWebSocket(socket, input, session);
            return null;
        }
        catch (Exception ex)
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    /// <summary>Resolves instruction text for a request, routing through AssistantService for
    /// canonical IDs and substituting <c>{{userName}}</c>, <c>{{userProfile}}</c>,
    /// <c>{{currentDate}}</c>, and <c>{{assistantName}}</c> from the calling user's profile.
    /// User-profile lookups are scoped strictly to <paramref name="user"/>.</summary>
    private static string ResolveInstructionForRequest(string instructionId, string assistantId, JObject settings, User user)
    {
        if (string.IsNullOrEmpty(instructionId))
        {
            instructionId = InstructionIds.Chat;
        }
        string text;
        if (InstructionIds.All.Contains(instructionId))
        {
            // Canonical instruction IDs resolve from the assistant
            text = AssistantService.ResolveInstruction(instructionId, assistantId, settings, user);
        }
        else
        {
            // Custom/legacy instruction IDs fall through to InstructionService
            text = InstructionService.ResolveInstruction(instructionId, settings, user);
        }
        // Inject per-user profile variables so the model knows who it's talking to.
        // Profile reads are strictly scoped to `user` by UserProfileService.
        JObject assistant = AssistantService.GetAssistant(assistantId, settings, user);
        Dictionary<string, string> vars = UserProfileService.BuildPromptVariables(user, assistant);
        return InstructionService.SubstituteVariables(text, vars);
    }

    /// <summary>Truncates message history to the configured maxContextMessages limit.</summary>
    private static List<ChatMessageData> TruncateHistory(List<ChatMessageData> history, JObject settings, JObject rawInput)
    {
        int maxCtx = rawInput["maxContextMessages"]?.Value<int>() ?? 0;
        if (maxCtx <= 0)
        {
            maxCtx = (settings["parameters"] as JObject)?["maxContextMessages"]?.Value<int>() ?? 0;
        }
        if (maxCtx > 0 && history.Count > maxCtx)
        {
            return history.GetRange(history.Count - maxCtx, maxCtx);
        }
        return history;
    }

    /// <summary>Applies per-request parameter overrides on top of resolved parameters.</summary>
    private static void ApplyParameters(ExtendedLLMInput input, JObject parameters, double temperature, int maxTokens)
    {
        input.Temperature = temperature >= 0 ? temperature : parameters?["temperature"]?.Value<double>() ?? 1.0;
        input.MaxTokens = maxTokens >= 0 ? maxTokens : parameters?["maxTokens"]?.Value<int>() ?? 1024;
        input.TopP = parameters?["topP"]?.Value<double>() ?? 0.9;
    }

    /// <summary>Counts tokens for a block of text or a chat history array.
    /// <para>Accepts either <c>text</c> (a single string) or <c>messages</c> (an array of
    /// <c>{role, content}</c> objects). If <c>messages</c> is provided, the array is flattened
    /// with role headers before counting.</para>
    /// <para>When a <see cref="LlamaSharpLLMBackend"/> is running with a model already loaded,
    /// the exact tokenizer is used and <c>exact=true</c> is returned. Otherwise a cheap
    /// <c>chars/4</c> heuristic is returned with <c>exact=false</c>. Calling this endpoint
    /// never loads a model — if nothing is loaded yet, the caller still gets a heuristic
    /// response immediately rather than paying a multi-second model-load cost.</para>
    /// </summary>
    public static async Task<JObject> LLMAssistantCountTokens(Session session, JObject rawInput)
    {
        try
        {
            string text = rawInput["text"]?.ToString();
            if (text is null)
            {
                JArray messages = rawInput["messages"] as JArray;
                if (messages is not null)
                {
                    StringBuilder sb = new();
                    foreach (JToken msg in messages)
                    {
                        string role = msg["role"]?.ToString() ?? "user";
                        string content = msg["content"]?.ToString() ?? "";
                        sb.Append(role).Append(": ").Append(content).Append('\n');
                    }
                    text = sb.ToString();
                }
            }
            text ??= "";
            // Prefer the running LlamaSharp backend's tokenizer when a model is already loaded.
            // Deliberately never call Load() here — tokenization should be near-instant; if the
            // model isn't loaded yet we fall back to the heuristic rather than paying load cost.
            try
            {
                LlamaSharpLLMBackend llama = Program.Backends.RunningBackendsOfType<LlamaSharpLLMBackend>().FirstOrDefault();
                if (llama?.LoadedContext is not null)
                {
                    LLama.Native.LLamaToken[] toks = llama.LoadedContext.Tokenize(text, addBos: true, special: true);
                    return new JObject
                    {
                        ["success"] = true,
                        ["count"] = toks.Length,
                        ["exact"] = true,
                        ["source"] = "llama.cpp"
                    };
                }
            }
            catch
            {
                // Fall through to heuristic on any tokenizer error.
            }
            int approx = Math.Max(0, (int)Math.Ceiling(text.Length / 4.0));
            return new JObject
            {
                ["success"] = true,
                ["count"] = approx,
                ["exact"] = false,
                ["source"] = "heuristic"
            };
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }
}
