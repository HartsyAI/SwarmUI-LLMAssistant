using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: make an HTTP request to a URL (GET/POST/etc).
/// Uses SwarmUI's native <see cref="NetworkBackendUtils.MakeHttpClient"/> helper so it inherits
/// SwarmUI's connection handler, user-agent, and timeouts. Blocks private/loopback addresses for safety.</summary>
public class HttpRequestTool : ToolHandler
{
    public override string HandlerId => ToolConstants.HttpRequest;

    private static readonly HttpClient _http = NetworkBackendUtils.MakeHttpClient();

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
        if (IsPrivateOrLoopback(uri))
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
            // Optional headers
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
            // Optional body
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
            using HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);

            // Collect response headers
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

            // Read body up to maxBytes
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

    /// <summary>Returns true if the URI resolves to a loopback, private, link-local, or otherwise non-routable address.
    /// Prevents SSRF to internal SwarmUI services, cloud metadata endpoints, LAN hosts, etc.</summary>
    private static bool IsPrivateOrLoopback(Uri uri)
    {
        string host = uri.Host;
        if (string.IsNullOrEmpty(host))
        {
            return true;
        }
        // Explicit metadata endpoints
        if (host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (IPAddress.TryParse(host, out IPAddress ip))
        {
            return IsPrivateIp(ip);
        }
        // Resolve and check all addresses
        try
        {
            IPAddress[] addrs = Dns.GetHostAddresses(host);
            foreach (IPAddress a in addrs)
            {
                if (IsPrivateIp(a))
                {
                    return true;
                }
            }
        }
        catch
        {
            // DNS failure — let the request attempt so the caller sees a real error
            return false;
        }
        return false;
    }

    private static bool IsPrivateIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            // 10.0.0.0/8
            if (b[0] == 10) return true;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return true;
            // 169.254.0.0/16 (link-local, includes cloud metadata 169.254.169.254)
            if (b[0] == 169 && b[1] == 254) return true;
            // 127.0.0.0/8
            if (b[0] == 127) return true;
            // 0.0.0.0/8
            if (b[0] == 0) return true;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            // fc00::/7 unique-local
            byte[] b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;
            // ::1 loopback handled above
        }
        return false;
    }
}
