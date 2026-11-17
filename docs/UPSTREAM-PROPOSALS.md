# Proposals for SwarmUI core

SwarmUI's extension rules say not to hack the core: "If the existing core is in the way, please open a PR to fix the issue."
These are the places this extension still has to reach past a documented hook. Each is isolated in one file (`Services/CoreBridge.cs`
for C#, `Assets/core-bridge.js` for the front end) so a core change has one place to fix. Each proposal below is small, and would let
the matching code in the bridge be deleted.

None of these has been filed. They are written to be opened as issues or PRs on `mcmonkeyprojects/SwarmUI` by a maintainer who has
agreed to that.

## 1. The API-key save route ignores the key registry

**Today.** `UserUpstreamApiKeys.Register(...)` (documented, public) draws a key field on the User tab. The route that saves the key,
`BasicAPIFeatures.SetAPIKey`, checks a different set, `BasicAPIFeatures.AcceptedAPIKeyTypes`, which holds only the three built-in
types. A type registered through the registry is therefore refused with "Invalid key type" unless the extension also adds it to that
set. (The error message also prints the set object instead of the rejected type.)

**Workaround.** `CoreBridge.AcceptApiKeyType` adds the type to the set.

**Proposal.** Make `SetAPIKey` check `UserUpstreamApiKeys.KeysByType.ContainsKey(keyType)`, and drop the second set (or fill it from
`Register`). Fix the message to name `keyType`.

## 2. No way to register a model type

**Today.** Model types (`Stable-Diffusion`, `LoRA`, ...) live in the public dictionary `Program.T2IModelSets`. Core fills it in
`BuildModelLists()` and clears it again on any model-path settings save, firing `Program.ModelPathsChangedEvent` afterwards.
An extension adding a type (this one adds `LLM`) must write into the dictionary and re-add its entry on every event, and call `Refresh()`
on the new handler itself because the event fires after core's own refresh pass.

**Workaround.** `CoreBridge.RegisterModelType` does the re-adding.

**Proposal.** A `Program.RegisterModelType(string type, string subfolder)` (or a list core consults in `BuildModelLists`) so registered
types survive a rebuild and refresh with the rest.

## 3. Late parameter handlers have no ordering contract

**Today.** `T2IParamInput.LateSpecialParameterHandlers` is a public list whose first entry is core's own prompt-tag pass
(`PreparsePromptLikes`). That pass consumes prompt tags, so a handler that needs to see a tag first (the `<llmprompt:...>` expansion,
which also has to run before wildcards so a pinned wildcard seed applies) must be inserted at index 0. A handler added the obvious way
(at the end) silently never sees its tags.

**Workaround.** `CoreBridge.RunBeforeCorePromptParsing` inserts at index 0.

**Proposal.** Either document the list as ordered and add a `PreParse` list that runs before core's pass, or give custom prompt tags
(`PromptTagProcessors`) a way to run async and to read per-batch state, so the `<llmprompt:...>` expansion could be a normal tag
processor and need no handler at all. The second is the better fix but a larger change.

## 4. No "image generated" event in the front end

**Today.** The Generate tab shows each new image through the global function `gotImageResult(image, metadata, batchId)`. The
companion overlay reacts to finished generations, and there is no callback list for it (core does have `sessionReadyCallbacks`,
`backendsRevisedCallbacks` and `postParamBuildSteps`).

**Workaround.** `llmaCoreBridge` (`Assets/core-bridge.js`) wraps the global once and fans out to listeners.

**Proposal.** `imageGeneratedCallbacks = []` in the Generate handler, called from `gotImageResult` with the same arguments.

## 5. No layout hook for backend cards

**Today.** `Server > Backends` builds each backend's settings card from the backend's settings class. The extension groups the LLM
cards' fields into sections, adds guide links and fills dropdowns (`Assets/llm-backend-cards.js`). It uses core's
`backendsRevisedCallbacks`, but core rebuilds a card's DOM when it is saved or edited without calling it, so a `MutationObserver`
re-applies the layout.

**Proposal.** A per-backend-type hook in the backend settings schema for grouping and for option lists (`[ConfigSection]`-style
attributes and a dynamic-values hook), so none of this needs the DOM at all. Larger, and the least urgent of the five.

## Not reach-ins, but worth a note

- `GenerateImageTool`, `CreateImagePresetTool` and the companion's image listing call core route methods directly
  (`T2IAPI.GenerateText2Image`, `BasicAPIFeatures.AddNewPreset`, `T2IAPI.ListImages`) rather than over HTTP. This skips core's route
  permission check, so each caller enforces its own permission (`llm_tool_generate_image`, `llm_tool_create_image_preset`, plus core's
  `Manage Presets` for presets). That is deliberate; it is recorded here so a reviewer does not have to rediscover it.
- `ShellExecTool` starts its process directly. It moves to a shared process runner in the code-mode work (Phase 5), which also adds
  cancellation, output limits and process-tree kill that `Utilities.QuickRunProcess` does not offer.
