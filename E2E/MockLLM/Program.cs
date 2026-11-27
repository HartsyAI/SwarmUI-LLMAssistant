using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// A scripted OpenAI-compatible server for browser e2e tests. No model, no GPU.
//
//   GET    /v1/models              the model list ("mock-model" with a 4000-token window, "mock-model-b" with none stated and no tool support)
//   POST   /v1/chat/completions    streaming (SSE) or plain; the reply comes from the active scenario
//   PUT    /__scenarios            replace the scenario list (JSON array of rules, see below)
//   GET    /__requests             every request body seen, oldest first
//   DELETE /__requests             forget them
//   GET    /__health               "ok"
//   POST   /v1/embeddings          a deterministic stand-in: words of one group (cat, kitten, feline; dog, puppy; car, vehicle; invoice, bill) add to the same
//                                  dimension, so a query can find text that shares no word with it. A model named "no-embed-model" is refused (404)
//   GET    /__embeddings           every embeddings request seen ({model, inputs}), kept apart from chat requests
//   DELETE /__embeddings           forget them
//   PUT    /__embeddings/fail      { "status": 500 } makes embeddings answer with that status (0 = work again)
//
// A rule is { "match": "text", "regex": "pattern", "model": "id", "rounds": [ round, ... ] }.
// A rule applies when the LAST USER message contains `match` (or matches `regex`) and the request's model equals `model`
// (each field optional; a rule with none matches everything). The first matching rule wins.
// A round is { "text", "reasoning", "toolCalls": [ { "name", "arguments", "id" } ], "usage": { "prompt_tokens", "completion_tokens" },
//              "delayMs", "chunkDelayMs", "error": { "status", "message" }, "noUsage" }. "noUsage": true streams no token counts, as a server that reports none.
// Which round answers a request is the number of assistant messages after the last user message, so a tool-call loop walks
// the rounds in order and the mock keeps no state between requests. Past the last round the last round repeats.
// With no matching rule the reply is "Mock reply: <last user text>".

string[] argv = Environment.GetCommandLineArgs();
int port = 17802;
for (int i = 0; i < argv.Length - 1; i++)
{
    if (argv[i] == "--port")
    {
        port = int.Parse(argv[i + 1]);
    }
}

WebApplicationBuilder builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://localhost:{port}");
WebApplication app = builder.Build();

object gate = new();
JsonArray scenarios = [];
List<JsonObject> requests = [];

app.MapGet("/__health", () => "ok");

app.MapPut("/__scenarios", async (HttpRequest req) =>
{
    using StreamReader reader = new(req.Body);
    JsonNode node = JsonNode.Parse(await reader.ReadToEndAsync());
    lock (gate)
    {
        scenarios = node as JsonArray ?? [];
    }
    return Results.Ok(new { count = scenarios.Count });
});

app.MapGet("/__requests", () =>
{
    lock (gate)
    {
        return Results.Json(requests.Select(r => r.DeepClone()).ToArray());
    }
});

app.MapDelete("/__requests", () =>
{
    lock (gate)
    {
        requests.Clear();
    }
    return Results.Ok();
});

// ---- Embeddings ----
List<JsonObject> embeddingRequests = [];
int embeddingFail = 0;
string[][] wordGroups = [["cat", "cats", "kitten", "feline", "tabby"], ["dog", "dogs", "puppy", "canine", "hound"], ["car", "cars", "vehicle", "automobile", "engine"], ["invoice", "bill", "payment", "receipt"]];
const int EmbeddingDimension = 24;

// A word's own dimension (or its group's), so the same text always gives the same vector, in any process.
float[] Embed(string text)
{
    float[] vector = new float[EmbeddingDimension];
    foreach (System.Text.RegularExpressions.Match word in System.Text.RegularExpressions.Regex.Matches((text ?? "").ToLowerInvariant(), @"[\p{L}\p{N}]+"))
    {
        int group = Array.FindIndex(wordGroups, g => g.Contains(word.Value));
        int hash = 17;
        foreach (char c in word.Value)
        {
            hash = unchecked(hash * 31 + c);
        }
        vector[group >= 0 ? group : wordGroups.Length + (hash & int.MaxValue) % (EmbeddingDimension - wordGroups.Length)] += 1;
    }
    return vector;
}

app.MapGet("/__embeddings", () =>
{
    lock (gate)
    {
        return Results.Json(embeddingRequests.Select(r => r.DeepClone()).ToArray());
    }
});

app.MapDelete("/__embeddings", () =>
{
    lock (gate)
    {
        embeddingRequests.Clear();
    }
    return Results.Ok();
});

app.MapPut("/__embeddings/fail", async (HttpRequest req) =>
{
    using StreamReader reader = new(req.Body);
    JsonNode node = JsonNode.Parse(await reader.ReadToEndAsync());
    lock (gate)
    {
        embeddingFail = node?["status"]?.GetValue<int>() ?? 0;
    }
    return Results.Ok();
});

