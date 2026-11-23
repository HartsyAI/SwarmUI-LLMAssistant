using Hartsy.Extensions.LLMAssistant.Services.ImageTools;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: lists the image models installed for the calling user (name, type, usual resolution, prompt notes), so the model can pick one for <c>generate_image</c> instead of guessing a name.
/// Only reads, and only what core shows this user. The listing is shared with the <c>LLMAssistantListImageModels</c> route (<see cref="ImageCatalogService"/>).</summary>
public sealed class ListImageModelsTool : ToolHandler
{
    public override string HandlerId => ToolConstants.ListImageModels;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Read;

    public override async Task<JObject> Execute(ToolExecutionContext ctx) => await ImageCatalogService.ListModelsAsync(ctx.Session, ctx.Args);
}

/// <summary>Built-in tool: lists the installed LoRAs (name, base model family, trigger words and tags from their metadata), optionally only those that fit one installed model.</summary>
public sealed class ListLorasTool : ToolHandler
{
    public override string HandlerId => ToolConstants.ListLoras;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Read;

    public override async Task<JObject> Execute(ToolExecutionContext ctx) => await ImageCatalogService.ListLorasAsync(ctx.Session, ctx.Args);
}

/// <summary>Built-in tool: lists the wildcard names the user can use in a prompt as <c>__name__</c>. Names only unless the call asks for a few sample lines, because the contents are the user's own files
/// and a hosted model should not read them unasked.</summary>
public sealed class ListWildcardsTool : ToolHandler
{
    public override string HandlerId => ToolConstants.ListWildcards;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Read;

    public override Task<JObject> Execute(ToolExecutionContext ctx) => Task.FromResult(ImageCatalogService.ListWildcards(ctx.Session, ctx.Args));
}

/// <summary>Built-in tool: lists the calling user's saved image presets (name, description, key settings).</summary>
public sealed class ListPresetsTool : ToolHandler
{
    public override string HandlerId => ToolConstants.ListPresets;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Read;

    public override Task<JObject> Execute(ToolExecutionContext ctx) => Task.FromResult(ImageCatalogService.ListPresets(ctx.Session, ctx.Args));
}
