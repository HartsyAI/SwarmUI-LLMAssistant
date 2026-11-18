using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.Mcp;

/// <summary>A JSON-RPC conversation with one MCP server. The client speaks the protocol; a transport only moves messages, so the protocol can be
/// tested against an in-memory transport.</summary>
public interface IMcpTransport : IDisposable
{
    /// <summary>False once the connection is gone (the process exited, or it was closed).</summary>
    bool IsAlive { get; }

    /// <summary>Sends a request and returns the server's JSON-RPC response (<c>result</c> or <c>error</c>). Throws when the connection fails or
    /// <paramref name="ct"/> is cancelled.</summary>
    Task<JObject> RequestAsync(JObject request, CancellationToken ct);

    /// <summary>Sends a notification, which has no answer.</summary>
    Task NotifyAsync(JObject notification, CancellationToken ct);

    /// <summary>Raised for a notification the server sends (a stdio server can send them at any time).</summary>
    event Action<JObject> ServerNotification;
}

/// <summary>Talks to a server program over its standard input and output, one JSON message per line. The program is started by the host, so
/// it is the same power as <c>shell_exec</c>: it gets a small safe environment (not the host's) plus what the admin configured, and it and its
/// children are killed when the connection closes.</summary>
public sealed class McpStdioTransport : IMcpTransport
{
    /// <summary>Environment variables a server program is given from the host's own: enough to find programs and a home folder, and no secrets.</summary>
    private static readonly string[] SafeEnvironment =
    [
        "PATH", "PATHEXT", "SystemRoot", "SYSTEMROOT", "windir", "COMSPEC", "ComSpec", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "ProgramData",
        "ProgramFiles", "ProgramFiles(x86)", "TEMP", "TMP", "TMPDIR", "USER", "USERNAME", "LANG", "LC_ALL", "SHELL",
    ];

    /// <summary>The longest a single line from the server may be before the connection is dropped.</summary>
    private const int MaxLineChars = 8 * 1024 * 1024;

    private readonly Process _process;
    private readonly Dictionary<long, TaskCompletionSource<JObject>> _pending = [];
    private readonly object _lock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private bool _disposed;

    /// <inheritdoc/>
    public event Action<JObject> ServerNotification;

    /// <summary>The last lines the program wrote to standard error, for the status shown when it fails.</summary>
    public string StderrTail
    {
        get
        {
            lock (_lock)
            {
                return _stderr.ToString().Trim();
            }
        }
    }

    /// <inheritdoc/>
    public bool IsAlive => !_disposed && !_process.HasExited;

    private McpStdioTransport(Process process)
    {
        _process = process;
    }

