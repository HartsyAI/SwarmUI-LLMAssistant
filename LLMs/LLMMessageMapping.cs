using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.LLMs;

/// <summary>Shared shape rules for tool turns: a stored tool call's arguments and result as the text or JSON a provider expects.</summary>
public static class LLMMessageMapping
{
    /// <summary>A stored call's arguments as a JSON object; a string holding JSON is parsed, anything else is an empty object.</summary>
    public static JObject ArgumentsObject(JToken arguments)
    {
        if (arguments is JObject obj)
        {
            return obj;
        }
        if (arguments is JValue value && value.Type == JTokenType.String)
        {
            try
            {
                return JObject.Parse(value.ToString());
            }
            catch (JsonException)
            {
                return new JObject();
            }
        }
        return new JObject();
    }

    /// <summary>The engine's tool definitions for a request. The model calls a tool by its id, so the id is the name.</summary>
    public static List<HartsyInference.Engine.Requests.ToolDefinition> ToEngineTools(IEnumerable<JObject> tools) => tools is null
        ? []
        : [.. tools.Select(t => new HartsyInference.Engine.Requests.ToolDefinition
        {
            Name = t["id"]?.ToString() ?? t["name"]?.ToString() ?? "",
            Description = t["description"]?.ToString() ?? "",
            JsonSchema = (t["parameters"] as JObject ?? new JObject()).ToString(Formatting.None),
        })];

    /// <summary>A message in the engine's shape, for counting and reconstructing a round (its content is not sent to a model).</summary>
    public static HartsyInference.Engine.Requests.TextMessage ToSeedTextMessage(LLMMessage message) => new()
    {
        Role = ToTextRole(message.Role),
        Content = message.Content ?? "",
    };

    /// <summary>A message the engine's tool loop appended, mapped back to the extension's shape with the tool-call fields kept.</summary>
    public static LLMMessage FromAppendedTextMessage(HartsyInference.Engine.Requests.TextMessage message) => new()
    {
        Role = message.Role switch
        {
            HartsyInference.Engine.Requests.TextRole.Assistant => LLMRoles.Assistant,
            HartsyInference.Engine.Requests.TextRole.Tool => LLMRoles.Tool,
            HartsyInference.Engine.Requests.TextRole.System => LLMRoles.System,
            _ => LLMRoles.User,
        },
        Content = message.Content,
        ToolCallId = message.ToolCallId,
        Name = message.Name,
        ToolCalls = message.ToolCalls is { Count: > 0 } calls
            ? [.. calls.Select(c => new JObject { ["id"] = c.Id, ["name"] = c.Name, ["arguments"] = ArgumentsOrRaw(new JValue(c.Arguments)) })]
            : null,
    };

    /// <summary>The engine role for an extension role; anything unknown is a user turn.</summary>
    private static HartsyInference.Engine.Requests.TextRole ToTextRole(string role) => role switch
    {
        LLMRoles.Assistant => HartsyInference.Engine.Requests.TextRole.Assistant,
        LLMRoles.Tool => HartsyInference.Engine.Requests.TextRole.Tool,
        LLMRoles.System => HartsyInference.Engine.Requests.TextRole.System,
        _ => HartsyInference.Engine.Requests.TextRole.User,
    };

    /// <summary>A call's arguments as the object a provider expects, or the raw text when it is not one JSON object, so history keeps what the model wrote.</summary>
    public static JToken ArgumentsOrRaw(JToken arguments)
    {
        if (arguments is JObject obj)
        {
            return obj;
        }
        if (arguments is JValue { Type: JTokenType.String } text)
        {
            return StreamedArguments(text.ToString());
        }
        return new JObject();
    }

    /// <summary>A streamed call's arguments for a <c>native_tool_call</c> frame: the parsed object, an empty object for no text, or the raw
    /// text when it is not one JSON object, so the turn answers the model with an error instead of running the tool on <c>{}</c>.</summary>
    public static JToken StreamedArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JObject();
        }
        return TryParseArguments(json, out JObject parsed, out _) ? parsed : new JValue(json);
    }

    /// <summary>Parses a call's arguments as one JSON object (blank text is an empty one); false, with the problem, for anything else.</summary>
    public static bool TryParseArguments(string json, out JObject arguments, out string problem)
    {
        arguments = new JObject();
        problem = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }
        try
        {
            if (JToken.Parse(json) is JObject parsed)
            {
                arguments = parsed;
                return true;
            }
            problem = "they are not a JSON object";
        }
        catch (JsonException ex)
        {
            problem = ex.Message;
        }
        return false;
    }

    /// <summary>A stored call's arguments as the JSON text OpenAI-shaped APIs expect.</summary>
    public static string ArgumentsText(JToken arguments)
    {
        if (arguments is JValue value && value.Type == JTokenType.String)
        {
            return string.IsNullOrWhiteSpace(value.ToString()) ? "{}" : value.ToString();
        }
        return arguments is null ? "{}" : arguments.ToString(Formatting.None);
    }
}
