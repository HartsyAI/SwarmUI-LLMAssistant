using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.Mcp;

/// <summary>One MCP server an admin has set up: how to reach it, and which of its tools may be used. A <b>stdio</b> server is a program the host
/// starts (and so is the same power as <c>shell_exec</c>); an <b>http</b> server is a URL the host connects to.</summary>
public sealed class McpServerConfig
{
    /// <summary>The transports.</summary>
    public const string TransportStdio = "stdio", TransportHttp = "http";

    /// <summary>Most servers the host keeps.</summary>
    public const int MaxServers = 20;

    /// <summary>The longest a call may be given, in seconds.</summary>
    public const int MaxTimeoutSeconds = 600;

    /// <summary>What a stored secret (an environment value or a header value) is shown as to someone who may not see it. Saving this text back
    /// leaves the stored value as it was.</summary>
    public const string Hidden = "••••••";

    /// <summary>Short name, used in the tool ids the model sees (<c>mcp__id__tool</c>): lowercase letters, digits, dash and underscore.</summary>
    public string Id = "";

    /// <summary>Shown in the list.</summary>
    public string Name = "";

    /// <summary>A disabled server is not connected and offers no tools.</summary>
    public bool Enabled = true;

    /// <summary><see cref="TransportStdio"/> or <see cref="TransportHttp"/>.</summary>
    public string Transport = TransportStdio;

    /// <summary>Stdio: the program to run (no shell).</summary>
    public string Command = "";

    /// <summary>Stdio: one entry per argument.</summary>
    public List<string> Args = [];

    /// <summary>Stdio: environment variables for the program, on top of a small safe set. Values are secrets.</summary>
    public JObject Env = [];

    /// <summary>Stdio: the folder to run in; empty for the server's own.</summary>
    public string Cwd = "";

    /// <summary>Http: the server's URL.</summary>
    public string Url = "";

    /// <summary>Http: headers to send (an authorization token, for example). Values are secrets.</summary>
    public JObject Headers = [];

    /// <summary>Seconds a single tool call may take before it is given up on.</summary>
    public int TimeoutSeconds = 60;

    /// <summary>Ask before every call to this server's tools, whatever the server says about them. A server describes its own tools (read-only, reaches
    /// outside systems), and Ask and Plan mode normally believe it; this is for one the admin does not fully trust to.</summary>
    public bool AlwaysAsk;

    /// <summary>Names of the server's own tools that are switched off here.</summary>
    public List<string> DisabledTools = [];

    /// <summary>Reads a server from its stored or submitted JSON. Missing or mistyped fields take their defaults; call <see cref="Validate"/> to
    /// find out whether the result is acceptable.</summary>
    public static McpServerConfig FromJson(JObject json)
    {
        return new McpServerConfig
        {
            Id = (json["id"]?.ToString() ?? "").Trim().ToLowerInvariant(),
            Name = (json["name"]?.ToString() ?? "").Trim(),
            Enabled = json["enabled"]?.Type != JTokenType.Boolean || json["enabled"].Value<bool>(),
            Transport = (json["transport"]?.ToString() ?? TransportStdio).Trim().ToLowerInvariant(),
            Command = (json["command"]?.ToString() ?? "").Trim(),
            Args = json["args"] is JArray args ? [.. args.Select(a => a?.ToString() ?? "")] : [],
            Env = StringMap(json["env"]),
            Cwd = (json["cwd"]?.ToString() ?? "").Trim(),
            Url = (json["url"]?.ToString() ?? "").Trim(),
            Headers = StringMap(json["headers"]),
            TimeoutSeconds = json["timeoutSeconds"]?.Type == JTokenType.Integer ? json["timeoutSeconds"].Value<int>() : 60,
            AlwaysAsk = json["alwaysAsk"]?.Type == JTokenType.Boolean && json["alwaysAsk"].Value<bool>(),
            DisabledTools = json["disabledTools"] is JArray off ? [.. off.Select(t => t?.ToString() ?? "").Where(t => t.Length > 0)] : [],
        };
    }

    /// <summary>A fingerprint of what the connection itself depends on: whether it is on, how it is reached and with what. A change to this means a
    /// new connection; a change to anything else (the name, the time limit, which tools are switched off) does not.</summary>
    public string ConnectionStamp() => new JObject
    {
        ["enabled"] = Enabled,
        ["transport"] = Transport,
        ["command"] = Command,
        ["args"] = new JArray(Args),
        ["env"] = Env.DeepClone(),
        ["cwd"] = Cwd,
        ["url"] = Url,
        ["headers"] = Headers.DeepClone(),
    }.ToString(Newtonsoft.Json.Formatting.None);