app.MapPost("/v1/embeddings", async (HttpContext ctx) =>
{
    using StreamReader reader = new(ctx.Request.Body);
    JsonObject body = JsonNode.Parse(await reader.ReadToEndAsync()) as JsonObject ?? [];
    string model = body["model"]?.GetValue<string>() ?? "";
    List<string> inputs = body["input"] is JsonArray many ? [.. many.Select(n => n?.GetValue<string>() ?? "")] : [body["input"]?.GetValue<string>() ?? ""];
    int fail;
    lock (gate)
    {
        embeddingRequests.Add(new JsonObject { ["model"] = model, ["inputs"] = new JsonArray([.. inputs.Select(i => (JsonNode)JsonValue.Create(i))]) });
        fail = embeddingFail;
    }
    if (fail > 0 || model == "no-embed-model")
    {
        ctx.Response.StatusCode = fail > 0 ? fail : 404;
        await ctx.Response.WriteAsJsonAsync(new { error = new { message = fail > 0 ? "mock embeddings failure" : $"model '{model}' not found", type = "mock_error" } });
        return;
    }
    // Answered out of order on purpose when asked for more than one, so a client that ignores "index" is caught.
    JsonArray data = [];
    for (int i = inputs.Count - 1; i >= 0; i--)
    {
        data.Add(new JsonObject { ["object"] = "embedding", ["index"] = i, ["embedding"] = new JsonArray([.. Embed(inputs[i]).Select(v => (JsonNode)JsonValue.Create(v))]) });
    }
    await ctx.Response.WriteAsJsonAsync(new JsonObject { ["object"] = "list", ["model"] = model, ["data"] = data });
});

app.MapGet("/v1/models", () => Results.Json(new
{
    @object = "list",
    // mock-model states a small window (so a long scripted chat crosses the compaction threshold); mock-model-b states none, and says (as OpenRouter would) that it takes no tools.
    data = new object[] { new { id = "mock-model", @object = "model", owned_by = "mock", context_length = 4000 }, new { id = "mock-model-b", @object = "model", owned_by = "mock", supported_parameters = new[] { "temperature" } } }
}));

app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
{
    using StreamReader reader = new(ctx.Request.Body);
    JsonObject body = JsonNode.Parse(await reader.ReadToEndAsync()) as JsonObject ?? [];
    lock (gate)
    {
        requests.Add(new JsonObject { ["path"] = "/v1/chat/completions", ["body"] = body.DeepClone() });
        if (requests.Count > 500)
        {
            requests.RemoveAt(0);
        }
    }
    string model = body["model"]?.GetValue<string>() ?? "mock-model";
    JsonArray messages = body["messages"] as JsonArray ?? [];
    int lastUser = -1;
    for (int i = 0; i < messages.Count; i++)
    {
        if (messages[i]?["role"]?.GetValue<string>() == "user")
        {
            lastUser = i;
        }
    }
    string userText = lastUser >= 0 ? TextOf(messages[lastUser]?["content"]) : "";
    int assistantsAfter = 0;
    for (int i = lastUser + 1; i < messages.Count; i++)
    {
        if (messages[i]?["role"]?.GetValue<string>() == "assistant")
        {
            assistantsAfter++;
        }
    }
    JsonObject round = PickRound(model, userText, assistantsAfter);
    bool stream = body["stream"]?.GetValue<bool>() ?? false;
    bool includeUsage = body["stream_options"]?["include_usage"]?.GetValue<bool>() ?? false;
    int delay = round["delayMs"]?.GetValue<int>() ?? 0;
    if (delay > 0)
    {
        await Task.Delay(delay, ctx.RequestAborted);
    }
    if (round["error"] is JsonObject err)
    {
        ctx.Response.StatusCode = err["status"]?.GetValue<int>() ?? 500;
        await ctx.Response.WriteAsJsonAsync(new { error = new { message = err["message"]?.GetValue<string>() ?? "mock error", type = "mock_error" } });
        return;
    }
    string text = round["text"]?.GetValue<string>() ?? "";
    string reasoning = round["reasoning"]?.GetValue<string>() ?? "";
    JsonArray calls = round["toolCalls"] as JsonArray ?? [];
    int promptTokens = round["usage"]?["prompt_tokens"]?.GetValue<int>() ?? Math.Max(1, messages.ToJsonString().Length / 4);
    int completionTokens = round["usage"]?["completion_tokens"]?.GetValue<int>() ?? Math.Max(1, (text.Length + reasoning.Length) / 4);
    string finish = calls.Count > 0 ? "tool_calls" : "stop";
    string id = "chatcmpl-mock" + Guid.NewGuid().ToString("N")[..8];
    long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    if (!stream)
    {
        JsonObject message = new() { ["role"] = "assistant", ["content"] = text };
        if (calls.Count > 0)
        {
            message["tool_calls"] = ToolCallsNode(calls, split: false);
        }
        JsonObject full = new()
        {
            ["id"] = id,
            ["object"] = "chat.completion",
            ["created"] = created,
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = finish }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = promptTokens, ["completion_tokens"] = completionTokens, ["total_tokens"] = promptTokens + completionTokens }
        };
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(full.ToJsonString());
        return;
    }
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    int chunkDelay = round["chunkDelayMs"]?.GetValue<int>() ?? 0;
    async Task Send(JsonObject delta, string finishReason, JsonObject usage = null)
    {
        JsonObject chunk = new()
        {
            ["id"] = id,
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = model,
            ["choices"] = usage is null ? new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finishReason }) : new JsonArray()
        };
        if (usage is not null)
        {
            chunk["usage"] = usage;
        }
        await ctx.Response.WriteAsync("data: " + chunk.ToJsonString() + "\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        if (chunkDelay > 0)
        {
            await Task.Delay(chunkDelay, ctx.RequestAborted);
        }
    }
    await Send(new JsonObject { ["role"] = "assistant", ["content"] = "" }, null);
    foreach (string piece in Pieces(reasoning))
    {
        await Send(new JsonObject { ["reasoning_content"] = piece }, null);
    }
    foreach (string piece in Pieces(text))
    {
        await Send(new JsonObject { ["content"] = piece }, null);
    }
    if (calls.Count > 0)
    {
        JsonArray first = ToolCallsNode(calls, split: true, part: 0);
        JsonArray second = ToolCallsNode(calls, split: true, part: 1);
        await Send(new JsonObject { ["tool_calls"] = first }, null);
        await Send(new JsonObject { ["tool_calls"] = second }, null);
    }
    await Send([], finish);
    if (includeUsage && round["noUsage"]?.GetValue<bool>() != true)
    {
        await Send([], null, new JsonObject { ["prompt_tokens"] = promptTokens, ["completion_tokens"] = completionTokens, ["total_tokens"] = promptTokens + completionTokens });
    }
    await ctx.Response.WriteAsync("data: [DONE]\n\n", ctx.RequestAborted);
});

