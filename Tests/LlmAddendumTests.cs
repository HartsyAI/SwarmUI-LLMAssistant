using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.PromptStyles;
using HartsyInference.Engine.Requests;
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
    public void TheTokenEstimateIsCharactersOverFourOfWhatIsAdded()
    {
        string body = new('x', 397);
        LlmAddendum add = Resolve([Style("qwen", "family:qwen3", body)], Model("q", "qwen3"));
        Assert.Equal(399, add.Chars);
        Assert.Equal(99, add.EstimatedTokens);
        Assert.Equal("S".Length + add.Chars, LlmAddendumService.Append("S", add).Length);
        Assert.Equal(0, Resolve([], Model("q")).EstimatedTokens);
        JObject json = add.ToJson();
        Assert.True((bool)json["applied"]);
        Assert.Equal(99, (int)json["estimatedTokens"]);
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

    // ---- the wiring: the call is in the right place in every path that builds a chat turn (the behaviour itself is checked end to end in E2E/tests/llm-addendum.spec.ts) ----

    private static string Read(params string[] parts)
    {
        DirectoryInfo dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SwarmUI-LLMAssistant.csproj")))
        {
            dir = dir.Parent;
        }
        return File.ReadAllText(Path.Combine([dir!.FullName, .. parts]));
    }

    /// <summary>The position of the first <paramref name="token"/> at or after <paramref name="from"/>, or -1. Tokens are method names only, so a change to a call's arguments does not matter.</summary>
    private static int At(string source, string token, int from = 0) => source.IndexOf(token, from, StringComparison.Ordinal);

    [Fact]
    public void TurnSetupAddsTheAddendumAfterTheInstructionAndTheFinalToolListAndBeforeAnythingElseIsAppended()
    {
        string source = Read("Services", "TurnSetupService.cs");
        int instruction = At(source, "ResolveInstructionForRequest(");
        int narrowed = At(source, "NarrowTools(");
        int addendum = At(source, "LlmAddendumService.ApplyAsync(");
        Assert.True(instruction > 0 && narrowed > instruction && addendum > narrowed, "The addendum is added after the instruction is resolved and the tools the turn will really send are known.");
        foreach (string later in new[] { "AppendSessionNote(", "PromptAssetService.AppendIndex(", "ProjectInstructionsService.Append(", "CompactionDue(", "AutoCompactIfDueAsync(", "AttachTools(" })
        {
            Assert.True(At(source, later) > addendum, $"The addendum comes before {later}");
        }
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(source, "LlmAddendumService\\.").Count);
        Assert.Contains("enabledTools.Count > 0", source[addendum..source.IndexOf(';', addendum)]);
    }

    [Fact]
    public void EachLaneOfACompareTurnAddsTheAddendumOfItsOwnModelAfterItsInstruction()
    {
        string source = Read("WebAPI", "ChatEndpoints.cs");
        int lane = At(source, "async Task RunLane(int lane)");
        int instruction = At(source, "ResolveInstructionForRequest(", lane);
        int addendum = At(source, "LlmAddendumService.ApplyAsync(", instruction);
        int index = At(source, "PromptAssetService.AppendIndex(", instruction);
        Assert.True(lane > 0 && instruction > lane && addendum > instruction && index > addendum, "A lane adds its own model's addendum right after its instruction.");
        string call = source[addendum..source.IndexOf(';', addendum)];
        Assert.Contains("L.Model", call);
        Assert.Contains("enabledTools.Count > 0", call);
    }

    [Fact]
    public void TheDryRunReportsTheAddendumAndUsesTheSameEstimatorForItsTotals()
    {
        string source = Read("Services", "HeadlessTurnService.cs");
        Assert.Contains("promptAddendum", source);
        Assert.Contains("prepared.Addendum", source);
        Assert.Contains("LlmAddendumService.EstimateTokens(characters)", source);
        Assert.DoesNotContain("characters / 4", source);
    }

    [Fact]
    public void TheAssistantEditorOnlyWritesItsOwnValueWhenItHadOneOrTheUserChangedIt()
    {
        string script = Read("Assets", "llmassistant.js");
        int save = At(script, "async function llmaSaveAssistantFromEditor");
        int write = At(script, "assistant.promptAddendum =", save);
        Assert.True(save > 0 && write > save);
        string guard = script[Math.Max(save, write - 160)..write];
        Assert.Contains("_editingPromptAddendumOwn", guard);
        Assert.Contains("_editingPromptAddendumTouched", guard);
    }

    // ---- what a turn is told about, as the real function decides it ----

    [Fact]
    public void AChildAssistantInheritsAParentsOptOutUnlessItSaysOtherwise()
    {
        string parent = "p" + Guid.NewGuid().ToString("N")[..8];
        string inherits = "c" + Guid.NewGuid().ToString("N")[..8];
        string overrides = "o" + Guid.NewGuid().ToString("N")[..8];
        JObject settings = JObject.Parse("{\"assistants\":{\"" + parent + "\":{\"name\":\"P\",\"promptAddendum\":false},\"" + inherits + "\":{\"name\":\"C\",\"extends\":\"" + parent
            + "\"},\"" + overrides + "\":{\"name\":\"O\",\"extends\":\"" + parent + "\",\"promptAddendum\":true}}}");
        Assert.False(AssistantResolver.Resolve(parent, null, settings).PromptAddendum);
        Assert.False(AssistantResolver.Resolve(inherits, null, settings).PromptAddendum);
        Assert.True(AssistantResolver.Resolve(overrides, null, settings).PromptAddendum);
    }

    [Fact]
    public void ARuleThatPointsAtASwitchedOffStyleAddsNothingAndSaysSo()
    {
        PromptStyle qwen = Style("qwen", "family:qwen3", "Qwen note.");
        PromptStyle mine = Style("mine", "", "Mine.");
        LLMModelInfo model = Model("qwen3-8b", "qwen3");
        StyleOverride rule = new("llm", "qwen3-*", "mine");
        LlmAddendum none = Resolve([qwen, mine], model, rules: [rule], settings: new AddendumSettings(true, ["mine"]));
        Assert.False(none.Applied);
        Assert.Equal("mine", none.StyleId);
        Assert.Equal("override", none.Via);
        Assert.Contains("'mine'", none.Reason);
        Assert.Contains("switched off", none.Reason);
        Assert.DoesNotContain("does not exist", none.Reason);
        // Not a different style the user did not choose: the family style would have matched without the rule.
        Assert.Equal("Qwen note.", Resolve([qwen, mine], model).Text);
        // A rule for another model is unaffected.
        Assert.Equal("Qwen note.", Resolve([qwen, mine], model, rules: [new StyleOverride("llm", "llama-*", "mine")], settings: new AddendumSettings(true, ["mine"])).Text);
        // A rule naming a style that does not exist still falls through, and says so.
        LlmAddendum ghost = Resolve([qwen], model, rules: [new StyleOverride("llm", "qwen3-8b", "ghost")]);
        Assert.True(ghost.Applied);
        Assert.Contains("does not exist", ghost.Reason);
    }

    [Fact]
    public void ThePreviewStillSaysWhichStyleAModelGetsWhenASwitchStopsIt()
    {
        PromptStyle qwen = Style("qwen", "family:qwen3", "Qwen note.");
        LLMModelInfo model = Model("q", "qwen3");
        LlmAddendum off = LlmAddendumService.Resolve([qwen], [], model, LlmAddendumService.TasksFor(false), new AddendumSettings(false, []), true, -1, resolveWhenOff: true);
        Assert.False(off.Applied);
        Assert.Contains("switched off", off.Reason);
        Assert.Equal("qwen", off.StyleId);
        Assert.Equal("qwen", off.Resolution.Style.Id);
        LlmAddendum optOut = LlmAddendumService.Resolve([qwen], [], model, LlmAddendumService.TasksFor(false), AddendumSettings.Default, false, -1, resolveWhenOff: true);
        Assert.False(optOut.Applied);
        Assert.Equal("qwen", optOut.Resolution.Style.Id);
        // A turn (not a preview) never looks.
        Assert.Null(LlmAddendumService.Resolve([qwen], [], model, LlmAddendumService.TasksFor(false), new AddendumSettings(false, [])).Resolution);
    }

    // ---- sizes ----

    [Fact]
    public void TheNumbersAreWhatWasReallyAppendedAndOneEstimatorIsUsed()
    {
        LlmAddendum add = Resolve([Style("qwen", "family:qwen3", new string('x', 397))], Model("q", "qwen3"));
        foreach ((string before, int delta) in new (string, int)[] { ("S", 399), ("", 397), ("S\n\n\n", 396), (null, 397) })
        {
            string after = LlmAddendumService.Append(before, add);
            LlmAddendum measured = LlmAddendumService.Measured(add, before, after);
            Assert.Equal(delta, measured.Chars);
            Assert.Equal(Math.Max(0, (after?.Length ?? 0) - (before?.Length ?? 0)), measured.Chars);
            Assert.Equal(LlmAddendumService.EstimateTokens(measured.Chars), measured.EstimatedTokens);
        }
        Assert.Equal(99, LlmAddendumService.EstimateTokens(399));
        Assert.Equal(0, LlmAddendumService.EstimateTokens(3));
        LlmAddendum nothing = LlmAddendum.Nothing("x");
        Assert.Same(nothing, LlmAddendumService.Measured(nothing, "S", "S"));
    }

    [Fact]
    public void AStyleOverTheCapIsRefusedWhenSavedNeverAddedWhenRead_AndALargeOneWarns()
    {
        string atCap = new('x', LlmAddendumService.MaxChars);
        Assert.Null(PromptStyleService.Validate(Style("ok", "family:qwen3", atCap)));
        PromptStyle tooBig = Style("big", "family:qwen3", atCap + "y");
        Assert.Contains("characters", PromptStyleService.Validate(tooBig));
        // The same text in an image style is not limited by this rule.
        PromptStyle image = new("img", "image", "img", "d", [], "text", "", "none", [], [], "", atCap + "y", "personal");
        Assert.Null(PromptStyleService.Validate(image));
        // A file that got past the check is not added.
        LlmAddendum refused = Resolve([tooBig], Model("q", "qwen3"));
        Assert.False(refused.Applied);
        Assert.Contains("longer than", refused.Reason);
        // A large (but allowed) one warns in the preview.
        Assert.Null(Resolve([Style("small", "family:qwen3", "Short.")], Model("q", "qwen3")).Warning);
        LlmAddendum large = Resolve([Style("large", "family:qwen3", new string('x', LlmAddendumService.WarnTokens * 4))], Model("q", "qwen3"));
        Assert.True(large.Applied);
        Assert.Contains("tokens", large.Warning);
        Assert.NotNull(large.ToJson()["warning"]?.ToString());
    }

    // ---- when no backend lists the model ----

    [Fact]
    public void AListingThatFailsDoesNotChangeWhichStyleAModelGets()
    {
        PromptStyle family = Style("family", "family:qwen3", "By family.");
        PromptStyle glob = Style("glob", "glob:*-8b", "By name.");
        string id = "outage-" + Guid.NewGuid().ToString("N")[..8] + "-8b";
        // Listed once: the family condition wins.
        LLMModelInfo listed = LlmAddendumService.FactsFor(id, Model(id, "qwen3"));
        Assert.Equal("By family.", Resolve([family, glob], listed).Text);
        // The listing then fails (nothing found for the id): what was last known is used, so the style does not flip to the name glob.
        LLMModelInfo during = LlmAddendumService.FactsFor(id, null);
        Assert.Equal("qwen3", during.Family);
        Assert.Equal("By family.", Resolve([family, glob], during).Text);
        // A model never seen still resolves by id and name glob.
        LLMModelInfo never = LlmAddendumService.FactsFor("never-seen-" + Guid.NewGuid().ToString("N")[..8] + "-8b", null);
        Assert.Null(never.Family);
        Assert.Equal("By name.", Resolve([family, glob], never).Text);
        Assert.Equal("", LlmAddendumService.FactsFor(null, null).Id);
    }

    [Fact]
    public void WhatWasLastKnownIsBounded()
    {
        for (int i = 0; i < 600; i++)
        {
            LlmAddendumService.FactsFor("bounded-" + i, Model("bounded-" + i, "f"));
        }
        // Still answers for a recent model and for an unknown one.
        Assert.Equal("f", LlmAddendumService.FactsFor("bounded-599", null).Family);
        Assert.Null(LlmAddendumService.FactsFor("bounded-unknown-x", null).Family);
    }
}

