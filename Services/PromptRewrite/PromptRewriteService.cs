using System.Security.Cryptography;
using System.Text;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services.Memory;
using Hartsy.Extensions.LLMAssistant.Services.PromptStyles;
using SwarmUI.Accounts;
using SwarmUI.Core;

namespace Hartsy.Extensions.LLMAssistant.Services.PromptRewrite;

/// <summary>Rewrites an image or video prompt for the model it is for: picks the style for that model (the user's rule, then the most specific match, then the default), asks the chat model through the same generic dispatcher
/// chat uses, and returns what to generate from. Nothing here is engine specific: it works with whatever LLM backend is set up, and when there is none the prompt is left as it was.
/// <para>The same prompt, style, size and writing model give the same answer from a cache with one call in flight at a time, so a batch of images makes one request, not one per image. A failed rewrite is never cached.</para></summary>
public static class PromptRewriteService
{
    /// <summary>The instruction id the cache keys on, so rewrites never share an entry with a prompt tag's.</summary>
    private const string CacheInstruction = "prompt-rewrite";

    /// <summary>Kept answers.</summary>
    private static readonly PromptCacheService Cache = new(maxEntries: 500);

    /// <summary>Thrown out of the cache's factory so a failed rewrite is not kept; carries the result the caller still returns.</summary>
    private sealed class NotCachedException(RewriteResult result) : Exception("The rewrite fell back and is not cached.")
    {
        /// <summary>The fallback result.</summary>
        public RewriteResult Result { get; } = result;
    }

