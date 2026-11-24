using System.Net;
using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.LLMAssistant.Backends;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Newtonsoft.Json.Linq;
using Xunit;
using RemoteSettings = Hartsy.Extensions.LLMAssistant.Backends.RemoteOpenAILLMProvider.RemoteOpenAILLMProviderSettings;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Claude runs through the Remote (OpenAI-Compatible) backend pointed at Anthropic's API: the request leaves out the sampling
/// settings newer Claude models reject, the model list authenticates the way Anthropic's Models API wants, and backends saved under the
/// removed Anthropic type come back as this backend.</summary>
public class AnthropicCompatTests
{
    private static RemoteOpenAILLMProvider Provider(string address, string authorization = "", string otherHeaders = "") => new()
    {
        SettingsRaw = new RemoteSettings { Address = address, DefaultModel = "claude-opus-4-8", AuthorizationHeader = authorization, OtherHeaders = otherHeaders }
    };

    [Fact]
    public void ARefusedModelList_SaysWhatTheEndpointAnswered()
    {
        string anthropic = """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"},"request_id":"req_1"}""";
        Assert.Equal("HTTP 401 Unauthorized: invalid x-api-key", RemoteOpenAILLMProvider.DescribeListFailure(HttpStatusCode.Unauthorized, anthropic));
        Assert.Equal("HTTP 403 Forbidden: Incorrect API key", RemoteOpenAILLMProvider.DescribeListFailure(HttpStatusCode.Forbidden, """{"error":{"message":"Incorrect API key"}}"""));
        Assert.Equal("HTTP 401 Unauthorized: bad token", RemoteOpenAILLMProvider.DescribeListFailure(HttpStatusCode.Unauthorized, """{"error":"bad token"}"""));
        Assert.Equal("HTTP 401 Unauthorized: plain words", RemoteOpenAILLMProvider.DescribeListFailure(HttpStatusCode.Unauthorized, "plain words"));
        // Nothing to say, or a page that is not an answer: the status alone.
        Assert.Equal("HTTP 401 Unauthorized", RemoteOpenAILLMProvider.DescribeListFailure(HttpStatusCode.Unauthorized, ""));
        Assert.Equal("HTTP 502 BadGateway", RemoteOpenAILLMProvider.DescribeListFailure(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        string cut = RemoteOpenAILLMProvider.DescribeListFailure(HttpStatusCode.Forbidden, "{\"error\":{\"message\":\"" + new string('x', 500) + "\"}}");
        Assert.Equal("HTTP 403 Forbidden: ".Length + 203, cut.Length);
        Assert.EndsWith("...", cut);
    }

    [Fact]
    public void ARefusedKey_ReachesTheUserAsAReadableErrorNamingWhoRefusedIt()
    {
        Assert.Equal("Anthropic rejected the API key (HTTP 401 Unauthorized: invalid x-api-key). Check the key saved in the User tab (and the workspace it belongs to).",
            RemoteOpenAILLMProvider.RejectedKeyMessage(true, "HTTP 401 Unauthorized: invalid x-api-key"));
        Assert.StartsWith("The server rejected the API key (HTTP 403 Forbidden)", RemoteOpenAILLMProvider.RejectedKeyMessage(false, "HTTP 403 Forbidden"));
    }

    [Theory]
    [InlineData("https://api.anthropic.com", true)]
    [InlineData("https://API.Anthropic.com/", true)]
    [InlineData("https://anthropic.com", true)]
    [InlineData("https://api.openai.com", false)]
    [InlineData("https://anthropic.com.example.net", false)]
    [InlineData("http://localhost:11434", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAnthropicAddress_MatchesOnlyAnthropicsHost(string address, bool expected)
    {
        Assert.Equal(expected, RemoteOpenAILLMProvider.IsAnthropicAddress(address));
    }

    [Fact]
    public void NewerClaudeModels_GetNoSamplingSettings()
    {
        ExtendedLLMInput input = new() { Model = "claude-opus-5-5", Temperature = 0.7, TopP = 0.9 };
        input.Messages.Add(new LLMMessage { Role = LLMRoles.User, Content = "hi" });
        JObject body = Provider("https://api.anthropic.com").BuildRequestBody(input, stream: true);
        Assert.Null(body["temperature"]);
        Assert.Null(body["top_p"]);
    }

    [Fact]
    public void OlderClaudeModels_GetTemperatureAloneCappedAtOne()
    {
        ExtendedLLMInput input = new() { Model = "claude-sonnet-4-6", Temperature = 1.5, TopP = 0.9 };
        input.Messages.Add(new LLMMessage { Role = LLMRoles.User, Content = "hi" });
        JObject body = Provider("https://api.anthropic.com").BuildRequestBody(input, stream: true);
        Assert.Equal(1.0, body.Value<double>("temperature"));
        Assert.Null(body["top_p"]);
    }

    [Fact]
    public void OtherServers_KeepBothSamplingSettings()
    {
        ExtendedLLMInput input = new() { Model = "llama3.2", Temperature = 0.7, TopP = 0.9 };
        input.Messages.Add(new LLMMessage { Role = LLMRoles.User, Content = "hi" });
        JObject body = Provider("http://localhost:11434").BuildRequestBody(input, stream: true);
        Assert.Equal(0.7, body.Value<double>("temperature"));
        Assert.Equal(0.9, body.Value<double>("top_p"));
    }

    [Fact]
    public void AnthropicChat_SendsTheKeyAsABearerToken()
    {
        HttpRequestMessage request = new(HttpMethod.Post, "https://api.anthropic.com/v1/chat/completions");
        Provider("https://api.anthropic.com", "Bearer sk-ant-test").ApplyHeaders(request);
        Assert.Equal("Bearer sk-ant-test", request.Headers.GetValues("Authorization").Single());
        Assert.False(request.Headers.Contains("x-api-key"));
    }

    [Fact]
    public void AnthropicModelList_SendsTheKeyAsXApiKeyWithAVersion()
    {
        HttpRequestMessage request = new(HttpMethod.Get, "https://api.anthropic.com/v1/models");
        Provider("https://api.anthropic.com", "Bearer sk-ant-test", "anthropic-workspace-id: wrkspc_1").ApplyHeaders(request, null, true);
        Assert.Equal("sk-ant-test", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal(RemoteOpenAILLMProvider.AnthropicVersion, request.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("wrkspc_1", request.Headers.GetValues("anthropic-workspace-id").Single());
        Assert.False(request.Headers.Contains("Authorization"));
    }

    [Fact]
    public void OtherServersModelList_KeepsTheBearerToken()
    {
        HttpRequestMessage request = new(HttpMethod.Get, "https://api.openai.com/v1/models");
        Provider("https://api.openai.com", "Bearer sk-test").ApplyHeaders(request, null, true);
        Assert.Equal("Bearer sk-test", request.Headers.GetValues("Authorization").Single());
        Assert.False(request.Headers.Contains("x-api-key"));
        Assert.False(request.Headers.Contains("anthropic-version"));
    }

    [Fact]
    public void Migration_TurnsAnOldAnthropicBackendIntoARemoteOneAtAnthropic()
    {
        FDSSection file = new("""
            0:
                type: llmassistant-anthropic
                title: Claude
                enabled: true
                settings:
                    DefaultModel: claude-sonnet-4-6
                    TimeoutSeconds: 300
                    WorkspaceId: wrkspc_1
                    BaseUrl: https://api.anthropic.com
            1:
                type: llmassistant-openai
                title: Ollama
                enabled: false
                settings:
                    Address: http://localhost:11434
            """);
        Assert.Equal(1, AnthropicBackendMigration.ConvertAll(file));
        FDSSection claude = file.GetSection("0");
        Assert.Equal(AnthropicBackendMigration.NewTypeId, claude.GetString("type"));
        Assert.Equal("Claude", claude.GetString("title"));
        Assert.True(claude.GetBool("enabled", false).Value);
        RemoteSettings settings = new();
        settings.Load(claude.GetSection("settings"));
        Assert.Equal("https://api.anthropic.com", settings.Address);
        Assert.Equal("claude-sonnet-4-6", settings.DefaultModel);
        Assert.Equal("anthropic-workspace-id: wrkspc_1", settings.OtherHeaders);
        Assert.Equal("", settings.AuthorizationHeader);
        Assert.Equal("http://localhost:11434", file.GetSection("1").GetSection("settings").GetString("Address"));
        Assert.Equal(0, AnthropicBackendMigration.ConvertAll(file));
    }

    [Fact]
    public void Migration_FillsInTheOldDefaultsForAnEmptyBackend()
    {
        RemoteSettings settings = AnthropicBackendMigration.ConvertSettings(null);
        Assert.Equal("https://api.anthropic.com", settings.Address);
        Assert.Equal("claude-opus-4-8", settings.DefaultModel);
        Assert.Equal("", settings.OtherHeaders);
    }
}