/// <summary>Which model answers a request that names none. The registry is process-wide, so these run apart from every other test and put it back as it was.</summary>
[Collection("LLMProviderRegistry")]
public sealed class EffectiveModelTests
{
    private sealed class Provider(string id, string defaultModel, params string[] models) : ILLMProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public string DefaultModelId => defaultModel;
        public Task<List<LLMModelInfo>> ListModels(CancellationToken ct = default) => Task.FromResult(models.Select(m => new LLMModelInfo { Id = m }).ToList());
        public Task<string> Generate(ExtendedLLMInput input, CancellationToken ct = default) => Task.FromResult("");
        public Task GenerateLive(ExtendedLLMInput input, string batchId, Func<JObject, Task> onChunk, CancellationToken ct) => Task.CompletedTask;
        public int? CountTokens(string text) => null;
        public Task<bool> Unload() => Task.FromResult(false);
        public IAsyncEnumerable<TextChunk> StreamAsync(ExtendedLLMInput input, CancellationToken ct) => throw new NotSupportedException();
    }

    private static async Task<string> WithProviders(ILLMProvider[] providers, string model)
    {
        List<ILLMProvider> before = [.. LLMProviderRegistry.All];
        foreach (ILLMProvider p in before)
        {
            LLMProviderRegistry.Unregister(p.Id);
        }
        foreach (ILLMProvider p in providers)
        {
            LLMProviderRegistry.Register(p);
        }
        try
        {
            return await LLMModelLookup.ResolveEffectiveModelIdAsync(model);
        }
        finally
        {
            foreach (ILLMProvider p in providers)
            {
                LLMProviderRegistry.Unregister(p.Id);
            }
            foreach (ILLMProvider p in before)
            {
                LLMProviderRegistry.Register(p);
            }
        }
    }

    [Fact]
    public async Task ANamedModelIsTheOneThatAnswers() => Assert.Equal("named-model", await WithProviders([new Provider("t1", "its-default", "listed")], "  named-model "));

    [Fact]
    public async Task ARequestWithNoModelGetsTheProvidersDefault() => Assert.Equal("its-default", await WithProviders([new Provider("t2", " its-default ", "listed")], ""));

    [Fact]
    public async Task AProviderWithNoDefaultFallsBackToTheFirstModelAnyProviderLists() => Assert.Equal("first-listed", await WithProviders([new Provider("t3", null, "first-listed", "second")], null));

    [Fact]
    public async Task WithNoBackendAtAllTheAnswerIsEmptyAndNothingBreaks() => Assert.Equal("", await WithProviders([], ""));
}
