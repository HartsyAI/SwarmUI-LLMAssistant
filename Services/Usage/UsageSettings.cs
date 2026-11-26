using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.Usage;

/// <summary>What one model costs per million tokens, in the table's currency.</summary>
/// <param name="Input">The price of a million prompt tokens.</param>
/// <param name="Output">The price of a million tokens the model writes.</param>
/// <param name="Cached">The price of a million prompt tokens the endpoint served from its cache, or null to charge them as ordinary input.</param>
public sealed record ModelPrice(decimal Input, decimal Output, decimal? Cached = null)
{
    /// <summary>What these tokens cost. <paramref name="cached"/> is the part of <paramref name="prompt"/> that came from the cache (as OpenAI-compatible endpoints report it), never more than it.</summary>
    public decimal Cost(long prompt, long completion, long cached = 0)
    {
        long input = Math.Max(0, prompt);
        long fromCache = Math.Clamp(cached, 0, input);
        return ((input - fromCache) * Input + fromCache * (Cached ?? Input) + Math.Max(0, completion) * Output) / 1_000_000m;
    }

    /// <summary>The JSON the price table stores: <c>{input, output, cached}</c>.</summary>
    public JObject ToJson() => new()
    {
        ["input"] = Input,
        ["output"] = Output,
        ["cached"] = Cached is decimal cached ? new JValue(cached) : JValue.CreateNull(),
    };
}

/// <summary>The soft budget: a warning once spending in a day or a week reaches a limit. It never stops a request.</summary>
/// <param name="Enabled">Whether warnings are shown. Off unless the user switches it on.</param>
/// <param name="Daily">The day's limit in the table's currency; 0 means no daily limit.</param>
/// <param name="Weekly">The week's limit in the table's currency; 0 means no weekly limit.</param>
public sealed record UsageBudget(bool Enabled = false, decimal Daily = 0, decimal Weekly = 0)
{
    /// <summary>The JSON the settings store: <c>{enabled, daily, weekly}</c>.</summary>
    public JObject ToJson() => new() { ["enabled"] = Enabled, ["daily"] = Daily, ["weekly"] = Weekly };
}

/// <summary>The user's price table, its currency label and the soft budget, kept in the user's own settings layer under <see cref="Key"/>.
/// Nothing is bundled: a model has a cost only when the user typed its price in.</summary>
/// <param name="Currency">A label shown beside every cost. It is only a label: nothing is converted.</param>
/// <param name="Prices">Prices by model id, exactly as the model list names it.</param>
/// <param name="Budget">The soft budget.</param>
public sealed record UsageSettings(string Currency, IReadOnlyDictionary<string, ModelPrice> Prices, UsageBudget Budget)
{
    /// <summary>The settings key. Only <c>LLMAssistantSaveUsageSettings</c> writes it.</summary>
    public const string Key = "usageCost";

    /// <summary>The currency label when the user has set none.</summary>
    public const string DefaultCurrency = "USD";

    /// <summary>The longest currency label.</summary>
    public const int MaxCurrencyChars = 12;

    /// <summary>The most models the table may hold.</summary>
    public const int MaxModels = 500;

    /// <summary>The longest model id.</summary>
    public const int MaxModelChars = 300;

    /// <summary>The largest price or limit accepted.</summary>
    public const decimal MaxAmount = 1_000_000_000m;

    /// <summary>The price of <paramref name="model"/>, or null when the table has none for it.</summary>
    public ModelPrice PriceFor(string model) => !string.IsNullOrEmpty(model) && Prices.TryGetValue(model, out ModelPrice price) ? price : null;

    /// <summary>Reads the stored settings. Anything missing or unusable is left out rather than guessed at.</summary>
    public static UsageSettings FromSettings(JObject settings)
    {
        JObject stored = settings?[Key] as JObject;
        string currency = TryParseCurrency(stored?["currency"], out string label, out _) ? label : DefaultCurrency;
        Dictionary<string, ModelPrice> prices = new(StringComparer.Ordinal);
        if (stored?["prices"] is JObject table)
        {
            foreach (JProperty row in table.Properties())
            {
                if (TryParsePrice(row.Name, row.Value, out ModelPrice price, out _))
                {
                    prices[row.Name] = price;
                }
            }
        }
        return new UsageSettings(currency, prices, TryParseBudget(stored?["budget"], out UsageBudget budget, out _) ? budget : new UsageBudget());
    }

    /// <summary>The JSON <c>LLMAssistantGetUsageSettings</c> returns and the settings store: <c>{currency, prices, budget}</c>.</summary>
    public JObject ToJson()
    {
        JObject table = [];
        foreach (KeyValuePair<string, ModelPrice> row in Prices)
        {
            table[row.Key] = row.Value.ToJson();
        }
        return new JObject { ["currency"] = Currency, ["prices"] = table, ["budget"] = Budget.ToJson() };
    }

