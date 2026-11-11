using System.IO;
using FreneticUtilities.FreneticDataSyntax;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Backends;

/// <summary>Turns each saved backend of the removed <c>llmassistant-anthropic</c> type into a Remote (OpenAI-Compatible) backend
/// pointed at Anthropic's API. Core skips a saved backend whose type is unknown, so this rewrites Data/Backends.fds before core
/// loads it, keeping a copy of the file as it was.</summary>
public static class AnthropicBackendMigration
{
    /// <summary>The type id the removed Anthropic backend saved under.</summary>
    public const string OldTypeId = "llmassistant-anthropic";

    /// <summary>The type id of the backend that replaces it.</summary>
    public const string NewTypeId = "llmassistant-openai";

    /// <summary>Converts any old Anthropic backends in the backends file. Runs during extension init, before core loads backends.</summary>
    public static void Run()
    {
        string path = Program.Backends?.SaveFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }
        try
        {
            FDSSection file = FDSUtility.ReadFile(path);
            if (file is null)
            {
                return;
            }
            int converted = ConvertAll(file);
            if (converted == 0)
            {
                return;
            }
            File.Copy(path, $"{path}.before-anthropic-migration.bak", true);
            FDSUtility.SaveToFile(file, path);
            Logs.Init($"[LLMAssistant] Moved {converted} Anthropic Claude backend(s) to the Remote (OpenAI-Compatible) backend at Anthropic's API. The old file is kept as {path}.before-anthropic-migration.bak.");
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] Could not move the old Anthropic Claude backends in {path}; they will not load until re-added as Remote (OpenAI-Compatible) backends: {ex.ReadableString()}");
        }
    }

    /// <summary>Converts every old Anthropic backend section in <paramref name="file"/> in place; returns how many it converted.</summary>
    internal static int ConvertAll(FDSSection file)
    {
        int converted = 0;
        foreach (string id in file.GetRootKeys())
        {
            FDSSection section = file.GetSection(id);
            if (section is null || section.GetString("type") != OldTypeId)
            {
                continue;
            }
            section.Set("type", NewTypeId);
            section.Set("settings", ConvertSettings(section.GetSection("settings")).Save(true));
            converted++;
        }
        return converted;
    }

    /// <summary>The Remote (OpenAI-Compatible) settings for one old Anthropic backend: its Base URL as the Address, its Default Model,
    /// and its Workspace ID as an <c>anthropic-workspace-id</c> header. Its reply timeout has no counterpart and is dropped.</summary>
    internal static RemoteOpenAILLMProvider.RemoteOpenAILLMProviderSettings ConvertSettings(FDSSection old)
    {
        string baseUrl = old?.GetString("BaseUrl", "")?.Trim();
        string model = old?.GetString("DefaultModel", "")?.Trim();
        string workspace = old?.GetString("WorkspaceId", "")?.Trim();
        return new RemoteOpenAILLMProvider.RemoteOpenAILLMProviderSettings
        {
            Address = string.IsNullOrEmpty(baseUrl) ? "https://api.anthropic.com" : baseUrl,
            DefaultModel = string.IsNullOrEmpty(model) ? "claude-opus-4-8" : model,
            OtherHeaders = string.IsNullOrEmpty(workspace) ? "" : $"{RemoteOpenAILLMProvider.AnthropicWorkspaceHeader}: {workspace}"
        };
    }
}
