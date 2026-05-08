using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.LLMs;
using SwarmUI.Extensions.LLMAssistant.Services;
using SwarmUI.LLMs;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: captions multiple images with role-specific prompts (style / subject /
/// setting / reference) and fuses the descriptions into a single image-generation prompt.
///
/// <para>Two LLM calls happen: one caption per image (parallel), then a single fusion call that
/// merges them. Both reuse <see cref="LLMDispatcher"/> + the existing vision plumbing — no new
/// generation infrastructure.</para>
///
/// <para>Use case: "use the style of image A, the character from image B, and the setting from
/// image C" → tool returns one cohesive prompt the user can feed into generate_image.</para></summary>
public class FuseImageDescriptionsTool : ToolHandler
{
    public override string HandlerId => ToolConstants.FuseImageDescriptions;

    private static readonly Dictionary<string, string> _rolePrompts = new()
    {
        ["style"]     = "Describe ONLY the artistic style of this image — medium, technique, color treatment, line work, rendering. Skip the subject and setting. Output one or two sentences.",
        ["subject"]   = "Describe ONLY the main subject of this image — what/who is depicted, their pose, expression, clothing, identifying features. Skip the background and art style. Output one or two sentences.",
        ["setting"]   = "Describe ONLY the setting/background of this image — environment, location, time of day, weather, ambient details. Skip the subject and art style. Output one or two sentences.",
        ["reference"] = "Describe what's notable in this image that should inform a generation — anything that stands out: composition, lighting choice, an unusual element. Output one or two sentences."
    };

    public override async Task<JObject> Execute(ToolExecutionContext ctx)
    {
        JObject args = ctx.Args;
        Session session = ctx.Session;
        if (session?.User is null)
        {
            return new JObject { ["success"] = false, ["error"] = "fuse_image_descriptions requires an authenticated user." };
        }
        if (args["images"] is not JArray imagesArr || imagesArr.Count == 0)
        {
            return new JObject { ["success"] = false, ["error"] = "images is required (non-empty array of { url, role } entries)." };
        }
        if (imagesArr.Count > 6)
        {
            return new JObject { ["success"] = false, ["error"] = "Too many images — max 6 per fuse." };
        }
        // Resolve each image + dispatch role-specific captions in parallel. Reusing
        // ImageInputResolver gives us a consistent "data URI / Output URL / https URL / base64"
        // input contract everywhere.
        List<(string Role, ImageInputResolver.ResolvedImage Image)> resolved = [];
        for (int i = 0; i < imagesArr.Count; i++)
        {
            JToken entry = imagesArr[i];
            string url = entry?["url"]?.ToString();
            string role = entry?["role"]?.ToString() ?? "reference";
            if (string.IsNullOrWhiteSpace(url))
            {
                return new JObject { ["success"] = false, ["error"] = $"images[{i}].url is required" };
            }
            if (!_rolePrompts.ContainsKey(role))
            {
                return new JObject { ["success"] = false, ["error"] = $"images[{i}].role must be one of: style, subject, setting, reference (got '{role}')." };
            }
            try
            {
                ImageInputResolver.ResolvedImage img = await ImageInputResolver.ResolveAsync(url, ctx.Ct);
                resolved.Add((role, img));
            }
            catch (InvalidOperationException ex)
            {
                return new JObject { ["success"] = false, ["error"] = $"images[{i}]: {ex.Message}" };
            }
        }
        // Fan out captioning. Each call is a one-shot vision request via the same dispatcher
        // path the caption_image tool uses.
        Task<string>[] captionTasks = [.. resolved.Select(r => CaptionRoleAsync(r.Role, r.Image, ctx))];
        string[] captions;
        try
        {
            captions = await Task.WhenAll(captionTasks);
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = $"Captioning failed: {ex.Message}" };
        }
        // Fuse step: ask the same model to merge the role-tagged captions into one prompt.
        StringBuilder fusionContext = new();
        for (int i = 0; i < captions.Length; i++)
        {
            fusionContext.Append("- ").Append(resolved[i].Role).Append(": ").AppendLine(captions[i]?.Trim());
        }
        string fusionSystem = "You are a prompt engineer. The user will provide several role-tagged image descriptions. Merge them into a single cohesive Stable Diffusion prompt that combines the requested elements. Keep tags / phrases concise. Output ONLY the prompt — no preamble, no quotation marks, no commentary.";
        ExtendedLLMInput fusionInput = new()
        {
            Model = ctx.ModelId,
            SystemPrompt = fusionSystem,
            RequestSession = session
        };
        fusionInput.Messages.Add(new LLMMessage { Role = LLMRoles.System, Content = fusionSystem });
        fusionInput.Messages.Add(new LLMMessage { Role = LLMRoles.User, Content = fusionContext.ToString() });
        try
        {
            string fused = (await LLMDispatcher.Generate(fusionInput, ctx.Ct))?.Trim() ?? "";
            JArray captionList = [];
            for (int i = 0; i < captions.Length; i++)
            {
                captionList.Add(new JObject { ["role"] = resolved[i].Role, ["caption"] = captions[i]?.Trim() ?? "" });
            }
            return new JObject
            {
                ["success"] = true,
                ["prompt"] = fused,
                ["captions"] = captionList
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] FuseImageDescriptionsTool fusion failed: {ex.Message}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>One vision call for one image with the role-specific instruction.</summary>
    private static async Task<string> CaptionRoleAsync(string role, ImageInputResolver.ResolvedImage img, ToolExecutionContext ctx)
    {
        string systemPrompt = _rolePrompts[role];
        ExtendedLLMInput input = new()
        {
            Model = ctx.ModelId,
            SystemPrompt = systemPrompt,
            RequestSession = ctx.Session
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
        return await LLMDispatcher.Generate(input, ctx.Ct);
    }
}