    /// <summary>Starts the program for <paramref name="config"/>. Throws with a readable message when it cannot be started.</summary>
    public static McpStdioTransport Start(McpServerConfig config)
    {
        (string file, List<string> arguments) = ResolveCommand(config.Command, config.Args);
        ProcessStartInfo startInfo = new()
        {
            FileName = file,
            WorkingDirectory = string.IsNullOrWhiteSpace(config.Cwd) ? "" : config.Cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment.Clear();
        foreach (string name in SafeEnvironment)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                startInfo.Environment[name] = value;
            }
        }
        foreach (JProperty variable in config.Env.Properties())
        {
            startInfo.Environment[variable.Name] = variable.Value.ToString();
        }
        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        McpStdioTransport transport = new(process);
        process.ErrorDataReceived += (_, e) => transport.AppendStderr(e.Data);
        try
        {
            if (!process.Start())
            {
                throw new SwarmReadableErrorException($"Could not start '{config.Command}'.");
            }
        }
        catch (SwarmReadableErrorException)
        {
            process.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new SwarmReadableErrorException($"Could not start '{config.Command}': {ex.Message}");
        }
        process.BeginErrorReadLine();
        _ = Task.Run(transport.ReadLoopAsync);
        return transport;
    }

    /// <summary>The program and arguments to run for a configured command. On Windows a command like <c>npx</c> is a <c>.cmd</c> script, which
    /// cannot be started directly, so it is found on the PATH and run through <c>cmd.exe</c>.</summary>
    internal static (string File, List<string> Arguments) ResolveCommand(string command, List<string> args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (command, args);
        }
        string resolved = FindOnPath(command);
        if (resolved is not null && (resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            return ("cmd.exe", ["/d", "/c", resolved, .. args]);
        }
        return (resolved ?? command, args);
    }

    /// <summary>The full path of a program found through the PATH and PATHEXT, or null.</summary>
    private static string FindOnPath(string command)
    {
        string[] extensions = ["", .. (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)];
        IEnumerable<string> folders = Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains('/')
            ? [""]
            : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (string folder in folders)
        {
            foreach (string extension in extensions)
            {
                string candidate = folder.Length == 0 ? command + extension : Path.Combine(folder, command + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    /// <summary>Keeps the end of what the program writes to standard error.</summary>
    private void AppendStderr(string line)
    {
        if (line is null)
        {
            return;
        }
        lock (_lock)
        {
            _stderr.AppendLine(line);
            if (_stderr.Length > 4000)
            {
                _stderr.Remove(0, _stderr.Length - 4000);
            }
        }
    }

    /// <summary>Reads the program's output a line at a time and hands each message to whoever is waiting for it.</summary>
    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length > MaxLineChars)
                {
                    break;
                }
                if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
                {
                    continue;
                }
                JObject message;
                try
                {
                    message = JObject.Parse(line);
                }
                catch (Newtonsoft.Json.JsonException)
                {
                    continue;
                }
                Dispatch(message);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process went away; pending requests are failed below.
        }
        FailAll(new IOException("The server program ended." + (StderrTail.Length > 0 ? " " + StderrTail : "")));
    }

    /// <summary>Routes one message from the server: a response to its waiting request, or a request or notification from the server.</summary>
    private void Dispatch(JObject message)
    {
        bool hasMethod = message["method"] is not null;
        if (!hasMethod && message["id"] is { } responseId)
        {
            long id = responseId.Type == JTokenType.Integer ? responseId.Value<long>() : -1;
            TaskCompletionSource<JObject> waiting = null;
            lock (_lock)
            {
                if (_pending.Remove(id, out TaskCompletionSource<JObject> found))
                {
                    waiting = found;
                }
            }
            waiting?.TrySetResult(message);
            return;
        }
        if (hasMethod && message["id"] is { } requestId)
        {
            // A request from the server (ping, sampling, roots): the host answers ping and refuses the rest, so the server is never left waiting.
            JObject reply = message["method"]?.ToString() == "ping"
                ? new JObject { ["jsonrpc"] = "2.0", ["id"] = requestId, ["result"] = new JObject() }
                : new JObject { ["jsonrpc"] = "2.0", ["id"] = requestId, ["error"] = new JObject { ["code"] = -32601, ["message"] = "Method not supported by this client." } };
            _ = Task.Run(async () =>
            {
                try
                {
                    await WriteAsync(reply, CancellationToken.None);
                }
                catch (Exception)
                {
                    // The connection is closing.
                }
            });
            return;
        }
        if (hasMethod)
        {
            ServerNotification?.Invoke(message);
        }
    }

    /// <summary>Fails every request still waiting, because the connection is gone.</summary>
    private void FailAll(Exception reason)
    {
        List<TaskCompletionSource<JObject>> waiting;
        lock (_lock)
        {
            waiting = [.. _pending.Values];
            _pending.Clear();
        }
        foreach (TaskCompletionSource<JObject> source in waiting)
        {
            source.TrySetException(reason);
        }
    }

    /// <summary>Writes one message as a line.</summary>
    private async Task WriteAsync(JObject message, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            string line = message.ToString(Newtonsoft.Json.Formatting.None) + "\n";
            await _process.StandardInput.WriteAsync(line.AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<JObject> RequestAsync(JObject request, CancellationToken ct)
    {
        if (!IsAlive)
        {
            throw new IOException("The server program is not running." + (StderrTail.Length > 0 ? " " + StderrTail : ""));
        }
        long id = request["id"]!.Value<long>();
        TaskCompletionSource<JObject> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _pending[id] = source;
        }
        try
        {
            // The program may have ended after the check above and before the entry was registered, when nothing would ever fail it.
            if (!IsAlive)
            {
                throw new IOException("The server program ended." + (StderrTail.Length > 0 ? " " + StderrTail : ""));
            }
            await WriteAsync(request, ct);
            using CancellationTokenRegistration registration = ct.Register(() => source.TrySetCanceled(ct));
            return await source.Task;
        }
        finally
        {
            lock (_lock)
            {
                _pending.Remove(id);
            }
        }
    }

    /// <inheritdoc/>
    public Task NotifyAsync(JObject notification, CancellationToken ct) => WriteAsync(notification, ct);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
        FailAll(new IOException("The connection was closed."));
        _process.Dispose();
    }
}

