using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.LLMs;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services;

namespace Hartsy.Extensions.LLMAssistant.Backends;

/// <summary>Shared base for the extension's removable LLM backends.
/// <para>Each concrete backend is both a Swarm <see cref="AbstractLLMBackend"/> (so it shows under
/// Server &gt; Backends with config UI + lifecycle) and an <see cref="ILLMProvider"/> (the rich path
/// the extension actually uses). This base self-registers the instance into
/// <see cref="LLMProviderRegistry"/> on <see cref="Init"/> and removes it on <see cref="Shutdown"/>,
/// bridges the thin core <c>Generate</c>/<c>GenerateLive</c> overloads to the rich ones, and
/// provides a default streaming-accumulator <see cref="Generate(ExtendedLLMInput)"/>.</para>
/// <para>This whole <c>Backends/</c> folder is the removable pack — delete it (and the
/// <c>LLMBackendPack.Register()</c> call) to fall back to a native Swarm LLM API.</para></summary>
public abstract class LLMProviderBackend : AbstractLLMBackend, ILLMProvider
{
    /// <summary>Short provider kind, eg <c>"anthropic"</c>. Combined with the backend instance id
    /// to form a unique <see cref="Id"/>.</summary>
    public abstract string ProviderKind { get; }

    /// <inheritdoc/>
    public string Id => $"{ProviderKind}:{AbstractBackendData?.ID ?? 0}";

    /// <inheritdoc/>
    public abstract string DisplayName { get; }