app.Run();

/// <summary>Plain text of a message's content, which is a string or an array of typed parts.</summary>
static string TextOf(JsonNode content)
{
    if (content is null)
    {
        return "";
    }
    if (content is JsonValue)
    {
        return content.GetValue<string>();
    }
    if (content is JsonArray parts)
    {
        StringBuilder sb = new();
        foreach (JsonNode part in parts)
        {
            if (part?["text"] is JsonNode t)
            {
                sb.Append(t.GetValue<string>());
            }
        }
        return sb.ToString();
    }
    return "";
}

/// <summary>Splits text into small pieces so the client sees several SSE chunks.</summary>
static IEnumerable<string> Pieces(string text)
{
    if (string.IsNullOrEmpty(text))
    {
        yield break;
    }
    int at = 0;
    while (at < text.Length)
    {
        int next = text.IndexOf(' ', Math.Min(at + 1, text.Length - 1));
        next = next < 0 ? text.Length : next + 1;
        yield return text[at..next];
        at = next;
    }
}

/// <summary>The tool_calls array. Streaming splits each call's arguments over two deltas, as real servers do.</summary>
static JsonArray ToolCallsNode(JsonArray calls, bool split, int part = 0)
{
    JsonArray result = [];
    for (int i = 0; i < calls.Count; i++)
    {
        JsonNode call = calls[i];
        string name = call["name"]?.GetValue<string>() ?? "unknown";
        string callId = call["id"]?.GetValue<string>() ?? $"call_mock_{i}";
        JsonNode argNode = call["arguments"];
        string args = argNode is null ? "{}" : argNode is JsonValue ? argNode.GetValue<string>() : argNode.ToJsonString();
        if (!split)
        {
            result.Add(new JsonObject { ["id"] = callId, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args } });
            continue;
        }
        int cut = args.Length / 2;
        if (part == 0)
        {
            result.Add(new JsonObject { ["index"] = i, ["id"] = callId, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args[..cut] } });
        }
        else
        {
            result.Add(new JsonObject { ["index"] = i, ["function"] = new JsonObject { ["arguments"] = args[cut..] } });
        }
    }
    return result;
}

/// <summary>Chooses the scenario round for a request.</summary>
JsonObject PickRound(string model, string userText, int roundIndex)
{
    JsonArray snapshot;
    lock (gate)
    {
        snapshot = (JsonArray)scenarios.DeepClone();
    }
    foreach (JsonNode rule in snapshot)
    {
        string matchModel = rule["model"]?.GetValue<string>();
        string match = rule["match"]?.GetValue<string>();
        string regex = rule["regex"]?.GetValue<string>();
        if (matchModel is not null && matchModel != model)
        {
            continue;
        }
        if (match is not null && !userText.Contains(match, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }
        if (regex is not null && !System.Text.RegularExpressions.Regex.IsMatch(userText, regex))
        {
            continue;
        }
        if (rule["rounds"] is JsonArray rounds && rounds.Count > 0)
        {
            return (JsonObject)rounds[Math.Min(roundIndex, rounds.Count - 1)].DeepClone();
        }
    }
    return new JsonObject { ["text"] = "Mock reply: " + userText };
}