    /// <summary>These settings with the parts <paramref name="patch"/> holds (<c>currency</c>, <c>prices</c>, <c>budget</c>) replaced: a <c>prices</c> table
    /// replaces the whole table, so a model left out of it loses its price. Returns null and says why when a part is unusable.</summary>
    public UsageSettings With(JObject patch, out string error)
    {
        error = null;
        UsageSettings result = this;
        if (patch?["currency"] is JToken currencyToken)
        {
            if (!TryParseCurrency(currencyToken, out string currency, out error))
            {
                return null;
            }
            result = result with { Currency = currency };
        }
        if (patch?["prices"] is JToken pricesToken)
        {
            if (pricesToken is not JObject table)
            {
                error = "prices must be an object of model id to {input, output, cached}.";
                return null;
            }
            if (table.Count > MaxModels)
            {
                error = $"The price table can hold at most {MaxModels} models.";
                return null;
            }
            Dictionary<string, ModelPrice> prices = new(StringComparer.Ordinal);
            foreach (JProperty row in table.Properties())
            {
                if (!TryParsePrice(row.Name, row.Value, out ModelPrice price, out error))
                {
                    return null;
                }
                prices[row.Name] = price;
            }
            result = result with { Prices = prices };
        }
        if (patch?["budget"] is JToken budgetToken)
        {
            if (!TryParseBudget(budgetToken, out UsageBudget budget, out error))
            {
                return null;
            }
            result = result with { Budget = budget };
        }
        return result;
    }

    /// <summary>A currency label: trimmed, short, printable; empty means <see cref="DefaultCurrency"/>.</summary>
    private static bool TryParseCurrency(JToken token, out string currency, out string error)
    {
        currency = DefaultCurrency;
        error = null;
        if (token is null || token.Type == JTokenType.Null)
        {
            return true;
        }
        string text = token.Type == JTokenType.String ? token.ToString().Trim() : null;
        if (text is null || text.Length > MaxCurrencyChars || text.Any(char.IsControl))
        {
            error = $"currency must be a label of at most {MaxCurrencyChars} characters.";
            return false;
        }
        currency = text.Length == 0 ? DefaultCurrency : text;
        return true;
    }

    /// <summary>One row of the table. Input and output are both required, so a half-filled row can never produce a half-made cost.</summary>
    private static bool TryParsePrice(string model, JToken token, out ModelPrice price, out string error)
    {
        price = null;
        error = null;
        if (string.IsNullOrWhiteSpace(model) || model.Length > MaxModelChars)
        {
            error = $"A model id must have 1 to {MaxModelChars} characters.";
            return false;
        }
        if (token is not JObject row || !TryAmount(row["input"], out decimal? input) || !TryAmount(row["output"], out decimal? output) || !TryAmount(row["cached"], out decimal? cached)
            || input is null || output is null)
        {
            error = $"The price of '{model}' needs input and output as numbers of 0 or more (per million tokens); cached is optional.";
            return false;
        }
        price = new ModelPrice(input.Value, output.Value, cached);
        return true;
    }

    /// <summary>The budget: <c>{enabled, daily, weekly}</c>, all optional.</summary>
    private static bool TryParseBudget(JToken token, out UsageBudget budget, out string error)
    {
        budget = new UsageBudget();
        error = null;
        if (token is null || token.Type == JTokenType.Null)
        {
            return true;
        }
        if (token is not JObject obj || (obj["enabled"] is JToken enabled && enabled.Type != JTokenType.Boolean) || !TryAmount(obj["daily"], out decimal? daily) || !TryAmount(obj["weekly"], out decimal? weekly))
        {
            error = "budget must be {enabled: true or false, daily, weekly}, with limits of 0 or more.";
            return false;
        }
        budget = new UsageBudget(obj.Value<bool?>("enabled") ?? false, daily ?? 0, weekly ?? 0);
        return true;
    }

    /// <summary>A number from 0 to <see cref="MaxAmount"/>. Missing, null and empty text give a null amount; anything else unusable is false.</summary>
    private static bool TryAmount(JToken token, out decimal? amount)
    {
        amount = null;
        if (token is null || token.Type == JTokenType.Null || (token.Type == JTokenType.String && string.IsNullOrWhiteSpace(token.ToString())))
        {
            return true;
        }
        decimal value;
        if (token.Type is JTokenType.Integer or JTokenType.Float)
        {
            double number = token.Value<double>();
            if (!double.IsFinite(number) || number < 0 || number > (double)MaxAmount)
            {
                return false;
            }
            value = (decimal)number;
        }
        else if (token.Type != JTokenType.String || !decimal.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }
        if (value < 0 || value > MaxAmount)
        {
            return false;
        }
        amount = value;
        return true;
    }
}
