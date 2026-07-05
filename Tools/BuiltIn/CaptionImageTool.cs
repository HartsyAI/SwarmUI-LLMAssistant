using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.LLMs;
using SwarmUI.Extensions.LLMAssistant.Services;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: runs a vision-model pass on a single image and returns a caption in
/// one of several styles (see <see cref="StyleInstructions"/>). Image resolution delegates to
/// <see cref="ImageInputResolver"/>, accepting the same input shapes as <c>generate_image</c>'s
/// <c>initImage</c>: data URI, Output URL, HTTPS URL, or raw base64.</summary>
public class CaptionImageTool : ToolHandler
{
    public override string HandlerId => ToolConstants.CaptionImage;

    /// <summary>Per-user, per-tool config key for the assistant's preferred default style.</summary>
    public const string ConfigDefaultStyle = "defaultStyle";

    /// <summary>The OllamaVision-derived style catalog. Each entry is a system prompt the vision
    /// model receives; the user message is just "describe this image" so the model focuses on the
    /// instruction. New styles can be added here without touching the rest of the tool.</summary>
    public static readonly Dictionary<string, string> StyleInstructions = new()
    {
        ["natural"] = "Describe this image in natural, flowing prose. Cover the subject, setting, mood, and notable details. Be concrete but not exhaustive — one paragraph.",
        ["detailed"] = "Provide a thorough, structured description of this image. Cover: subject(s) and pose, composition and framing, color palette and lighting, art style or medium, mood, and any notable details (text, symbols, anachronisms). Use bullet points.",
        ["simple"] = "Describe this image in a single short sentence.",
        ["danbooru"] = "Describe this image as a Danbooru-style tag list: comma-separated lowercase tags, no sentences. Cover subject, pose, clothing, setting, lighting, art style, and any notable details. Output ONLY the tags.",
        ["artistic"] = "Describe this image's artistic qualities: art style or movement, medium, technique, brushwork or rendering style, influences, and aesthetic mood. Skip the literal subject description unless it's central to the style.",
        ["technical"] = "Analyze this image's technical aspects: composition (rule of thirds, leading lines, etc.), lighting setup and direction, color theory and palette, depth of field, and apparent post-processing. Be precise.",
        ["color-palette"] = "Describe this image's color palette in detail. List the dominant colors (with rough hex codes if possible), their proportions, the harmony type (complementary, analogous, etc.), and the mood the palette creates.",
        ["facial-features"] = "Describe the facial features visible in this image in detail: face shape, eye color and shape, nose, mouth, jawline, hair color and style, skin tone, and any distinguishing marks. If no faces are visible, say so plainly.",
        ["lora-trigger"] = "Caption this image as training data for a LoRA model. Output a comma-separated list of natural-language phrases describing subject, pose, clothing, setting, and style. No sentences, no markdown.",
        ["story"] = "Tell a short three-paragraph story inspired by this image: a beginning that sets the scene, a middle that introduces conflict or motion, and an end that resolves or hints at what comes next."
    };

    public override JObject EnrichForUser(JObject toolDef, Session session, string assistantId = null)
    {
        if (toolDef is null)
        {
            return toolDef;
        }
        // Surface the style enum in the param schema, derived from the live dictionary so adding
        // new styles to StyleInstructions auto-updates the LLM-visible enum.
        JObject enriched = (JObject)toolDef.DeepClone();
        if (enriched["parameters"] is JObject parameters && parameters["properties"] is JObject properties
            && properties["style"] is JObject styleProp)
        {
            JArray styleEnum = [];
            foreach (string key in StyleInstructions.Keys)
            {
                styleEnum.Add(key);
            }
            styleProp["enum"] = styleEnum;
        }
        // Mention the per-assistant default style if one is configured.
        if (session?.User is not null)
        {
            string defaultStyle = ToolConfigService.GetConfig(HandlerId, session.User, assistantId)[ConfigDefaultStyle]?.ToString();
            if (!string.IsNullOrEmpty(defaultStyle) && StyleInstructions.ContainsKey(defaultStyle))
            {
                string desc = enriched["description"]?.ToString() ?? "";
                enriched["description"] = $"{desc}\n\nIf no `style` is provided, the default style (`{defaultStyle}`) will be used.";
            }
        }
        return enriched;
    }

    public override async Task<JObject> Execute(ToolExecutionContext ctx)
    {
        JObject args = ctx.Args;
        Session session = ctx.Session;
        if (session?.User is null)
        {
            return new JObject { ["success"] = false, ["error"] = "caption_image requires an authenticated user." };
        }
        string image = args["image"]?.ToString();
        if (string.IsNullOrWhiteSpace(image))
        {
            return new JObject { ["success"] = false, ["error"] = "image is required (data URI, Output URL, https URL, or raw base64)." };
        }
        string style = args["style"]?.ToString();
        if (string.IsNullOrWhiteSpace(style))
        {
            style = ToolConfigService.GetConfig(HandlerId, session.User, ctx.AssistantId)[ConfigDefaultStyle]?.ToString() ?? "natural";
        }
        if (!StyleInstructions.TryGetValue(style, out string systemPrompt))
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = $"Unknown style '{style}'. Available: {string.Join(", ", StyleInstructions.Keys)}."
            };
        }
        string prependText = args["prependText"]?.ToString() ?? "";
        // Resolve the image (same plumbing as initImage in generate_image).
        ImageInputResolver.ResolvedImage img;
        try
        {
            img = await ImageInputResolver.ResolveAsync(image, ctx.Ct);
        }
        catch (InvalidOperationException ex)
        {
            return new JObject { ["success"] = false, ["error"] = $"image: {ex.Message}" };
        }
        // Build a one-shot LLM input. Use whatever model the chat is using; the LLM dispatcher
        // routes by model id and the backends emit vision content blocks for media-bearing
        // messages automatically (see AnthropicLLMBackend.BuildAnthropicContent etc).
        ExtendedLLMInput input = new()
        {
            Model = ctx.ModelId,
            SystemPrompt = systemPrompt,
            RequestSession = session
        };
        input.Messages.Add(new LLMMessage { Role = LLMRoles.System, Content = systemPrompt });
        input.Messages.Add(new LLMMessage
        {
            Role = LLMRoles.User,
            Content = "Describe this image.",
            Media =
            [
                new LLMMediaAttachment
                {
                    Type = "base64",
                    Data = Convert.ToBase64String(img.Bytes),
                    MediaType = img.MimeType
                }
            ]
        });
        try
        {
            string caption = await LLMDispatcher.Generate(input, ctx.Ct);
            if (caption is null)
            {
                return new JObject { ["success"] = false, ["error"] = "Vision model returned no caption." };
            }
            caption = caption.Trim();
            string finalText = string.IsNullOrEmpty(prependText) ? caption : $"{prependText}{caption}";
            return new JObject
            {
                ["success"] = true,
                ["style"] = style,
                ["caption"] = finalText,
                ["mediaType"] = img.MimeType
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] CaptionImageTool failed: {ex.Message}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }
}
