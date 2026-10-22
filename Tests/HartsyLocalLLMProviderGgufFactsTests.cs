using System.IO;
using System.Text;
using Hartsy.Extensions.LLMAssistant.Backends;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Pins <see cref="HartsyLocalLLMProvider.ReadGgufModelFacts"/>, which fills the context length, family and quantization that
/// <c>ListModels</c> reports from a GGUF header. Each test writes a minimal, header-only GGUF (no tensors) into a temp folder.</summary>
public class HartsyLocalLLMProviderGgufFactsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "llmassistant-gguf-" + Guid.NewGuid().ToString("N"));

    public HartsyLocalLLMProviderGgufFactsTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void ReadsContextLengthFamilyAndQuantizationFromTheHeader()
    {
        string path = WriteGguf(Path.Combine(_dir, "qwen.gguf"),
            ("general.architecture", "qwen2"),
            ("qwen2.context_length", (uint)32768),
            ("general.file_type", (uint)15));

        (int ContextLength, string Family, string Quantization) facts = HartsyLocalLLMProvider.ReadGgufModelFacts(path);

        Assert.Equal(32768, facts.ContextLength);
        Assert.Equal("qwen2", facts.Family);
        Assert.Equal("Q4_K_M", facts.Quantization);
    }

    [Fact]
    public void UnknownFileTypeLeavesQuantizationEmptyAndKeepsTheRest()
    {
        string path = WriteGguf(Path.Combine(_dir, "unknown.gguf"),
            ("general.architecture", "llama"),
            ("llama.context_length", (uint)4096),
            ("general.file_type", (uint)9999));

        (int ContextLength, string Family, string Quantization) facts = HartsyLocalLLMProvider.ReadGgufModelFacts(path);

        Assert.Equal(4096, facts.ContextLength);
        Assert.Equal("llama", facts.Family);
        Assert.Null(facts.Quantization);
    }

    [Fact]
    public void MissingArchitectureLeavesFamilyAndContextLengthEmpty()
    {
        // The context length key is "<architecture>.context_length", so without an architecture there is nothing to read.
        string path = WriteGguf(Path.Combine(_dir, "bare.gguf"), ("general.file_type", (uint)1));

        (int ContextLength, string Family, string Quantization) facts = HartsyLocalLLMProvider.ReadGgufModelFacts(path);

        Assert.Equal(-1, facts.ContextLength);
        Assert.Null(facts.Family);
        Assert.Equal("F16", facts.Quantization);
    }

    [Fact]
    public void MissingFileReturnsEmptyFacts()
    {
        (int ContextLength, string Family, string Quantization) facts = HartsyLocalLLMProvider.ReadGgufModelFacts(Path.Combine(_dir, "absent.gguf"));

        Assert.Equal(-1, facts.ContextLength);
        Assert.Null(facts.Family);
        Assert.Null(facts.Quantization);
    }

    [Fact]
    public void NotAGgufReturnsEmptyFactsWithoutThrowing()
    {
        string path = Path.Combine(_dir, "text.gguf");
        File.WriteAllText(path, "this is not a GGUF header at all");

        (int ContextLength, string Family, string Quantization) facts = HartsyLocalLLMProvider.ReadGgufModelFacts(path);

        Assert.Equal(-1, facts.ContextLength);
        Assert.Null(facts.Family);
        Assert.Null(facts.Quantization);
    }

    [Fact]
    public void ReplacingTheFileRefreshesItsFacts()
    {
        // The second write is longer, so its length (part of the cache key) changes even if the write time is coarse.
        string path = Path.Combine(_dir, "swap.gguf");
        WriteGguf(path, ("general.architecture", "llama"), ("general.file_type", (uint)15));
        Assert.Equal("Q4_K_M", HartsyLocalLLMProvider.ReadGgufModelFacts(path).Quantization);

        WriteGguf(path, ("general.architecture", "llama"), ("general.file_type", (uint)7), ("general.name", "replacement"));

        Assert.Equal("Q8_0", HartsyLocalLLMProvider.ReadGgufModelFacts(path).Quantization);
    }

    /// <summary>Writes a GGUF v3 header with the given metadata and no tensors. Values are a string or a uint32.</summary>
    private static string WriteGguf(string path, params (string Key, object Value)[] entries)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(new byte[] { 0x47, 0x47, 0x55, 0x46 }); // "GGUF"
        writer.Write((uint)3);                               // version
        writer.Write((ulong)0);                              // tensor count
        writer.Write((ulong)entries.Length);                 // metadata key-value count
        foreach ((string key, object value) in entries)
        {
            WriteString(writer, key);
            switch (value)
            {
                case string text:
                    writer.Write((uint)8); // STRING
                    WriteString(writer, text);
                    break;
                case uint number:
                    writer.Write((uint)4); // UINT32
                    writer.Write(number);
                    break;
                default:
                    throw new ArgumentException($"Unsupported test value type for '{key}'.");
            }
        }
        writer.Flush();
        File.WriteAllBytes(path, stream.ToArray());
        return path;
    }

    private static void WriteString(BinaryWriter writer, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }
}
