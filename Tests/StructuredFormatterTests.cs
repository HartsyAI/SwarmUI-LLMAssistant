using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services.PromptAssets;
using Hartsy.Extensions.LLMAssistant.Services.PromptRewrite;
using Hartsy.Extensions.LLMAssistant.Services.PromptStyles;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Golden cases for the formatters of style pack 1: Flux.3 (caption and layout), Fibo 2, Ming Image and MOSS Image, and the tag-style check the SDXL and SD 1 styles use. No backend, no SwarmUI host.</summary>
public class StructuredFormatterTests
{
    private static readonly RewriteSettings Settings = new("", 5, 2048, 0.4);

    private static PromptStyle Style(string formatter, string format = "json", string negative = "none", string supports = "")
        => new(formatter, "image", formatter, "d", [], format, formatter, negative, [.. supports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)], [], "", "Write a prompt.", "bundled");

    private static RewriteRequest Request(string prompt, int w = 0, int h = 0) => new(prompt, "", "img.safetensors", w, h, "m", "", true);

    private sealed class Scripted(params string[] replies)
    {
        public int Calls;

        public Task<LLMGenerateResult> Ask(string system, string user, CancellationToken ct)
            => Task.FromResult(LLMGenerateResult.FromText(replies[Math.Min(Interlocked.Increment(ref Calls) - 1, replies.Length - 1)]));
    }

    private static string Compact(string json) => JToken.Parse(json).ToString(Newtonsoft.Json.Formatting.None);

    // ---- Flux.3: caption, then a JSON array of rows ----

    private const string FluxCaption = "Flat vector poster of a lighthouse <lighthouse_1> on a cliff under a pale yellow sky <background_1>, with the title <En_Text_1> across the top in bold navy capitals.";

    private const string FluxRows = """[{"id":"background_1","bbox":[0,0,1000,1000],"desc":"A smooth gradient from pale yellow to soft peach."},{"id":"lighthouse_1","bbox":[250,550,900,800],"desc":"A white lighthouse with a red band."},{"id":"En_Text_1","bbox":[60,100,200,900],"desc":"The words \"NORTH POINT\" in bold navy capitals."}]""";

    private static FormatOutcome Flux(string raw) => new Flux3Formatter().Normalize(raw, Style("flux-3", "text"));

    [Fact]
    public void AGoodFluxCaptionWithLayoutComesBackOnOneLineUnchanged()
    {
        FormatOutcome outcome = Flux($"{FluxCaption} {FluxRows}");
        Assert.Null(outcome.Error);
        Assert.Equal($"{FluxCaption} {FluxRows}", outcome.Text);
        Assert.Empty(outcome.Warnings);
    }

    [Fact]
    public void FluxRowKeysAreReorderedAndUnknownKeysDropped()
    {
        string messy = FluxCaption + " " + """[{"desc":"A gradient.","bbox":[0,0,1000,1000],"id":"background_1","note":"x"},{"bbox":[250,550,900,800],"desc":"A lighthouse.","id":"lighthouse_1"},{"desc":"Words.","id":"En_Text_1","bbox":[60,100,200,900]}]""";
        FormatOutcome outcome = Flux(messy);
        Assert.Null(outcome.Error);
        Assert.Equal(FluxCaption + " " + """[{"id":"background_1","bbox":[0,0,1000,1000],"desc":"A gradient."},{"id":"lighthouse_1","bbox":[250,550,900,800],"desc":"A lighthouse."},{"id":"En_Text_1","bbox":[60,100,200,900],"desc":"Words."}]""", outcome.Text);
        Assert.Contains(outcome.Warnings, w => w.Contains("note"));
    }

    [Fact]
    public void FencedAndChattyFluxAnswersAreCleaned()
    {
        Assert.Equal($"{FluxCaption} {FluxRows}", Flux($"Here is the prompt:\n```\n{FluxCaption} {FluxRows}\n```").Text);
        Assert.Equal($"{FluxCaption} {FluxRows}", Flux($"```text\n{FluxCaption} {FluxRows}\n```").Text);
        Assert.Equal($"{FluxCaption} {FluxRows}", Flux($"<think>placing boxes</think>\n{FluxCaption}\n{FluxRows}").Text);
    }

    [Fact]
    public void AFluxBoxOutOfRangeIsClampedAndABadOneRefused()
    {
        FormatOutcome clamped = Flux("A dog <dog_1>. " + """[{"id":"dog_1","bbox":[-5,0,1200,1000.4],"desc":"A dog."}]""");
        Assert.Null(clamped.Error);
        Assert.Contains("\"bbox\":[0,0,1000,1000]", clamped.Text);
        Assert.Contains(clamped.Warnings, w => w.Contains("clamped"));
        Assert.Contains("needs a bbox", Flux("A dog <dog_1>. " + """[{"id":"dog_1","bbox":[800,100,200,200],"desc":"A dog."}]""").Error);
        Assert.Contains("needs a bbox", Flux("A dog <dog_1>. " + """[{"id":"dog_1","desc":"A dog."}]""").Error);
        Assert.Contains("needs a bbox", Flux("A dog <dog_1>. " + """[{"id":"dog_1","bbox":[1,2,3],"desc":"A dog."}]""").Error);
        Assert.Contains("fractions", Flux("A dog <dog_1>. " + """[{"id":"dog_1","bbox":[0.1,0.1,0.9,0.9],"desc":"A dog."}]""").Error);
    }

    [Theory]
    [InlineData("A dog <dog_1> and a cat <cat_1>. [{\"id\":\"dog_1\",\"bbox\":[0,0,500,500],\"desc\":\"A dog.\"}]", "cat_1")]
    [InlineData("A dog. [{\"id\":\"dog_1\",\"bbox\":[0,0,500,500],\"desc\":\"A dog.\"}]", "never names <dog_1>")]
    [InlineData("A dog <dog_1> <dog_1>. [{\"id\":\"dog_1\",\"bbox\":[0,0,500,500],\"desc\":\"A.\"},{\"id\":\"dog_1\",\"bbox\":[0,0,500,500],\"desc\":\"B.\"}]", "used twice")]
    [InlineData("A dog <dog>. [{\"id\":\"dog\",\"bbox\":[0,0,500,500],\"desc\":\"A dog.\"}]", "needs an \"id\"")]
    [InlineData("A dog <break_1>. [{\"id\":\"dog_1\",\"bbox\":[0,0,500,500],\"desc\":\"A dog.\"}]", "break_1")]
    [InlineData("A dog <dog_1>. [{\"id\":\"dog_1\",\"bbox\":[0,0,500,500],\"desc\":\"A dog.\"},", "not one valid JSON array")]
    [InlineData("[{\"id\":\"dog_1\",\"bbox\":[0,0,500,500],\"desc\":\"A dog.\"}]", "no caption")]
    [InlineData("A dog <dog_1>. [{\"id\":\"dog_1\",\"bbox\":[0,0,500,500],\"desc\":\"\"}]", "non-empty \"desc\"")]
    [InlineData("A dog <dog_1>. [{\"id\":\"dog_1\",\"bbox\":[0,0,500,500],\"desc\":\"A {{SWARM_TAG_0}} dog.\"}]", "inside a layout row")]
    public void ABrokenFluxLayoutIsRefusedWithAReason(string raw, string mentions)
    {
        FormatOutcome outcome = Flux(raw);
        Assert.Null(outcome.Text);
        Assert.Contains(mentions, outcome.Error);
    }

    [Fact]
    public void AFluxCaptionWithoutALayoutIsAPlainCaption()
    {
        Assert.Equal("A red kayak on a still alpine lake at sunrise.", Flux("Sure, here you go:\nA red kayak on a still alpine lake\nat sunrise.").Text.Replace("sunrise.", "sunrise.").Replace("lake at", "lake at"));
        Assert.Contains("empty", Flux("  ").Error);
        Assert.Null(new Flux3Formatter().Wrap("a cat"));
    }

    [Fact]
    public void OnlyRealLayoutIdsAreLetThroughTheInventedTagCheck()
    {
        string text = $"{FluxCaption} {FluxRows}";
        Flux3Formatter formatter = new();
        Assert.True(formatter.IsOwnTag("<lighthouse_1>", text));
        Assert.True(formatter.IsOwnTag("<En_Text_1>", text));
        Assert.False(formatter.IsOwnTag("<dog_1>", text));
        Assert.False(formatter.IsOwnTag("<break>", text));
        Assert.False(formatter.IsOwnTag("<lora:lighthouse_1>", text));
        Assert.False(formatter.IsOwnTag("<lighthouse_1>", FluxCaption));
        Assert.False(formatter.EscapesTags(text));
    }

    [Fact]
    public async Task AFluxRewriteKeepsCitesAndPutsUserTagsBackInTheCaption()
    {
        Scripted model = new($"Flat poster of a lighthouse <lighthouse_1> {{{{SWARM_TAG_0}}}} at dusk. " + """[{"id":"lighthouse_1","bbox":[250,550,900,800],"desc":"A white lighthouse."}]""");
        RewriteResult result = await PromptRewriter.RunAsync(Style("flux-3", "text"), Request("lighthouse <lora:sea:0.8>"), Settings, model.Ask, default);
        Assert.True(result.Rewritten);
        Assert.False(result.Retried);
        Assert.Equal("Flat poster of a lighthouse <lighthouse_1> <lora:sea:0.8> at dusk. " + """[{"id":"lighthouse_1","bbox":[250,550,900,800],"desc":"A white lighthouse."}]""", result.Text);
    }

    [Fact]
    public async Task AFluxRewriteStillRefusesATagTheModelMadeUp()
    {
        Scripted model = new("Poster <lighthouse_1> <lora:sea:1>. " + """[{"id":"lighthouse_1","bbox":[250,550,900,800],"desc":"A lighthouse."}]""",
            "A plain poster of a lighthouse at dusk.");
        RewriteResult result = await PromptRewriter.RunAsync(Style("flux-3", "text"), Request("lighthouse"), Settings, model.Ask, default);
        Assert.True(result.Retried);
        Assert.Equal("A plain poster of a lighthouse at dusk.", result.Text);
    }

    // ---- Fibo 2 ----

    private static FormatOutcome Fibo(string raw) => new Fibo2Formatter().Normalize(raw, Style("fibo-2"));

    private const string FiboGood = """{"short_description":"A white lighthouse on a rocky headland at dusk.","objects":[{"description":"A tall white lighthouse with a red lantern room.","location":"center-right","relative_size":"large within frame"}],"background_setting":"Dark sea with long swells.","lighting":{"conditions":"low dusk light","direction":"from the west","shadows":"long and soft"},"aesthetics":{"composition":"vertical","color_scheme":"violet and amber","mood_atmosphere":"quiet"},"photographic_characteristics":{"depth_of_field":"deep","focus":"sharp on the tower","camera_angle":"slightly low","lens_focal_length":"35mm"},"style_medium":"photograph","context":"A fine-art landscape photograph.","artistic_style":"realistic","text_render":[{"text":"NORTH POINT","location":"top","color":"white"}]}""";

    [Fact]
    public void AGoodFiboCaptionComesBackCompactAndUnchanged()
    {
        FormatOutcome outcome = Fibo(FiboGood);
        Assert.Null(outcome.Error);
        Assert.Equal(FiboGood, outcome.Text);
        Assert.Empty(outcome.Warnings);
    }

    [Fact]
    public void FiboKeysAreReorderedEmptyValuesDroppedAndUnknownKeysKept()
    {
        string messy = """{"style_medium":"photograph","short_description":" A lighthouse. ","lighting":{"shadows":"long","conditions":"dusk"},"objects":[{"location":"center","description":"A tower.","texture":""}],"background_setting":"","text_render":[],"artistic_style":"realistic","mystery":"x"}""";
        FormatOutcome outcome = Fibo(messy);
        Assert.Null(outcome.Error);
        Assert.Equal("""{"short_description":"A lighthouse.","objects":[{"description":"A tower.","location":"center"}],"lighting":{"conditions":"dusk","shadows":"long"},"style_medium":"photograph","artistic_style":"realistic","mystery":"x"}""", outcome.Text);
        Assert.Contains(outcome.Warnings, w => w.Contains("mystery"));
    }

    [Fact]
    public void FencedAndChattyFiboAnswersAreCleaned()
    {
        Assert.Equal(FiboGood, Fibo($"Here is the caption:\n```json\n{FiboGood}\n```").Text);
        Assert.Equal(FiboGood, Fibo($"<think>hmm</think>{FiboGood}").Text);
    }

    [Theory]
    [InlineData("a lighthouse at dusk", "not a JSON object")]
    [InlineData("""{"objects":[{"description":"x"}]}""", "short_description")]
    [InlineData("""{"short_description":"x","objects":[{"location":"center"}]}""", "needs a non-empty \"description\"")]
    [InlineData("""{"short_description":"x","text_render":[{"location":"top"}]}""", "needs a non-empty \"text\"")]
    [InlineData("""{"short_description":"x","lighting":"soft"}""", "lighting must be an object")]
    [InlineData("""{"short_description":"x","objects":"a tower"}""", "list of objects")]
    [InlineData("""{"short_description":"x","style_medium":["photo"]}""", "style_medium must be a string")]
    public void ABrokenFiboAnswerIsRefusedWithAReason(string raw, string mentions)
    {
        FormatOutcome outcome = Fibo(raw);
        Assert.Null(outcome.Text);
        Assert.Contains(mentions, outcome.Error);
    }

    [Fact]
    public void AFailedFiboRewriteLeavesTheUsersWordsAsTheTrainedPlainShortForm()
    {
        Assert.Null(new Fibo2Formatter().Wrap("a cat"));
        Assert.True(new Fibo2Formatter().ProducesJson);
    }

    // ---- Ming Image ----

    private static FormatOutcome Ming(string raw) => new MingImageFormatter().Normalize(raw, Style("ming-image"));

    private const string MingGood = """{"canvas_settings":{"aspect_ratio":"2:3, 1664 × 2496 px","ambient_lighting":"flat, even studio light","image_style":"Bold mid-century screen-print poster."},"layers":[{"description":"Deep navy background.","coordinates":"cx: 0.500, cy: 0.500, w: 1.000, h: 1.000","hierarchy_and_relation":"Bottom layer.","color_specs":["#14213D","#FCA311"]},{"description":"Large capitals reading \"JAZZ NIGHT\" in cream.","coordinates":"cx: 0.500, cy: 0.120, w: 0.800, h: 0.140","hierarchy_and_relation":"Top of the canvas.","color_specs":["#F5E6C8"]}]}""";

    [Fact]
    public void AGoodMingCaptionComesBackCompactAndUnchanged()
    {
        FormatOutcome outcome = Ming(MingGood);
        Assert.Null(outcome.Error);
        Assert.Equal(MingGood, outcome.Text);
        Assert.Empty(outcome.Warnings);
    }

    [Fact]
    public void MingKeysAreOrderedColorsFixedAndMissingPartsFilled()
    {
        string messy = """{"layers":[{"color_specs":["#c98b4b","#fff","red"],"coordinates":"cx:0.5,cy:0.5,w:1,h:1","description":"Background.","extra":1}],"canvas_settings":{"image_style":"s","ambient_lighting":"l","aspect_ratio":"1:1, 1024 × 1024 px"}}""";
        FormatOutcome outcome = Ming(messy);
        Assert.Null(outcome.Error);
        Assert.Equal("""{"canvas_settings":{"aspect_ratio":"1:1, 1024 × 1024 px","ambient_lighting":"l","image_style":"s"},"layers":[{"description":"Background.","coordinates":"cx: 0.500, cy: 0.500, w: 1.000, h: 1.000","hierarchy_and_relation":"","color_specs":["#C98B4B","#FFFFFF"]}]}""", outcome.Text);
        Assert.Contains(outcome.Warnings, w => w.Contains("extra"));
        Assert.Contains(outcome.Warnings, w => w.Contains("#RRGGBB"));
        FormatOutcome none = Ming("""{"canvas_settings":{"aspect_ratio":"a","ambient_lighting":"l","image_style":"s"},"layers":[{"description":"d","coordinates":"cx: 0.5, cy: 0.5, w: 0.5, h: 0.5"}]}""");
        Assert.Contains("\"color_specs\":[]", none.Text);
    }

    [Fact]
    public void MingCoordinatesAsAnObjectAreWrittenAsTheStringAndABoxOutsideTheCanvasIsMovedIn()
    {
        string raw = """{"canvas_settings":{"aspect_ratio":"a","ambient_lighting":"l","image_style":"s"},"layers":[{"description":"d","coordinates":{"cx":0.5,"cy":0.12,"w":0.8,"h":0.14}},{"description":"e","coordinates":"cx: 0.950, cy: 0.500, w: 0.400, h: 0.400"},{"description":"f","coordinates":"cx: 0.5, cy: 0.5, w: 1.4, h: 0.5"}]}""";
        FormatOutcome outcome = Ming(raw);
        Assert.Null(outcome.Error);
        JArray layers = (JArray)JObject.Parse(outcome.Text)["layers"];
        Assert.Equal("cx: 0.500, cy: 0.120, w: 0.800, h: 0.140", (string)layers[0]["coordinates"]);
        Assert.Equal("cx: 0.800, cy: 0.500, w: 0.400, h: 0.400", (string)layers[1]["coordinates"]);
        Assert.Equal("cx: 0.500, cy: 0.500, w: 1.000, h: 0.500", (string)layers[2]["coordinates"]);
        Assert.Equal(3, outcome.Warnings.Count);
    }

    [Fact]
    public void MingCoordinatesAreWrittenWithPointsWhateverTheMachineCulture()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            FormatOutcome outcome = Ming(MingGood);
            Assert.Null(outcome.Error);
            Assert.Contains("cx: 0.500, cy: 0.120, w: 0.800, h: 0.140", outcome.Text);
            Assert.DoesNotContain("0,500", outcome.Text);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData("a jazz poster", "not a JSON object")]
    [InlineData("""{"layers":[{"description":"d","coordinates":"cx: 0.5, cy: 0.5, w: 1, h: 1"}]}""", "canvas_settings")]
    [InlineData("""{"canvas_settings":{"aspect_ratio":"a","ambient_lighting":"l"},"layers":[{"description":"d","coordinates":"cx: 0.5, cy: 0.5, w: 1, h: 1"}]}""", "image_style")]
    [InlineData("""{"canvas_settings":{"aspect_ratio":"a","ambient_lighting":"l","image_style":"s"},"layers":[]}""", "non-empty list")]
    [InlineData("""{"canvas_settings":{"aspect_ratio":"a","ambient_lighting":"l","image_style":"s"},"layers":[{"coordinates":"cx: 0.5, cy: 0.5, w: 1, h: 1"}]}""", "description")]
    [InlineData("""{"canvas_settings":{"aspect_ratio":"a","ambient_lighting":"l","image_style":"s"},"layers":[{"description":"d","coordinates":"center"}]}""", "one string")]
    [InlineData("""{"canvas_settings":{"aspect_ratio":"a","ambient_lighting":"l","image_style":"s"},"layers":[{"description":"d","coordinates":"cx: 500, cy: 500, w: 200, h: 200"}]}""", "pixels")]
    [InlineData("""{"canvas_settings":{"aspect_ratio":"a","ambient_lighting":"l","image_style":"s"},"layers":[{"description":"d","coordinates":"cx: 0.5, cy: 0.5, w: 0, h: 0.5"}]}""", "above 0")]
    public void ABrokenMingAnswerIsRefusedWithAReason(string raw, string mentions)
    {
        FormatOutcome outcome = Ming(raw);
        Assert.Null(outcome.Text);
        Assert.Contains(mentions, outcome.Error);
    }

    // ---- MOSS Image ----

    private static FormatOutcome Moss(string raw, int w = 0, int h = 0) => new MossImageFormatter().Normalize(raw, Style("moss-image", "text", "separate"), w, h);

    private const string MossDraft = """{"width":1024,"height":1024,"bbox_coord_space":"abs","bbox_format":"xyxy","summary":{"caption":"A minimal travel poster.","meta":["poster"],"canvas":"single"},"items":[{"label":"object","caption":"A small white sailboat.","bbox":[128,128,896,512]},{"label":"text","caption":"Thin sans-serif capitals in navy.","text":"CALM WATERS","bbox":[212,250,812,330]}]}""";

    private const string MossCompiled = """{"summary":{"caption":"A minimal travel poster.","meta":["poster"],"canvas":"single"},"items":[{"label":"object","caption":"A small white sailboat.","bbox":[-384,-384,0,384]},{"label":"text","caption":"Thin sans-serif capitals in navy.","text":"CALM WATERS","bbox":[-262,-300,-182,300]}],"bbox_coord_space":"abs_centralized","bbox_format":"yxyx"}""";

    [Fact]
    public void AMossDraftIsCompiledToCentredYxyxBoxesLikeTheOfficialCompiler()
    {
        // The first box is the one the guide works out by hand: x1=128, y1=128, x2=896, y2=512 on 1024 becomes [-384,-384,0,384].
        FormatOutcome outcome = Moss(MossDraft, 1024, 1024);
        Assert.Null(outcome.Error);
        Assert.Equal(MossCompiled, outcome.Text);
        Assert.Equal(MossCompiled, Moss(MossDraft).Text);
        Assert.Equal(MossCompiled, Moss($"Sure, here is the layout:\n```json\n{MossDraft}\n```", 1024, 1024).Text);
    }

    [Fact]
    public void MossCompilesNonSquareCanvasesRoundingMinDownAndMaxUp()
    {
        string draft = """{"width":1024,"height":768,"summary":{"caption":"c"},"items":[{"label":"object","caption":"a","bbox":[10.6,20.2,100.1,200.9]}]}""";
        FormatOutcome outcome = Moss(draft, 1024, 768);
        Assert.Null(outcome.Error);
        // x1=floor(10.6)=10, y1=floor(20.2)=20, x2=ceil(100.1)=101, y2=ceil(200.9)=201; minus width/2=512 and height/2=384.
        Assert.Contains("\"bbox\":[-364,-502,-183,-411]", outcome.Text);
    }

    [Fact]
    public void AMossPlainCaptionIsAcceptedAsItIs()
    {
        FormatOutcome outcome = Moss("Here is the prompt:\nA rain-soaked night street in a port town, a yellow bicycle against a lamp post.", 1024, 1024);
        Assert.Null(outcome.Error);
        Assert.Equal("A rain-soaked night street in a port town, a yellow bicycle against a lamp post.", outcome.Text);
        Assert.Null(new MossImageFormatter().Wrap("a cat"));
        Assert.False(new MossImageFormatter().EscapesTags(outcome.Text));
        Assert.True(new MossImageFormatter().EscapesTags(MossCompiled));
    }

    [Theory]
    [InlineData("""{"width":1000,"height":1024,"summary":{"caption":"c"},"items":[{"label":"object","caption":"a","bbox":[0,0,10,10]}]}""", 0, 0, "multiples of 16")]
    [InlineData("""{"summary":{"caption":"c"},"items":[{"label":"object","caption":"a","bbox":[0,0,10,10]}]}""", 0, 0, "multiples of 16")]
    [InlineData("""{"width":1024,"height":1024,"summary":{"caption":"c"},"items":[{"label":"object","caption":"a","bbox":[0,0,10,10]}]}""", 1024, 768, "image size, 1024 and 768")]
    [InlineData("""{"width":1024,"height":1024,"bbox_coord_space":"rel","summary":{"caption":"c"},"items":[{"label":"object","caption":"a","bbox":[0,0,10,10]}]}""", 0, 0, "abs")]
    [InlineData("""{"width":1024,"height":1024,"summary":{},"items":[{"label":"object","caption":"a","bbox":[0,0,10,10]}]}""", 0, 0, "summary")]
    [InlineData("""{"width":1024,"height":1024,"summary":{"caption":"c"},"items":[]}""", 0, 0, "non-empty")]
    [InlineData("""{"width":1024,"height":1024,"summary":{"caption":"c"},"items":[{"label":"banner","caption":"a","bbox":[0,0,10,10]}]}""", 0, 0, "label")]
    [InlineData("""{"width":1024,"height":1024,"summary":{"caption":"c"},"items":[{"label":"text","caption":"a","bbox":[0,0,10,10]}]}""", 0, 0, "exact characters")]
    [InlineData("""{"width":1024,"height":1024,"summary":{"caption":"c"},"items":[{"label":"object","caption":"a","bbox":[0,0,2000,10]}]}""", 0, 0, "outside")]
    [InlineData("""{"width":1024,"height":1024,"summary":{"caption":"c"},"items":[{"label":"object","caption":"a","bbox":[-4,0,10,10]}]}""", 0, 0, "outside")]
    [InlineData("""{"width":1024,"height":1024,"summary":{"caption":"c"},"items":[{"label":"object","caption":"a","bbox":[500,0,100,10]}]}""", 0, 0, "x_min less than x_max")]
    [InlineData("""{"width":1024,"height":1024,"summary":{"caption":"c"},"items":[{"label":"object","caption":"a","bbox":[0.1,0.1,0.9,0.9]}]}""", 0, 0, "fractions")]
    [InlineData("""{"width":1024,"height":1024,"summary":{"caption":"c"},"items":[{"label":"object","caption":"a"}]}""", 0, 0, "four numbers")]
    public void ABrokenMossLayoutIsRefusedWithAReason(string raw, int w, int h, string mentions)
    {
        FormatOutcome outcome = Moss(raw, w, h);
        Assert.Null(outcome.Text);
        Assert.Contains(mentions, outcome.Error);
    }

    [Fact]
    public async Task AMossLayoutRewriteIsCompiledAndEscapesATagInsideTheJson()
    {
        string draft = MossDraft.Replace("A minimal travel poster.", "A poster {{SWARM_TAG_0}}.");
        Scripted model = new(draft);
        RewriteResult result = await PromptRewriter.RunAsync(Style("moss-image", "text", "separate"), Request("poster <wildcard:a\"b>", 1024, 1024), Settings, model.Ask, default);
        Assert.True(result.Rewritten);
        JObject json = JObject.Parse(result.Text);
        Assert.Equal("A poster <wildcard:a\"b>.", (string)json["summary"]["caption"]);
        Assert.Equal("abs_centralized", (string)json["bbox_coord_space"]);
    }

    [Fact]
    public async Task AMossRewriteWhoseCanvasIsNotTheImageSizeIsAskedAgain()
    {
        Scripted model = new(MossDraft, "A calm travel poster of a small sailboat on a pale sea.");
        RewriteResult result = await PromptRewriter.RunAsync(Style("moss-image", "text", "separate"), Request("poster", 1024, 768), Settings, model.Ask, default);
        Assert.True(result.Retried);
        Assert.Equal("A calm travel poster of a small sailboat on a pale sea.", result.Text);
        Assert.Equal(2, model.Calls);
    }

    // ---- the formatters are found by name ----

    [Fact]
    public void TheNewFormattersAreRegisteredByTheNameAStyleGives()
    {
        Assert.IsType<Flux3Formatter>(PromptFormatters.For(Style("flux-3", "text")));
        Assert.IsType<Fibo2Formatter>(PromptFormatters.For(Style("fibo-2")));
        Assert.IsType<MingImageFormatter>(PromptFormatters.For(Style("ming-image")));
        Assert.IsType<MossImageFormatter>(PromptFormatters.For(Style("moss-image", "text")));
        Assert.All(new[] { "flux-3", "fibo-2", "ming-image", "moss-image" }, id => Assert.True(PromptFormatters.Exists(id)));
    }

    // ---- tag style, with the bundled SDXL and SD 1 styles ----

    private static PromptStyle Bundled(string id, [CallerFilePath] string here = "")
        => PromptStyleStore.Load(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here)) ?? "", "Assets", "prompt-assets"), PromptAsset.ScopeBundled).First(s => s.Id == id);

    [Theory]
    [InlineData("sdxl")]
    [InlineData("sd1-sd2")]
    public void TheClipStylesAcceptWeightsAndRefuseBrokenBrackets(string id)
    {
        PromptStyle style = Bundled(id);
        FormatOutcome ok = PromptFormatters.For(style).Normalize("a fisherman mending a net, (golden hour:1.2), mist, 35mm photograph", style);
        Assert.Null(ok.Error);
        Assert.Empty(ok.Warnings);
        Assert.Contains("Mist".ToLowerInvariant(), ok.Text);
        Assert.Contains("round brackets", PromptFormatters.For(style).Normalize("a fisherman, (golden hour:1.2, mist", style).Error);
        FormatOutcome broken = PromptFormatters.For(style).Normalize("a fisherman,\n(golden hour:1.2),\nmist", style);
        Assert.Equal("a fisherman, (golden hour:1.2), mist", broken.Text);
        Assert.Contains(PromptFormatters.For(style).Normalize("a fisherman BREAK mist", style).Warnings, w => w.Contains("BREAK"));
    }

    [Fact]
    public void AStyleThatDoesNotListWeightsWarnsWhenTheModelWritesThem()
    {
        PromptStyle style = Bundled("sd3") with { Format = "tags" };
        FormatOutcome outcome = PromptFormatters.For(style).Normalize("a fox, (snow:1.3)", style);
        Assert.Contains(outcome.Warnings, w => w.Contains("weights"));
    }
}
