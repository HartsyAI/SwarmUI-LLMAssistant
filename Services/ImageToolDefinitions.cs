using Newtonsoft.Json.Linq;
using Hartsy.Extensions.LLMAssistant.Services.ImageTools;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>The definitions of the image catalog tools, as the tool list stores them. They only read, so they are on by default; each needs <c>llm_tool_image_catalog</c>.
/// The descriptions tell a model when to call them (before choosing a model or LoRA name) because that is the guidance a model reads when it has the tool.</summary>
public static class ImageToolDefinitions
{
    /// <summary>The guidance every image tool shares about choosing a model, used in <c>generate_image</c>'s description too.</summary>
    public const string ModelGuidance = "Never guess a model name: call list_image_models first (or use a preset), then pass an exact name from its list.";

    /// <summary>The definitions keyed by tool id.</summary>
    public static JObject Build(string now)
    {
        JObject tools = [];
        Add(tools, now, ToolConstants.ListImageModels, "List Image Models",
            "List the text-to-image models installed for this user: exact name (what generate_image's `model` takes), model type, usual resolution and prompt notes from the model's metadata. Call this BEFORE generate_image when no preset names a model, and whenever the user asks what models they have or wants a style that suits a particular model (photo, anime, ...). Filter with `query` (words in the name) or `class` (a model type, e.g. 'xl', 'flux'). Results are bounded; narrow the filter instead of asking for everything.",
            Props(("query", "string", "Words that must appear in the model's name or title, e.g. 'anime'."), ("class", "string", "Only models of this type or family: a substring of the architecture id, class name or compatibility family, e.g. 'xl' or 'flux'."), ("includeVideo", "boolean", "Also list models of video and audio families (left out by default, since this is the list of image models)."), ("limit", "integer", $"How many to return (default {ImageCatalog.DefaultLimit}, at most {ImageCatalog.MaxLimit})."), ("offset", "integer", "Skip this many matches, to read the next page when the result says more match.")));
        Add(tools, now, ToolConstants.ListLoras, "List LoRAs",
            "List the LoRAs installed for this user with the base model family each fits, the trigger words and tags from its metadata. Use it when the user asks for a style or character a LoRA could give, or to see which LoRAs fit a model: pass `forModel` (an installed model name) to get only the compatible ones. Apply one with generate_image's params: { loras: \"<name>\", loraweights: \"<weight>\" } and put its triggerWords in the prompt.",
            Props(("query", "string", "Words that must appear in the LoRA's name, title or tags."), ("forModel", "string", "An installed image model's name: only LoRAs of exactly the same family are listed."), ("baseModel", "string", "Only LoRAs for this model type or family (substring), e.g. 'xl' or 'flux'."), ("limit", "integer", $"How many to return (default {ImageCatalog.DefaultLimit}, at most {ImageCatalog.MaxLimit})."), ("offset", "integer", "Skip this many matches, to read the next page when the result says more match.")));
        Add(tools, now, ToolConstants.ListWildcards, "List Wildcards",
            "List the user's wildcard names. A wildcard `name` is used inside a prompt as __name__ and expands to a random line of that file. Returns names only; set `includeSamples` to true only when the user asks what a wildcard contains, because its lines are the user's own files.",
            Props(("query", "string", "Words that must appear in the wildcard's name."), ("includeSamples", "boolean", "Also return a few lines of the first wildcards. Off by default; leave it off unless the user wants to see the contents."), ("limit", "integer", $"How many to return (default {ImageCatalog.DefaultLimit}, at most {ImageCatalog.MaxLimit}).")));
        Add(tools, now, ToolConstants.ListPresets, "List Presets",
            "List the user's saved image presets: name, description and key settings (model, steps, size). A preset bundles a model and settings, so when one fits the request, pass its name as generate_image's `preset` instead of choosing a model.",
            Props(("query", "string", "Words that must appear in the preset's name or description."), ("limit", "integer", $"How many to return (default {ImageCatalog.DefaultLimit}, at most {ImageCatalog.MaxLimit}).")));
        return tools;
    }

    /// <summary>An object schema's <c>properties</c> from (name, type, description) triples.</summary>
    private static JObject Props(params (string Name, string Type, string Description)[] properties)
    {
        JObject result = [];
        foreach ((string name, string type, string description) in properties)
        {
            result[name] = new JObject { ["type"] = type, ["description"] = description };
        }
        return result;
    }

    /// <summary>Adds one definition.</summary>
    private static void Add(JObject tools, string now, string id, string name, string description, JObject properties)
    {
        tools[id] = new JObject
        {
            ["id"] = id,
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JObject { ["type"] = "object", ["properties"] = properties },
            ["handlerType"] = ToolConstants.HandlerBuiltIn,
            ["handlerId"] = id,
            ["enabled"] = true,
            ["isBuiltIn"] = true,
            ["created"] = now,
            ["updated"] = now,
        };
    }
}
