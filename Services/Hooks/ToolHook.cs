using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.Hooks;

/// <summary>A policy that runs around a tool call the model makes. A <b>pre</b> hook runs before the call and can refuse it, force the
/// user to be asked, change its arguments or add a note for the model; a <b>post</b> hook runs after and can add a note to the result.
/// <para>A <b>rule</b> hook is plain data (match a tool and optionally one argument, then act), so it needs no process and is safe for any
/// user to write. A <b>command</b> hook runs a program, so only someone holding <c>llm_hook_command</c> may create one.</para></summary>
public sealed class ToolHook
{
    /// <summary>The kinds of hook.</summary>
    public const string KindRule = "rule", KindCommand = "command";

    /// <summary>When a hook runs.</summary>
    public const string PhasePre = "pre", PhasePost = "post";

    /// <summary>What a rule hook does when it matches.</summary>
    public const string ActionDeny = "deny", ActionAsk = "ask", ActionRewrite = "rewrite", ActionNote = "note";

    /// <summary>How a rule hook compares an argument with its value.</summary>
    public const string MatchAny = "any", MatchGlob = "glob", MatchRegex = "regex", MatchContains = "contains";

    /// <summary>What to do when a command hook cannot run or fails.</summary>
    public const string OnErrorAllow = "allow", OnErrorDeny = "deny";

    /// <summary>Most hooks one layer may hold.</summary>
    public const int MaxPerLayer = 50;

    /// <summary>Most seconds a command hook may run.</summary>
    public const int MaxTimeoutSeconds = 60;

    /// <summary>Unique within its layer; letters, digits, dash and underscore.</summary>
    public string Id;

    /// <summary>Shown in the list and in what the model is told.</summary>
    public string Name;

    /// <summary>A disabled hook never runs.</summary>
    public bool Enabled = true;

    /// <summary>Hooks run in this order within a layer, lowest first.</summary>
    public int Order;

    /// <summary><see cref="PhasePre"/> or <see cref="PhasePost"/>.</summary>
    public string Phase = PhasePre;

    /// <summary>Which tools it applies to: a tool id, or a pattern with <c>*</c> and <c>?</c> (<c>file_*</c>, <c>*</c> for all).</summary>
    public string Tool = "*";

    /// <summary><see cref="KindRule"/> or <see cref="KindCommand"/>.</summary>
    public string Kind = KindRule;

    /// <summary>Rule: the argument to look at; <c>*</c> looks at all of them as text; empty matches every call of the tool.</summary>
    public string Argument = "";

    /// <summary>Rule: how <see cref="Value"/> is compared (<see cref="MatchAny"/> only needs the argument to be present).</summary>
    public string MatchType = MatchAny;

    /// <summary>Rule: what the argument is compared with.</summary>
    public string Value = "";

    /// <summary>Rule: <see cref="ActionDeny"/>, <see cref="ActionAsk"/>, <see cref="ActionRewrite"/> or <see cref="ActionNote"/> (post hooks only note).</summary>
    public string Action = ActionDeny;

    /// <summary>Rule: the reason a deny gives the model, or the text of a note.</summary>
    public string Message = "";

    /// <summary>Rule rewrite: arguments to set (or replace) on the call.</summary>
    public JObject Set = [];

    /// <summary>Command: the program to run (no shell).</summary>
    public string Command = "";

    /// <summary>Command: one entry per argument.</summary>
    public List<string> Args = [];

    /// <summary>Command: seconds it may run before it is killed.</summary>
    public int TimeoutSeconds = 10;

    /// <summary>Command: <see cref="OnErrorDeny"/> (the default for a pre hook) refuses the call when the hook cannot run; <see cref="OnErrorAllow"/> carries on.</summary>
    public string OnError = OnErrorDeny;

    /// <summary>Which layer it came from, <c>shared</c> or <c>personal</c>; not stored.</summary>
    public string Scope = "personal";

    /// <summary>Reads a hook from its stored or submitted JSON. Fields that are missing or the wrong type take their defaults; call
    /// <see cref="Validate"/> to find out whether the result is acceptable.</summary>
    public static ToolHook FromJson(JObject json, string scope = "personal")
    {
        ToolHook hook = new()
        {
            Id = json["id"]?.ToString()?.Trim() ?? "",
            Name = json["name"]?.ToString()?.Trim() ?? "",
            Enabled = json["enabled"]?.Type != JTokenType.Boolean || json["enabled"].Value<bool>(),
            Order = json["order"]?.Type == JTokenType.Integer ? json["order"].Value<int>() : 0,
            Phase = (json["phase"]?.ToString() ?? PhasePre).Trim().ToLowerInvariant(),
            Tool = (json["tool"]?.ToString() ?? "*").Trim(),
            Kind = (json["kind"]?.ToString() ?? KindRule).Trim().ToLowerInvariant(),
            Argument = json["argument"]?.ToString()?.Trim() ?? "",
            MatchType = (json["matchType"]?.ToString() ?? MatchAny).Trim().ToLowerInvariant(),
            Value = json["value"]?.ToString() ?? "",
            Action = (json["action"]?.ToString() ?? ActionDeny).Trim().ToLowerInvariant(),
            Message = json["message"]?.ToString() ?? "",
            Set = json["set"] as JObject ?? [],
            Command = json["command"]?.ToString()?.Trim() ?? "",
            Args = json["args"] is JArray args ? [.. args.Select(a => a?.ToString() ?? "")] : [],
            TimeoutSeconds = json["timeoutSeconds"]?.Type == JTokenType.Integer ? json["timeoutSeconds"].Value<int>() : 10,
            OnError = (json["onError"]?.ToString() ?? OnErrorDeny).Trim().ToLowerInvariant(),
            Scope = scope,
        };
        return hook;
    }

