using System.Runtime.CompilerServices;

// Lets the extension's first Tests/ project exercise internal, pure-logic helpers (eg
// HartsyLocalLLMProvider.RoleFor/ToTextMessage) without needing a live SwarmUI host to construct a provider.
[assembly: InternalsVisibleTo("Hartsy.Extensions.LLMAssistant.Tests")]
