using System.IO;
using System.Text;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.PromptAssets;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Loads a skill: the instructions the model was told about in its system prompt (see <see cref="PromptAssetService.BuildIndex"/>). The skill is looked up in
/// the same set of assets the index was built from, for the calling user and chat, so it can only give what was advertised. With <c>file</c> it reads another file from the
/// skill's own folder (a reference, a template), confined to that folder.</summary>
public sealed class SkillTool : ToolHandler
{
    /// <summary>The most characters of a skill's extra file given to the model.</summary>
    public const int MaxFileChars = 32_000;

    /// <inheritdoc/>
    public override string HandlerId => ToolConstants.Skill;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Read;

    /// <inheritdoc/>
    public override Task<JObject> Execute(ToolExecutionContext ctx)
    {
        string name = ctx.Args["name"]?.ToString()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name))
        {
            return Task.FromResult(Fail("name is required."));
        }
        SwarmUI.Accounts.User user = ctx.Session?.User;
        JObject thread = string.IsNullOrEmpty(ctx.ThreadId) || user is null ? null : ThreadStorageService.GetThread(user, ctx.ThreadId);
        PromptAssetSet set = PromptAssetService.For(user, thread);
        PromptAsset skill = set.ModelSkills.FirstOrDefault(s => s.Name == name);
        if (skill is null)
        {
            string available = string.Join(", ", set.ModelSkills.Select(s => s.Name));
            return Task.FromResult(Fail($"There is no skill named '{name}'." + (available.Length > 0 ? $" Available: {available}." : "")));
        }
        string file = ctx.Args["file"]?.ToString();
        return Task.FromResult(string.IsNullOrWhiteSpace(file) ? Body(skill) : ReadFile(skill, file.Trim()));
    }

    /// <summary>The skill's instructions, and the names of the other files in its folder.</summary>
    private static JObject Body(PromptAsset skill)
    {
        List<string> files = PromptAssetStore.ListSkillFiles(skill);
        JObject result = new() { ["success"] = true, ["name"] = skill.Name, ["description"] = skill.Description, ["instructions"] = skill.Body };
        if (files.Count > 0)
        {
            result["files"] = new JArray(files);
            result["note"] = "Call this tool again with `file` to read one of the files.";
        }
        return result;
    }

    /// <summary>One text file from inside the skill's folder.</summary>
    private static JObject ReadFile(PromptAsset skill, string file)
    {
        if (skill.Directory is null)
        {
            return Fail("This skill has no other files.");
        }
        if (!PathSandbox.TryResolve(skill.Directory, file, out string full, out string error))
        {
            return Fail(error ?? "That file is not in the skill's folder.");
        }
        if (!File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
        {
            return Fail($"The skill has no file '{file}'.");
        }
        FileInfo info = new(full);
        if (info.Length > 512 * 1024)
        {
            return Fail("That file is too large to read.");
        }
        byte[] bytes = File.ReadAllBytes(full);
        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0)
        {
            return Fail("That file is not text.");
        }
        string text = Encoding.UTF8.GetString(bytes);
        bool cut = text.Length > MaxFileChars;
        JObject result = new() { ["success"] = true, ["name"] = skill.Name, ["file"] = PathSandbox.Relative(skill.Directory, full), ["content"] = cut ? text[..MaxFileChars] : text };
        if (cut)
        {
            result["note"] = $"The file was cut to {MaxFileChars} characters.";
        }
        return result;
    }

    /// <summary>A failed result.</summary>
    private static JObject Fail(string error) => new() { ["success"] = false, ["error"] = error };
}
