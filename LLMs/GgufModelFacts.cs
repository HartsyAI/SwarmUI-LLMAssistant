namespace Hartsy.Extensions.LLMAssistant.LLMs;

/// <summary>What a GGUF header says about a model, for the model list: its context length, architecture family and quantization. A field the header does not carry is <c>-1</c> or null.</summary>
/// <param name="ContextLength">Trained context window in tokens, or <c>-1</c> when the header does not record one.</param>
/// <param name="Family">The <c>general.architecture</c> value, e.g. <c>qwen2</c>, or null.</param>
/// <param name="Quantization">The llama.cpp file type name, e.g. <c>Q4_K_M</c>, or null.</param>
public sealed record GgufModelFacts(int ContextLength, string Family, string Quantization)
{
    /// <summary>The facts of a file whose header could not be read.</summary>
    public static GgufModelFacts Unknown { get; } = new(-1, null, null);
}
