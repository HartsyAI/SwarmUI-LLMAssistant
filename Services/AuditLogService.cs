using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>Append-only JSONL audit trail for dangerous tool calls and shared-layer mutations.
///
/// <para>Two write paths:</para>
/// <list type="bullet">
/// <item><see cref="RecordToolCall"/> — logged for every invocation of a handler in the
/// <see cref="AuditedHandlers"/> set, success or failure. Lets admins answer "who ran
/// <c>shell_exec</c> last Tuesday?" without trawling app logs.</item>
/// <item><see cref="RecordSharedWrite"/> — logged when the <c>shared</c> scope of settings /
/// assistants / tools is mutated. Lets admins answer "who edited the shared <c>code-reviewer</c>
/// assistant?".</item>
/// </list>
///
/// <para>Storage: <c>{DataDir}/LLMAssistant/audit.log</c>, one JSON object per line, size-rotated
/// at <see cref="MaxLogSizeBytes"/> with up to <see cref="MaxRotatedGenerations"/> kept. Rotation
/// is best-effort — failures are logged and writing continues; we never block a user request on
/// the audit write succeeding.</para>
///
/// <para>Opt-in: writes are no-ops unless the <c>LLMAssistantAuditLog</c> ServerSettings flag is
/// true. Default false so single-user installs don't accrue a log they don't want.</para>
///
/// <para>Reads (admin viewer endpoint coming in a future sprint) — see <see cref="ReadRecent"/>
/// which streams the tail of the current log file.</para>
/// </summary>
public static class AuditLogService
{
    public const string AuditFileName = "audit.log";
    public const long MaxLogSizeBytes = 10L * 1024 * 1024;     // 10 MB per generation
    public const int MaxRotatedGenerations = 5;
    public const int MaxArgsSerializedChars = 1024;             // Truncate args/result for storage

    /// <summary>Handlers whose every invocation is recorded. Read tools (file_read, memory_read,
    /// swarm_docs) are deliberately excluded — they don't mutate anything dangerous and would
    /// dominate the log. Add a handler ID here to start auditing it.</summary>
    public static readonly HashSet<string> AuditedHandlers =
    [
        ToolConstants.ShellExec,
        ToolConstants.FileWrite,
        ToolConstants.HttpRequest,
    ];

    private static readonly JsonSerializer Serializer = JsonSerializer.CreateDefault(new JsonSerializerSettings
    {
        Formatting = Formatting.None,
        NullValueHandling = NullValueHandling.Ignore
    });

    private static readonly object WriteLock = new();

    /// <summary>Settings flag name — read at write-time so the admin can toggle without restart.</summary>
    public const string EnabledFlag = "LLMAssistantAuditLog";

    /// <summary>True if auditing is on. Reads <see cref="EnabledFlag"/> via SwarmUI's standard
    /// settings mechanism. Defaults to false so untouched installs accrue no log.</summary>
    public static bool IsEnabled()
    {
        try
        {
            // ServerSettings is an AutoConfiguration; we don't add a strongly-typed field for an
            // optional admin-only feature, so we read the underlying FDS data when present.
            // If the key isn't set, the audit log stays off.
            string raw = Program.Sessions?.GenericSharedUser?.GetGenericData("llmassistant_audit", "enabled");
            if (string.IsNullOrEmpty(raw)) { return false; }
            return raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Programmatic toggle for the audit flag — used by the admin API. Idempotent.</summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            Program.Sessions?.GenericSharedUser?.SaveGenericData("llmassistant_audit", "enabled", enabled ? "true" : "false");
        }
        catch (Exception ex)
        {
            Logs.Warning($"[LLMAssistant] AuditLog flag write failed: {ex.Message}");
        }
    }

    /// <summary>Records a tool execution. No-op if the handler isn't in <see cref="AuditedHandlers"/>
    /// or if auditing is disabled. Wraps everything in a try/catch — audit failures must never
    /// surface to the user or cause the tool call itself to fail.</summary>
    public static void RecordToolCall(string handlerId, string toolId, User user, string assistantId, string threadId, JObject args, JObject result)
    {
        if (string.IsNullOrEmpty(handlerId) || !AuditedHandlers.Contains(handlerId))
        {
            return;
        }
        if (!IsEnabled())
        {
            return;
        }
        try
        {
            bool success = result?["success"]?.Value<bool>() ?? true;
            string error = result?["error"]?.ToString();
            JObject entry = new()
            {
                ["ts"] = DateTime.UtcNow.ToString("o"),
                ["kind"] = "tool",
                ["handler"] = handlerId,
                ["tool"] = toolId,
                ["userId"] = user?.UserID ?? "<anonymous>",
                ["assistantId"] = assistantId,
                ["threadId"] = threadId,
                ["success"] = success,
                ["args"] = Truncate(args),
                ["result"] = Truncate(result),
            };
            if (!string.IsNullOrEmpty(error))
            {
                entry["error"] = error.Length > 500 ? error[..500] : error;
            }
            Append(entry);
        }
        catch (Exception ex)
        {
            Logs.Warning($"[LLMAssistant] Audit write (tool) failed: {ex.Message}");
        }
    }