    /// <summary>The server as JSON for storing. Secrets are in clear; use <see cref="ToPublicJson"/> to send it to a browser.</summary>
    public JObject ToJson() => new()
    {
        ["id"] = Id,
        ["name"] = Name,
        ["enabled"] = Enabled,
        ["transport"] = Transport,
        ["command"] = Command,
        ["args"] = new JArray(Args),
        ["env"] = Env.DeepClone(),
        ["cwd"] = Cwd,
        ["url"] = Url,
        ["headers"] = Headers.DeepClone(),
        ["timeoutSeconds"] = TimeoutSeconds,
        ["alwaysAsk"] = AlwaysAsk,
        ["disabledTools"] = new JArray(DisabledTools),
    };

    /// <summary>The server as JSON for a browser. Secret values (environment variables and headers) are never sent, to anyone: they come back as
    /// <see cref="Hidden"/>, and saving that text back keeps the stored value. A user who may not manage servers also gets no program, arguments
    /// or full URL, since those can hold a token or a path.</summary>
    public JObject ToPublicJson(bool isManager)
    {
        JObject json = ToJson();
        json["env"] = Mask(Env);
        json["headers"] = Mask(Headers);
        if (!isManager)
        {
            json["args"] = new JArray();
            json["command"] = Transport == TransportStdio ? "(set by an admin)" : "";
            json["url"] = Transport == TransportHttp ? RedactUrl(Url) : "";
            json["cwd"] = "";
        }
        return json;
    }

    /// <summary>For an update: keeps the stored value of every secret the browser sent back still hidden, so editing a server's URL does not
    /// require typing its token again.</summary>
    public void KeepHiddenSecrets(McpServerConfig stored)
    {
        if (stored is null)
        {
            return;
        }
        foreach (JProperty property in Env.Properties().ToList())
        {
            if (property.Value.ToString() == Hidden && stored.Env[property.Name] is { } old)
            {
                property.Value = old.DeepClone();
            }
        }
        foreach (JProperty property in Headers.Properties().ToList())
        {
            if (property.Value.ToString() == Hidden && stored.Headers[property.Name] is { } old)
            {
                property.Value = old.DeepClone();
            }
        }
    }

    /// <summary>Why this server cannot be saved, or null when it is fine.</summary>
    public string Validate()
    {
        if (!Regex.IsMatch(Id, "^[a-z0-9][a-z0-9_-]{0,31}$"))
        {
            return "The id must be 1 to 32 lowercase letters, digits, dashes or underscores, starting with a letter or digit.";
        }
        if (Id.Contains("__"))
        {
            return "The id cannot contain two underscores in a row (that separates it from the tool name).";
        }
        if (Name.Length is 0 or > 100)
        {
            return "The name must be 1 to 100 characters.";
        }
        if (Transport != TransportStdio && Transport != TransportHttp)
        {
            return "The transport must be stdio or http.";
        }
        if (TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
        {
            return $"The time limit must be between 1 and {MaxTimeoutSeconds} seconds.";
        }
        if (DisabledTools.Count > 500)
        {
            return "Too many switched-off tools.";
        }
        if (Transport == TransportStdio)
        {
            if (Command.Length is 0 or > 300)
            {
                return "A stdio server needs a program to run, up to 300 characters.";
            }
            if (Args.Count > 40 || Args.Any(a => a.Length > 500))
            {
                return "A stdio server can have up to 40 arguments of up to 500 characters.";
            }
            if (Env.Count > 40 || Env.Properties().Any(p => !Regex.IsMatch(p.Name, "^[A-Za-z_][A-Za-z0-9_]{0,99}$") || p.Value.ToString().Length > 2000))
            {
                return "Environment variable names must be letters, digits and underscores, and values up to 2000 characters (up to 40 variables).";
            }
            return null;
        }
        if (!Uri.TryCreate(Url, UriKind.Absolute, out Uri uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return "An http server needs a full http:// or https:// URL.";
        }
        if (Headers.Count > 20 || Headers.Properties().Any(p => !Regex.IsMatch(p.Name, "^[A-Za-z0-9-]{1,100}$") || p.Value.ToString().Length > 4000))
        {
            return "Header names must be letters, digits and dashes, and values up to 4000 characters (up to 20 headers).";
        }
        return null;
    }

    /// <summary>A copy of a string-to-string map from JSON; a value that is not a string is read as its text, and anything that is not an object is empty.</summary>
    private static JObject StringMap(JToken token)
    {
        JObject result = [];
        if (token is JObject obj)
        {
            foreach (JProperty property in obj.Properties())
            {
                result[property.Name] = property.Value.Type == JTokenType.String ? property.Value.ToString() : property.Value.ToString(Newtonsoft.Json.Formatting.None);
            }
        }
        return result;
    }

    /// <summary>The map with every value replaced by <see cref="Hidden"/>.</summary>
    private static JObject Mask(JObject map)
    {
        JObject masked = [];
        foreach (JProperty property in map.Properties())
        {
            masked[property.Name] = Hidden;
        }
        return masked;
    }

    /// <summary>A URL without its user info or query, which can hold a token.</summary>
    private static string RedactUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ? $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}{uri.AbsolutePath}" : "";
    }
}
