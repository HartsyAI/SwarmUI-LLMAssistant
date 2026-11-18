using System.Net;
using System.Text;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Mcp;
using Hartsy.Extensions.LLMAssistant.Tests.Fakes;
using Hartsy.Extensions.LLMAssistant.Tools;
using Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Server configuration: validation, and what a browser or a non-admin is shown.</summary>
public class McpServerConfigTests
{
    private static JObject Stdio(Action<JObject> shape = null)
    {
        JObject json = new() { ["id"] = "files", ["name"] = "Files", ["transport"] = "stdio", ["command"] = "node", ["args"] = new JArray("server.js", "--token=abc"), ["env"] = new JObject { ["API_KEY"] = "secret" } };
        shape?.Invoke(json);
        return json;
    }

    [Fact]
    public void AGoodStdioServerValidates() => Assert.Null(McpServerConfig.FromJson(Stdio()).Validate());

    [Fact]
    public void AGoodHttpServerValidates()
    {
        JObject json = new() { ["id"] = "web", ["name"] = "Web", ["transport"] = "http", ["url"] = "https://mcp.example.com/mcp", ["headers"] = new JObject { ["Authorization"] = "Bearer x" } };
        Assert.Null(McpServerConfig.FromJson(json).Validate());
    }

    [Theory]
    [InlineData("Bad Id")]
    [InlineData("")]
    [InlineData("a__b")]
    [InlineData("-x")]
    public void AnIdThatCannotBePartOfAToolNameIsRefused(string id)
        => Assert.NotNull(McpServerConfig.FromJson(Stdio(j => j["id"] = id)).Validate());

    [Fact]
    public void ProblemsAreNamed()
    {
        Assert.Contains("program", McpServerConfig.FromJson(Stdio(j => j["command"] = "")).Validate());
        Assert.Contains("URL", McpServerConfig.FromJson(new JObject { ["id"] = "w", ["name"] = "W", ["transport"] = "http", ["url"] = "ftp://x" }).Validate());
        Assert.Contains("transport", McpServerConfig.FromJson(Stdio(j => j["transport"] = "carrier-pigeon")).Validate());
        Assert.Contains("time limit", McpServerConfig.FromJson(Stdio(j => j["timeoutSeconds"] = 0)).Validate());
        Assert.Contains("Environment", McpServerConfig.FromJson(Stdio(j => j["env"] = new JObject { ["BAD NAME"] = "x" })).Validate());
    }

    [Fact]
    public void SecretsNeverLeaveTheServer()
    {
        McpServerConfig config = McpServerConfig.FromJson(Stdio());
        JObject manager = config.ToPublicJson(isManager: true);
        Assert.Equal(McpServerConfig.Hidden, manager["env"]!["API_KEY"]!.ToString());
        Assert.DoesNotContain("secret", manager.ToString());
        Assert.Equal("node", manager["command"]!.ToString());
        Assert.Contains("--token=abc", manager["args"]!.ToString());
    }

    [Fact]
    public void AUserWhoCannotManageSeesNeitherTheProgramNorItsArguments()
    {
        JObject user = McpServerConfig.FromJson(Stdio()).ToPublicJson(isManager: false);
        Assert.Empty((JArray)user["args"]!);
        Assert.DoesNotContain("abc", user.ToString());
        Assert.DoesNotContain("secret", user.ToString());
        Assert.NotEqual("node", user["command"]!.ToString());
        JObject http = McpServerConfig.FromJson(new JObject { ["id"] = "w", ["name"] = "W", ["transport"] = "http", ["url"] = "https://user:pw@host.example:8443/mcp?token=zzz" }).ToPublicJson(false);
        Assert.Equal("https://host.example:8443/mcp", http["url"]!.ToString());
    }

    [Fact]
    public void SavingBackTheHiddenPlaceholderKeepsTheStoredSecret()
    {
        McpServerConfig stored = McpServerConfig.FromJson(Stdio());
        McpServerConfig edited = McpServerConfig.FromJson(stored.ToPublicJson(true));
        edited.Env["OTHER"] = "new";
        edited.KeepHiddenSecrets(stored);
        Assert.Equal("secret", edited.Env["API_KEY"]!.ToString());
        Assert.Equal("new", edited.Env["OTHER"]!.ToString());
    }