    /// <summary>The hook as JSON for storing or sending to the browser.</summary>
    public JObject ToJson() => new()
    {
        ["id"] = Id,
        ["name"] = Name,
        ["enabled"] = Enabled,
        ["order"] = Order,
        ["phase"] = Phase,
        ["tool"] = Tool,
        ["kind"] = Kind,
        ["argument"] = Argument,
        ["matchType"] = MatchType,
        ["value"] = Value,
        ["action"] = Action,
        ["message"] = Message,
        ["set"] = Set.DeepClone(),
        ["command"] = Command,
        ["args"] = new JArray(Args),
        ["timeoutSeconds"] = TimeoutSeconds,
        ["onError"] = OnError,
    };

    /// <summary>Why this hook cannot be saved, or null when it is fine. Keeps every field inside sane limits and rejects a pattern that cannot work.</summary>
    public string Validate()
    {
        if (!Regex.IsMatch(Id, "^[A-Za-z0-9_-]{1,64}$"))
        {
            return "The id must be 1 to 64 letters, digits, dashes or underscores.";
        }
        if (Name.Length is 0 or > 100)
        {
            return "The name must be 1 to 100 characters.";
        }
        if (Phase != PhasePre && Phase != PhasePost)
        {
            return "The phase must be pre or post.";
        }
        if (Tool.Length is 0 or > 100 || Tool.Any(char.IsWhiteSpace))
        {
            return "The tool must be a tool id or a pattern (for example file_* or *), up to 100 characters.";
        }
        if (Kind != KindRule && Kind != KindCommand)
        {
            return "The kind must be rule or command.";
        }
        if (Order is < -1000 or > 1000)
        {
            return "The order must be between -1000 and 1000.";
        }
        if (Kind == KindCommand)
        {
            return ValidateCommand();
        }
        return ValidateRule();
    }

    /// <summary>The checks that apply to a rule hook.</summary>
    private string ValidateRule()
    {
        if (Argument.Length > 100 || Value.Length > 500 || Message.Length > 1000)
        {
            return "The argument can be up to 100 characters, the value 500 and the message 1000.";
        }
        if (MatchType is not (MatchAny or MatchGlob or MatchRegex or MatchContains))
        {
            return "The match type must be any, glob, regex or contains.";
        }
        if (Argument.Length > 0 && MatchType != MatchAny && Value.Length == 0)
        {
            return "A value is needed to compare the argument with.";
        }
        if (MatchType == MatchRegex && Value.Length > 0 && !HookMatcher.TryCompile(Value, out _))
        {
            return "The regular expression is not valid.";
        }
        string[] allowed = Phase == PhasePost ? [ActionNote] : [ActionDeny, ActionAsk, ActionRewrite, ActionNote];
        if (!allowed.Contains(Action))
        {
            return Phase == PhasePost ? "A post hook can only add a note." : "The action must be deny, ask, rewrite or note.";
        }
        if (Action == ActionNote && Message.Trim().Length == 0)
        {
            return "A note needs a message.";
        }
        if (Action == ActionRewrite && (Set.Count == 0 || Set.ToString(Newtonsoft.Json.Formatting.None).Length > 2000))
        {
            return "A rewrite needs at least one argument to set, up to 2000 characters in all.";
        }
        return null;
    }

    /// <summary>The checks that apply to a command hook.</summary>
    private string ValidateCommand()
    {
        if (Command.Length is 0 or > 300)
        {
            return "A command hook needs a program to run, up to 300 characters.";
        }
        if (Args.Count > 20 || Args.Any(a => a.Length > 300))
        {
            return "A command hook can have up to 20 arguments of up to 300 characters.";
        }
        if (TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
        {
            return $"The time limit must be between 1 and {MaxTimeoutSeconds} seconds.";
        }
        if (OnError != OnErrorAllow && OnError != OnErrorDeny)
        {
            return "On error must be allow or deny.";
        }
        return null;
    }
}
