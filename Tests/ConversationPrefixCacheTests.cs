using Hartsy.Extensions.LLMAssistant.Backends;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using HartsyInference.Engine;
using HartsyInference.Engine.Dispatch;
using HartsyInference.Engine.Requests;
using HartsyInference.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Pins the local provider's per-conversation prefix-KV keying: which requests carry
/// <see cref="TextRequest.PrefixCacheKey"/>, what scopes it (user, conversation, model), how
/// <see cref="TextRequest.PrefixCacheCapacityHint"/> is sized, that every tool round keeps the turn's key, and that
/// no other provider's request ever carries the conversation scope. Everything here goes through the same pure
/// pieces the routes use — <see cref="HartsyLocalLLMProvider.BuildRequestCore"/> and the
/// <see cref="ChatEndpoints"/> scope helpers — since the routes themselves need a live <c>Session</c>/host (see
/// <see cref="HartsyLocalLLMProviderTests"/>' own class doc).</summary>
public class ConversationPrefixCacheTests
{
    private const string Model = "Qwen3-4B-Q4_K_M.gguf";

    private static ExtendedLLMInput Turn(string conversationId, string model = Model, params (string Role, string Content)[] history)
    {
        ExtendedLLMInput input = new() { Model = model, MaxTokens = 1024, Temperature = 0.7, ConversationId = conversationId };
        foreach ((string role, string content) in history)
        {
            input.Messages.Add(new LLMMessage { Role = role, Content = content });
        }
        return input;
    }

    private static List<TextMessage> Messages(ExtendedLLMInput input)
        => [.. input.Messages.Select(m => HartsyLocalLLMProvider.ToTextMessage(m, m.Content, images: null))];

    private static TextRequest Build(ExtendedLLMInput input, string userId, HartsyLocalLLMProvider.HartsyLocalLLMProviderSettings settings = null,
        Func<int> estimatePromptTokens = null, List<ToolDefinition> tools = null)
        => HartsyLocalLLMProvider.BuildRequestCore(input, Messages(input), tools, deviceKey: "cuda:0", settings ?? new(), userId, estimatePromptTokens);

    private static string KeyFor(string userId, string conversationId, string model = Model)
        => Build(Turn(conversationId, model, (LLMRoles.User, "hi")), userId).PrefixCacheKey;

    [Fact]
    public void ThreadKey_IsSetAndStableAcrossTurnsOfOneConversation()
    {
        string thread = ChatEndpoints.ThreadConversationId("6f1d0c2b9a8e4f0f9d6c3b2a1e0f9d8c");
        TextRequest turn1 = Build(Turn(thread, Model, (LLMRoles.System, "Be helpful."), (LLMRoles.User, "hi")), "alice");
        TextRequest turn2 = Build(Turn(thread, Model, (LLMRoles.System, "Be helpful."), (LLMRoles.User, "hi"),
            (LLMRoles.Assistant, "Hello! How can I help?"), (LLMRoles.User, "What is 2+2?")), "alice");
        Assert.NotNull(turn1.PrefixCacheKey);
        Assert.Equal(turn1.PrefixCacheKey, turn2.PrefixCacheKey);
    }

    [Fact]
    public void Key_DiffersAcrossConversations()
    {
        Assert.NotEqual(KeyFor("alice", ChatEndpoints.ThreadConversationId("thread-a")), KeyFor("alice", ChatEndpoints.ThreadConversationId("thread-b")));
    }

    [Fact]
    public void Key_DiffersAcrossUsers_EvenForTheSameConversationId()
    {
        // A conversation id is only unique per user (and on the WS voice route the client picks it), so the
        // user is what keeps one user's conversation from ever mapping onto another's.
        string thread = ChatEndpoints.ThreadConversationId("shared-id");
        Assert.NotEqual(KeyFor("alice", thread), KeyFor("bob", thread));
        string voice = ChatEndpoints.VoiceTurnWsConversationId("call-1", sessionId: null);
        Assert.NotEqual(KeyFor("alice", voice), KeyFor("bob", voice));
    }

    [Fact]
    public void Key_DiffersAcrossModels_ButNotAcrossTheModelIdsCase()
    {
        // Compare-mode lanes share the thread's conversation id; the model keeps each lane's entry apart.
        string thread = ChatEndpoints.ThreadConversationId("t1");
        Assert.NotEqual(KeyFor("alice", thread, "Qwen3-4B-Q4_K_M.gguf"), KeyFor("alice", thread, "Llama-3.2-1B-Instruct-Q8_0.gguf"));
        // ResolvePath matches model ids case-insensitively, so the same file must not get two entries.
        Assert.Equal(KeyFor("alice", thread, "Qwen3-4B-Q4_K_M.gguf"), KeyFor("alice", thread, "qwen3-4b-q4_k_m.gguf"));
    }