    [Fact]
    public void SettingsSentToABrowserNeverCarryMcpServersOrHooks()
    {
        JObject settings = new()
        {
            ["parameters"] = new JObject { ["temperature"] = 1 },
            ["mcpServers"] = new JObject { ["files"] = new JObject { ["env"] = new JObject { ["TOKEN"] = "hunter2" } } },
            ["hooks"] = new JObject { ["h"] = new JObject { ["command"] = "secret-program" } },
        };
        JObject forClient = SettingsService.ForClient(settings);
        Assert.Equal(["parameters"], forClient.Properties().Select(p => p.Name));
        Assert.DoesNotContain("hunter2", forClient.ToString());
        // The original is untouched: the server still needs those keys.
        Assert.NotNull(settings["mcpServers"]);
    }

    [Fact]
    public void AJsonRoundTripKeepsEverything()
    {
        McpServerConfig config = McpServerConfig.FromJson(Stdio(j => { j["disabledTools"] = new JArray("rm"); j["timeoutSeconds"] = 120; j["enabled"] = false; j["alwaysAsk"] = true; }));
        McpServerConfig back = McpServerConfig.FromJson(config.ToJson());
        Assert.True(back.AlwaysAsk);
        Assert.Equal(["rm"], back.DisabledTools);
        Assert.Equal(120, back.TimeoutSeconds);
        Assert.False(back.Enabled);
        Assert.Equal(["server.js", "--token=abc"], back.Args);
    }
}

/// <summary>The ids the model sees for a server's tools.</summary>
public class McpToolNamingTests
{
    private static readonly System.Text.RegularExpressions.Regex Valid = new("^[A-Za-z0-9_-]{1,64}$");

    [Fact]
    public void AnIdIsPrefixedServerAndTool() => Assert.Equal("mcp__files__read_file", McpToolNaming.ToolId("files", "read_file", new HashSet<string>()));

    [Fact]
    public void CharactersAFunctionNameCannotHaveBecomeUnderscores()
    {
        string id = McpToolNaming.ToolId("files", "read file.v2/√", new HashSet<string>());
        Assert.Equal("mcp__files__read_file_v2__", id);
        Assert.Matches(Valid, id);
    }

    [Fact]
    public void ALongNameIsCutAndKeepsAStableHash()
    {
        string name = new('a', 200);
        string id = McpToolNaming.ToolId("files", name, new HashSet<string>());
        Assert.Equal(64, id.Length);
        Assert.Matches(Valid, id);
        Assert.Equal(id, McpToolNaming.ToolId("files", name, new HashSet<string>()));
        Assert.NotEqual(id, McpToolNaming.ToolId("files", name + "b", new HashSet<string>()));
    }

    [Fact]
    public void TwoNamesThatCleanToTheSameIdDoNotCollide()
    {
        HashSet<string> taken = [];
        string first = McpToolNaming.ToolId("s", "a.b", taken);
        taken.Add(first);
        string second = McpToolNaming.ToolId("s", "a b", taken);
        Assert.NotEqual(first, second);
        Assert.Matches(Valid, second);
    }
}

/// <summary>The protocol client against an in-memory server.</summary>
public class McpClientTests
{
    [Fact]
    public async Task StartUpSaysHelloThenInitializedAndReadsTheServersName()
    {
        FakeMcpTransport transport = new();
        McpClient client = new(transport);
        await client.InitializeAsync(CancellationToken.None);

        Assert.Equal("fake-server", client.ServerName);
        Assert.Equal(["initialize", "notifications/initialized"], transport.Seen.Select(m => m["method"]!.ToString()));
        Assert.Equal(McpClient.ProtocolVersion, transport.Seen[0]["params"]!["protocolVersion"]!.ToString());
        Assert.Null(transport.Seen[1]["id"]);
    }

    [Fact]
    public async Task ToolsAreListedWithTheirHintsAndAllPagesAreFollowed()
    {
        FakeMcpTransport transport = new() { Pages = 3, Tools = [.. Enumerable.Range(0, 7).Select(i => FakeMcpTransport.Tool($"t{i}", $"tool {i}", readOnly: i == 0, openWorld: i == 1))] };
        McpClient client = new(transport);
        await client.InitializeAsync(CancellationToken.None);
        List<McpTool> tools = await client.ListToolsAsync(CancellationToken.None);

        Assert.Equal(7, tools.Count);
        Assert.True(tools[0].ReadOnly);
        Assert.True(tools[1].OpenWorld);
        Assert.False(tools[2].ReadOnly);
        Assert.Equal(3, transport.Seen.Count(m => m["method"]?.ToString() == "tools/list"));
    }