    /// <summary>Rewrites <paramref name="request"/>. <paramref name="session"/> is the caller's Swarm session (it can be null for the server's own use); <paramref name="ct"/> cancels the wait, not other callers' shared call.
    /// <paramref name="chatModel"/> is the model of the chat that asked for the image, if one did (see <see cref="WritingModel"/>).</summary>
    public static async Task<RewriteResult> RewriteAsync(RewriteRequest request, User user, SwarmUI.Accounts.Session session, CancellationToken ct = default, string chatModel = null)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return RewriteResult.Unchanged(request, "There is no prompt to rewrite.");
        }
        PromptStyleSet set = PromptStyleService.For(user);
        ImageModelFacts facts = CoreModelFacts.For(request.ImageModel, user) ?? new ImageModelFacts(request.ImageModel ?? "", null, null, null, false);
        StyleResolution resolution;
        if (!string.IsNullOrWhiteSpace(request.StyleId))
        {
            PromptStyle chosen = PromptStyleService.Find(user, request.StyleId.Trim(), "");
            if (chosen is null || chosen.Kind == PromptStyle.KindLlm)
            {
                return RewriteResult.Unchanged(request, $"There is no image or video style called \"{request.StyleId}\".");
            }
            resolution = new StyleResolution(chosen, false, "request", "The style was chosen for this request.", []);
        }
        else
        {
            resolution = PromptStyleResolver.ResolveImage(set.Styles, PromptStyleService.GetOverrides(user), facts);
        }
        if (resolution.Style is null)
        {
            return RewriteResult.Unchanged(request, resolution.Off ? "Styling is turned off for this model." : "No style applies to this model.") with { Via = resolution.Via, Reason = resolution.Reason };
        }
        PromptStyle style = resolution.Style;
        RewriteSettings settings = RewriteSettings.ForUser(user);
        // The default is named here, not only by the dispatcher, so the cache and the image's metadata say which model wrote.
        string model = await LLMModelLookup.ResolveEffectiveModelIdAsync(WritingModel(request.RewriteModel, settings.Model, chatModel), session);
        // That model's chat-model style and the user's style notes go into its instructions (see PromptRewriter.BuildSystem).
        LlmAddendum addendum = await LlmAddendumService.DescribeUnvariedAsync(user, session, AddendumSettings.ForUser(user), model, ["rewrite"]);
        (string notes, List<string> noteIds) = MemoryService.StyleNotes(user, request.Prompt);
        request = request with { RewriteModel = model, Addendum = addendum.Text, StyleNotes = notes };
        RewriteGenerate generate = DispatcherGenerate(request, settings, session);
        RewriteResult result = await RewriteWithStyleAsync(user?.UserID, style, request, settings, generate, ct);
        // Only a rewrite that asked the writing model (now, or for the cached answer) counts for the notes: a prompt left as it was never sent them.
        if (noteIds.Count > 0 && result.Usage is not null)
        {
            MemoryService.StoreFor(user).MarkRewritten(noteIds);
        }
        return result with { Via = resolution.Via, Reason = resolution.Reason, StyleScope = style.Scope, StyleId = style.Id };
    }

    /// <summary>The model asked to write: the one the request names, else the user's rewrite model, else the model of the chat that asked for the image. Empty when none of them says, and then the default chat model writes.</summary>
    internal static string WritingModel(string requested, string saved, string chat)
        => new[] { requested, saved, chat }.FirstOrDefault(model => !string.IsNullOrWhiteSpace(model))?.Trim() ?? "";

    /// <summary>Rewrites with a style already chosen, through the cache: a repeat of the same prompt, style, size, negative and writing model is answered from it, and identical requests made at once share one call. A result that fell
    /// back is returned but not kept. <paramref name="ct"/> cancels this caller's wait, never the call other callers share. A <see cref="RewriteRequest.Fresh"/> request skips the cache.</summary>
    internal static async Task<RewriteResult> RewriteWithStyleAsync(string userId, PromptStyle style, RewriteRequest request, RewriteSettings settings, RewriteGenerate generate, CancellationToken ct)
    {
        if (request.Fresh)
        {
            return await PromptRewriter.RunAsync(style, request, settings, generate, ct);
        }
        string failureKey = string.Join('\u001F', userId ?? "<anon>", request.RewriteModel ?? "", StyleKey(style, request, settings), request.Prompt?.Trim() ?? "");
        if (RecentFailure(failureKey) is RewriteResult failed)
        {
            return failed;
        }
        try
        {
            string json = await Cache.GetOrCreate(userId, request.RewriteModel, StyleKey(style, request, settings), request.Prompt, CacheInstruction, async () =>
            {
                RewriteResult fresh = await PromptRewriter.RunAsync(style, request, settings, generate, CancellationToken.None);
                return fresh.FellBack || fresh.Skipped is not null ? throw new NotCachedException(fresh) : fresh.ToCacheJson();
            }, timeoutMs: SharedWaitMs(settings)).WaitAsync(ct);
            return RewriteResult.FromCacheJson(json) ?? await PromptRewriter.RunAsync(style, request, settings, generate, ct);
        }
        catch (NotCachedException ex)
        {
            if (ex.Result.FellBack)
            {
                RememberFailure(failureKey, ex.Result);
            }
            return ex.Result;
        }
    }

    /// <summary>How long a failed rewrite is remembered, so a batch (or a second image, or a grid) does not wait out a hung model once for every wave of images.</summary>
    internal static readonly TimeSpan FailureLifetime = TimeSpan.FromSeconds(60);

    /// <summary>The most failures remembered.</summary>
    private const int MaxFailures = 200;

    /// <summary>Recent failed rewrites by request, with when each happened.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, RewriteResult Result)> Failures = new();

    /// <summary>The failed result for <paramref name="key"/> if one happened within <see cref="FailureLifetime"/>, else null.</summary>
    private static RewriteResult RecentFailure(string key)
    {
        if (Failures.TryGetValue(key, out (DateTime At, RewriteResult Result) entry))
        {
            if (DateTime.UtcNow - entry.At <= FailureLifetime)
            {
                return entry.Result;
            }
            Failures.TryRemove(key, out _);
        }
        return null;
    }

    /// <summary>Remembers a failed result, dropping expired ones (and, if still too many, all of them).</summary>
    private static void RememberFailure(string key, RewriteResult result)
    {
        if (Failures.Count >= MaxFailures)
        {
            DateTime now = DateTime.UtcNow;
            foreach (KeyValuePair<string, (DateTime At, RewriteResult Result)> old in Failures.Where(f => now - f.Value.At > FailureLifetime).ToList())
            {
                Failures.TryRemove(old.Key, out _);
            }
            if (Failures.Count >= MaxFailures)
            {
                Failures.Clear();
            }
        }
        Failures[key] = (DateTime.UtcNow, result);
    }

    /// <summary>How long a caller may wait on a call another caller started: both attempts and the negative request, each up to the timeout, plus a margin, so a waiter never gives up before the call can.</summary>
    private static int SharedWaitMs(RewriteSettings settings) => (settings.TimeoutSeconds * 3 + 15) * 1000;

    /// <summary>Forgets every kept rewrite (for tests).</summary>
    internal static void ClearCache()
    {
        Cache.Clear();
        Failures.Clear();
    }

    /// <summary>The call to the chat model through the dispatcher the chat uses, with the settings' limits.</summary>
    internal static RewriteGenerate DispatcherGenerate(RewriteRequest request, RewriteSettings settings, SwarmUI.Accounts.Session session)
    {
        return async (system, userMessage, token) =>
        {
            ExtendedLLMInput input = ExtendedLLMInput.Create(userMessage, system, string.IsNullOrWhiteSpace(request.RewriteModel) ? null : request.RewriteModel);
            input.Stream = false;
            input.Temperature = settings.Temperature;
            input.MaxTokens = settings.MaxTokens;
            input.EnableThinking = false;
            input.RequestSession = session;
            return await LLMDispatcher.GenerateDetailed(input, token);
        };
    }

    /// <summary>The part of the cache key that is not the prompt: the style (and a hash of everything that shapes the instructions and the answer, so editing a style, a chat-model style or a style note makes new rewrites), the size, the limits and the negative.</summary>
    private static string StyleKey(PromptStyle style, RewriteRequest request, RewriteSettings settings)
    {
        string content = $"{style.Format}|{style.Formatter}|{style.Negative}|{string.Join(',', style.Supports)}|{style.Body}|{request.Addendum}|{request.StyleNotes}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..12];
        return $"{style.Id}|{style.Scope}|{hash}|{request.Width}x{request.Height}|{settings.MaxTokens}|{settings.Temperature:R}|{request.Negative?.Trim()}";
    }
}
