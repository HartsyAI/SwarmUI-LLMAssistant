using System.Reflection;
using FreneticUtilities.FreneticDataSyntax;
using Hartsy.Extensions.LLMAssistant.Backends;
using Hartsy.Extensions.LLMAssistant.LLMs;
using HartsyInference.Engine.Requests;
using HartsyInference.LLM.Looped;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using Xunit;
using LocalSettings = Hartsy.Extensions.LLMAssistant.Backends.HartsyLocalLLMProvider.HartsyLocalLLMProviderSettings;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The LLM backend cards' settings: saved backends still load, the dropdowns offer what the code reads, the tooltips stay
/// short, and the card script and README cover every field.</summary>
public class BackendCardSettingsTests
{
    /// <summary>A backend saved before the dropdowns existed, as Data/Backends.fds stores it.</summary>
    [Fact]
    public void LocalSettings_SavedBackendFromFile_LoadsUnchanged()
    {
        LocalSettings settings = new();
        settings.Load(new FDSSection("Device: cuda\nGPUDeviceId: 1\nVramMode: Balanced\nPlacement: offload\nLowVramQuant: true\n"
            + "RepetitionPenalty: 1.15\nTopK: 20\nMinP: 0.05\nEvictBelowGb: 12\nRecurrentLoops: 3\nReuseConversationPrefix: false\n"));
        Assert.Equal("cuda", settings.Device);
        Assert.Equal(1, settings.GPUDeviceId);
        Assert.Equal("Balanced", settings.VramMode);
        Assert.Equal("offload", settings.Placement);
        Assert.True(settings.LowVramQuant);
        Assert.Equal(1.15, settings.RepetitionPenalty);
        Assert.Equal(20, settings.TopK);
        Assert.Equal(0.05, settings.MinP);
        Assert.Equal(12, settings.EvictBelowGb);
        Assert.Equal(3, settings.RecurrentLoops);
        Assert.False(settings.ReuseConversationPrefix);
    }

    /// <summary>Core's Save sends every dropdown and number as the string the input holds, which the int fields must still take.</summary>
    [Fact]
    public void LocalSettings_CardSave_LoadsDropdownStrings()
    {
        LocalSettings settings = new();
        settings.Load(FDSSection.FromSimple(new Dictionary<string, object>
        {
            ["Device"] = "vulkan",
            ["GPUDeviceId"] = "2",
            ["RecurrentLoops"] = "4",
            ["TopK"] = "64",
            ["GraphDecode"] = true
        }));
        Assert.Equal("vulkan", settings.Device);
        Assert.Equal(2, settings.GPUDeviceId);
        Assert.Equal(4, settings.RecurrentLoops);
        Assert.Equal(64, settings.TopK);
        Assert.True(settings.GraphDecode);
    }

    /// <summary>A saved backend keeps its Device; only a new backend starts on Auto.</summary>
    [Fact]
    public void LocalSettings_NewBackend_StartsOnAuto()
    {
        LocalSettings settings = new();
        Assert.Equal("auto", settings.Device);
        Assert.Equal(0, settings.GPUDeviceId);
    }

    [Fact]
    public void LocalSettings_SavedBackendFromFile_IgnoresStructuredToolCalling()
    {
        // A backend saved before StructuredToolCalling was removed still sets it; the unknown key is ignored.
        LocalSettings settings = new();
        settings.Load(new FDSSection("Device: cpu\nVramMode: Balanced\nStructuredToolCalling: true\n"));
        Assert.Equal("cpu", settings.Device);
        Assert.Equal("Balanced", settings.VramMode);
    }