    [Fact]
    public async Task AToolWithNoSchemaGetsAnEmptyObjectSchema()
    {
        FakeMcpTransport transport = new() { Tools = [new JObject { ["name"] = "bare" }] };
        McpClient client = new(transport);
        await client.InitializeAsync(CancellationToken.None);
        McpTool tool = (await client.ListToolsAsync(CancellationToken.None)).Single();
        Assert.Equal("object", tool.InputSchema["type"]!.ToString());
    }

    [Fact]
    public async Task ACallJoinsTextAndCountsWhatItDropsAndReportsErrors()
    {
        FakeMcpTransport transport = new()
        {
            OnCall = (_, args) => new JObject
            {
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "one" }, new JObject { ["type"] = "image", ["data"] = "x" }, new JObject { ["type"] = "text", ["text"] = "two" }),
                ["isError"] = args["fail"]?.Value<bool>() ?? false,
                ["structuredContent"] = new JObject { ["n"] = 1 },
            },
        };
        McpClient client = new(transport);
        await client.InitializeAsync(CancellationToken.None);

        McpCallResult ok = await client.CallToolAsync("t", new JObject(), CancellationToken.None);
        Assert.False(ok.IsError);
        Assert.Equal("one\ntwo", ok.Text);
        Assert.Equal(1, ok.OtherParts);
        Assert.Equal(1, ok.Structured!["n"]!.Value<int>());
        Assert.True((await client.CallToolAsync("t", new JObject { ["fail"] = true }, CancellationToken.None)).IsError);
    }

    [Fact]
    public async Task AJsonRpcErrorBecomesAnException()
    {
        FakeMcpTransport transport = new() { OnCall = (_, _) => throw new McpException(-32602, "bad arguments") };
        McpClient client = new(transport);
        await client.InitializeAsync(CancellationToken.None);
        McpException error = await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync("t", new JObject(), CancellationToken.None));
        Assert.Equal(-32602, error.Code);
        Assert.Equal("bad arguments", error.Message);
    }

    [Fact]
    public async Task TheServerSayingItsToolsChangedIsPassedOn()
    {
        FakeMcpTransport transport = new();
        McpClient client = new(transport);
        int changes = 0;
        client.ToolsChanged += () => changes++;
        transport.Notify("notifications/tools/list_changed");
        transport.Notify("notifications/message");
        Assert.Equal(1, changes);
        await Task.CompletedTask;
    }
}

/// <summary>The connections: connecting, tool definitions, failure and waiting, and calls.</summary>
public class McpServerManagerTests
{
    private sealed class Rig
    {
        public readonly List<McpServerConfig> Configs = [];
        public readonly List<FakeMcpTransport> Made = [];
        public Func<McpServerConfig, FakeMcpTransport> Build = _ => new FakeMcpTransport { Tools = [FakeMcpTransport.Tool("echo", "Echo text", readOnly: true), FakeMcpTransport.Tool("write", "Write", false), FakeMcpTransport.Tool("fetch", "Fetch", openWorld: true)] };
        public DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        public McpServerManager Manager;

        public Rig()
        {
            Manager = new McpServerManager(() => Configs, config =>
            {
                FakeMcpTransport transport = Build(config);
                Made.Add(transport);
                return new McpClient(transport);
            }, () => Now);
        }

        public McpServerConfig Add(string id = "srv", Action<McpServerConfig> shape = null)
        {
            McpServerConfig config = McpServerConfig.FromJson(new JObject { ["id"] = id, ["name"] = id.ToUpperInvariant(), ["transport"] = "stdio", ["command"] = "x" });
            shape?.Invoke(config);
            Configs.Add(config);
            return config;
        }
    }

