using Hartsy.Extensions.LLMAssistant.LLMs;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Pins <see cref="ExtendedLLMInput.CreateFromMessages"/> — the WS voice-turn route's <c>messages</c>
/// (conversation-history) request field maps onto this. The one rule worth getting wrong twice: the resolved
/// system prompt is prepended only when the caller's own history doesn't already open with a system turn, the
/// same no-double-injection rule <c>HartsyInference.LLM.Generation.PromptBuilder.WithSystemPrompt</c> applies at
/// the engine's own prompt-build layer — <see cref="HartsyLocalLLMProviderTests"/>-adjacent code
/// (<c>BuildRequestAsync</c>'s own comment) trusts that <see cref="ExtendedLLMInput.Messages"/>[0] is the single
/// source of truth and never also sets <c>TextRequest.SystemPrompt</c>, so getting this wrong here would
/// silently double-inject the system prompt into the chat template exactly the way a confirmed 2026-07-25 bug
/// already did for a different caller.</summary>
public class ExtendedLLMInputCreateFromMessagesTests
{
    [Fact]
    public void CreateFromMessages_HistoryHasNoSystemTurn_PrependsOneFromTheResolvedSystemPrompt()
    {
        List<LLMMessage> history = [new() { Role = LLMRoles.User, Content = "hi" }];
        ExtendedLLMInput input = ExtendedLLMInput.CreateFromMessages(history, systemPrompt: "Be helpful.", model: "qwen3-4b");
        Assert.Equal(2, input.Messages.Count);
        Assert.Equal(LLMRoles.System, input.Messages[0].Role);
        Assert.Equal("Be helpful.", input.Messages[0].Content);
        Assert.Equal(LLMRoles.User, input.Messages[1].Role);
        Assert.Equal("Be helpful.", input.SystemPrompt); // mirrored, per the field's own doc
    }

    [Fact]
    public void CreateFromMessages_HistoryAlreadyOpensWithSystem_DoesNotPrependASecondOne()
    {
        // The exact "no double injection" case: a client replaying its own history that already carries the
        // system turn from an earlier turn in the same conversation must not get a second one stacked in front.
        List<LLMMessage> history =
        [
            new() { Role = LLMRoles.System, Content = "Caller's own system turn." },
            new() { Role = LLMRoles.User, Content = "hi" }
        ];
        ExtendedLLMInput input = ExtendedLLMInput.CreateFromMessages(history, systemPrompt: "A different resolved prompt.", model: "qwen3-4b");
        Assert.Equal(2, input.Messages.Count); // not 3 -- nothing was prepended
        Assert.Equal(LLMRoles.System, input.Messages[0].Role);
        Assert.Equal("Caller's own system turn.", input.Messages[0].Content);
        // Mirrored from the history's own system turn, not the freshly resolved prompt -- the caller's own
        // message is what will actually reach the model, per the method's own doc.
        Assert.Equal("Caller's own system turn.", input.SystemPrompt);
    }

    [Fact]
    public void CreateFromMessages_NullOrEmptySystemPrompt_NeverPrependsAnything()
    {
        List<LLMMessage> history = [new() { Role = LLMRoles.User, Content = "hi" }];
        ExtendedLLMInput input = ExtendedLLMInput.CreateFromMessages(history, systemPrompt: null, model: "qwen3-4b");
        Assert.Single(input.Messages);
        Assert.Equal(LLMRoles.User, input.Messages[0].Role);
        Assert.Null(input.SystemPrompt);
    }

    [Fact]
    public void CreateFromMessages_UserMessage_IsTheLastUserRoleEntryNotTheLastEntry()
    {
        // A client replaying history right after an earlier native tool call: the conversation's own last
        // entry is a Tool-role result, not a User turn -- UserMessage must still reflect the last *user* turn,
        // not messages[^1].
        List<LLMMessage> history =
        [
            new() { Role = LLMRoles.User, Content = "what time is it?" },
            new() { Role = LLMRoles.Assistant, Content = "", ToolCalls = [new() { ["id"] = "call_1", ["name"] = "get_time", ["arguments"] = new Newtonsoft.Json.Linq.JObject() }] },
            new() { Role = LLMRoles.Tool, Content = "{\"time\":\"noon\"}", ToolCallId = "call_1", Name = "get_time" }
        ];
        ExtendedLLMInput input = ExtendedLLMInput.CreateFromMessages(history, systemPrompt: null, model: "qwen3-4b");
        Assert.Equal("what time is it?", input.UserMessage);
    }

    [Fact]
    public void CreateFromMessages_NoUserTurnAtAll_LeavesUserMessageNull()
    {
        List<LLMMessage> history = [new() { Role = LLMRoles.System, Content = "sys only" }];
        ExtendedLLMInput input = ExtendedLLMInput.CreateFromMessages(history, systemPrompt: null, model: "qwen3-4b");
        Assert.Null(input.UserMessage);
    }

    [Fact]
    public void CreateFromMessages_EmptyMessagesList_StillPrependsTheSystemPromptAlone()
    {
        ExtendedLLMInput input = ExtendedLLMInput.CreateFromMessages([], systemPrompt: "Be helpful.", model: "qwen3-4b");
        Assert.Single(input.Messages);
        Assert.Equal(LLMRoles.System, input.Messages[0].Role);
    }

    [Fact]
    public void CreateFromMessages_DoesNotMutateTheCallersOriginalList()
    {
        // Inserting the system turn must happen on a copy -- a caller (eg ChatEndpoints, replaying a parsed
        // JArray) must still see its own list unchanged after this call.
        List<LLMMessage> history = [new() { Role = LLMRoles.User, Content = "hi" }];
        int originalCount = history.Count;
        _ = ExtendedLLMInput.CreateFromMessages(history, systemPrompt: "Be helpful.", model: "qwen3-4b");
        Assert.Equal(originalCount, history.Count);
    }
}
