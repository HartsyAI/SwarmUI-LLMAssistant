using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Extensions.LLMAssistant.Services;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: make an HTTP request to a URL (GET/POST/etc).
/// Uses SwarmUI's native <see cref="NetworkBackendUtils.MakeHttpClient"/> helper so it inherits
/// SwarmUI's connection handler, user-agent, and timeouts. Blocks private/loopback addresses for safety.</summary>
public class HttpRequestTool : ToolHandler
{
    public override string HandlerId => ToolConstants.HttpRequest;

    private static readonly HttpClient Http = NetworkBackendUtils.MakeHttpClient();

    public override async Task<JObject> Execute(ToolExecutionContext ctx)
    {
        JObject args = ctx.Args;
        CancellationToken ct = ctx.Ct;
        string url = args["url"]?.ToString();
        if (string.IsNullOrWhiteSpace(url))
        {
            return new JObject { ["success"] = false, ["error"] = "url is required" };
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new JObject { ["success"] = false, ["error"] = "url must be an absolute http(s) URL" };
        }
        if (NetworkSafety.IsPrivateOrLoopback(uri))
        {
            return new JObject { ["success"] = false, ["error"] = "Requests to loopback/private/link-local addresses are blocked." };
        }

        string methodName = (args["method"]?.ToString() ?? "GET").ToUpperInvariant();
        HttpMethod method = methodName switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "DELETE" => HttpMethod.Delete,
            "HEAD" => HttpMethod.Head,
            "PATCH" => HttpMethod.Patch,
            _ => null
        };
        if (method is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Unsupported method '{methodName}'" };
        }

        int maxBytes = args["maxBytes"]?.Value<int?>() ?? 262144;
        if (maxBytes <= 0) maxBytes = 262144;
        if (maxBytes > 2 * 1024 * 1024) maxBytes = 2 * 1024 * 1024;

        int timeoutSeconds = args["timeoutSeconds"]?.Value<int?>() ?? 30;
        if (timeoutSeconds <= 0) timeoutSeconds = 30;
        if (timeoutSeconds > 120) timeoutSeconds = 120;

        try
        {
            using HttpRequestMessage req = new(method, uri);
            if (args["headers"] is JObject headers)
            {
                foreach (KeyValuePair<string, JToken> kvp in headers)
                {
                    string name = kvp.Key;
                    string value = kvp.Value?.ToString() ?? "";
                    // Content headers must go on the content, not the request.
                    if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // handled below when body is set
                    }
                    req.Headers.TryAddWithoutValidation(name, value);
                }
            }
            string bodyStr = args["body"]?.ToString();
            if (!string.IsNullOrEmpty(bodyStr) && method != HttpMethod.Get && method != HttpMethod.Head)
            {
                string contentType = "text/plain";
                if (args["headers"] is JObject h2)
                {
                    foreach (KeyValuePair<string, JToken> kvp in h2)
                    {
                        if (kvp.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        {
                            contentType = kvp.Value?.ToString() ?? contentType;
                        }
                    }
                }
                req.Content = new StringContent(bodyStr, Encoding.UTF8, contentType);
            }

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using HttpResponseMessage resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);

            JObject respHeaders = new();
            foreach (KeyValuePair<string, IEnumerable<string>> h in resp.Headers)
            {
                respHeaders[h.Key] = string.Join(", ", h.Value);
            }
            if (resp.Content is not null)
            {
                foreach (KeyValuePair<string, IEnumerable<string>> h in resp.Content.Headers)
                {
                    respHeaders[h.Key] = string.Join(", ", h.Value);
                }
            }

            string bodyText = "";
            bool truncated = false;
            long? contentLength = resp.Content?.Headers?.ContentLength;
            if (resp.Content is not null)
            {
                await using System.IO.Stream stream = await resp.Content.ReadAsStreamAsync(linked.Token);
                byte[] buffer = new byte[maxBytes];
                int totalRead = 0;
                while (totalRead < maxBytes)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(totalRead, maxBytes - totalRead), linked.Token);
                    if (read == 0)
                    {
                        break;
                    }
                    totalRead += read;
                }
                // Try to drain a tiny bit more just to see if there's overflow
                if (totalRead == maxBytes)
                {
                    byte[] peek = new byte[1];
                    int extra = await stream.ReadAsync(peek.AsMemory(0, 1), linked.Token);
                    if (extra > 0)
                    {
                        truncated = true;
                    }
                }
                bodyText = Encoding.UTF8.GetString(buffer, 0, totalRead);
            }

            return new JObject
            {
                ["success"] = true,
                ["url"] = uri.ToString(),
                ["method"] = methodName,
                ["status"] = (int)resp.StatusCode,
                ["statusText"] = resp.ReasonPhrase ?? "",
                ["ok"] = resp.IsSuccessStatusCode,
                ["headers"] = respHeaders,
                ["contentLength"] = contentLength ?? -1,
                ["truncated"] = truncated,
                ["body"] = bodyText
            };
        }
        catch (TaskCanceledException)
        {
            return new JObject { ["success"] = false, ["error"] = $"Request timed out after {timeoutSeconds}s" };
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] HttpRequestTool failed: {ex.Message}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

}