    [Fact]
    public void RemoteAndAnthropicSettings_SavedBackendFromFile_LoadsUnchanged()
    {
        RemoteOpenAILLMProvider.RemoteOpenAILLMProviderSettings remote = new();
        // A backend saved before NativeToolCalling was removed still sets it; the unknown key is ignored.
        remote.Load(new FDSSection("Address: http://localhost:11434\nDefaultModel: llama3.2\nTokenLimitParameter: max_tokens\n"
            + "ConnectionAttemptTimeoutSeconds: 5\nNativeToolCalling: on\nAllowIdle: true\n"));
        Assert.Equal("http://localhost:11434", remote.Address);
        Assert.Equal("llama3.2", remote.DefaultModel);
        Assert.Equal("max_tokens", remote.TokenLimitParameter);
        Assert.Equal(5, remote.ConnectionAttemptTimeoutSeconds);
        Assert.True(remote.AllowIdle);
        AnthropicLLMProvider.AnthropicLLMProviderSettings anthropic = new();
        anthropic.Load(new FDSSection("DefaultModel: claude-sonnet-4-6\nTimeoutSeconds: 300\nWorkspaceId: wrkspc_1\n"));
        Assert.Equal("claude-sonnet-4-6", anthropic.DefaultModel);
        Assert.Equal(300, anthropic.TimeoutSeconds);
        Assert.Equal("wrkspc_1", anthropic.WorkspaceId);
        Assert.Equal("https://api.anthropic.com", anthropic.BaseUrl);
    }

    [Theory]
    [InlineData("cuda", "cuda")]
    [InlineData("CUDA", "cuda")]
    [InlineData("", "cuda")]
    [InlineData(null, "cuda")]
    [InlineData("something-old", "cuda")]
    [InlineData("cuda:1", "cuda")]
    [InlineData("cpu", "cpu")]
    [InlineData(" Vulkan ", "vulkan")]
    [InlineData("vulkan:2", "vulkan")]
    [InlineData("auto", "auto")]
    public void RequestedKind_KeepsEveryOldValueOnItsDevice(string device, string expected)
    {
        Assert.Equal(expected, LocalDeviceChoices.RequestedKind(device));
    }

    [Theory]
    [InlineData("cpu", 3, "cpu")]
    [InlineData("cuda", 0, "cuda:0")]
    [InlineData("cuda", 1, "cuda:1")]
    [InlineData("vulkan", 0, "vulkan")]
    [InlineData("vulkan", 2, "vulkan:2")]
    [InlineData("vulkan", -1, "vulkan")]
    public void DeviceKeyFor_MatchesWhatTheEngineReads(string kind, int gpuId, string expected)
    {
        Assert.Equal(expected, LocalDeviceChoices.DeviceKeyFor(kind, gpuId));
    }

