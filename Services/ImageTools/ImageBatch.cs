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

    /// <summary>Starts the generation and waits for it, watching <paramref name="ct"/>: when it is cancelled, the generation this call started is interrupted (core's own claim for it, not the whole session's
    /// generations: a Generate-tab run in the same session is left alone, with the one exception that a run the user starts in the same moments after this one is indistinguishable and is stopped with it), and the call
    /// returns what had been saved. <paramref name="images"/> is how many to make; <paramref name="rawInput"/> is the parameter map.</summary>
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
        while (!generation.IsCompleted)
        {
            await Task.WhenAny(generation, Task.Delay(200));
            if (ct.IsCancellationRequested && !generation.IsCompleted)
            {
                cancelled = true;
                InterruptClaimsAfter(session, claimsBefore);
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

    /// <summary>Interrupts the generations claimed after <paramref name="claimId"/> (ones this call started).</summary>
    private static void InterruptClaimsAfter(Session session, long claimId)
    {
        foreach (Session.GenClaim claim in session.Claims.Values.Where(c => c.ID > claimId).ToList())
        {
            try
            {
                claim.LocalClaimInterrupt.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The generation finished while it was being stopped.
            }
        }
    }
}
