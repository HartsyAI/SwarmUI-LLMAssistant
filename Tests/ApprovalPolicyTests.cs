using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Tools;
using Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The approval rules: what each mode does with a call of each risk.</summary>
public class ApprovalPolicyTests
{
    [Theory]
    [InlineData("auto", ToolRisk.Read, false, ApprovalDecision.Allow)]
    [InlineData("auto", ToolRisk.Write, false, ApprovalDecision.Allow)]
    [InlineData("auto", ToolRisk.Exec, false, ApprovalDecision.Allow)]
    [InlineData("auto", ToolRisk.Network, false, ApprovalDecision.Allow)]
    [InlineData("ask", ToolRisk.Read, false, ApprovalDecision.Allow)]
    [InlineData("ask", ToolRisk.Write, false, ApprovalDecision.Ask)]
    [InlineData("ask", ToolRisk.Exec, false, ApprovalDecision.Ask)]
    [InlineData("ask", ToolRisk.Network, false, ApprovalDecision.Ask)]
    [InlineData("ask", ToolRisk.Write, true, ApprovalDecision.Allow)]
    [InlineData("ask", ToolRisk.Exec, true, ApprovalDecision.Allow)]
    [InlineData("plan", ToolRisk.Read, false, ApprovalDecision.Allow)]
    [InlineData("plan", ToolRisk.Write, false, ApprovalDecision.Deny)]
    [InlineData("plan", ToolRisk.Exec, false, ApprovalDecision.Deny)]
    [InlineData("plan", ToolRisk.Network, false, ApprovalDecision.Deny)]
    // An allowance for the chat does not lift plan mode: plan means nothing changes.
    [InlineData("plan", ToolRisk.Write, true, ApprovalDecision.Deny)]
    public void EachModeDecidesEachRisk(string mode, ToolRisk risk, bool allowedForChat, ApprovalDecision expected)
    {
        Assert.Equal(expected, ApprovalPolicy.Decide(mode, risk, allowedForChat));
    }

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData("nonsense", "auto")]
    [InlineData("ASK", "ask")]
    [InlineData(" plan ", "plan")]
    public void AMissingOrUnknownModeIsAuto_SoExistingChatsBehaveAsBefore(string mode, string expected)
    {
        Assert.Equal(expected, ApprovalPolicy.Normalize(mode));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nonsense")]
    public void AnUnknownModeNeverAsksOrRefuses(string mode)
    {
        Assert.Equal(ApprovalDecision.Allow, ApprovalPolicy.Decide(mode, ToolRisk.Exec, false));
    }

    [Fact]
    public void AHandlerThatDoesNotSayIsTreatedAsAChange()
    {
        Assert.Equal(ToolRisk.Write, new UnclassifiedHandler().Risk);
    }

    [Fact]
    public void EachBuiltInHandlerHasTheRiskItsEffectsCallFor()
    {
        Assert.Equal(ToolRisk.Read, new CaptionImageTool().Risk);
        Assert.Equal(ToolRisk.Read, new FuseImageDescriptionsTool().Risk);
        Assert.Equal(ToolRisk.Read, new WebSearchTool().Risk);
        Assert.Equal(ToolRisk.Read, new FileReadTool().Risk);
        Assert.Equal(ToolRisk.Read, new MemoryReadTool().Risk);
        Assert.Equal(ToolRisk.Read, new SwarmDocsTool().Risk);
        Assert.Equal(ToolRisk.Write, new GenerateImageTool().Risk);
        Assert.Equal(ToolRisk.Write, new CreateImagePresetTool().Risk);
        Assert.Equal(ToolRisk.Write, new BatchCaptionFolderTool().Risk);
        Assert.Equal(ToolRisk.Write, new FileWriteTool().Risk);
        Assert.Equal(ToolRisk.Write, new MemoryWriteTool().Risk);
        Assert.Equal(ToolRisk.Network, new HttpRequestTool().Risk);
        Assert.Equal(ToolRisk.Exec, new ShellExecTool().Risk);
    }

    /// <summary>A handler that does not override <see cref="ToolHandler.Risk"/>.</summary>
    private sealed class UnclassifiedHandler : ToolHandler
    {
        public override string HandlerId => "unclassified";

        public override Task<JObject> Execute(ToolExecutionContext ctx) => Task.FromResult(new JObject());
    }
}
