using System.IO;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services.Hooks;
using Hartsy.Extensions.LLMAssistant.Services.Knowledge;
using Hartsy.Extensions.LLMAssistant.Services.Mcp;
using Hartsy.Extensions.LLMAssistant.Services.PromptAssets;
using Hartsy.Extensions.LLMAssistant.Services.Schedules;
using Hartsy.Extensions.LLMAssistant.Services.Sessions;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.Readiness;

/// <summary>Looks at the whole setup and says whether it is ready: backends and models, tools, MCP servers, hooks, skills, the knowledge library, schedules, sessions and storage. It only
/// reads. It starts no connection (an MCP server that is not connected is reported as it is, not connected for the check), loads no model, opens no session and changes no setting; the
/// only things it sends over the network are the model list and the embedding model's test, and only when asked for a <b>deep</b> check. What an administrator alone may see (an MCP
/// server's error text, which can hold a command line) is gathered only for one, so it cannot reach another user. Every part is gathered on its own, so one that cannot be looked at
/// is reported as such and does not stop the rest.</summary>
public static class ReadinessService
{
    /// <summary>The report for the caller: each check, the worst status, and whether it was deep.</summary>
    public static async Task<JObject> ReportAsync(Session session, bool deep, CancellationToken ct)
    {
        ReadinessInputs inputs = await GatherAsync(session, deep, ct);
        List<ReadinessCheck> checks = ReadinessChecks.Evaluate(inputs);
        return new JObject
        {
            ["success"] = true,
            ["overall"] = Name(ReadinessChecks.Overall(checks)),
            ["deep"] = deep,
            ["checkedAt"] = DateTime.UtcNow.ToString("o"),
            ["checks"] = new JArray(checks.Select(c =>
            {
                JObject json = new() { ["id"] = c.Id, ["title"] = c.Title, ["status"] = Name(c.Status), ["detail"] = c.Detail };
                if (c.Fix is not null)
                {
                    json["fix"] = c.Fix;
                }
                return json;
            })),
        };
    }

    /// <summary>The status as it is written in the report.</summary>
    public static string Name(CheckStatus status) => status.ToString().ToLowerInvariant();

    /// <summary>Gathers what the checks look at.</summary>
    public static async Task<ReadinessInputs> GatherAsync(Session session, bool deep, CancellationToken ct)
    {
        User user = session.User;
        ReadinessInputs i = new() { Deep = deep };
        JObject settings = SettingsService.GetMergedSettings(user);

        await Safe(i, "backends", "LLM backends", async () =>
        {
            (int providers, List<LLMModelInfo> models) = await LLMModelLookup.ListAllAsync(session, deep);
            i.Providers = providers;
            i.Models = models.Count;
            i.PreferredModel = settings["preferredModel"]?.ToString() ?? "";
            i.PreferredModelFound = string.IsNullOrWhiteSpace(i.PreferredModel) || models.Any(m => string.Equals(m.Id, i.PreferredModel, StringComparison.OrdinalIgnoreCase));
        });
        await Safe(i, "tools", "Tools", () =>
        {
            foreach (JObject tool in (ToolRegistryService.GetToolList(settings, user) ?? []).OfType<JObject>())
            {
                if (tool["enabled"]?.Value<bool>() == false)
                {
                    continue;
                }
                i.ToolsEnabled++;
                string handlerId = tool["handlerId"]?.ToString();
                if (ToolRegistryService.GetHandler(handlerId) is null)
                {
                    i.ToolsWithoutHandler.Add(tool["id"]?.ToString() ?? "?");
                }
                else if (ToolExecutorService.RequiredPermission(handlerId) is { } permission && !user.HasPermission(permission))
                {
                    i.ToolsDenied++;
                }
            }
            return Task.CompletedTask;
        });
        await Safe(i, "mcp", "MCP servers", () =>
        {
            bool mayManage = user.HasPermission(LLMAssistantAPI.PermMcpManage);
            foreach (McpConnection connection in McpServerManager.Instance.Snapshot())
            {
                i.McpTotal++;
                switch (connection.State)
                {
                    case McpState.Ready:
                        i.McpReady++;
                        break;
                    case McpState.Failed:
                        i.McpFailed++;
                        // The text of a failure can hold the command line or address an administrator set up.
                        if (mayManage)
                        {
                            i.McpErrors.Add((connection.Config?.Name ?? connection.Config?.Id ?? "?", connection.Error));
                        }
                        break;
                    case McpState.Connecting:
                        i.McpConnecting++;
                        break;
                    default:
                        i.McpDisabled++;
                        break;
                }
            }
            return Task.CompletedTask;
        });
        await Safe(i, "hooks", "Hooks", () =>
        {
            (i.HooksTotal, i.HooksInvalid) = HookService.Health(user);
            return Task.CompletedTask;
        });
        await Safe(i, "skills", "Skills and commands", () =>
        {
            i.PromptAssets = PromptAssetService.For(user, null).Assets.Count;
            return Task.CompletedTask;
        });
        await Safe(i, "knowledge", "Knowledge library", async () =>
        {
            i.KnowledgeDocuments = KnowledgeService.List(user).Count;
            KnowledgeSettings knowledge = KnowledgeService.GetSettings(user);
            i.EmbeddingSet = knowledge.Enabled;
            if (deep && knowledge.Enabled)
            {
                i.EmbeddingProbed = true;
                i.EmbeddingError = (await KnowledgeService.ProbeAsync(session, knowledge, ct)).Error;
            }
        });
        await Safe(i, "schedules", "Schedules", () =>
        {
            i.SchedulerRunning = ScheduleService.IsRunning;
            DateTime dayAgo = DateTime.UtcNow.AddDays(-1);
            foreach (JObject schedule in ScheduleStore.List(user).OfType<JObject>().Where(s => s[ScheduleFields.Enabled]?.Value<bool>() == true))
            {
                i.SchedulesEnabled++;
                if (ScheduleTrigger.ReadDate(schedule[ScheduleFields.LastSkippedAt]) is DateTime skipped && skipped > dayAgo)
                {
                    i.SchedulesSkipping++;
                }
            }
            return Task.CompletedTask;
        });
        await Safe(i, "sessions", "Sessions", () =>
        {
            (i.SessionsWorking, i.SessionsQueued) = BackgroundTurnService.CountFor(user.UserID);
            return Task.CompletedTask;
        });
        await Safe(i, "storage", "Storage", () =>
        {
            (i.StorageWritable, i.StorageError) = ProbeStorage();
            return Task.CompletedTask;
        });
        return i;
    }

    /// <summary>Runs one gathering step; a failure becomes a line of its own saying that this part could not be looked at, instead of an error for the whole report.</summary>
    private static async Task Safe(ReadinessInputs inputs, string id, string title, Func<Task> gather)
    {
        try
        {
            await gather();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logs.Warning($"[LLMAssistant] The readiness check for {id} could not look: {ex.Message}");
            inputs.Unchecked.Add(new ReadinessCheck(id, title, CheckStatus.Warn, "Could not be checked.", "See the server log."));
        }
    }

    /// <summary>Whether a file can be made and removed in the extension's data folder.</summary>
    private static (bool Writable, string Error) ProbeStorage()
    {
        try
        {
            string folder = Path.Combine(Program.DataDir, "LLMAssistant");
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, $".readiness-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return (true, null);
        }
        catch (Exception ex)
        {
            // The text of the exception holds the server's folder path, which is not for every user to read; the log has it.
            Logs.Warning($"[LLMAssistant] The extension's data folder could not be written: {ex.Message}");
            return (false, "a file could not be created there (the server log says why).");
        }
    }
}
