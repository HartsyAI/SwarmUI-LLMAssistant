using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Documents;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>Attaching documents to chats and reading their text. Both need <c>llm_chat</c>, take the file in the request (no request names a path), and hold it to the caps in the
/// settings. Adding a file to the knowledge library is <see cref="KnowledgeEndpoints.LLMAssistantAddKnowledge"/>.</summary>
public static class DocumentEndpoints
{
    /// <summary>Reads a file and keeps its text, to be attached to a message by the returned id (<c>documents</c> on the chat and headless turn routes). Request: <c>{name, data}</c>
    /// (<c>data</c> is the file as base64 or a data URI). Reply: <c>{success, document{id, name, kind, chars, pages, truncated, tokens, warnings[]}}</c>, or <c>{success: false, error,
    /// status}</c> where <c>status</c> is <c>no_text_layer</c>, <c>encrypted</c>, <c>unsupported</c>, <c>malformed</c> or <c>too_large</c>.</summary>
    public static async Task<JObject> LLMAssistantUploadChatDocument(Session session, JObject rawInput)
    {
        DocumentCaps caps = DocumentCaps.From(SettingsService.GetMergedSettings(session.User));
        (byte[] bytes, string error, bool tooLarge) = DocumentUpload.Decode(rawInput["data"], caps.MaxBytes);
        if (error is not null)
        {
            return Fail(error, tooLarge ? DocumentStatus.TooLarge : DocumentStatus.Malformed);
        }
        (ChatDocument document, ExtractedDocument extraction) = await ChatDocuments.AttachAsync(session.User, rawInput["name"]?.ToString(), bytes, caps, CancellationToken.None);
        if (document is null)
        {
            return Fail(extraction.Error, extraction.Status);
        }
        JObject reference = ChatDocuments.Reference(document);
        reference["tokens"] = ContextWindowService.EstimateTokens(document.Text);
        reference["warnings"] = new JArray(document.Warnings);
        return new JObject { ["success"] = true, ["document"] = reference };
    }

    /// <summary>Reads a file's text and returns it, keeping nothing. Request: <c>{name, data}</c>. Reply: <c>{success, name, kind, chars, pages, truncated, warnings[], text}</c>, or
    /// a failure as <see cref="LLMAssistantUploadChatDocument"/> gives it. The text is untrusted: it is whatever the file says.</summary>
    public static async Task<JObject> LLMAssistantExtractDocument(Session session, JObject rawInput)
    {
        DocumentCaps caps = DocumentCaps.From(SettingsService.GetMergedSettings(session.User));
        (byte[] bytes, string error, bool tooLarge) = DocumentUpload.Decode(rawInput["data"], caps.MaxBytes);
        if (error is not null)
        {
            return Fail(error, tooLarge ? DocumentStatus.TooLarge : DocumentStatus.Malformed);
        }
        string name = rawInput["name"]?.ToString();
        ExtractedDocument extraction = await DocumentExtraction.ExtractAsync(name, bytes, new DocumentLimits(caps.MaxPages, caps.MaxChars), CancellationToken.None);
        if (!extraction.Success)
        {
            return Fail(extraction.Error, extraction.Status);
        }
        string text = ChatDocuments.TextWithPages(extraction);
        return new JObject
        {
            ["success"] = true,
            ["name"] = ChatDocuments.CleanName(name),
            ["kind"] = extraction.Kind,
            ["chars"] = text.Length,
            ["pages"] = extraction.TotalPages,
            ["truncated"] = extraction.Truncated,
            ["warnings"] = new JArray(extraction.Warnings),
            ["text"] = text,
        };
    }

    /// <summary>A failed reply, with what kind of failure it was.</summary>
    private static JObject Fail(string error, DocumentStatus status) => new() { ["success"] = false, ["error"] = error, ["status"] = ExtractedDocument.StatusName(status) };
}