    [Theory]
    [InlineData(false, false, "auto,cpu")]
    [InlineData(true, false, "auto,cuda,cpu")]
    [InlineData(false, true, "auto,vulkan,cpu")]
    [InlineData(true, true, "auto,cuda,vulkan,cpu")]
    public void DeviceChoices_OfferOnlyWhatWorks(bool cuda, bool vulkan, string expected)
    {
        IReadOnlyList<LocalDeviceChoices.Choice> choices = LocalDeviceChoices.BuildKindChoices(cuda, vulkan);
        Assert.Equal(expected, string.Join(',', choices.Select(c => c.Value)));
        Assert.All(choices, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
    }

    [Fact]
    public void GpuLabel_NamesTheCardWithItsSize()
    {
        Assert.Equal("GPU 0: NVIDIA GeForce RTX 2060 SUPER (8 GB)", LocalDeviceChoices.GpuLabel(0, "NVIDIA GeForce RTX 2060 SUPER", 8589934592));
        Assert.Equal("GPU 1: AMD Radeon RX 7600 (7.5 GB)", LocalDeviceChoices.GpuLabel(1, "AMD Radeon RX 7600", 8053063680));
        Assert.Equal("GPU 2: Unknown card", LocalDeviceChoices.GpuLabel(2, "Unknown card", 0));
        Assert.Contains("CPU", LocalDeviceChoices.EmptyGpuLabel("cpu", auto: true));
        Assert.Equal("Not used on CPU", LocalDeviceChoices.EmptyGpuLabel("cpu", auto: false));
    }

    /// <summary>The Default Loops dropdown follows the engine's ceiling, so an engine bump cannot leave it offering a count the backend ignores.</summary>
    [Fact]
    public void LoopOptions_RunFromModelDefaultToTheEngineCeiling()
    {
        FieldInfo field = typeof(LocalSettings).GetField(nameof(LocalSettings.RecurrentLoops))!;
        SettingsOptionsAttribute options = field.GetCustomAttribute<SettingsOptionsAttribute>()!;
        string[] expected = [.. Enumerable.Range(0, LoopedTransformerConfig.DefaultMaxLoops + 1).Select(i => $"{i}")];
        Assert.Equal(expected, options.Options);
        Assert.Equal(options.Options.Length, options.Names.Length);
        Assert.Equal("Model default", options.Names[0]);
        Assert.Contains("most accurate", options.Names[^1]);
    }

    [Theory]
    [InlineData("cuda:0", true)]
    [InlineData("cuda:0+cuda:1", true)]
    [InlineData("cpu", true)]
    [InlineData("vulkan", null)]
    [InlineData("vulkan:1", null)]
    public void GraphDecode_IsLeftToTheEngineOnVulkan(string deviceKey, bool? expected)
    {
        LocalSettings settings = new() { GraphDecode = true };
        TextRequest request = HartsyLocalLLMProvider.BuildRequestCore(new ExtendedLLMInput { Model = "m.gguf" }, [], null, deviceKey, settings);
        Assert.Equal(expected, request.GraphDecode);
        TextRequest off = HartsyLocalLLMProvider.BuildRequestCore(new ExtendedLLMInput { Model = "m.gguf" }, [], null, deviceKey, new LocalSettings());
        Assert.Null(off.GraphDecode);
    }

    [Fact]
    public void AnthropicModelList_ReadsIdsAndNamesInOrder()
    {
        JObject body = JObject.Parse("""
            {"data": [
              {"type": "model", "id": "claude-opus-4-8", "display_name": "Claude Opus 4.8", "created_at": "2026-05-01T00:00:00Z"},
              {"type": "model", "id": "claude-haiku-4-5", "display_name": ""},
              {"type": "model", "id": "", "display_name": "Broken"}
            ], "has_more": false}
            """);
        List<(string Id, string Name)> models = AnthropicLLMProvider.ParseModelList(body);
        List<(string Id, string Name)> expected = [("claude-opus-4-8", "Claude Opus 4.8"), ("claude-haiku-4-5", "claude-haiku-4-5")];
        Assert.Equal(expected, models);
        Assert.Empty(AnthropicLLMProvider.ParseModelList(null));
    }

    [Fact]
    public void ModelChoices_ListEachModelOnceWithItsId()
    {
        JObject result = BackendCardAPI.ModelChoices([("gpt-4o", "gpt-4o"), ("claude-opus-4-8", "Claude Opus 4.8"), ("gpt-4o", "again"), ("", "blank")]);
        string[] values = ["gpt-4o", "claude-opus-4-8"];
        string[] names = ["gpt-4o", "Claude Opus 4.8 (claude-opus-4-8)"];
        Assert.Equal(values, result["values"]!.Values<string>().ToArray());
        Assert.Equal(names, result["names"]!.Values<string>().ToArray());
        Assert.Null(result["reason"]);
        // "reason", not "error": core logs an error for any reply with an error key, and an empty list is an expected state.
        JObject empty = BackendCardAPI.ModelChoices([], "offline");
        Assert.Equal("offline", empty["reason"]!.ToString());
        Assert.Null(empty["error"]);
    }

    /// <summary>The three settings classes, by the backend type id their card renders for.</summary>
    private static readonly Dictionary<string, Type> SettingsByType = new()
    {
        ["llmassistant-hartsy-local"] = typeof(LocalSettings),
        ["llmassistant-openai"] = typeof(RemoteOpenAILLMProvider.RemoteOpenAILLMProviderSettings),
        ["llmassistant-anthropic"] = typeof(AnthropicLLMProvider.AnthropicLLMProviderSettings)
    };

    /// <summary>Every tooltip is short, plain and free of em dashes.</summary>
    [Fact]
    public void Tooltips_AreShort()
    {
        foreach (Type settings in SettingsByType.Values)
        {
            foreach (FieldInfo field in SettingFields(settings))
            {
                string comment = field.GetCustomAttribute<AutoConfiguration.ConfigComment>()?.Comments;
                Assert.False(string.IsNullOrWhiteSpace(comment), $"{field.Name} has no tooltip");
                Assert.DoesNotContain("\u2014", comment);
                Assert.True(comment.Length <= 300, $"{field.Name}'s tooltip is {comment.Length} characters");
                Assert.True(comment.Split('\n').Length <= 3, $"{field.Name}'s tooltip has more than 3 lines");
            }
        }
    }

    /// <summary>The card script puts every field of each card in exactly one section and gives it a label.</summary>
    [Fact]
    public void CardScript_PlacesEveryField()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "Assets", "llm-backend-cards.js"));
        foreach ((string typeId, Type settings) in SettingsByType)
        {
            string block = LayoutBlock(script, typeId);
            List<string> placed = [];
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(block, @"fields: \[([^\]]*)\]"))
            {
                placed.AddRange(match.Groups[1].Value.Split(',').Select(n => n.Trim().Trim('\'')).Where(n => n.Length > 0));
            }
            List<string> expected = [.. SettingFields(settings).Select(f => f.Name)];
            Assert.Equal(expected.Order(), placed.Order());
            foreach (string name in expected)
            {
                Assert.Contains($"{name}: {{ label: '", block);
            }
        }
    }

    /// <summary>Every guide link the card script makes, and every link in the README's contents, lands on a README heading or anchor.</summary>
    [Fact]
    public void GuideLinks_MatchReadmeHeadings()
    {
        string root = RepoRoot();
        string readme = File.ReadAllText(Path.Combine(root, "README.md"));
        HashSet<string> anchors = ReadmeAnchors(readme);
        string script = File.ReadAllText(Path.Combine(root, "Assets", "llm-backend-cards.js"));
        List<string> used = [.. System.Text.RegularExpressions.Regex.Matches(script, @"guide: '([^']+)'").Select(m => m.Groups[1].Value)];
        Assert.NotEmpty(used);
        foreach (string anchor in used.Concat(System.Text.RegularExpressions.Regex.Matches(readme, @"\]\(#([^)]+)\)").Select(m => m.Groups[1].Value)))
        {
            Assert.True(anchors.Contains(anchor), $"No README heading or anchor for #{anchor}");
        }
    }

    /// <summary>The public instance fields a settings class saves, in declaration order.</summary>
    private static IEnumerable<FieldInfo> SettingFields(Type settings)
        => settings.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => f.DeclaringType == settings);

    /// <summary>The part of the script's layouts table for one backend type.</summary>
    private static string LayoutBlock(string script, string typeId)
    {
        int start = script.IndexOf($"'{typeId}': {{", StringComparison.Ordinal);
        Assert.True(start >= 0, $"The card script has no layout for {typeId}");
        int next = script.IndexOf("'llmassistant-", start + typeId.Length + 4, StringComparison.Ordinal);
        int end = script.IndexOf("/** LLMAssistantListGpus", start, StringComparison.Ordinal);
        return script[start..(next > start && next < end ? next : end)];
    }

    /// <summary>GitHub's anchors for the README: each heading's slug (repeats get -1, -2) plus every explicit &lt;a id&gt;.</summary>
    private static HashSet<string> ReadmeAnchors(string readme)
    {
        HashSet<string> anchors = [];
        Dictionary<string, int> seen = [];
        bool inFence = false;
        foreach (string line in readme.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence || !line.StartsWith('#'))
            {
                continue;
            }
            string text = line.TrimStart('#').Trim();
            string slug = new([.. text.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').Select(c => c == ' ' ? '-' : c)]);
            int count = seen.GetValueOrDefault(slug);
            seen[slug] = count + 1;
            anchors.Add(count == 0 ? slug : $"{slug}-{count}");
        }
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(readme, "<a id=\"([^\"]+)\"></a>"))
        {
            anchors.Add(match.Groups[1].Value);
        }
        return anchors;
    }

    /// <summary>The extension's source folder, found by walking up from the test binary.</summary>
    private static string RepoRoot()
    {
        DirectoryInfo dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SwarmUI-LLMAssistant.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
