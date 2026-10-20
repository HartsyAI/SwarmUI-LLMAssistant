using System.Globalization;
using HartsyInference.Cuda;
using HartsyInference.Engine;
using HartsyInference.Vulkan;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Backends;

/// <summary>The Device and GPU choices on the local LLM backend card, read from the engine so the card lists only what runs here.</summary>
/// <remarks>GPU values are the ordinals GPUDeviceId has always saved: under CUDA a CUDA ordinal (fastest-first, not nvidia-smi order);
/// under Vulkan 0 lets the engine pick and any other value is the loader's raw index. Each list is built once per process, since a
/// probe opens a real device.</remarks>
public static class LocalDeviceChoices
{
    /// <summary>One dropdown entry: the value saved to Backends.fds, and the label shown for it.</summary>
    public readonly record struct Choice(string Value, string Name);

    /// <summary>Labels for the Device kinds the engine's text path runs on, in card order.</summary>
    internal static readonly (string Kind, string Label)[] KindLabels =
    [
        ("auto", "Auto (recommended)"),
        ("cuda", "CUDA (NVIDIA GPU)"),
        ("vulkan", "Vulkan (any GPU)"),
        ("cpu", "CPU (slow, no GPU needed)")
    ];

    /// <summary>The kind Auto runs on here. The engine caches its probes, so a backend's Init reuses this answer.</summary>
    private static readonly Lazy<string> _autoKind = new(() => Guard(() => BackendFactory.ResolveProbed("auto"), "Auto probe") ?? "cpu");

    /// <summary>The Device dropdown, probed once.</summary>
    private static readonly Lazy<IReadOnlyList<Choice>> _kinds = new(() => BuildKindChoices(CudaUsable(), VulkanUsable()));

    /// <summary>CUDA GPUs that passed the engine's test computation.</summary>
    private static readonly Lazy<IReadOnlyList<Choice>> _cuda = new(BuildCuda);

    /// <summary>Vulkan GPUs, led by 0 for "let the engine pick".</summary>
    private static readonly Lazy<IReadOnlyList<Choice>> _vulkan = new(BuildVulkan);

    /// <summary>The kind Auto runs on here: "cuda", "vulkan" or "cpu".</summary>
    public static string AutoKind => _autoKind.Value;

    /// <summary>The Device dropdown: Auto, then each kind that works on this machine.</summary>
    public class DeviceOptions : SettingsOptionsAttribute.AbstractImpl
    {
        public override string[] GetOptions => [.. _kinds.Value.Select(c => c.Value)];

        public override string[] Names => [.. _kinds.Value.Select(c => c.Name)];
    }

    /// <summary>The GPU dropdown: the GPUs Auto runs on here. The card script relists them when Device changes.</summary>
    public class GpuOptions : SettingsOptionsAttribute.AbstractImpl
    {
        public override string[] GetOptions => [.. ForDevice("auto").Choices.Select(c => c.Value)];

        public override string[] Names => [.. ForDevice("auto").Choices.Select(c => c.Name)];
    }

    /// <summary>The Device dropdown for the given probe results. Auto and CPU always work, since Auto falls back to the CPU.</summary>
    internal static IReadOnlyList<Choice> BuildKindChoices(bool cudaUsable, bool vulkanUsable)
    {
        List<Choice> choices = [];
        foreach ((string kind, string label) in KindLabels)
        {
            if ((kind == "cuda" && !cudaUsable) || (kind == "vulkan" && !vulkanUsable))
            {
                continue;
            }
            choices.Add(new Choice(kind, label));
        }
        return choices;
    }

    /// <summary>The kind a saved Device value asks for: "auto", "cuda", "vulkan" or "cpu". Anything else is CUDA, as it always was.</summary>
    internal static string RequestedKind(string device)
    {
        string value = (device ?? "").Trim().ToLowerInvariant();
        int colon = value.IndexOf(':');
        if (colon >= 0)
        {
            value = value[..colon];
        }
        return value is "auto" or "vulkan" or "cpu" ? value : "cuda";
    }

    /// <summary>The kind a backend runs on: the Device value itself, or for Auto the engine's probed choice for this GPU.</summary>
    public static string ResolveKind(string device, int gpuId)
    {
        string requested = RequestedKind(device);
        if (requested != "auto")
        {
            return requested;
        }
        if (gpuId <= 0)
        {
            return AutoKind;
        }
        return Guard(() => BackendFactory.ResolveProbed(BackendFactory.WithOrdinal("auto", gpuId)), $"Auto probe for GPU {gpuId}") ?? "cpu";
    }

    /// <summary>The engine device key for a resolved kind and GPU: "cpu", "cuda:N", or "vulkan" (engine picks) / "vulkan:N" (raw index).</summary>
    internal static string DeviceKeyFor(string kind, int gpuId) => kind switch
    {
        "cpu" => "cpu",
        "vulkan" => BackendFactory.WithOrdinal("vulkan", Math.Max(0, gpuId)),
        _ => $"cuda:{gpuId}"
    };

