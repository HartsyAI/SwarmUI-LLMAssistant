using Newtonsoft.Json.Linq;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Tools whose effect happens on a voice satellite, not on the server: setting its LED show, its
/// volume, or muting its microphone.
///
/// <para>Execution here is deliberately a no-op that reports success. The server cannot turn on an LED ring
/// attached to a microcontroller on someone's shelf — the device does that. So the call is validated, recorded,
/// and acknowledged, and <see cref="ChatEndpoints.LLMAssistantVoiceTurn"/> returns it to the caller, which
/// executes it against its own hardware. Acknowledging rather than erroring is what lets the agentic loop
/// continue normally, so the model can say "done, the ring is blue" in the same turn instead of apologising for
/// a failure that did not happen.</para>
///
/// <para>That also means these tools are only useful through a transport that carries the calls back to a
/// device. Enabled on a plain browser chat they will appear to work and do nothing visible, which is why they
/// are off by default and gated behind their own permission.</para>
///
/// <para>One parameterized handler serves all three ids rather than three near-identical classes; the
/// per-tool argument schema lives in the tool definitions, and validation is shared here.</para></summary>
public class DeviceActionTool(string handlerId) : ToolHandler
{
    /// <inheritdoc/>
    public override string HandlerId { get; } = handlerId;

    /// <summary>Tool ids this handler backs, in the order they appear to the model.</summary>
    public static readonly string[] Ids =
        [ToolConstants.SetLedProfile, ToolConstants.SetVolume, ToolConstants.MuteMic];

    public override Task<JObject> Execute(ToolExecutionContext ctx)
    {
        JObject args = ctx.Args ?? [];
        string error = HandlerId switch
        {
            ToolConstants.SetLedProfile => string.IsNullOrWhiteSpace(args["profile"]?.ToString())
                ? "A 'profile' name is required." : null,
            ToolConstants.SetVolume => ValidateVolume(args["level"]),
            ToolConstants.MuteMic => args["muted"] is null ? "A 'muted' boolean is required." : null,
            _ => $"Unknown device action '{HandlerId}'."
        };
        if (error is not null)
        {
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = error });
        }
        return Task.FromResult(new JObject
        {
            ["success"] = true,
            // The device has not acted yet — it acts when the turn's response reaches it. Saying so keeps the
            // model from claiming more certainty than the server has.
            ["status"] = "queued for the device"
        });
    }

    private static string ValidateVolume(JToken level)
    {
        if (level is null) return "A 'level' between 0 and 100 is required.";
        if (level.Type is not (JTokenType.Integer or JTokenType.Float)) return "'level' must be a number.";
        double value = level.Value<double>();
        return value is < 0 or > 100 ? "'level' must be between 0 and 100." : null;
    }

    /// <summary>Whether <paramref name="toolId"/> is one of these device actions, i.e. a call the caller is
    /// expected to carry out itself.</summary>
    public static bool IsDeviceAction(string toolId) =>
        Ids.Contains(toolId, StringComparer.OrdinalIgnoreCase);
}
