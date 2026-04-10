using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Text2Image;

namespace SwarmUI.Extensions.LLMAssistant.WebAPI;

/// <summary>Model listing endpoints for LLM models.</summary>
public static class ModelEndpoints
{
    /// <summary>Returns available LLM models from the model registry.</summary>
    public static async Task<JObject> LLMAssistantGetModels(Session session)
    {
        JArray models = [];
        if (Program.T2IModelSets.TryGetValue("LLM", out T2IModelHandler handler))
        {
            foreach (string name in handler.ListModelNamesFor(session))
            {
                if (name == "(None)")
                {
                    continue;
                }
                T2IModel model = handler.GetModel(name);
                JObject entry = new()
                {
                    ["name"] = name,
                    ["title"] = model?.Title ?? name
                };
                if (model?.Description is not null)
                {
                    entry["description"] = model.Description;
                }
                models.Add(entry);
            }
        }
        return new JObject
        {
            ["success"] = true,
            ["models"] = models
        };
    }
}