    [Fact]
    public async Task ASwitchedOnServerConnectsAndOffersItsTools()
    {
        Rig rig = new();
        rig.Add();
        await rig.Manager.SyncAsync();

        McpConnection connection = Assert.Single(rig.Manager.Snapshot());
        Assert.Equal(McpState.Ready, connection.State);
        Assert.Equal("fake-server", connection.ServerName);
        List<JObject> defs = rig.Manager.ToolDefinitions();
        Assert.Equal(["mcp__srv__echo", "mcp__srv__fetch", "mcp__srv__write"], defs.Select(d => d["id"]!.ToString()).Order(StringComparer.Ordinal));
        JObject echo = defs.Single(d => d["id"]!.ToString() == "mcp__srv__echo");
        Assert.Equal("mcp", echo["handlerId"]!.ToString());
        Assert.Equal("srv", echo["server"]!.ToString());
        Assert.Equal("echo", echo["remoteName"]!.ToString());
        Assert.True(echo["isDynamic"]!.Value<bool>());
        Assert.Equal("object", echo["parameters"]!["type"]!.ToString());
    }

    [Fact]
    public async Task RiskComesFromTheServersHints()
    {
        Rig rig = new();
        rig.Add();
        await rig.Manager.SyncAsync();
        Dictionary<string, string> risks = rig.Manager.ToolDefinitions().ToDictionary(d => d["remoteName"]!.ToString(), d => d["risk"]!.ToString());
        Assert.Equal("read", risks["echo"]);
        Assert.Equal("network", risks["fetch"]);
        Assert.Equal("write", risks["write"]);
    }

    [Fact]
    public async Task ASwitchedOffToolIsNotOffered()
    {
        Rig rig = new();
        rig.Add(shape: c => c.DisabledTools = ["write"]);
        await rig.Manager.SyncAsync();
        Assert.DoesNotContain(rig.Manager.ToolDefinitions(), d => d["remoteName"]!.ToString() == "write");
        Assert.Contains(rig.Manager.Snapshot().Single().Tools, t => t.Name == "write");
    }

    [Fact]
    public async Task ADisabledServerIsNeverStarted()
    {
        Rig rig = new();
        rig.Add(shape: c => c.Enabled = false);
        await rig.Manager.SyncAsync();
        Assert.Empty(rig.Made);
        Assert.Equal(McpState.Disabled, rig.Manager.Snapshot().Single().State);
        Assert.Empty(rig.Manager.ToolDefinitions());
    }

    [Fact]
    public async Task ASecondSyncChangesNothingUntilTheConfigurationChanges()
    {
        Rig rig = new();
        McpServerConfig config = rig.Add();
        await rig.Manager.SyncAsync();
        await rig.Manager.SyncAsync();
        Assert.Single(rig.Made);

        config.Args = ["--different"];
        await rig.Manager.SyncAsync();
        Assert.Equal(2, rig.Made.Count);
        Assert.True(rig.Made[0].Disposed);
        Assert.False(rig.Made[1].Disposed);
    }

    [Fact]
    public async Task ChangesThatDoNotTouchHowTheServerIsReachedKeepTheConnection()
    {
        Rig rig = new();
        McpServerConfig config = rig.Add();
        await rig.Manager.SyncAsync();
        Assert.Equal(3, rig.Manager.ToolDefinitions().Count);

        config.Name = "Renamed";
        config.TimeoutSeconds = 90;
        config.DisabledTools = ["write"];
        await rig.Manager.SyncAsync();

        Assert.Single(rig.Made);
        Assert.False(rig.Made[0].Disposed);
        Assert.Equal(McpState.Ready, rig.Manager.Snapshot().Single().State);
        List<JObject> defs = rig.Manager.ToolDefinitions();
        Assert.Equal(2, defs.Count);
        Assert.DoesNotContain(defs, d => d["remoteName"]!.ToString() == "write");
        Assert.All(defs, d => Assert.Equal(90, d["timeoutSeconds"]!.Value<int>()));
        Assert.Equal("RENAMED", rig.Manager.Snapshot().Single().Config.Name.ToUpperInvariant());
    }

    [Fact]
    public async Task AConnectionReplacedWhileItIsStillStartingClosesWhatItStartedWhenItFinishes()
    {
        Rig rig = new();
        TaskCompletionSource release = new();
        int made = 0;
        rig.Build = _ => new FakeMcpTransport { Tools = [FakeMcpTransport.Tool("echo")], HoldInitialize = ++made == 1 ? release.Task : null };
        McpServerConfig config = rig.Add();
        Task firstAttempt = rig.Manager.SyncAsync();
        for (int i = 0; i < 100 && rig.Made.Count < 1; i++)
        {
            await Task.Delay(10);
        }

        // The admin changes how the server is reached while the first program is still starting.
        config.Args = ["--different"];
        await rig.Manager.SyncAsync();
        release.SetResult();
        await firstAttempt;

        Assert.Equal(2, rig.Made.Count);
        Assert.True(rig.Made[0].Disposed, "The replaced connection's program must be closed, not kept running with nothing referring to it.");
        Assert.False(rig.Made[1].Disposed);
        Assert.Equal(McpState.Ready, rig.Manager.Snapshot().Single().State);
        Assert.Equal(1, rig.Manager.ToolDefinitions().Count);
    }

