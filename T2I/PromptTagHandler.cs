using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Hartsy.Extensions.LLMAssistant.Services;

namespace Hartsy.Extensions.LLMAssistant.T2I;

/// <summary>Registers T2I parameters and prompt tag handlers for LLM integration.</summary>
public static class PromptTagHandler
{
    /// <summary>T2I param group housing all LLM prompt-processing parameters.</summary>
    private static T2IParamGroup paramGroup;
    /// <summary>Whether to cache LLM responses for identical prompts.</summary>
    private static T2IRegisteredParam<bool> paramUseCache;
    /// <summary>Which LLM model to use for prompt processing.</summary>
    private static T2IRegisteredParam<string> paramModelId;
    /// <summary>Which instruction set to use for prompt processing.</summary>
    private static T2IRegisteredParam<string> paramInstructions;
    /// <summary>Which assistant's instructions/persona to use for prompt processing.</summary>
    private static T2IRegisteredParam<string> paramAssistantId;
    /// <summary>Whether to generate a consistent wildcard seed per batch.</summary>
    private static T2IRegisteredParam<bool> paramWildcardSeed;

    /// <summary>Public accessor for <see cref="paramUseCache"/>.</summary>
    public static T2IRegisteredParam<bool> ParamUseCache => paramUseCache;
    /// <summary>Public accessor for <see cref="paramModelId"/>.</summary>
    public static T2IRegisteredParam<string> ParamModelId => paramModelId;
    /// <summary>Public accessor for <see cref="paramInstructions"/>.</summary>
    public static T2IRegisteredParam<string> ParamInstructions => paramInstructions;
    /// <summary>Public accessor for <see cref="paramAssistantId"/>.</summary>
    public static T2IRegisteredParam<string> ParamAssistantId => paramAssistantId;
    /// <summary>Public accessor for <see cref="paramWildcardSeed"/>.</summary>
    public static T2IRegisteredParam<bool> ParamWildcardSeed => paramWildcardSeed;

    /// <summary>Registers both the T2I parameters and the prompt tag handlers.</summary>
    public static void RegisterAll()
    {
        RegisterParameters();
        RegisterPromptTags();
    }

    /// <summary>Registers the LLM prompt-processing T2I parameters (cache, model, instructions, assistant, wildcard seed).</summary>
    private static void RegisterParameters()
    {
        paramGroup = new T2IParamGroup(Name: "LLM Prompt Processing",
            Description: "Parameters for LLM-based prompt enhancement via <llmprompt> tags.",
            Toggles: true, Open: false, OrderPriority: 5);

        paramUseCache = T2IParamTypes.Register<bool>(new(
            "LLM Use Cache",
            "Cache LLM responses for identical prompts. Useful for batch generation.",
            "true",
            Group: paramGroup,
            OrderPriority: 1
        ));

        paramWildcardSeed = T2IParamTypes.Register<bool>(new(
            "LLM Generate Wildcard Seed",
            "Generate a consistent wildcard seed per batch for reproducible results.",
            "false",
            Group: paramGroup,
            OrderPriority: 2
        ));

        paramModelId = T2IParamTypes.Register<string>(new(
            "LLM Model ID",
            "Which LLM model to use for prompt processing.",
            "default",
            IgnoreIf: "default",
            Group: paramGroup,
            OrderPriority: 3,
            ValidateValues: false
        ));

        paramInstructions = T2IParamTypes.Register<string>(new(
            "LLM Instructions",
            "Which instruction set to use for prompt processing.",
            InstructionIds.Prompt,
            Group: paramGroup,
            OrderPriority: 4,
            ValidateValues: false,
            GetValues: session =>
            {
                List<string> values = [.. InstructionIds.All];
                JArray instructions = InstructionService.GetInstructionList(user: session?.User);
                foreach (JToken instr in instructions)
                {
                    string id = instr["id"]?.ToString();
                    if (!string.IsNullOrEmpty(id) && !values.Contains(id))
                    {
                        values.Add(id);
                    }
                }
                return values;
            }
        ));

        // Lets the user pin a generation to a specific assistant's instructions/persona —
        // overrides the active-assistant default. Empty string ("default") means "use the
        // user's current active assistant" — the same one Chat is using.
        paramAssistantId = T2IParamTypes.Register<string>(new(
            "LLM Assistant ID",
            "Which assistant's instructions (and per-model variants) to use for <llmprompt> processing. Default = active assistant.",
            "default",
            IgnoreIf: "default",
            Group: paramGroup,
            OrderPriority: 5,
            ValidateValues: false,
            GetValues: session =>
            {
                List<string> values = ["default"];
                if (session?.User is not null)
                {
                    JArray list = AssistantService.GetAssistantList(user: session.User);
                    foreach (JToken a in list)
                    {
                        string id = a["id"]?.ToString();
                        if (!string.IsNullOrEmpty(id))
                        {
                            values.Add(id);
                        }
                    }
                }
                return values;
            }
        ));

        // Register late parameter handler for prompt processing
        T2IParamInput.LateSpecialParameterHandlers.Add(input =>
        {
            PromptProcessor.ProcessPrompt(input);
        });
    }

    /// <summary>Registers the <c>llmprompt</c>/<c>llmresponse</c>/<c>llmoriginal</c> custom prompt-tag prefixes and their (no-op) post-processors.</summary>
    private static void RegisterPromptTags()
    {
        PromptRegion.RegisterCustomPrefix("llmprompt");
        PromptRegion.RegisterCustomPrefix("llmresponse");
        PromptRegion.RegisterCustomPrefix("llmoriginal");
        T2IPromptHandling.PromptTagPostProcessors["llmprompt"] = (tag, _) => tag;
        T2IPromptHandling.PromptTagPostProcessors["llmresponse"] = (tag, _) => tag;
        T2IPromptHandling.PromptTagPostProcessors["llmoriginal"] = (tag, _) => tag;
    }
}