    [Fact]
    public void Key_DiffersAcrossRouteScopes_ThatShareAnId()
    {
        string[] scopes =
        [
            ChatEndpoints.ThreadConversationId("x"),
            ChatEndpoints.VoiceTurnConversationId("x"),
            ChatEndpoints.VoiceTurnWsConversationId("x", sessionId: null),
            ChatEndpoints.VoiceTurnWsConversationId(null, sessionId: "x")
        ];
        Assert.Equal(scopes.Length, scopes.Select(s => KeyFor("alice", s)).Distinct().Count());
    }

    [Fact]
    public void Key_IsAnOpaqueHash_NotTheRawIds()
    {
        // The WS voice route's fallback scope is a SwarmUI session id, which is a credential.
        const string sessionId = "0123456789abcdef0123456789abcdef";
        string key = KeyFor("alice", ChatEndpoints.VoiceTurnWsConversationId(null, sessionId));
        Assert.Matches("^llmassistant:[0-9a-f]{32}$", key);
        Assert.DoesNotContain(sessionId, key);
        Assert.DoesNotContain("alice", key);
    }

    [Fact]
    public void Key_LengthPrefixesEachPart_SoShiftingASeparatorChangesTheKey()
    {
        // Joined naively with '|', both of these would hash "alice|thread:x|y|model".
        Assert.NotEqual(HartsyLocalLLMProvider.PrefixCacheKeyFor("alice|thread:x", "y", Model),
            HartsyLocalLLMProvider.PrefixCacheKeyFor("alice", "thread:x|y", Model));
    }

    [Theory]
    [InlineData(null, "thread:t1", Model)]
    [InlineData("alice", null, Model)]
    [InlineData("alice", "thread:t1", null)]
    [InlineData(" ", "thread:t1", Model)]
    [InlineData("alice", " ", Model)]
    public void KeyFor_IsNullUnlessUserConversationAndModelAreAllPresent(string userId, string conversationId, string model)
    {
        Assert.Null(HartsyLocalLLMProvider.PrefixCacheKeyFor(userId, conversationId, model));
    }

    [Fact]
    public void OneOffCall_WithNoConversation_CarriesNoKeyAndNeverPaysForAnEstimate()
    {
        // Titles, prompt enhancement, a tool's own caption request: none sets ConversationId, so none may touch
        // (and truncate) a conversation's retained entry, or tokenize its prompt for nothing.
        bool estimated = false;
        TextRequest request = Build(Turn(conversationId: null, Model, (LLMRoles.User, "Write a title for this chat.")), "alice",
            estimatePromptTokens: () => { estimated = true; return 100; });
        Assert.Null(request.PrefixCacheKey);
        Assert.Null(request.PrefixCacheCapacityHint);
        Assert.False(estimated);
    }

    [Fact]
    public void NoUser_CarriesNoKey()
    {
        Assert.Null(Build(Turn(ChatEndpoints.ThreadConversationId("t1"), Model, (LLMRoles.User, "hi")), userId: null).PrefixCacheKey);
    }

    [Fact]
    public void ReuseConversationPrefixOff_CarriesNoKey()
    {
        TextRequest request = Build(Turn(ChatEndpoints.ThreadConversationId("t1"), Model, (LLMRoles.User, "hi")), "alice",
            new() { ReuseConversationPrefix = false });
        Assert.Null(request.PrefixCacheKey);
        Assert.Null(request.PrefixCacheCapacityHint);
    }

    [Fact]
    public void AlwaysFreeMemory_CarriesNoKey()
    {
        // The slot (and the engine's prefix store with it) is unloaded after every request, so nothing could be
        // reused; a key would only make each request allocate a bigger KV cache than it needs.
        TextRequest request = Build(Turn(ChatEndpoints.ThreadConversationId("t1"), Model, (LLMRoles.User, "hi")), "alice",
            new() { AlwaysFreeMemory = true });
        Assert.Null(request.PrefixCacheKey);
        Assert.Null(request.PrefixCacheCapacityHint);
        Assert.True(request.AlwaysFreeMemory);
    }

    [Fact]
    public void CapacityHint_IsThePromptEstimatePlusMaxTokensPlusHeadroom()
    {
        TextRequest request = Build(Turn(ChatEndpoints.ThreadConversationId("t1"), Model, (LLMRoles.User, "hi")), "alice",
            estimatePromptTokens: () => 700);
        Assert.Equal(1024, request.MaxTokens);
        Assert.Equal(700 + 1024 + HartsyLocalLLMProvider.PrefixCacheHeadroomTokens, request.PrefixCacheCapacityHint);
    }

