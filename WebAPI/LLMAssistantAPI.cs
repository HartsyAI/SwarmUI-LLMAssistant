using SwarmUI.Accounts;
using SwarmUI.WebAPI;

namespace SwarmUI.Extensions.LLMAssistant.WebAPI;

/// <summary>Registers all LLM Assistant API endpoints and permissions.</summary>
public static class LLMAssistantAPI
{
    public static readonly PermInfoGroup LLMAssistantPermGroup = new("LLMAssistant",
        "Permissions for the LLM Assistant extension.");

    public static readonly PermInfo PermChat = Permissions.Register(new(
        "llm_chat", "LLM Chat",
        "Allows sending messages to LLM backends.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    public static readonly PermInfo PermSettings = Permissions.Register(new(
        "llm_settings", "LLM Settings",
        "Allows reading and modifying LLM Assistant settings.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    public static readonly PermInfo PermModels = Permissions.Register(new(
        "llm_models", "LLM Models",
        "Allows listing available LLM models.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    public static readonly PermInfo PermThreads = Permissions.Register(new(
        "llm_threads", "LLM Threads",
        "Allows creating and managing chat threads.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    public static void Register()
    {
        // Chat
        API.RegisterAPICall(ChatEndpoints.LLMAssistantSendMessage, true, PermChat);
        API.RegisterAPICall(ChatEndpoints.LLMAssistantSendMessageWS, true, PermChat);
        // Settings
        API.RegisterAPICall(SettingsEndpoints.LLMAssistantGetSettings, false, PermSettings);
        API.RegisterAPICall(SettingsEndpoints.LLMAssistantSaveSettings, true, PermSettings);
        API.RegisterAPICall(SettingsEndpoints.LLMAssistantResetSettings, true, PermSettings);
        // Models
        API.RegisterAPICall(ModelEndpoints.LLMAssistantGetModels, false, PermModels);
        API.RegisterAPICall(ModelEndpoints.LLMAssistantGetBackends, false, PermModels);
        // Threads
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantGetThreads, false, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantGetThread, false, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantSaveThread, true, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantDeleteThread, true, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantExportThread, false, PermThreads);
        // Instructions
        API.RegisterAPICall(InstructionEndpoints.LLMAssistantGetInstructions, false, PermSettings);
        API.RegisterAPICall(InstructionEndpoints.LLMAssistantSaveInstruction, true, PermSettings);
        API.RegisterAPICall(InstructionEndpoints.LLMAssistantDeleteInstruction, true, PermSettings);
    }
}