    [Fact]
    public async Task AServerTheAdminDoesNotTrustIsAskedAboutEveryTime()
    {
        Rig rig = new();
        rig.Add(shape: c => c.AlwaysAsk = true);
        await rig.Manager.SyncAsync();
        Assert.All(rig.Manager.ToolDefinitions(), d => Assert.Equal("write", d["risk"]!.ToString()));
    }

    [Fact]
    public async Task ARemovedServerIsClosed()
    {
        Rig rig = new();
        rig.Add();
        await rig.Manager.SyncAsync();
        rig.Configs.Clear();
        await rig.Manager.SyncAsync();
        Assert.True(rig.Made[0].Disposed);
        Assert.Empty(rig.Manager.Snapshot());
    }

    [Fact]
    public async Task AServerThatFailsToStartIsRecordedAndWaitedOnLongerEachTime()
    {
        Rig rig = new();
        rig.Build = _ => new FakeMcpTransport { InitializeFailure = new IOException("boom") };
        rig.Add();
        await rig.Manager.SyncAsync();

        McpConnection connection = rig.Manager.Snapshot().Single();
        Assert.Equal(McpState.Failed, connection.State);
        Assert.Contains("boom", connection.Error);
        Assert.Equal(rig.Now + TimeSpan.FromSeconds(2), connection.NextAttempt);
        Assert.Equal(TimeSpan.FromSeconds(4), McpServerManager.BackoffFor(2));
        Assert.Equal(McpServerManager.MaxBackoff, McpServerManager.BackoffFor(20));
    }