    [Fact]
    public void CapacityHint_UsesTheDefaultReplyBudgetWhenMaxTokensIsUnset()
    {
        ExtendedLLMInput input = Turn(ChatEndpoints.ThreadConversationId("t1"), Model, (LLMRoles.User, "hi"));
        input.MaxTokens = 0;
        TextRequest request = Build(input, "alice", estimatePromptTokens: () => 50);
        Assert.Equal(4096, request.MaxTokens);
        Assert.Equal(50 + 4096 + HartsyLocalLLMProvider.PrefixCacheHeadroomTokens, request.PrefixCacheCapacityHint);
    }

    [Fact]
    public void CapacityHint_WithNoEstimator_StillReservesTheReplyAndHeadroom()
    {
        TextRequest request = Build(Turn(ChatEndpoints.ThreadConversationId("t1"), Model, (LLMRoles.User, "hi")), "alice");
        Assert.Equal(1024 + HartsyLocalLLMProvider.PrefixCacheHeadroomTokens, request.PrefixCacheCapacityHint);
    }

    [Theory]
    [InlineData(-5, 100, 100 + HartsyLocalLLMProvider.PrefixCacheHeadroomTokens)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue)]
    public void PrefixCacheCapacityFor_ClampsANegativeEstimateAndSaturates(int promptTokens, int maxTokens, int expected)
    {
        Assert.Equal(expected, HartsyLocalLLMProvider.PrefixCacheCapacityFor(promptTokens, maxTokens));
    }

    [Fact]
    public void PromptTextForEstimate_CoversContentToolCallsAndNativeToolSchemas()
    {
        List<TextMessage> messages =
        [
            new() { Role = TextRole.User, Content = "what time is it?" },
            new() { Role = TextRole.Assistant, Content = "", ToolCalls = [new NativeToolCall { Id = "c1", Name = "get_time", Arguments = "{\"tz\":\"UTC\"}" }] }
        ];
        List<ToolDefinition> tools = [new() { Name = "get_time", Description = "Gets the current time", JsonSchema = "{\"type\":\"object\"}" }];
        string text = HartsyLocalLLMProvider.PromptTextForEstimate(messages, tools);
        Assert.Contains("what time is it?", text);
        Assert.Contains("{\"tz\":\"UTC\"}", text);
        Assert.Contains("Gets the current time", text);
        Assert.Contains("{\"type\":\"object\"}", text);
    }

    [Fact]
    public async Task ToolLoopRounds_AllCarryTheTurnsKeyAndHint()
    {
        // LLMAssistantVoiceTurnWS: StreamToolLoopAsync builds one TextRequest per turn and ToolLoop re-issues it
        // (with the tool call and its result appended) for every round.
        ExtendedLLMInput input = Turn(ChatEndpoints.VoiceTurnWsConversationId("call-1", sessionId: null), Model,
            (LLMRoles.System, "You are a phone assistant."), (LLMRoles.User, "what time is it?"));
        List<ToolDefinition> tools = [new() { Name = "get_time", Description = "Gets the current time" }];
        TextRequest request = Build(input, "alice", estimatePromptTokens: () => 300, tools: tools);
        ScriptedTextService fake = new(
            [
                new TextChunk { Kind = TextChunkKind.NativeToolCall, ToolCall = new NativeToolCall { Id = "c1", Name = "get_time", Arguments = "{}" } },
                new TextChunk { Kind = TextChunkKind.StopReason, Stop = StopReason.ToolCall }
            ],
            [
                new TextChunk { Kind = TextChunkKind.Chunk, Text = "It is noon." },
                new TextChunk { Kind = TextChunkKind.StopReason, Stop = StopReason.Stop }
            ]);
        ToolRegistry registry = new ToolRegistry().Add("get_time", "Gets the current time", "{}", (_, _) => Task.FromResult("{\"time\":\"noon\"}"));

        await foreach (TextChunk _ in ToolLoop.RunAsync(fake, new ModelSpec { Requested = Model, Modality = Modality.Text }, request, registry))
        {
        }

        Assert.Equal(2, fake.SeenRequests.Count);
        Assert.NotNull(request.PrefixCacheKey);
        Assert.All(fake.SeenRequests, round =>
        {
            Assert.Equal(request.PrefixCacheKey, round.PrefixCacheKey);
            Assert.Equal(request.PrefixCacheCapacityHint, round.PrefixCacheCapacityHint);
        });
        Assert.True(fake.SeenRequests[1].Messages.Count > fake.SeenRequests[0].Messages.Count);
    }

    [Fact]
    public void TagLoopRounds_RebuildTheSameKeyFromTheGrowingInput()
    {
        // LLMStreamHelper's agentic loop and LLMAssistantVoiceTurn rebuild the request every round from the same
        // ExtendedLLMInput, appending the model's tool-call turn and the tool result as they go.
        ExtendedLLMInput input = Turn(ChatEndpoints.ThreadConversationId("t1"), Model,
            (LLMRoles.System, "Be helpful."), (LLMRoles.User, "search for cats"));
        string round1 = Build(input, "alice").PrefixCacheKey;
        input.Messages.Add(new LLMMessage { Role = LLMRoles.Assistant, Content = "<tool_call>{\"name\":\"web_search\",\"arguments\":{\"q\":\"cats\"}}</tool_call>" });
        input.Messages.Add(new LLMMessage { Role = LLMRoles.User, Content = ToolPromptServiceResult() });
        string round2 = Build(input, "alice").PrefixCacheKey;
        Assert.NotNull(round1);
        Assert.Equal(round1, round2);
    }

    private static string ToolPromptServiceResult()
        => Services.ToolPromptService.FormatToolResult("web_search", new JObject { ["success"] = true, ["results"] = new JArray("cats.example") });

    [Fact]
    public void ThreadConversationId_IsNullForABlankThread()
    {
        Assert.Equal("thread:t1", ChatEndpoints.ThreadConversationId("t1"));
        Assert.Null(ChatEndpoints.ThreadConversationId(" "));
        Assert.Null(ChatEndpoints.ThreadConversationId(null));
    }

    [Fact]
    public void VoiceTurnConversationId_IsScopedPerAssistant()
    {
        // The one-shot route is stateless: the only reusable prefix is the assistant's system prompt and tools,
        // so every caller using that assistant shares one entry (the provider still scopes it by user and model).
        Assert.Equal(ChatEndpoints.VoiceTurnConversationId("assistant-1"), ChatEndpoints.VoiceTurnConversationId("assistant-1"));
        Assert.NotEqual(ChatEndpoints.VoiceTurnConversationId("assistant-1"), ChatEndpoints.VoiceTurnConversationId("assistant-2"));
    }

    [Theory]
    [InlineData("call-1", "sess", "voice-ws:call-1")]
    [InlineData("  call-1  ", "sess", "voice-ws:call-1")]
    [InlineData(null, "sess", "voice-ws-session:sess")]
    [InlineData("  ", "sess", "voice-ws-session:sess")]
    [InlineData(null, null, null)]
    public void VoiceTurnWsConversationId_PrefersTheClientsIdThenTheSession(string clientConversationId, string sessionId, string expected)
    {
        Assert.Equal(expected, ChatEndpoints.VoiceTurnWsConversationId(clientConversationId, sessionId));
    }

    [Fact]
    public void VoiceTurnWsConversationId_AClientIdCannotLandOnASessionScope()
    {
        string spoofed = ChatEndpoints.VoiceTurnWsConversationId("-session:sess", sessionId: null);
        string session = ChatEndpoints.VoiceTurnWsConversationId(null, "sess");
        Assert.NotEqual(spoofed, session);
        Assert.NotEqual(KeyFor("alice", spoofed), KeyFor("alice", session));
    }

    [Fact]
    public void RemoteOpenAIProvider_RequestBodyNeverCarriesTheConversationScope()
    {
        RemoteOpenAILLMProvider provider = new()
        {
            SettingsRaw = new RemoteOpenAILLMProvider.RemoteOpenAILLMProviderSettings { Address = "http://localhost:11434", DefaultModel = "llama3.2" }
        };
        ExtendedLLMInput input = Turn(ChatEndpoints.ThreadConversationId("6f1d0c2b9a8e4f0f"), "llama3.2", (LLMRoles.User, "hi"));
        JObject body = provider.BuildRequestBody(input, stream: true);
        Assert.Equal(["max_tokens", "messages", "model", "stream", "temperature", "top_p"], body.Properties().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("6f1d0c2b9a8e4f0f", body.ToString());
    }

    [Fact]
    public void AnthropicProvider_RequestBodyNeverCarriesTheConversationScope()
    {
        AnthropicLLMProvider provider = new() { SettingsRaw = new AnthropicLLMProvider.AnthropicLLMProviderSettings() };
        ExtendedLLMInput input = Turn(ChatEndpoints.ThreadConversationId("6f1d0c2b9a8e4f0f"), "claude-opus-4-8", (LLMRoles.User, "hi"));
        JObject body = provider.BuildRequestBody(input, stream: true);
        Assert.Equal(["max_tokens", "messages", "model", "stream"], body.Properties().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("6f1d0c2b9a8e4f0f", body.ToString());
    }
}
