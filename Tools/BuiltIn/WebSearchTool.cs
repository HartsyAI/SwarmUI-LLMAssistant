using System.Net.Http;
using System.Text.RegularExpressions;
using System.Web;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: basic web search via DuckDuckGo HTML scraping.</summary>
public partial class WebSearchTool : ToolHandler
{
    public override string HandlerId => ToolConstants.WebSearch;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    [GeneratedRegex(@"<a rel=""nofollow"" class=""result__a"" href=""([^""]+)"">([^<]+)</a>.*?<a class=""result__snippet""[^>]*>([^<]*)</a>", RegexOptions.Singleline)]
    private static partial Regex ResultRegex();

    public override async Task<JObject> Execute(ToolExecutionContext ctx)
    {
        JObject args = ctx.Args;
        CancellationToken ct = ctx.Ct;
        string query = args["query"]?.ToString();
        if (string.IsNullOrWhiteSpace(query))
        {
            return new JObject { ["success"] = false, ["error"] = "query is required" };
        }
        int limit = args["limit"]?.Value<int>() ?? 5;
        if (limit < 1) limit = 1;
        if (limit > 10) limit = 10;
        try
        {
            string url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
            using HttpRequestMessage req = new(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (SwarmUI-LLMAssistant)");
            using HttpResponseMessage resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return new JObject { ["success"] = false, ["error"] = $"Search failed with HTTP {(int)resp.StatusCode}" };
            }
            string html = await resp.Content.ReadAsStringAsync(ct);
            JArray results = [];
            MatchCollection matches = ResultRegex().Matches(html);
            foreach (Match m in matches)
            {
                if (results.Count >= limit)
                {
                    break;
                }
                string resultUrl = HttpUtility.HtmlDecode(m.Groups[1].Value);
                // DuckDuckGo wraps URLs; try to extract the real target
                if (resultUrl.StartsWith("//"))
                {
                    resultUrl = "https:" + resultUrl;
                }
                int uddgIdx = resultUrl.IndexOf("uddg=");
                if (uddgIdx >= 0)
                {
                    string encoded = resultUrl.Substring(uddgIdx + 5);
                    int amp = encoded.IndexOf('&');
                    if (amp >= 0)
                    {
                        encoded = encoded.Substring(0, amp);
                    }
                    resultUrl = HttpUtility.UrlDecode(encoded);
                }
                string title = HttpUtility.HtmlDecode(StripTags(m.Groups[2].Value)).Trim();
                string snippet = HttpUtility.HtmlDecode(StripTags(m.Groups[3].Value)).Trim();
                results.Add(new JObject
                {
                    ["title"] = title,
                    ["url"] = resultUrl,
                    ["snippet"] = snippet
                });
            }
            return new JObject
            {
                ["success"] = true,
                ["query"] = query,
                ["count"] = results.Count,
                ["results"] = results
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] WebSearchTool failed: {ex.Message}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    private static string StripTags(string input)
    {
        return Regex.Replace(input ?? "", "<[^>]+>", "");
    }
}
