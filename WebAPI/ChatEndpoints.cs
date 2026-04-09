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

    /// <summary>Sends a message to an LLM backend and returns the full response.</summary>
    public static async Task<JObject> LLMAssistantSendMessage(Session session,
        string message, string instructionId = null, string model = null,
        double temperature = -1, int maxTokens = -1, int backendId = -1, bool noCache = false)
    {
        try
        {
            JObject settings = SettingsService.GetSettings();
            string systemPrompt = InstructionService.ResolveInstruction(instructionId, settings);
            ExtendedLLMInput input = ExtendedLLMInput.Create(message, systemPrompt, model);
            ApplyParameters(input, settings, temperature, maxTokens);
            string response;
            if (noCache)
            {
                response = await LLMDispatcher.Generate(input, backendId);
            }
            else
            {
                response = await _cache.GetOrCreate(message, instructionId, async () =>
                {
                    return await LLMDispatcher.Generate(input, backendId);
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
            int backendId = rawInput["backendId"]?.Value<int>() ?? -1;
            // Build history from previous messages if provided
            JArray historyArray = rawInput["history"] as JArray;
            JObject settings = SettingsService.GetSettings();
            string systemPrompt = InstructionService.ResolveInstruction(instructionId, settings);
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
            ApplyParameters(input, settings, temperature, maxTokens);
            await LLMStreamHelper.StreamToWebSocket(socket, input, backendId);
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

    /// <summary>Truncates message history to the configured maxContextMessages limit.</summary>
    private static List<ChatMessageData> TruncateHistory(List<ChatMessageData> history, JObject settings, JObject rawInput)
    {
        // Per-thread override takes priority, then global setting
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

    private static void ApplyParameters(ExtendedLLMInput input, JObject settings, double temperature, int maxTokens)
    {
        JObject parameters = settings["parameters"] as JObject;
        input.Temperature = temperature >= 0 ? temperature : parameters?["temperature"]?.Value<double>() ?? 1.0;
        input.MaxTokens = maxTokens >= 0 ? maxTokens : parameters?["maxTokens"]?.Value<int>() ?? 1024;
        input.TopP = parameters?["topP"]?.Value<double>() ?? 0.9;
    }
}