    [Fact]
    public async Task ACallToAFailedServerWaitsOutItsBackoffThenReconnects()
    {
        Rig rig = new();
        int attempt = 0;
        rig.Build = _ => ++attempt == 1
            ? new FakeMcpTransport { InitializeFailure = new IOException("down") }
            : new FakeMcpTransport { Tools = [FakeMcpTransport.Tool("echo")] };
        rig.Add();
        await rig.Manager.SyncAsync();

        SwarmUI.Utils.SwarmReadableErrorException early = await Assert.ThrowsAsync<SwarmUI.Utils.SwarmReadableErrorException>(
            () => rig.Manager.CallAsync("mcp__srv__echo", new JObject(), TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Contains("not available", early.Message);
        Assert.Contains("tried again", early.Message);

        rig.Now += TimeSpan.FromSeconds(3);
        McpCallResult result = await rig.Manager.CallAsync("mcp__srv__echo", new JObject(), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("called echo", result.Text);
        Assert.Equal(McpState.Ready, rig.Manager.Snapshot().Single().State);
    }

    [Fact]
    public async Task ACallPassesTheRemoteNameAndArguments()
    {
        Rig rig = new();
        rig.Add();
        await rig.Manager.SyncAsync();
        McpCallResult result = await rig.Manager.CallAsync("mcp__srv__echo", new JObject { ["text"] = "hi" }, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("called echo", result.Text);
        JObject call = rig.Made[0].Seen.Single(m => m["method"]?.ToString() == "tools/call");
        Assert.Equal("echo", call["params"]!["name"]!.ToString());
        Assert.Equal("hi", call["params"]!["arguments"]!["text"]!.ToString());
    }

    [Fact]
    public async Task ACallThatRunsPastItsTimeIsGivenUpOn()
    {
        Rig rig = new();
        TaskCompletionSource never = new();
        rig.Build = _ => new FakeMcpTransport { Tools = [FakeMcpTransport.Tool("slow")], HoldCalls = never.Task };
        rig.Add();
        await rig.Manager.SyncAsync();
        SwarmUI.Utils.SwarmReadableErrorException error = await Assert.ThrowsAsync<SwarmUI.Utils.SwarmReadableErrorException>(
            () => rig.Manager.CallAsync("mcp__srv__slow", new JObject(), TimeSpan.FromMilliseconds(200), CancellationToken.None));
        Assert.Contains("did not finish", error.Message);
        // A timeout is not a dropped connection: the server is still there.
        Assert.Equal(McpState.Ready, rig.Manager.Snapshot().Single().State);
    }

    [Fact]
    public async Task StopCancelsACallAndIsNotReportedAsATimeout()
    {
        Rig rig = new();
        TaskCompletionSource never = new();
        rig.Build = _ => new FakeMcpTransport { Tools = [FakeMcpTransport.Tool("slow")], HoldCalls = never.Task };
        rig.Add();
        await rig.Manager.SyncAsync();
        using CancellationTokenSource stop = new(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Manager.CallAsync("mcp__srv__slow", new JObject(), TimeSpan.FromSeconds(30), stop.Token));
    }

    [Fact]
    public async Task AServerWhoseProgramHasExitedIsReconnectedByTheNextCall()
    {
        Rig rig = new();
        rig.Add();
        await rig.Manager.SyncAsync();
        rig.Made[0].Drop();

        McpCallResult result = await rig.Manager.CallAsync("mcp__srv__echo", new JObject(), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("called echo", result.Text);
        Assert.Equal(2, rig.Made.Count);
        Assert.True(rig.Made[0].Disposed);
    }

    [Fact]
    public async Task AConnectionThatBreaksUnderACallFailsItAndWaitsBeforeTryingAgain()
    {
        Rig rig = new();
        rig.Build = _ => new FakeMcpTransport { Tools = [FakeMcpTransport.Tool("echo")], OnCall = (_, _) => throw new IOException("pipe closed") };
        rig.Add();
        await rig.Manager.SyncAsync();

        await Assert.ThrowsAsync<SwarmUI.Utils.SwarmReadableErrorException>(() => rig.Manager.CallAsync("mcp__srv__echo", new JObject(), TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(McpState.Failed, rig.Manager.Snapshot().Single().State);
        SwarmUI.Utils.SwarmReadableErrorException soon = await Assert.ThrowsAsync<SwarmUI.Utils.SwarmReadableErrorException>(
            () => rig.Manager.CallAsync("mcp__srv__echo", new JObject(), TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Contains("tried again", soon.Message);
        Assert.Single(rig.Made);

        rig.Now += TimeSpan.FromMinutes(5);
        await Assert.ThrowsAsync<SwarmUI.Utils.SwarmReadableErrorException>(() => rig.Manager.CallAsync("mcp__srv__echo", new JObject(), TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(2, rig.Made.Count);
    }

    [Fact]
    public async Task ToolsTheServerAddsLaterAppearAfterItSaysSo()
    {
        Rig rig = new();
        rig.Add();
        await rig.Manager.SyncAsync();
        Assert.Equal(3, rig.Manager.ToolDefinitions().Count);

        rig.Made[0].Tools.Add(FakeMcpTransport.Tool("later"));
        rig.Made[0].Notify("notifications/tools/list_changed");
        for (int i = 0; i < 50 && rig.Manager.ToolDefinitions().Count < 4; i++)
        {
            await Task.Delay(20);
        }
        Assert.Equal(4, rig.Manager.ToolDefinitions().Count);
    }

    [Fact]
    public async Task AnUnknownToolIsAnErrorNotACrash()
    {
        Rig rig = new();
        rig.Add();
        await rig.Manager.SyncAsync();
        await Assert.ThrowsAsync<SwarmUI.Utils.SwarmReadableErrorException>(() => rig.Manager.CallAsync("mcp__nope__x", new JObject(), TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task ReconnectingBringsAFailedServerBackWhateverItsWait()
    {
        Rig rig = new();
        int attempt = 0;
        rig.Build = _ => ++attempt == 1 ? new FakeMcpTransport { InitializeFailure = new IOException("down") } : new FakeMcpTransport { Tools = [FakeMcpTransport.Tool("echo")] };
        rig.Add();
        await rig.Manager.SyncAsync();
        Assert.Equal(McpState.Failed, rig.Manager.Snapshot().Single().State);
        await rig.Manager.RefreshAsync("srv");
        Assert.Equal(McpState.Ready, rig.Manager.Snapshot().Single().State);
    }

    [Fact]
    public async Task ManyServersKeepTheirOwnToolsApart()
    {
        Rig rig = new();
        rig.Add("one");
        rig.Add("two");
        await rig.Manager.SyncAsync();
        List<string> ids = [.. rig.Manager.ToolDefinitions().Select(d => d["id"]!.ToString())];
        Assert.Equal(6, ids.Count);
        Assert.Equal(6, ids.Distinct().Count());
        Assert.Contains("mcp__one__echo", ids);
        Assert.Contains("mcp__two__echo", ids);
    }
}

/// <summary>How a server's answer becomes the result the model reads, and what the handler says about risk and time.</summary>
public class McpToolHandlerTests
{
    [Fact]
    public void ASuccessfulResultCarriesItsText()
    {
        JObject result = McpToolHandler.ToResult(new McpCallResult(false, "hello", null, 0));
        Assert.True(result["success"]!.Value<bool>());
        Assert.Equal("hello", result["text"]!.ToString());
        Assert.Null(result["error"]);
        Assert.Null(result["note"]);
    }

    [Fact]
    public void AToolErrorIsAFailureWithItsMessage()
    {
        JObject result = McpToolHandler.ToResult(new McpCallResult(true, "no such file", null, 0));
        Assert.False(result["success"]!.Value<bool>());
        Assert.Equal("no such file", result["error"]!.ToString());
    }

    [Fact]
    public void LongTextIsCutAndPartsThatAreNotTextAreSaidToBeLeftOut()
    {
        JObject result = McpToolHandler.ToResult(new McpCallResult(false, new string('x', McpToolHandler.MaxResultChars + 10), new JObject { ["a"] = 1 }, 2));
        Assert.Equal(McpToolHandler.MaxResultChars, result["text"]!.ToString().Length);
        Assert.Contains("cut", result["note"]!.ToString());
        Assert.Contains("2 part(s)", result["note"]!.ToString());
        Assert.Equal(1, result["structured"]!["a"]!.Value<int>());
    }

    [Fact]
    public void RiskAndTimeComeFromTheToolDefinition()
    {
        McpToolHandler handler = new();
        Assert.Equal(ToolRisk.Read, handler.RiskFor(new JObject { ["risk"] = "read" }));
        Assert.Equal(ToolRisk.Network, handler.RiskFor(new JObject { ["risk"] = "network" }));
        Assert.Equal(ToolRisk.Write, handler.RiskFor(new JObject { ["risk"] = "write" }));
        Assert.Equal(ToolRisk.Write, handler.RiskFor(new JObject()));
        Assert.Equal(ToolRisk.Write, handler.RiskFor(null));
        // The executor's limit is the server's own plus a grace period, so the call gives up first and can say why.
        Assert.Equal(TimeSpan.FromSeconds(120), McpToolHandler.CallLimit(new JObject { ["timeoutSeconds"] = 120 }));
        Assert.Equal(TimeSpan.FromSeconds(125), handler.TimeoutFor(new JObject { ["timeoutSeconds"] = 120 }));
        Assert.Equal(TimeSpan.FromSeconds(65), handler.TimeoutFor(new JObject()));
        Assert.Equal(TimeSpan.FromSeconds(McpServerConfig.MaxTimeoutSeconds), McpToolHandler.CallLimit(new JObject { ["timeoutSeconds"] = 999999 }));
    }

    [Fact]
    public void OtherHandlersKeepTheirOwnFixedRiskAndTimeout()
    {
        ToolHandler memoryRead = new MemoryReadTool();
        Assert.Equal(memoryRead.Risk, memoryRead.RiskFor(new JObject { ["risk"] = "network" }));
        Assert.Equal(TimeSpan.FromSeconds(60), memoryRead.TimeoutFor(null));
    }
}

/// <summary>How a source's tools join the list.</summary>
public class ToolSourcesTests
{
    private sealed class FixedSource(string id, params JObject[] tools) : IToolSource
    {
        public string Id => id;

        public IEnumerable<JObject> GetTools(User user) => tools.Select(t => (JObject)t.DeepClone());
    }

    private sealed class BrokenSource : IToolSource
    {
        public string Id => "broken";

        public IEnumerable<JObject> GetTools(User user) => throw new InvalidOperationException("down");
    }

    [Fact]
    public void ASourcesToolsAreAddedAndMarked()
    {
        ToolSources.Register(new FixedSource("test-a", new JObject { ["id"] = "src_tool", ["name"] = "T" }));
        JObject tools = new() { ["stored"] = new JObject { ["id"] = "stored" } };
        ToolSources.AddTo(tools, null);
        Assert.Equal("source", tools["src_tool"]!["_scope"]!.ToString());
        Assert.NotNull(tools["stored"]);
        ToolSources.Register(new FixedSource("test-a"));
    }

    [Fact]
    public void ASourcesToolReplacesAStoredOneWithTheSameId()
    {
        ToolSources.Register(new FixedSource("test-b", new JObject { ["id"] = "clash", ["handlerId"] = "mcp", ["from"] = "source" }));
        JObject tools = new() { ["clash"] = new JObject { ["id"] = "clash", ["handlerId"] = "shell_exec", ["from"] = "stored" } };
        ToolSources.AddTo(tools, null);
        Assert.Equal("source", tools["clash"]!["from"]!.ToString());
        ToolSources.Register(new FixedSource("test-b"));
    }

    [Fact]
    public void ABrokenSourceOffersNothingAndDoesNotBreakTheList()
    {
        ToolSources.Register(new BrokenSource());
        JObject tools = new() { ["stored"] = new JObject { ["id"] = "stored" } };
        ToolSources.AddTo(tools, null);
        Assert.Single(tools.Properties());
        ToolSources.Register(new FixedSource("broken"));
    }
}

/// <summary>The HTTP transport against a fake endpoint.</summary>
public class McpHttpTransportTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }

    private static McpServerConfig Config() => McpServerConfig.FromJson(new JObject
    {
        ["id"] = "w", ["name"] = "W", ["transport"] = "http", ["url"] = "https://mcp.example.com/mcp", ["headers"] = new JObject { ["Authorization"] = "Bearer t" },
    });

    private static JObject Request(long id, string method = "tools/list") => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = new JObject() };

    private static HttpResponseMessage Json(string body, Action<HttpResponseMessage> shape = null)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        shape?.Invoke(response);
        return response;
    }

    [Fact]
    public async Task ItPostsWithTheConfiguredHeadersAndKeepsTheSessionAndVersion()
    {
        Handler handler = new(request => Json("""{"jsonrpc":"2.0","id":1,"result":{}}""", r => r.Headers.Add("Mcp-Session-Id", "abc123")));
        McpHttpTransport transport = new(Config(), new HttpClient(handler));
        transport.SetProtocolVersion("2025-06-18");

        await transport.RequestAsync(Request(1, "initialize"), CancellationToken.None);
        await transport.RequestAsync(Request(2), CancellationToken.None);

        Assert.All(handler.Requests, r => Assert.Equal("Bearer t", r.Headers.GetValues("Authorization").Single()));
        Assert.Contains("text/event-stream", string.Join(",", handler.Requests[0].Headers.GetValues("Accept")));
        Assert.False(handler.Requests[0].Headers.Contains("Mcp-Session-Id"));
        Assert.Equal("abc123", handler.Requests[1].Headers.GetValues("Mcp-Session-Id").Single());
        Assert.Equal("2025-06-18", handler.Requests[1].Headers.GetValues("MCP-Protocol-Version").Single());
    }

    [Fact]
    public async Task AnEventStreamAnswerIsReadForTheMatchingResponse()
    {
        string sse = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\"}\n\nevent: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{\"ok\":true}}\n\n";
        Handler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        McpHttpTransport transport = new(Config(), new HttpClient(handler));
        JObject response = await transport.RequestAsync(Request(7), CancellationToken.None);
        Assert.True(response["result"]!["ok"]!.Value<bool>());
    }

    [Fact]
    public async Task ANon2xxAnswerIsAnErrorWithTheServersWords()
    {
        Handler handler = new(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("bad token") });
        McpHttpTransport transport = new(Config(), new HttpClient(handler));
        IOException error = await Assert.ThrowsAsync<IOException>(() => transport.RequestAsync(Request(1), CancellationToken.None));
        Assert.Contains("401", error.Message);
        Assert.Contains("bad token", error.Message);
    }

    [Fact]
    public async Task ANotificationIsPostedWithNoId()
    {
        Handler handler = new(_ => new HttpResponseMessage(HttpStatusCode.Accepted));
        McpHttpTransport transport = new(Config(), new HttpClient(handler));
        await transport.NotifyAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, CancellationToken.None);
        Assert.DoesNotContain("\"id\"", handler.Bodies.Single());
    }
}
