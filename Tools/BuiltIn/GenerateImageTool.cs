using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace SwarmUI.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: generate an image via SwarmUI's native T2I engine.</summary>
public class GenerateImageTool : ToolHandler
{
    public override string HandlerId => ToolConstants.GenerateImage;

    public override async Task<JObject> Execute(JObject args, Session session, CancellationToken ct)
    {
        string prompt = args["prompt"]?.ToString();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new JObject { ["success"] = false, ["error"] = "prompt is required" };
        }
        string aspect = args["aspect"]?.ToString() ?? "square";
        (int width, int height) = aspect.ToLowerInvariant() switch
        {
            "portrait" => (832, 1216),
            "landscape" => (1216, 832),
            _ => (1024, 1024)
        };
        JObject rawInput = new()
        {
            ["prompt"] = prompt,
            ["width"] = width,
            ["height"] = height
        };
        try
        {
            JObject result = await T2IAPI.GenerateText2Image(session, 1, rawInput);
            if (result.TryGetValue("error", out JToken err))
            {
                return new JObject { ["success"] = false, ["error"] = err.ToString() };
            }
            if (result.TryGetValue("images", out JToken imagesToken) && imagesToken is JArray images && images.Count > 0)
            {
                return new JObject
                {
                    ["success"] = true,
                    ["imageUrl"] = images[0].ToString(),
                    ["prompt"] = prompt,
                    ["width"] = width,
                    ["height"] = height
                };
            }
            return new JObject { ["success"] = false, ["error"] = "No image was generated." };
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] GenerateImageTool failed: {ex.Message}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }
}
