using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.LLMAssistant.Services.ImageTools;

/// <summary>What a generation call made.</summary>
/// <param name="Images">The image urls that came back, in order, without the contact-sheet image core may add for a small batch and without discarded ones.</param>
/// <param name="Error">The first error core reported, or null. With several images some may have come back as well.</param>
/// <param name="Cancelled">Whether the call was stopped (by the user's Stop or the tool's time limit) before it finished.</param>
public sealed record BatchOutcome(List<string> Images, string Error, bool Cancelled);

/// <summary>Runs a generation through core's own route and reports what came back, so a batch is handled as a batch: what was saved is returned even when one image failed, the grid image core adds is not
/// counted as one of the images, and a Stop ends the generation.</summary>
public static class ImageBatch
{
    /// <summary>How long a stopped generation is given to wind down before the call stops waiting for it.</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(15);

    /// <summary>Turns core's outputs for a generation into a <see cref="BatchOutcome"/>. An output with an <c>image</c> and a <c>batch_index</c> is an image: a negative index is not one of the requested images (the
    /// contact sheet core adds when a small batch finishes is -1, and a non-image result is lower); <c>discard_indices</c> remove images that an extension rejected; an <c>error</c> is kept (the first).</summary>
    public static BatchOutcome Summarize(IEnumerable<JObject> outputs)
    {
        SortedDictionary<int, string> images = [];
        HashSet<int> discarded = [];
        string error = null;
        foreach (JObject output in outputs)
        {
            if (output.TryGetValue("error", out JToken err))
            {
                error ??= err.ToString();
            }
            if (output.TryGetValue("image", out JToken image) && output.TryGetValue("batch_index", out JToken index) && int.TryParse(index.ToString(), out int i) && i >= 0)
            {
                images[i] = image.ToString();
            }
            if (output.TryGetValue("discard_indices", out JToken discard))
            {
                foreach (int d in discard.Values<int>())
                {
                    discarded.Add(d);
                }
            }
        }
        return new BatchOutcome([.. images.Where(p => !discarded.Contains(p.Key)).Select(p => p.Value)], error, false);
    }

    /// <summary>Starts the generation and waits for it, watching <paramref name="ct"/>. When it is cancelled the claim core made for this call (the first one newer than the call's start, pinned as soon as it
    /// appears, so a Generate-tab run started later is left alone) is interrupted, and the call returns what had been saved after a short grace. <b>Two limits:</b> interrupting a claim only stops images that have
    /// not started yet; an image the backend is already making watches the session-wide interrupt instead, so it finishes and is saved (a one-image call is not shortened by Stop), and a run the user starts in the very
    /// instant after this one can be taken for it. <paramref name="images"/> is how many to make; <paramref name="rawInput"/> is the parameter map.</summary>
    public static async Task<BatchOutcome> RunAsync(Session session, int images, JObject rawInput, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return new BatchOutcome([], null, true);
        }
        long claimsBefore = Session.GenClaim.ClaimID;
        ConcurrentQueue<JObject> outputs = new();
        Task generation = Task.Run(() => T2IAPI.GenT2I_Internal(session, (images, rawInput, new T2IAPI.SharedGenT2IData(), 0), o => { if (o is not null) { outputs.Enqueue(o); } }, false));
        bool cancelled = false;
        string failure = null;
        Session.GenClaim own = null;
        while (!generation.IsCompleted)
        {
            await Task.WhenAny(generation, Task.Delay(own is null ? 20 : 200));
            // The claim is made as the generation starts; pinning the first one seen keeps a run started later from being taken for this one.
            own ??= FirstClaimAfter(session, claimsBefore);
            if (ct.IsCancellationRequested && !generation.IsCompleted)
            {
                cancelled = true;
                Interrupt(own);
                if (await Task.WhenAny(generation, Task.Delay(StopGrace)) != generation)
                {
                    break;
                }
            }
        }
        if (generation.IsCompleted)
        {
            try
            {
                await generation;
            }
            catch (Exception ex)
            {
                Logs.Error($"[LLMAssistant] Image generation failed: {ex.Message}");
                failure = ex.Message;
            }
        }
        else
        {
            _ = generation.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        BatchOutcome outcome = Summarize(outputs.ToArray());
        return outcome with { Error = outcome.Error ?? failure, Cancelled = cancelled };
    }

    /// <summary>The oldest claim of the session newer than <paramref name="claimId"/>, or null when there is none yet.</summary>
    private static Session.GenClaim FirstClaimAfter(Session session, long claimId) => session.Claims.Values.Where(c => c.ID > claimId).OrderBy(c => c.ID).FirstOrDefault();

    /// <summary>Interrupts one claim (a no-op for null: the generation had not made its claim yet, and the next pass pins it and tries again).</summary>
    private static void Interrupt(Session.GenClaim claim)
    {
        try
        {
            claim?.LocalClaimInterrupt.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The generation finished while it was being stopped.
        }
    }
}
