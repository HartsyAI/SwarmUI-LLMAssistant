using System.IO;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Extensions.LLMAssistant.LLMs;
using SwarmUI.Extensions.LLMAssistant.Services;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: walks a folder under the user's OutputDirectory, captions every image
/// inside (using a chosen <see cref="CaptionImageTool"/> style), and writes the result as
/// <c>{name}.txt</c> next to each image. Used for LoRA-training dataset prep.
///
/// <para>Sandboxing: the folder argument is resolved relative to <see cref="User.OutputDirectory"/>
/// via <see cref="WebServer.CheckFilePath"/>. Caption files write into the same folder. The tool
/// is disabled by default (long-running, produces files) — users opt in per-assistant.</para>
///
/// <para>Cancellable mid-run: the <see cref="ToolExecutionContext.Ct"/> is checked between
/// images so killing the chat WS aborts the batch promptly.</para></summary>
public class BatchCaptionFolderTool : ToolHandler
{
    public override string HandlerId => ToolConstants.BatchCaptionFolder;

    /// <summary>Hard cap on how many images one invocation will process. Avoids runaway batches
    /// that could chew through quota or take hours.</summary>
    public const int MaxImagesPerRun = 200;

    private static readonly HashSet<string> _imageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"
    };

    public override async Task<JObject> Execute(ToolExecutionContext ctx)
    {
        JObject args = ctx.Args;
        Session session = ctx.Session;
        if (session?.User is null)
        {
            return new JObject { ["success"] = false, ["error"] = "batch_caption_folder requires an authenticated user." };
        }
        string folder = args["folder"]?.ToString();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return new JObject { ["success"] = false, ["error"] = "folder is required (path relative to your Output directory)." };
        }
        string style = args["style"]?.ToString();
        if (string.IsNullOrWhiteSpace(style))
        {
            style = "lora-trigger"; // sensible default for the dataset-prep use case
        }
        if (!CaptionImageTool.StyleInstructions.ContainsKey(style))
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = $"Unknown style '{style}'. Available: {string.Join(", ", CaptionImageTool.StyleInstructions.Keys)}."
            };
        }
        string prependText = args["prependText"]?.ToString() ?? "";
        // Sandbox the folder under the user's OutputDirectory.
        string outputRoot = Path.GetFullPath(session.User.OutputDirectory);
        (string fullFolder, string consoleErr, string userErr) = WebServer.CheckFilePath(outputRoot, folder);
        if (fullFolder is null)
        {
            if (consoleErr is not null) Logs.Warning($"[LLMAssistant] batch_caption_folder rejected path: {consoleErr}");
            return new JObject { ["success"] = false, ["error"] = userErr ?? "Invalid folder." };
        }
        if (!Directory.Exists(fullFolder))
        {
            return new JObject { ["success"] = false, ["error"] = $"Folder not found: {folder}" };
        }
        // Enumerate images. Top level only; recursion is a sharp footgun for the LLM to wield.
        List<string> images = [.. Directory.EnumerateFiles(fullFolder, "*", SearchOption.TopDirectoryOnly)
            .Where(p => _imageExts.Contains(Path.GetExtension(p)))
            .OrderBy(p => p)];
        if (images.Count == 0)
        {
            return new JObject { ["success"] = false, ["error"] = $"No supported images in {folder} (looking for {string.Join(", ", _imageExts)})." };
        }
        if (images.Count > MaxImagesPerRun)
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = $"Folder has {images.Count} images; max {MaxImagesPerRun} per run. Split it into smaller folders."
            };
        }
        string systemPrompt = CaptionImageTool.StyleInstructions[style];
        JArray writtenList = [];
        JArray failedList = [];
        foreach (string imagePath in images)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            string baseName = Path.GetFileNameWithoutExtension(imagePath);
            string captionPath = Path.Combine(fullFolder, baseName + ".txt");
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(imagePath, ctx.Ct);
                string mime = GuessMimeFromExt(imagePath);
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
                            Data = Convert.ToBase64String(bytes),
                            MediaType = mime
                        }
                    ]
                });
                string caption = (await LLMDispatcher.Generate(input, ctx.Ct))?.Trim() ?? "";
                string finalText = string.IsNullOrEmpty(prependText) ? caption : $"{prependText}{caption}";
                await File.WriteAllTextAsync(captionPath, finalText, System.Text.Encoding.UTF8, ctx.Ct);
                writtenList.Add(new JObject { ["image"] = Path.GetFileName(imagePath), ["captionFile"] = baseName + ".txt" });
            }
            catch (OperationCanceledException)
            {
                throw; // bubble up; partial results stay on disk
            }
            catch (Exception ex)
            {
                Logs.Warning($"[LLMAssistant] batch_caption_folder failed on {imagePath}: {ex.Message}");
                failedList.Add(new JObject { ["image"] = Path.GetFileName(imagePath), ["error"] = ex.Message });
            }
        }
        return new JObject
        {
            ["success"] = true,
            ["folder"] = folder,
            ["style"] = style,
            ["written"] = writtenList,
            ["failed"] = failedList,
            ["totalProcessed"] = writtenList.Count,
            ["totalFailed"] = failedList.Count
        };
    }

    private static string GuessMimeFromExt(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/png"
        };
    }
}