/// <summary>Talks to a server over the MCP "Streamable HTTP" transport: each message is a POST, and the answer is either one JSON body or an event
/// stream holding it. A session id the server gives on the first answer is sent on every later request. The older HTTP+SSE transport is not supported.</summary>
public sealed class McpHttpTransport : IMcpTransport
{
    private readonly HttpClient _http;
    private readonly string _url;
    private readonly JObject _headers;
    private string _sessionId;
    private string _protocolVersion;
    private bool _closed;

    /// <inheritdoc/>
    public event Action<JObject> ServerNotification
    {
        add { }
        remove { }
    }

    /// <summary>Connects to <paramref name="config"/>'s URL with its headers. The server is reached only when a request is sent.</summary>
    public McpHttpTransport(McpServerConfig config, HttpClient http = null)
    {
        _url = config.Url;
        _headers = (JObject)config.Headers.DeepClone();
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <inheritdoc/>
    public bool IsAlive => !_closed;

    /// <summary>Remembers the protocol version the server agreed to, sent on every later request.</summary>
    public void SetProtocolVersion(string version) => _protocolVersion = version;

    /// <summary>Builds a POST for one message.</summary>
    private HttpRequestMessage Build(JObject message)
    {
        HttpRequestMessage request = new(HttpMethod.Post, _url) { Content = new StringContent(message.ToString(Newtonsoft.Json.Formatting.None), new UTF8Encoding(false), "application/json") };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        foreach (JProperty header in _headers.Properties())
        {
            request.Headers.TryAddWithoutValidation(header.Name, header.Value.ToString());
        }
        if (_sessionId is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        }
        if (_protocolVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);
        }
        return request;
    }

    /// <inheritdoc/>
    public async Task<JObject> RequestAsync(JObject request, CancellationToken ct)
    {
        using HttpRequestMessage http = Build(request);
        using HttpResponseMessage response = await _http.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string> session))
        {
            _sessionId ??= session.FirstOrDefault();
        }
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            throw new IOException($"The server answered {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}");
        }
        string wantedId = request["id"]!.ToString();
        string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            using StreamReader reader = new(await response.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
            StringBuilder data = new();
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Append(line.AsSpan(5).TrimStart(' '));
                }
                else if (line.Length == 0 && data.Length > 0)
                {
                    JObject message = TryParse(data.ToString());
                    data.Clear();
                    if (message is not null && message["method"] is null && message["id"]?.ToString() == wantedId)
                    {
                        return message;
                    }
                }
            }
            if (data.Length > 0 && TryParse(data.ToString()) is { } last && last["id"]?.ToString() == wantedId)
            {
                return last;
            }
            throw new IOException("The server's event stream ended without an answer.");
        }
        return TryParse(await response.Content.ReadAsStringAsync(ct)) ?? throw new IOException("The server's answer was not a JSON object.");
    }

    /// <inheritdoc/>
    public async Task NotifyAsync(JObject notification, CancellationToken ct)
    {
        using HttpRequestMessage http = Build(notification);
        using HttpResponseMessage response = await _http.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new IOException($"The server answered {(int)response.StatusCode} to a notification.");
        }
    }

    /// <summary>Parses a JSON object, or null for text that is not one.</summary>
    private static JObject TryParse(string text)
    {
        try
        {
            return JObject.Parse(text);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _closed = true;
        _http.Dispose();
    }
}
