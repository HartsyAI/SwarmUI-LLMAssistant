using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.PromptStyles;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The chat-model addendum: which style a turn gets for the model that answers it, the switches, how it is laid into the system prompt, that the text is identical every time (so a backend's prefix cache
/// stays warm), what it costs in tokens, and that nothing changes when no style matches.</summary>
public sealed class LlmAddendumTests
{
    private static PromptStyle Style(string id, string matches = "", string body = "Be brief.", string tasks = "", string scope = "personal")
        => new(id, "llm", id, "d", PromptStyleFile.ParseMatches(matches, "llm"), "text", "", "none", [], [.. tasks.Split(',', StringSplitOptions.RemoveEmptyEntries)], "", body, scope);

    private static LLMModelInfo Model(string id, string family = null, string provider = null) => new() { Id = id, Family = family, Provider = provider };

    private static LlmAddendum Resolve(IEnumerable<PromptStyle> styles, LLMModelInfo model, bool tools = false, AddendumSettings settings = null, IEnumerable<StyleOverride> rules = null,
        bool assistantAllows = true, int variantRank = -1)
        => LlmAddendumService.Resolve(styles, rules ?? [], model, LlmAddendumService.TasksFor(tools), settings ?? AddendumSettings.Default, assistantAllows, variantRank);

    // ---- nothing changes unless a style matches ----

    [Fact]
    public void WithNoStyleTheSystemPromptIsExactlyWhatItWas()
    {
        LlmAddendum none = Resolve([], Model("qwen3-8b", "qwen3"));
        Assert.False(none.Applied);
        Assert.Equal(0, none.EstimatedTokens);
        Assert.Equal("Be helpful.\n", LlmAddendumService.Append("Be helpful.\n", none));
        Assert.Null(LlmAddendumService.Append(null, none));
    }

    [Fact]
    public void ImageStylesAndStylesForOtherModelsAddNothing()
    {
        PromptStyle image = new("generic", "image", "g", "d", [], "text", "", "none", [], [], "", "Write a prompt.", "bundled");
        Assert.False(Resolve([image, Style("qwen", "family:qwen3")], Model("llama-3", "llama")).Applied);
    }

    [Fact]
    public void AStyleWithNoTextAddsNothing()
    {
        LlmAddendum empty = Resolve([Style("blank", "family:qwen3", body: "  \n ")], Model("q", "qwen3"));
        Assert.False(empty.Applied);
        Assert.Equal("blank", empty.StyleId);
        Assert.Contains("no text", empty.Reason);
    }

    // ---- which style, for which model ----

    [Fact]
    public void TheStyleIsChosenByTheModelThatAnswers()
    {
        PromptStyle qwen = Style("qwen", "family:qwen3", "Qwen note.");
        PromptStyle claude = Style("claude", "glob:claude-*", "Claude note.");
        Assert.Equal("Qwen note.", Resolve([qwen, claude], Model("local/qwen3-8b", "qwen3")).Text);
        Assert.Equal("Claude note.", Resolve([qwen, claude], Model("claude-sonnet-5", null)).Text);
        Assert.False(Resolve([qwen, claude], Model("gpt-5")).Applied);
    }

    [Fact]
    public void AGlobStillMatchesWhenTheFamilyIsUnknownAndWhenNoBackendListsTheModel()
    {
        PromptStyle family = Style("family", "family:claude", "By family.");
        PromptStyle glob = Style("glob", "glob:*claude*", "By name.");
        Assert.Equal("By name.", Resolve([family, glob], Model("anthropic/claude-sonnet-5", null)).Text);
        Assert.Equal("By family.", Resolve([family, glob], Model("anthropic/claude-sonnet-5", "claude")).Text);
        // Core and the dry run build a model with only its id when no backend lists it.
        Assert.Equal("By name.", Resolve([family, glob], new LLMModelInfo { Id = "claude-x" }).Text);
    }

    [Fact]
    public void AUsersRuleBeatsAnyConditionAndCanTurnTheStyleOffForAModel()
    {
        PromptStyle qwen = Style("qwen", "family:qwen3", "Qwen note.");
        PromptStyle mine = Style("mine", "", "Mine.");
        LLMModelInfo model = Model("qwen3-8b", "qwen3");
        Assert.Equal("Mine.", Resolve([qwen, mine], model, rules: [new StyleOverride("llm", "qwen3-*", "mine")]).Text);
        LlmAddendum off = Resolve([qwen, mine], model, rules: [new StyleOverride("llm", "qwen3-8b", "off")]);
        Assert.False(off.Applied);
        Assert.Equal("off", off.Via);
        // A rule for an image model never touches a chat model.
        Assert.Equal("Qwen note.", Resolve([qwen], model, rules: [new StyleOverride("image", "qwen3-8b", "off")]).Text);
    }

    [Fact]
    public void ADefaultStyleWithNoConditionsAppliesToEveryModelButLosesToASpecificOne()
    {
        PromptStyle all = Style("all", "", "For all.");
        PromptStyle qwen = Style("qwen", "family:qwen3", "Qwen note.");
        Assert.Equal("For all.", Resolve([all, qwen], Model("llama", "llama")).Text);
        Assert.Equal("Qwen note.", Resolve([all, qwen], Model("q", "qwen3")).Text);
    }

    [Fact]
    public void AStyleLimitedToToolsApplyOnlyWhenToolsAreOffered()
    {
        PromptStyle tools = Style("tools", "family:qwen3", "Call tools like this.", tasks: "tools");
        PromptStyle both = Style("both", "family:qwen3", "Chat or tools.", tasks: "chat, tools");
        LLMModelInfo model = Model("q", "qwen3");
        Assert.False(Resolve([tools], model, tools: false).Applied);
        Assert.True(Resolve([tools], model, tools: true).Applied);
        Assert.True(Resolve([both], model, tools: false).Applied);
        Assert.False(Resolve([Style("vision", "family:qwen3", tasks: "vision")], model, tools: true).Applied);
    }

    // ---- switches ----

    [Fact]
    public void TheMasterSwitchStopsEverything()
    {
        LlmAddendum off = Resolve([Style("qwen", "family:qwen3")], Model("q", "qwen3"), settings: new AddendumSettings(false, []));
        Assert.False(off.Applied);
        Assert.Contains("switched off", off.Reason);
        Assert.Equal("S", LlmAddendumService.Append("S", off));
    }

    [Fact]
    public void ADisabledStyleIsSkippedSoALessSpecificOneCanApply()
    {
        PromptStyle exact = Style("exact", "exact:qwen3-8b", "Exact.");
        PromptStyle family = Style("family", "family:qwen3", "Family.");
        LLMModelInfo model = Model("qwen3-8b", "qwen3");
        Assert.Equal("Exact.", Resolve([exact, family], model).Text);
        Assert.Equal("Family.", Resolve([exact, family], model, settings: new AddendumSettings(true, ["exact"])).Text);
        Assert.False(Resolve([exact, family], model, settings: new AddendumSettings(true, ["exact", "family"])).Applied);
    }

    [Fact]
    public void AnAssistantCanOptOut()
    {
        LlmAddendum optOut = Resolve([Style("qwen", "family:qwen3")], Model("q", "qwen3"), assistantAllows: false);
        Assert.False(optOut.Applied);
        Assert.Contains("assistant", optOut.Reason);
    }

    [Fact]
    public void AnAssistantVariantMoreSpecificThanTheStyleWinsAndAnEqualOneDoesNot()
    {
        PromptStyle family = Style("family", "family:qwen3", "Family.");
        LLMModelInfo model = Model("qwen3-8b", "qwen3");
        // The assistant wrote an instruction for exactly this model (100) and the style is only by family (75): the assistant's own text is used alone.
        LlmAddendum alone = Resolve([family], model, variantRank: 100);
        Assert.False(alone.Applied);
        Assert.Equal("family", alone.StyleId);
        Assert.Contains("more specific", alone.Reason);
        // The same specificity (both by family), or a less specific variant (a glob), still gets the addendum on top.
        Assert.True(Resolve([family], model, variantRank: 75).Applied);
        Assert.True(Resolve([family], model, variantRank: 20).Applied);
        Assert.True(Resolve([family], model, variantRank: -1).Applied);
        // A style with no conditions loses to any variant written for the model.
        Assert.False(Resolve([Style("all", "")], model, variantRank: 20).Applied);
        // A rule the user made for the model always applies.
        Assert.True(Resolve([family], model, variantRank: 100, rules: [new StyleOverride("llm", "qwen3-8b", "family")]).Applied);
    }

    [Fact]
    public void TheSwitchesAreReadFromTheSettingsAndAnythingOddMeansTheDefault()
    {
        Assert.Equal(AddendumSettings.Default.Enabled, AddendumSettings.From(null).Enabled);
        Assert.True(AddendumSettings.From(JObject.Parse("{}")).Enabled);
        Assert.True(AddendumSettings.From(JObject.Parse("""{"promptStyles":{"llmAddenda":"no"}}""")).Enabled);
        Assert.False(AddendumSettings.From(JObject.Parse("""{"promptStyles":{"llmAddenda":false}}""")).Enabled);
        AddendumSettings read = AddendumSettings.From(JObject.Parse("""{"promptStyles":{"disabled":["Qwen", " qwen ", "", 5, null, "b"]}}"""));
        Assert.Equal(["qwen", "b"], read.Disabled);
        Assert.Empty(AddendumSettings.From(JObject.Parse("""{"promptStyles":{"disabled":"qwen"}}""")).Disabled);
    }

    // ---- the text: layering and byte-identical output ----

    [Fact]
    public void TheAddendumGoesAfterTheInstructionWithOneBlankLineBetween()
    {
        LlmAddendum add = Resolve([Style("qwen", "family:qwen3", "Be brief.")], Model("q", "qwen3"));
        Assert.Equal("You are Swarmie.\n\nBe brief.", LlmAddendumService.Append("You are Swarmie.", add));
        Assert.Equal("You are Swarmie.\n\nBe brief.", LlmAddendumService.Append("You are Swarmie.\n\n\r\n", add));
        Assert.Equal("Be brief.", LlmAddendumService.Append("", add));
        Assert.Equal("Be brief.", LlmAddendumService.Append(null, add));
    }

    [Fact]
    public void TheTextIsTheSameBytesEveryTimeAndOnEveryLineEndingConvention()
    {
        PromptStyle lf = Style("qwen", "family:qwen3", "  Line one.\nLine two.\n\n");
        PromptStyle crlf = Style("qwen", "family:qwen3", "Line one.\r\nLine two.\r\n");
        LLMModelInfo model = Model("q", "qwen3");
        string first = LlmAddendumService.Append("Instruction.", Resolve([lf], model));
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(first, LlmAddendumService.Append("Instruction.", Resolve([lf], model)));
        }
        Assert.Equal(first, LlmAddendumService.Append("Instruction.", Resolve([crlf], model)));
        Assert.Equal("Instruction.\n\nLine one.\nLine two.", first);
        // The order the styles are listed in changes nothing.
        PromptStyle other = Style("other", "glob:zzz*", "Other.");
        Assert.Equal(first, LlmAddendumService.Append("Instruction.", Resolve([other, lf], model)));
        Assert.Equal(first, LlmAddendumService.Append("Instruction.", Resolve([lf, other], model)));
    }

    [Fact]
    public void TwoModelsGetTheirOwnTextAndEachKeepsItsOwnBytes()
    {
        PromptStyle a = Style("a", "exact:model-a", "Note for A.");
        PromptStyle b = Style("b", "exact:model-b", "Note for B.");
        string forA = LlmAddendumService.Append("S", Resolve([a, b], Model("model-a")));
        string forB = LlmAddendumService.Append("S", Resolve([a, b], Model("model-b")));
        Assert.Equal("S\n\nNote for A.", forA);
        Assert.Equal("S\n\nNote for B.", forB);
        Assert.Equal("S", LlmAddendumService.Append("S", Resolve([a, b], Model("model-c"))));
    }

    [Fact]
    public void TheBodyIsNotRunThroughVariableSubstitution()
    {
        // Instructions get the user's profile substituted; the addendum is appended after that and must not vary per user.
        LlmAddendum add = Resolve([Style("qwen", "family:qwen3", "Hello {{userProfile}}")], Model("q", "qwen3"));
        Assert.Equal("S\n\nHello {{userProfile}}", LlmAddendumService.Append("S", add));
    }

    // ---- the cost ----

    [Fact]
    public void TheTokenEstimateCountsTheTextAndItsSeparatorAsTheDryRunDoes()
    {
        string body = new('x', 397);
        LlmAddendum add = Resolve([Style("qwen", "family:qwen3", body)], Model("q", "qwen3"));
        Assert.Equal(399, add.Chars);
        Assert.Equal(100, add.EstimatedTokens);
        string result = LlmAddendumService.Append("S", add);
        // The prompt grew by the separator and the text; the estimate is characters over four, rounded up.
        Assert.Equal("S".Length + add.Chars, result.Length);
        Assert.Equal(0, Resolve([], Model("q")).EstimatedTokens);
        JObject json = add.ToJson();
        Assert.True((bool)json["applied"]);
        Assert.Equal(100, (int)json["estimatedTokens"]);
        Assert.Equal("qwen", (string)json["styleId"]);
    }

    // ---- the assistant's side ----

    /// <summary>The resolver caches per assistant id, so each call gets its own id.</summary>
    private static ResolvedAssistant Assistant(string json)
    {
        string id = "a" + Guid.NewGuid().ToString("N")[..8];
        return AssistantResolver.Resolve(id, null, JObject.Parse("{\"assistants\":{\"" + id + "\":" + json + "}}"));
    }

    [Fact]
    public void AnAssistantSaysWhetherItTakesAnAddendumAndHowSpecificItsOwnVariantIsForAModel()
    {
        Assert.True(Assistant("""{"name":"A"}""").PromptAddendum);
        Assert.False(Assistant("""{"name":"A","promptAddendum":false}""").PromptAddendum);
        ResolvedAssistant plain = Assistant("""{"name":"A","instructions":{"chat":"Default text."}}""");
        Assert.Equal(-1, plain.MatchedVariantSpecificity("chat", Model("qwen3-8b", "qwen3")));
        ResolvedAssistant variants = Assistant("""{"name":"A","instructions":{"chat":{"default":"D","variants":[{"match":"qwen3","matchKind":"family","text":"For qwen."},{"match":"claude-*","matchKind":"glob","text":"For claude."},{"match":"x","matchKind":"exact","text":""}]}}}""");
        Assert.Equal(75, variants.MatchedVariantSpecificity("chat", Model("qwen3-8b", "qwen3")));
        Assert.Equal(20, variants.MatchedVariantSpecificity("chat", Model("claude-3", null)));
        Assert.Equal(-1, variants.MatchedVariantSpecificity("chat", Model("llama", "llama")));
        // A variant with no text is not one that wins, and another instruction id is separate.
        Assert.Equal(-1, variants.MatchedVariantSpecificity("chat", Model("x")));
        Assert.Equal(-1, variants.MatchedVariantSpecificity("prompt", Model("qwen3-8b", "qwen3")));
    }

    // ---- the wiring: one call, in the right place, in every path that builds a chat turn ----

    private static string Read(params string[] parts)
    {
        DirectoryInfo dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SwarmUI-LLMAssistant.csproj")))
        {
            dir = dir.Parent;
        }
        return File.ReadAllText(Path.Combine([dir!.FullName, .. parts]));
    }

    [Fact]
    public void TurnSetupAddsTheAddendumRightAfterTheInstructionAndBeforeAnythingElseIsAppended()
    {
        string source = Read("Services", "TurnSetupService.cs");
        int instruction = source.IndexOf("ResolveInstructionForRequest(", StringComparison.Ordinal);
        int addendum = source.IndexOf("LlmAddendumService.Apply(", StringComparison.Ordinal);
        Assert.True(instruction > 0 && addendum > instruction, "The addendum is added after the instruction is resolved.");
        foreach (string later in new[] { "AppendSessionNote(", "PromptAssetService.AppendIndex(", "ProjectInstructionsService.Append(", "CompactionDue(", "AutoCompactIfDueAsync(" })
        {
            Assert.True(source.IndexOf(later, StringComparison.Ordinal) > addendum, $"The addendum comes before {later}");
        }
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, "LlmAddendumService\\."));
        Assert.Contains("new PreparedTurn(", source);
        Assert.Contains("addendum)", source[source.IndexOf("new PreparedTurn(", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void EachLaneOfACompareTurnAddsTheAddendumOfItsOwnModel()
    {
        string source = Read("WebAPI", "ChatEndpoints.cs");
        int lane = source.IndexOf("async Task RunLane(int lane)", StringComparison.Ordinal);
        int instruction = source.IndexOf("ResolveInstructionForRequest(instructionId, assistantId, settings, session.User, modelInfo)", lane, StringComparison.Ordinal);
        int addendum = source.IndexOf("LlmAddendumService.Apply(systemPrompt, session.User, settings, assistantId, instructionId, L.Model, modelInfo, toolsOn", instruction, StringComparison.Ordinal);
        int index = source.IndexOf("PromptAssetService.AppendIndex(", instruction, StringComparison.Ordinal);
        Assert.True(lane > 0 && instruction > lane && addendum > instruction && index > addendum, "A lane adds its own model's addendum right after its instruction.");
    }

    [Fact]
    public void TheDryRunReportsTheAddendumAndItsTokens()
    {
        string source = Read("Services", "HeadlessTurnService.cs");
        Assert.Contains("[\"promptAddendum\"] = (prepared.Addendum ?? LlmAddendum.Nothing(\"\")).ToJson()", source);
    }
}
