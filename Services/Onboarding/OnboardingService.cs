using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services.Knowledge;
using Hartsy.Extensions.LLMAssistant.Services.Readiness;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.Onboarding;

/// <summary>The first-run guide for the calling user: their state, the checklist, where backends are set up, and the starter set.</summary>
public static class OnboardingService
{
    /// <summary>The path the guide's link opens: core's Server tab, Backends page.</summary>
    public const string BackendsHash = "#,server_tab,Settings-Backends";

    /// <summary>The report: <c>{success, status, show}</c> always; with <paramref name="checks"/> also <c>{canChat, checks[], setup{}, starter{}}</c>. With <paramref name="refresh"/> the backends are
    /// asked for their models again instead of using the recent list, for use after the user changed a backend.</summary>
    public static async Task<JObject> ReportAsync(Session session, bool checks, bool refresh, CancellationToken ct)
    {
        User user = session.User;
        bool hasChats = HasChats(user);
        string status = OnboardingState.Observe(new UserOnboardingStorage(user), hasChats);
        JObject report = new()
        {
            ["success"] = true,
            ["status"] = status,
            ["show"] = OnboardingState.ShouldShow(status, hasChats),
        };
        if (!checks)
        {
            return report;
        }
        ReadinessInputs setup = await ReadinessService.GatherAsync(session, false, ct);
        if (refresh)
        {
            (int providers, List<LLMModelInfo> models) = await LLMModelLookup.ListAllAsync(session, true);
            setup.Providers = providers;
            setup.Models = models.Count;
            setup.PreferredModelFound = string.IsNullOrWhiteSpace(setup.PreferredModel) || models.Any(m => string.Equals(m.Id, setup.PreferredModel, StringComparison.OrdinalIgnoreCase));
        }
        bool canView = user.HasPermission(Permissions.ViewServerTab) && user.HasPermission(Permissions.ViewBackendsList);
        bool canAdd = canView && user.HasPermission(Permissions.AddRemoveBackends);
        OnboardingInputs inputs = new(setup, KnowledgeService.Providers().Count, canAdd);
        report["canChat"] = OnboardingChecks.CanChat(inputs);
        report["checks"] = new JArray(OnboardingChecks.Evaluate(inputs).Select(c =>
        {
            JObject json = new() { ["id"] = c.Id, ["title"] = c.Title, ["status"] = ReadinessService.Name(c.Status), ["detail"] = c.Detail };
            if (c.Fix is not null)
            {
                json["fix"] = c.Fix;
            }
            return json;
        }));
        report["setup"] = new JObject { ["page"] = "Server > Backends", ["hash"] = BackendsHash, ["canView"] = canView, ["canAdd"] = canAdd };
        report["starter"] = StarterInstaller.Describe(new UserStarterTarget(user));
        return report;
    }

    /// <summary>Skips, finishes, runs again or resets the guide: <c>{success, status, show}</c>.</summary>
    public static JObject Apply(User user, string action)
    {
        if (!OnboardingState.Apply(new UserOnboardingStorage(user), action, out string status, out string error))
        {
            return new JObject { ["success"] = false, ["error"] = error };
        }
        return new JObject { ["success"] = true, ["status"] = status, ["show"] = OnboardingState.ShouldShow(status, HasChats(user)) };
    }

    /// <summary>Records that the user sent a message in a chat, which ends their first run. Never throws: it runs on the way to a reply, which must not fail over the guide's bookkeeping.</summary>
    public static void NoteMessage(User user)
    {
        try
        {
            OnboardingState.OnMessage(new UserOnboardingStorage(user));
        }
        catch (Exception ex)
        {
            Logs.Warning($"[LLMAssistant] The first-run state could not be saved: {ex.Message}");
        }
    }

    /// <summary>Installs what is missing of the starter set: <c>{success, added[], skipped[], starter{}}</c>. A new user with no chats who gets something is left <c>pending</c>, so the example
    /// chats do not end their first run.</summary>
    public static JObject InstallStarterSet(User user)
    {
        UserStarterTarget target = new(user);
        JArray added = [], skipped = [];
        OnboardingState.DuringInstall(new UserOnboardingStorage(user), HasChats(user), () =>
        {
            (added, skipped) = StarterInstaller.Install(target);
            return added.Count > 0;
        });
        return new JObject { ["success"] = true, ["added"] = added, ["skipped"] = skipped, ["starter"] = StarterInstaller.Describe(target) };
    }

    /// <summary>Whether the user has any chat.</summary>
    private static bool HasChats(User user) => ThreadStorageService.GetThreadIndex(user).Count > 0;
}
