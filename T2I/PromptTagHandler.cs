using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.Extensions.LLMAssistant.Services;

namespace SwarmUI.Extensions.LLMAssistant.T2I;

/// <summary>Registers T2I parameters and prompt tag handlers for LLM integration.</summary>
public static class PromptTagHandler
{
    private static T2IParamGroup _paramGroup;
    private static T2IRegisteredParam<bool> _paramUseCache;
    private static T2IRegisteredParam<string> _paramModelId;
    private static T2IRegisteredParam<string> _paramInstructions;
    private static T2IRegisteredParam<bool> _paramWildcardSeed;

    public static T2IRegisteredParam<bool> ParamUseCache => _paramUseCache;
    public static T2IRegisteredParam<string> ParamModelId => _paramModelId;
    public static T2IRegisteredParam<string> ParamInstructions => _paramInstructions;
    public static T2IRegisteredParam<bool> ParamWildcardSeed => _paramWildcardSeed;

    public static void RegisterAll()
    {
        RegisterParameters();
        RegisterPromptTags();
    }

    private static void RegisterParameters()
    {
        _paramGroup = new T2IParamGroup(Name: "LLM Prompt Processing",
            Description: "Parameters for LLM-based prompt enhancement via <llmprompt> tags.",
            Toggles: true, Open: false, OrderPriority: 5);

        _paramUseCache = T2IParamTypes.Register<bool>(new(
            "LLM Use Cache",
            "Cache LLM responses for identical prompts. Useful for batch generation.",
            "true",
            Group: _paramGroup,
            OrderPriority: 1
        ));

        _paramWildcardSeed = T2IParamTypes.Register<bool>(new(
            "LLM Generate Wildcard Seed",
            "Generate a consistent wildcard seed per batch for reproducible results.",
            "false",
            Group: _paramGroup,
            OrderPriority: 2
        ));

        _paramModelId = T2IParamTypes.Register<string>(new(
            "LLM Model ID",
            "Which LLM model to use for prompt processing.",
            "default",
            IgnoreIf: "default",
            Group: _paramGroup,
            OrderPriority: 3,
            ValidateValues: false
        ));

        _paramInstructions = T2IParamTypes.Register<string>(new(
            "LLM Instructions",
            "Which instruction set to use for prompt processing.",
            "prompt",
            Group: _paramGroup,
            OrderPriority: 4,
            ValidateValues: false,
            GetValues: _ =>
            {
                List<string> values = ["prompt", "caption", "chat", "vision", "randomprompt"];
                JArray instructions = InstructionService.GetInstructionList();
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

        // Register late parameter handler for prompt processing
        T2IParamInput.LateSpecialParameterHandlers.Add(input =>
        {
            PromptProcessor.ProcessPrompt(input);
        });
    }

    private static void RegisterPromptTags()
    {
        // Register our primary tags
        PromptRegion.RegisterCustomPrefix("llmprompt");
        PromptRegion.RegisterCustomPrefix("llmresponse");
        PromptRegion.RegisterCustomPrefix("llmoriginal");
        // Register backward-compatible aliases (only if MagicPrompt is not installed)
        bool magicPromptInstalled = SwarmUI.Core.Program.Extensions.Extensions.Any(
            e => e.GetType().Name == "MagicPromptExtension");
        if (!magicPromptInstalled)
        {
            PromptRegion.RegisterCustomPrefix("mpprompt");
            PromptRegion.RegisterCustomPrefix("mpresponse");
            PromptRegion.RegisterCustomPrefix("mporiginal");
        }
        // Register post-processors for our tags
        T2IPromptHandling.PromptTagPostProcessors["llmprompt"] = (tag, _) => tag;
        T2IPromptHandling.PromptTagPostProcessors["llmresponse"] = (tag, _) => tag;
        T2IPromptHandling.PromptTagPostProcessors["llmoriginal"] = (tag, _) => tag;
        if (!magicPromptInstalled)
        {
            T2IPromptHandling.PromptTagPostProcessors["mpprompt"] = (tag, _) => tag;
            T2IPromptHandling.PromptTagPostProcessors["mpresponse"] = (tag, _) => tag;
            T2IPromptHandling.PromptTagPostProcessors["mporiginal"] = (tag, _) => tag;
        }
    }
}
