using System.Reflection;
using Hartsy.Extensions.LLMAssistant.Backends;
using SwarmUI.Core;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The default-model dropdown must offer exactly the models the provider lists, so a typo cannot be saved
/// and a model added to the list cannot be missing from the dropdown.</summary>
public class AnthropicDefaultModelOptionsTests
{
    [Fact]
    public void DefaultModelDropdown_OffersExactlyTheListedModels()
    {
        FieldInfo field = typeof(AnthropicLLMProvider.AnthropicLLMProviderSettings).GetField(nameof(AnthropicLLMProvider.AnthropicLLMProviderSettings.DefaultModel))!;
        ManualSettingsOptionsAttribute options = field.GetCustomAttribute<ManualSettingsOptionsAttribute>()!;
        Assert.NotNull(options);
        string[] listed = AnthropicLLMProvider.KnownModels.Select(model => model.Id).ToArray();
        Assert.Equal(listed, options.Options);
        Assert.Equal(options.Options.Length, options.Names.Length);
    }

    [Fact]
    public void DefaultModel_DefaultsToAListedModel()
    {
        string defaultModel = new AnthropicLLMProvider.AnthropicLLMProviderSettings().DefaultModel;
        Assert.Contains(defaultModel, AnthropicLLMProvider.KnownModels.Select(model => model.Id));
    }
}
