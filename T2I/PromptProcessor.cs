using System.Text.RegularExpressions;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.T2I;

/// <summary>Processes <llmprompt> and <mpprompt> tags in T2I prompts.</summary>
public static class PromptProcessor
{
    /// <summary>Matches <c>&lt;llmprompt[OptionalInstructionId]:content&gt;</c> or <c>&lt;mpprompt[OptionalInstructionId]:content&gt;</c>.</summary>
    private static readonly Regex TagRegex = new(
        @"<(?:llm|mp)prompt(?:\[([^\]]+)\])?:((?:[^<>]|<[^>]*>)+)>",
        RegexOptions.Compiled);

    /// <summary>Matches <c>&lt;llmresponse:N&gt;</c> or <c>&lt;mpresponse:N&gt;</c>.</summary>
    private static readonly Regex ResponseRegex = new(
        @"<(?:llm|mp)response:(\d+)>",
        RegexOptions.Compiled);

    /// <summary>Matches <c>&lt;llmoriginal&gt;</c> or <c>&lt;mporiginal&gt;</c>.</summary>
    private static readonly Regex OriginalRegex = new(
        @"<(?:llm|mp)original>",
        RegexOptions.Compiled);

    /// <summary>Process-lifetime cache of prompt-tag LLM responses, keyed by prompt+instruction.</summary>
    private static readonly PromptCacheService Cache = new();

    /// <summary>Processes LLM prompt tags in a T2I parameter input.</summary>
    public static void ProcessPrompt(T2IParamInput input)
    {
        string prompt = input.Get(T2IParamTypes.Prompt);
        if (string.IsNullOrEmpty(prompt))
        {
            return;
        }
        // Check if prompt contains any LLM tags
        if (!prompt.Contains("<llmprompt") && !prompt.Contains("<mpprompt"))
        {
            return;
        }
        bool useCache = input.TryGet(PromptTagHandler.ParamUseCache, out bool cacheVal) && cacheVal;
        string instructionOverride = input.TryGet(PromptTagHandler.ParamInstructions, out string instrVal) ? instrVal : null;
        string modelOverride = input.TryGet(PromptTagHandler.ParamModelId, out string modelVal) ? modelVal : null;
        string assistantOverride = input.TryGet(PromptTagHandler.ParamAssistantId, out string asstVal) ? asstVal : null;
        User user = input.SourceSession?.User;
        // Resolve which assistant to use (T2I param override → user's active → fall back to default).
        string assistantId = !string.IsNullOrEmpty(assistantOverride) && assistantOverride != "default"
            ? assistantOverride
            : (user is not null ? AssistantService.GetActiveAssistantId(user: user) : null);
        string originalPrompt = prompt;
        List<string> responses = [];
        // Process all LLM tags
        prompt = TagRegex.Replace(prompt, match =>
        {
            string tagInstructionId = match.Groups[1].Success ? match.Groups[1].Value : null;
            string content = match.Groups[2].Value;
            string effectiveInstruction = tagInstructionId ?? instructionOverride ?? InstructionIds.Prompt;
            try
            {
                string response;
                if (useCache)
                {
                    response = Cache.GetOrCreate(content, effectiveInstruction, async () =>
                    {
                        return await CallLLM(content, effectiveInstruction, modelOverride, user, assistantId);
                    }).Result;
                }
                else
                {
                    response = CallLLM(content, effectiveInstruction, modelOverride, user, assistantId).Result;
                }
                responses.Add(response);
                return response;
            }
            catch (Exception ex)
            {
                Logs.Error($"[LLMAssistant] Failed to process prompt tag: {ex.Message}");
                return content; // Fall back to original content on error
            }
        });
        // Resolve <llmresponse:N> / <mpresponse:N> references
        prompt = ResponseRegex.Replace(prompt, match =>
        {
            if (int.TryParse(match.Groups[1].Value, out int index) && index >= 0 && index < responses.Count)
            {
                return responses[index];
            }
            return match.Value;
        });
        // Resolve <llmoriginal> / <mporiginal>
        prompt = OriginalRegex.Replace(prompt, _ => originalPrompt);
        input.Set(T2IParamTypes.Prompt, prompt);
    }

    /// <summary>One LLM call from a T2I prompt tag. Resolves the system prompt through the
    /// chosen assistant (so the persona/character active in chat shapes prompt enhancement too)
    /// with per-model instruction variants applied.</summary>
    private static async Task<string> CallLLM(string content, string instructionId, string model, User user, string assistantId)
    {
        // Look up model facts so per-model variants can pick the right text. Tolerates unknown
        // models (returns null; only Default/Exact/Glob matchers will then match).
        LLMModelInfo modelInfo = await LLMModelLookup.GetByIdAsync(model);
        string systemPrompt = InstructionIds.All.Contains(instructionId)
            ? AssistantService.ResolveInstruction(instructionId, assistantId, user: user, modelInfo: modelInfo)
            : InstructionService.ResolveInstruction(instructionId, user: user);
        ExtendedLLMInput input = ExtendedLLMInput.Create(content, systemPrompt, model);
        return await LLMDispatcher.Generate(input);
    }
}
