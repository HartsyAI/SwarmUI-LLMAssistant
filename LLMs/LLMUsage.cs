using HartsyInference.Engine.Requests;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.LLMs;

/// <summary>What a turn cost in tokens, summed over its model rounds. A turn that calls tools makes one request per round, and each
/// request reports its own usage; billing adds them up, but the size of the conversation the model last saw is the last round's.</summary>
public sealed class LLMUsage
{
    /// <summary>Prompt tokens, summed over every round.</summary>
    public long PromptTokens;

    /// <summary>Tokens the model wrote, summed over every round.</summary>
    public long CompletionTokens;

    /// <summary>Prompt tokens the endpoint served from its cache, summed over every round (0 when it does not say).</summary>
    public long CachedTokens;

    /// <summary>The last round's prompt size: the conversation, as the model last saw it.</summary>
    public long LastPromptTokens;

    /// <summary>The last round's output size.</summary>
    public long LastCompletionTokens;

    /// <summary>How many rounds reported usage.</summary>
    public int Rounds;

    /// <summary>A request of the turn did not report in full (the turn was stopped or failed during it, or the endpoint skipped it), so the turn cost more than the counts say.</summary>
    public bool Incomplete;

    /// <summary>Whether any round reported usage. A provider that does not report it leaves this false, and callers estimate instead.</summary>
    public bool Any => Rounds > 0;

    /// <summary>Tokens the conversation holds after this turn: the last prompt plus the reply the model wrote on top of it.</summary>
    public long ContextTokens => LastPromptTokens + LastCompletionTokens;

    /// <summary>Adds one round's report.</summary>
    public void Add(long promptTokens, long completionTokens, long cachedTokens = 0)
    {
        PromptTokens += Math.Max(0, promptTokens);
        CompletionTokens += Math.Max(0, completionTokens);
        CachedTokens += Math.Max(0, cachedTokens);
        LastPromptTokens = Math.Max(0, promptTokens);
        LastCompletionTokens = Math.Max(0, completionTokens);
        Rounds++;
    }

    /// <summary>Adds one engine usage report.</summary>
    public void Add(TextUsage usage) => Add(usage.PromptTokens, usage.CompletionTokens, usage.CachedPromptTokens);

    /// <summary>The JSON a message saves in <c>meta.usage</c> and a <c>done</c> frame carries; <c>incomplete</c> is present only when true.</summary>
    public JObject ToJson()
    {
        JObject json = new()
        {
            ["promptTokens"] = PromptTokens,
            ["completionTokens"] = CompletionTokens,
            ["cachedTokens"] = CachedTokens,
            ["contextTokens"] = ContextTokens,
            ["rounds"] = Rounds,
        };
        if (Incomplete)
        {
            json["incomplete"] = true;
        }
        return json;
    }

    /// <summary>Reads what <see cref="ToJson"/> wrote, or null when <paramref name="json"/> is not a usage object.</summary>
    public static LLMUsage FromJson(JToken json)
    {
        if (json is not JObject obj)
        {
            return null;
        }
        long context = obj.Value<long?>("contextTokens") ?? 0;
        return new LLMUsage
        {
            PromptTokens = obj.Value<long?>("promptTokens") ?? 0,
            CompletionTokens = obj.Value<long?>("completionTokens") ?? 0,
            CachedTokens = obj.Value<long?>("cachedTokens") ?? 0,
            LastPromptTokens = context,
            Rounds = obj.Value<int?>("rounds") ?? 1,
            Incomplete = obj.Value<bool?>("incomplete") ?? false,
        };
    }

    /// <summary>The usage a provider's <c>usage</c> frame carries (<c>{promptTokens, completionTokens, cachedTokens}</c>) as an engine chunk,
    /// or null when the frame holds no counts.</summary>
    public static TextUsage? FrameToEngine(JToken frame)
    {
        if (frame is not JObject obj)
        {
            return null;
        }
        int prompt = obj.Value<int?>("promptTokens") ?? 0;
        int completion = obj.Value<int?>("completionTokens") ?? 0;
        if (prompt <= 0 && completion <= 0)
        {
            return null;
        }
        return new TextUsage { PromptTokens = prompt, CompletionTokens = completion, CachedPromptTokens = obj.Value<int?>("cachedTokens") ?? 0 };
    }

    /// <summary>The <c>usage</c> frame for a chunk's engine usage; the inverse of <see cref="FrameToEngine"/>.</summary>
    public static JObject EngineToFrame(TextUsage usage) => new()
    {
        ["promptTokens"] = usage.PromptTokens,
        ["completionTokens"] = usage.CompletionTokens,
        ["cachedTokens"] = usage.CachedPromptTokens,
    };
}
