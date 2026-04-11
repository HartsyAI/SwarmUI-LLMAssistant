using System.Net.WebSockets;
using LLama.Common;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
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

    /// <summary>Resolves instruction text for a request, routing through AssistantService for canonical IDs.</summary>
    private static string ResolveInstructionForRequest(string instructionId, string assistantId, JObject settings, User user)
    {
        if (string.IsNullOrEmpty(instructionId))
        {
            instructionId = InstructionIds.Chat;
        }
        // Canonical instruction IDs resolve from the assistant
        if (InstructionIds.All.Contains(instructionId))
        {
            return AssistantService.ResolveInstruction(instructionId, assistantId, settings, user);
        }
        // Custom/legacy instruction IDs fall through to InstructionService
        return InstructionService.ResolveInstruction(instructionId, settings, user);
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
}