    /// <inheritdoc/>
    public abstract Task<List<LLMModelInfo>> ListModels(CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task GenerateLive(ExtendedLLMInput input, string batchId, Func<JObject, Task> onChunk, CancellationToken ct);

    /// <inheritdoc/>
    public virtual int? CountTokens(string text) => null;

    /// <inheritdoc/>
    public async Task<bool> Unload() => await FreeMemory(true);

    /// <inheritdoc/>
    public async Task<string> Generate(ExtendedLLMInput input, CancellationToken ct = default)
    {
        StringBuilder output = new();
        await GenerateLive(input, "0", j =>
        {
            AppendGenerateChunk(output, j);
            return Task.CompletedTask;
        }, ct);
        return output.ToString();
    }

    /// <summary>The one-shot accumulation step: folds one <see cref="GenerateLive"/> event into
    /// <paramref name="output"/>. Pulled out of <see cref="Generate(ExtendedLLMInput, CancellationToken)"/> so
    /// it is unit-testable without a live backend.
    ///
    /// <para><b>native_tool_call.</b> A provider with <see cref="ILLMProvider.SupportsNativeToolCalling"/> on
    /// (Anthropic always; Hartsy-local when <c>StructuredToolCalling</c> is on) emits this event instead of a
    /// literal <c>&lt;tool_call&gt;</c> tag in the text stream — which used to mean the one-shot path (this
    /// method, and everything built on it: <see cref="LLMDispatcher.Generate"/>,
    /// <see cref="WebAPI.ChatEndpoints.LLMAssistantVoiceTurn"/>'s agentic loop) silently dropped every native
    /// call, while the streaming WS path (<see cref="LLMs.LLMStreamHelper"/>) already handled it correctly.
    /// Appending the equivalent <c>&lt;tool_call&gt;{"name":…,"arguments":…}&lt;/tool_call&gt;</c> tag instead
    /// routes a native call through the exact same downstream dispatch every one-shot caller already has
    /// (<see cref="ToolPromptService.ParseToolCalls"/> → <see cref="Services.ToolExecutorService.ExecuteTool"/>
    /// → fed back as a <see cref="LLMRoles.User"/> turn via <see cref="ToolPromptService.FormatToolResult"/>),
    /// with no new code or contract change on their side: <see cref="Generate(ExtendedLLMInput, CancellationToken)"/>
    /// still just returns <c>Task&lt;string&gt;</c>. The call's native id is not preserved (the tag convention
    /// never carried one — <see cref="ToolPromptService.ParseToolCalls"/> assigns its own), which costs
    /// nothing here: no one-shot caller surfaces a tool-call id in its response today.</para>
    ///
    /// <para>A request with no tools enabled never reaches this branch regardless of the provider (nothing
    /// streams a native_tool_call event for a request with no <c>Tools</c> offered), so this changes nothing
    /// for <see cref="WebAPI.ChatEndpoints.LLMAssistantSendMessage"/> or <c>LLMAssistantTestInstruction</c>,
    /// neither of which sets tools.</para>
    ///
    /// <para><b>Tag-splitting defense.</b> The synthesized JSON is written with every <c>&lt;</c>/<c>&gt;</c>
    /// (and a few more HTML-sensitive characters) as a <c>\uXXXX</c> escape, never a literal angle bracket —
    /// see <see cref="SerializeEscapingAngleBrackets"/>. Without that, a tool name or argument value
    /// containing the literal substring <c>&lt;/tool_call&gt;</c> (plausible for anything free-form: a search
    /// query, a value a tool result echoed back from a web page the model then quotes into its next call)
    /// would close the tag early and split one call into two fragments at <see cref="ToolPromptService"/>'s
    /// plain-text regex, which has no JSON-string awareness. A fragment that still happens to repair into
    /// valid JSON risks dispatching on attacker-shaped text; one that does not lands in
    /// <c>ParseToolCalls</c>'s malformed list and used to leak its raw, unstripped tag text into
    /// spoken/displayed output (see <see cref="WebAPI.ChatEndpoints.LLMAssistantVoiceTurn"/>'s malformed-match
    /// stripping, added alongside this). Escaping removes the vulnerability rather than mitigating it: no
    /// substring of the escaped JSON can ever equal <c>&lt;tool_call&gt;</c> or <c>&lt;/tool_call&gt;</c>, and
    /// <c><</c>/<c>></c> decode back to the exact original characters once
    /// <c>JObject.Parse</c> reads the call out of the tag, so no argument value is altered.</para></summary>
    internal static void AppendGenerateChunk(StringBuilder output, JObject chunk)
    {
        if (chunk.TryGetValue("chunk", out JToken text))
        {
            output.Append($"{text}");
        }
        else if (chunk.TryGetValue("result", out JToken result))
        {
            output.Append($"{result}");
        }
        else if (chunk.TryGetValue("native_tool_call", out JToken nativeToken) && nativeToken is JObject native)
        {
            JObject tagBody = new()
            {
                ["name"] = native["name"],
                ["arguments"] = native["arguments"] as JObject ?? new JObject()
            };
            output.Append($"<tool_call>{SerializeEscapingAngleBrackets(tagBody)}</tool_call>");
        }
    }

    /// <summary>Serializes <paramref name="obj"/> with <see cref="StringEscapeHandling.EscapeHtml"/>, which
    /// (unlike the default <c>JObject.ToString()</c>) escapes <c>&lt;</c>/<c>&gt;</c> (and <c>&amp;</c>) as
    /// <c>\uXXXX</c> rather than passing them through literally — the result is still exactly equivalent JSON
    /// (any compliant parser, <see cref="JObject.Parse(string)"/> included, decodes a <c>\uXXXX</c> escape
    /// back to that one character), it just cannot contain a literal angle bracket anywhere, at any nesting
    /// depth, so it cannot be mistaken for — or split by a regex looking for — an HTML/XML-style tag.</summary>
    private static string SerializeEscapingAngleBrackets(JObject obj)
    {
        using StringWriter stringWriter = new();
        using JsonTextWriter jsonWriter = new(stringWriter)
        {
            Formatting = Formatting.None,
            StringEscapeHandling = StringEscapeHandling.EscapeHtml
        };
        obj.WriteTo(jsonWriter);
        return stringWriter.ToString();
    }

    /// <summary>Per-backend startup (set <see cref="AbstractBackend.Status"/>, validate config, etc.).</summary>
    protected abstract Task OnProviderInit();

    /// <summary>Per-backend teardown (release any held resources).</summary>
    protected abstract Task OnProviderShutdown();

    /// <inheritdoc/>
    public sealed override async Task Init()
    {
        await OnProviderInit();
        if (Status is BackendStatus.RUNNING or BackendStatus.IDLE)
        {
            LLMProviderRegistry.Register(this);
        }
    }

    /// <inheritdoc/>
    public sealed override async Task Shutdown()
    {
        LLMProviderRegistry.Unregister(Id);
        await OnProviderShutdown();
    }

    /// <inheritdoc/>
    public override Task<string> Generate(LLMParamInput user_input) => Generate(ToRich(user_input));

    /// <inheritdoc/>
    public override Task GenerateLive(LLMParamInput user_input, string batchId, Action<JObject> takeOutput)
        => GenerateLive(ToRich(user_input), batchId, j => { takeOutput(j); return Task.CompletedTask; }, CancellationToken.None);

    /// <summary>Adapts the minimal upstream <c>LLMParamInput</c> to the extension's rich input.</summary>
    private static ExtendedLLMInput ToRich(LLMParamInput core)
    {
        ExtendedLLMInput rich = new() { UserMessage = core.UserMessage, Model = core.Model };
        if (!string.IsNullOrEmpty(core.UserMessage))
        {
            rich.Messages.Add(new LLMMessage { Role = LLMRoles.User, Content = core.UserMessage });
        }
        return rich;
    }

    /// <inheritdoc/>
    public override async Task<bool> FreeMemory(bool systemRam) => false;
}
