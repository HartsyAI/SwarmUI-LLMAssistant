using System.Runtime.CompilerServices;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>This project targets a newer .NET than the extension, so the parser beside its copy of the extension is the build for that newer .NET, which the runtime the extension asks
/// for cannot load. The PDF workers these tests start are therefore told to use the newest runtime, as the test host does. A SwarmUI build has the parser that matches the extension.</summary>
internal static class PdfWorkerRuntime
{
    [ModuleInitializer]
    internal static void UseTheNewestRuntime() => Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD", "LatestMajor");
}