    /// <summary>The GPU choices for one Device value, with the kind they belong to. Never empty: every list offers "0", the default.</summary>
    public static (string Kind, IReadOnlyList<Choice> Choices) ForDevice(string device)
    {
        string requested = RequestedKind(device);
        bool auto = requested == "auto";
        string kind = auto ? AutoKind : requested;
        IReadOnlyList<Choice> choices = kind switch
        {
            "cuda" => _cuda.Value,
            "vulkan" => _vulkan.Value,
            _ => []
        };
        if (choices.Count == 0)
        {
            choices = [new Choice("0", EmptyGpuLabel(kind, auto))];
        }
        return (kind, choices);
    }

    /// <summary>The one entry a GPU list shows when there is no GPU to pick.</summary>
    internal static string EmptyGpuLabel(string kind, bool auto) => kind switch
    {
        "cpu" => auto ? "No usable GPU found, Auto runs on the CPU" : "Not used on CPU",
        "cuda" => "Default GPU (no NVIDIA GPU passed the CUDA check)",
        _ => "Default GPU"
    };

    /// <summary>"GPU 0: NVIDIA GeForce RTX 3060 (12 GB)".</summary>
    internal static string GpuLabel(int index, string name, double bytes) => $"GPU {index}: {Describe(name, bytes)}";

    /// <summary>"NVIDIA GeForce RTX 3060 (12 GB)", or just the name when the size is unknown.</summary>
    internal static string Describe(string name, double bytes)
        => bytes > 0 ? $"{name} ({(bytes / 1073741824.0).ToString("0.#", CultureInfo.InvariantCulture)} GB)" : name;

    /// <summary>Whether CUDA computes on this machine. A present but failing GPU says why in the log, since its option disappears.</summary>
    private static bool CudaUsable()
    {
        if (Guard(() => BackendFactory.ProbeCuda(), "CUDA probe"))
        {
            return true;
        }
        if (Guard(() => CudaContext.IsAvailable(), "CUDA availability"))
        {
            string why = Guard(() => BackendFactory.CudaProbeFailureReason, "CUDA probe reason") ?? "no reason reported";
            Logs.Info($"[LLMAssistant] Local LLM card: CUDA is left out of the Device list: {why.TrimEnd('.')}. Auto picks Vulkan or the CPU instead.");
        }
        return false;
    }

    /// <summary>Whether a real Vulkan GPU is present. Software rasterizers do not count, as they are slower than the CPU backend.</summary>
    private static bool VulkanUsable() => Guard(() =>
    {
        BackendFactory.Validate("vulkan");
        return true;
    }, "Vulkan check");

    /// <summary>Each CUDA GPU the engine's test computation passes on, by CUDA ordinal.</summary>
    /// <remarks>A GPU that fails is left out: Auto would run its number on Vulkan instead, which can be a different card.</remarks>
    private static IReadOnlyList<Choice> BuildCuda()
    {
        List<Choice> choices = [];
        foreach (GpuTopologyInfo gpu in Guard(() => CudaTopology.Probe(), "CUDA topology probe") ?? [])
        {
            int ordinal = gpu.Ordinal;
            if (!Guard(() => BackendFactory.ProbeCuda(ordinal), $"CUDA probe of GPU {ordinal}"))
            {
                Logs.Debug($"[LLMAssistant] GPU list: leaving out CUDA GPU {ordinal} ({gpu.Name}): {Guard(() => BackendFactory.CudaProbeFailureReason, "CUDA probe reason")}");
                continue;
            }
            choices.Add(new Choice($"{ordinal}", GpuLabel(ordinal, gpu.Name, gpu.TotalMemoryBytes)));
        }
        return choices;
    }

    /// <summary>The Vulkan choices: 0 lets the engine pick, then every other GPU by the loader's raw index.</summary>
    /// <remarks>Software rasterizers and devices the engine cannot open are left out. Each device is opened briefly to read its name.</remarks>
    private static IReadOnlyList<Choice> BuildVulkan()
    {
        List<Choice> choices = [];
        Guard(() =>
        {
            using VulkanInstance instance = new();
            int count = instance.EnumeratePhysicalDevices().Length;
            VulkanCapabilities best = Guard(() => Open(instance, null), "Vulkan best-device probe");
            choices.Add(new Choice("0", best is null ? "Auto (the engine picks the best GPU)" : $"Auto: {Describe(best.DeviceName, best.TotalVramBytes)}"));
            for (int ordinal = 1; ordinal < count; ordinal++)
            {
                int index = ordinal;
                VulkanCapabilities caps = Guard(() => Open(instance, index), $"Vulkan device {index} probe");
                if (caps is null || caps.DeviceType == VkPhysicalDeviceType.Cpu)
                {
                    continue;
                }
                choices.Add(new Choice($"{index}", GpuLabel(index, caps.DeviceName, caps.TotalVramBytes)));
            }
            return true;
        }, "Vulkan device list");
        return choices;
    }

    /// <summary>Opens one Vulkan device, or the one the engine would pick when <paramref name="ordinal"/> is null, just long enough to read it.</summary>
    private static VulkanCapabilities Open(VulkanInstance instance, int? ordinal)
    {
        using VulkanDevice device = VulkanDevice.Create(instance, ordinal);
        return device.Capabilities;
    }

    /// <summary>Runs a probe that must never break backend registration. A throw (no engine, no driver) returns default.</summary>
    private static T Guard<T>(Func<T> probe, string what)
    {
        try
        {
            return probe();
        }
        catch (Exception ex)
        {
            Logs.Debug($"[LLMAssistant] Device list: {what} failed ({ex.Message}).");
            return default;
        }
    }
}
