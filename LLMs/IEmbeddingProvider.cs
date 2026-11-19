using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.LLMs;

/// <summary>A backend that can turn text into embedding vectors, as a capability separate from chatting: a provider is chosen for embeddings by what it can do, not by its name, and the
/// model it uses is the user's own setting. The knowledge library searches by meaning through whichever provider (and model) the user picked here, and by keywords alone when none is
/// picked, so nothing in it depends on one provider or one model.</summary>
public interface IEmbeddingProvider
{
    /// <summary>The same id as the provider's <see cref="ILLMProvider.Id"/>, so a setting can name it.</summary>
    string EmbeddingProviderId { get; }

    /// <summary>The provider's name for lists.</summary>
    string EmbeddingProviderName { get; }

    /// <summary>Whether this backend serves embeddings at all (a provider whose API has none says false, and is not offered).</summary>
    bool SupportsEmbeddings { get; }

    /// <summary>Embeds each of <paramref name="inputs"/> with <paramref name="model"/>, in order. <paramref name="session"/> is the user it is done for (so a key the user brought is used).
    /// A failure throws with a message that says why.</summary>
    Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, Session session, CancellationToken ct);
}
