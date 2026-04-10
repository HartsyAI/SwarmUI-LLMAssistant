using System.Text.RegularExpressions;
using SwarmUI.Extensions.LLMAssistant.LLMs;
using SwarmUI.Extensions.LLMAssistant.Services;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.T2I;

/// <summary>Processes <llmprompt> and <mpprompt> tags in T2I prompts.</summary>
public static class PromptProcessor
{
    // Matches <llmprompt[OptionalInstructionId]:content> or <mpprompt[OptionalInstructionId]:content>
    private static readonly Regex TagRegex = new(
        @"<(?:llm|mp)prompt(?:\[([^\]]+)\])?:((?:[^<>]|<[^>]*>)+)>",
        RegexOptions.Compiled);

    // Matches <llmresponse:N> or <mpresponse:N>
    private static readonly Regex ResponseRegex = new(
        @"<(?:llm|mp)response:(\d+)>",
        RegexOptions.Compiled);

    // Matches <llmoriginal> or <mporiginal>
    private static readonly Regex OriginalRegex = new(
        @"<(?:llm|mp)original>",
        RegexOptions.Compiled);

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
                        return await CallLLM(content, effectiveInstruction, modelOverride);
                    }).Result;
                }
                else
                {
                    response = CallLLM(content, effectiveInstruction, modelOverride).Result;
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

    private static async Task<string> CallLLM(string content, string instructionId, string model)
    {
        // Use active assistant for canonical instruction IDs, fallback to legacy resolution
        string systemPrompt = InstructionIds.All.Contains(instructionId)
            ? AssistantService.ResolveInstruction(instructionId)
            : InstructionService.ResolveInstruction(instructionId);
        ExtendedLLMInput input = ExtendedLLMInput.Create(content, systemPrompt, model);
        return await LLMDispatcher.Generate(input);
    }
}
