using Hartsy.Extensions.LLMAssistant.Services.Readiness;

namespace Hartsy.Extensions.LLMAssistant.Services.Onboarding;

/// <summary>What the first-run checks look at: the setup check's gathered numbers, and the few things only the guide asks about.</summary>
/// <param name="Setup">What <see cref="ReadinessService.GatherAsync"/> gathered.</param>
/// <param name="EmbeddingBackends">How many backends can be asked for embeddings.</param>
/// <param name="CanAddBackends">Whether the user may add a backend themselves.</param>
public sealed record OnboardingInputs(ReadinessInputs Setup, int EmbeddingBackends, bool CanAddBackends);

/// <summary>The first-run checklist: is there a backend, a model to chat with, tools, and embeddings. Only the first two are needed to chat; the others are said as information. It reads the same
/// numbers as the setup check, so the two cannot disagree, and says nothing about which engine or provider to use. Pure, so each case is tested with made-up inputs.</summary>
public static class OnboardingChecks
{
    /// <summary>The checks, in the order shown.</summary>
    public static List<ReadinessCheck> Evaluate(OnboardingInputs i) => [Backend(i), Model(i), Tools(i), Embeddings(i)];

    /// <summary>Whether a message can be sent: nothing needed for that failed or could not be looked at.</summary>
    public static bool CanChat(OnboardingInputs i) => !BackendsUnknown(i) && i.Setup.Providers > 0 && i.Setup.Models > 0;

    /// <summary>Whether gathering the backends failed.</summary>
    private static bool BackendsUnknown(OnboardingInputs i) => i.Setup.Unchecked.Any(c => c.Id == "backends");

    /// <summary>The line for a part the setup check could not look at, or null.</summary>
    private static ReadinessCheck Missed(OnboardingInputs i, string part, string id, string title)
        => i.Setup.Unchecked.Any(c => c.Id == part) ? new(id, title, CheckStatus.Warn, "Could not be checked.", "See the server log.") : null;

    /// <summary>Is a backend running.</summary>
    private static ReadinessCheck Backend(OnboardingInputs i)
    {
        const string title = "LLM backend";
        if (Missed(i, "backends", "backend", title) is { } missed)
        {
            return missed;
        }
        if (i.Setup.Providers == 0)
        {
            return new("backend", title, CheckStatus.Fail, "Swarm has no LLM backend running.", i.CanAddBackends
                ? "Add one under Server > Backends (or check the status of one already there), then press Check again."
                : "Ask an administrator to add an LLM backend under Server > Backends.");
        }
        return new("backend", title, CheckStatus.Ok, $"{i.Setup.Providers} running.");
    }

    /// <summary>Is there a model to choose.</summary>
    private static ReadinessCheck Model(OnboardingInputs i)
    {
        const string title = "Chat model";
        if (Missed(i, "backends", "model", title) is { } missed)
        {
            return missed;
        }
        if (i.Setup.Providers == 0)
        {
            return new("model", title, CheckStatus.Info, "Models are listed once a backend is running.");
        }
        if (i.Setup.Models == 0)
        {
            return new("model", title, CheckStatus.Fail, "The backend offers no model.", "Open Server > Backends and check that the backend is running and has a model.");
        }
        if (!i.Setup.PreferredModelFound && !string.IsNullOrWhiteSpace(i.Setup.PreferredModel))
        {
            return new("model", title, CheckStatus.Warn, $"{i.Setup.Models} to choose from, but your preferred model '{i.Setup.PreferredModel}' is not one of them.", "Pick another model in the chat's model list.");
        }
        return new("model", title, CheckStatus.Ok, $"{i.Setup.Models} to choose from, in the model list above the chat.");
    }

    /// <summary>Are there tools this user can use.</summary>
    private static ReadinessCheck Tools(OnboardingInputs i)
    {
        const string title = "Tools";
        if (Missed(i, "tools", "tools", title) is { } missed)
        {
            return missed;
        }
        int usable = Math.Max(0, i.Setup.ToolsEnabled - i.Setup.ToolsDenied - i.Setup.ToolsWithoutHandler.Count);
        return usable == 0
            ? new("tools", title, CheckStatus.Info, "None is switched on for you. Chat works without them.")
            : new("tools", title, CheckStatus.Ok, $"{usable} available. Whether a model calls them depends on the model and its backend.");
    }

    /// <summary>Can anything be searched by meaning.</summary>
    private static ReadinessCheck Embeddings(OnboardingInputs i)
    {
        const string title = "Embeddings (optional)";
        if (Missed(i, "knowledge", "embeddings", title) is { } missed)
        {
            return missed;
        }
        if (i.Setup.EmbeddingSet)
        {
            return new("embeddings", title, CheckStatus.Ok, "An embedding model is set (it was not tried here).");
        }
        return new("embeddings", title, CheckStatus.Info, i.EmbeddingBackends > 0
            ? "No embedding model is set. Searches match words; to also search by meaning, pick one under Settings > Knowledge."
            : "No backend offers embeddings. Searches match words, and everything else works.");
    }
}