    /// <summary>Records a mutation to the shared / admin layer (shared assistant, shared tool,
    /// or shared settings replacement). <paramref name="target"/> is a free-form descriptor like
    /// <c>"assistant:code-reviewer"</c> or <c>"settings"</c>.</summary>
    public static void RecordSharedWrite(string action, string target, User user, JObject details = null)
    {
        if (!IsEnabled())
        {
            return;
        }
        try
        {
            JObject entry = new()
            {
                ["ts"] = DateTime.UtcNow.ToString("o"),
                ["kind"] = "shared",
                ["action"] = action,
                ["target"] = target,
                ["userId"] = user?.UserID ?? "<anonymous>",
            };
            if (details is not null)
            {
                entry["details"] = Truncate(details);
            }
            Append(entry);
        }
        catch (Exception ex)
        {
            Logs.Warning($"[LLMAssistant] Audit write (shared) failed: {ex.Message}");
        }
    }

    /// <summary>Reads the last <paramref name="maxLines"/> lines of the current audit log file.
    /// Returns an empty array if auditing is disabled or the file doesn't exist. Used by the
    /// admin viewer endpoint.</summary>
    public static JArray ReadRecent(int maxLines = 500)
    {
        JArray result = [];
        try
        {
            string path = GetLogPath();
            if (!File.Exists(path))
            {
                return result;
            }
            // Read whole file then take the tail — audit files cap at MaxLogSizeBytes so this is bounded.
            string[] lines = File.ReadAllLines(path);
            int start = Math.Max(0, lines.Length - maxLines);
            for (int i = start; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { result.Add(JObject.Parse(line)); }
                catch { /* skip malformed lines rather than failing the whole read */ }
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[LLMAssistant] Audit read failed: {ex.Message}");
        }
        return result;
    }

    private static string GetLogPath()
    {
        string dir = Path.Combine(Program.DataDir, "LLMAssistant");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, AuditFileName);
    }

    private static void Append(JObject entry)
    {
        string path = GetLogPath();
        lock (WriteLock)
        {
            try
            {
                // Rotate first so we never write to a file that's already over the cap.
                RotateIfNeeded(path);
                using StreamWriter sw = new(path, append: true, encoding: System.Text.Encoding.UTF8);
                using JsonTextWriter jw = new(sw) { Formatting = Formatting.None };
                Serializer.Serialize(jw, entry);
                sw.WriteLine();
            }
            catch (Exception ex)
            {
                Logs.Warning($"[LLMAssistant] Audit append failed: {ex.Message}");
            }
        }
    }

    /// <summary>Rotates <c>audit.log</c> -> <c>audit.log.1</c>, shifting older generations along,
    /// dropping the oldest beyond <see cref="MaxRotatedGenerations"/>.</summary>
    private static void RotateIfNeeded(string path)
    {
        try
        {
            if (!File.Exists(path)) { return; }
            FileInfo fi = new(path);
            if (fi.Length < MaxLogSizeBytes) { return; }
            // Shift: audit.log.{N-1} -> audit.log.N, drop audit.log.{Max}
            for (int i = MaxRotatedGenerations; i > 1; i--)
            {
                string older = $"{path}.{i - 1}";
                string newer = $"{path}.{i}";
                if (File.Exists(older))
                {
                    if (File.Exists(newer)) { File.Delete(newer); }
                    File.Move(older, newer);
                }
            }
            string firstRotated = $"{path}.1";
            if (File.Exists(firstRotated)) { File.Delete(firstRotated); }
            File.Move(path, firstRotated);
        }
        catch (Exception ex)
        {
            Logs.Warning($"[LLMAssistant] Audit rotate failed: {ex.Message}");
        }
    }

    /// <summary>Truncates a JSON blob's serialized form so a runaway tool result (eg a 5 MB
    /// HTTP response body) can't blow up the audit log. Returns either the original object or
    /// a string-truncated placeholder.</summary>
    private static JToken Truncate(JObject obj)
    {
        if (obj is null) { return null; }
        string serialized = obj.ToString(Formatting.None);
        if (serialized.Length <= MaxArgsSerializedChars) { return obj; }
        return new JValue(serialized[..MaxArgsSerializedChars] + "…");
    }
}
