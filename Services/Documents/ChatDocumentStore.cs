using System.IO;
using System.Text;
using Newtonsoft.Json;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.Documents;

/// <summary>A document attached to a chat: what a message's reference points at. Only the server writes one, from a file it read itself.</summary>
public sealed class ChatDocument
{
    /// <summary>Its id (unguessable, made by the server).</summary>
    public string Id = "";
    /// <summary>The file's name, cleaned for showing and for the prompt.</summary>
    public string Name = "";
    /// <summary>What kind of file it was read as (<c>pdf</c>, <c>docx</c>, <c>html</c>, <c>text</c>).</summary>
    public string Kind = "";
    /// <summary>The characters of text kept.</summary>
    public int Chars;
    /// <summary>What the text costs of a request's budget, in quarters of a token (<see cref="DocumentText.Cost"/>).</summary>
    public int Cost;
    /// <summary>The pages the file has, or 0 for a kind without pages.</summary>
    public int Pages;
    /// <summary>Whether a limit cut the document short.</summary>
    public bool Truncated;
    /// <summary>What the user was told about reading it.</summary>
    public List<string> Warnings = [];
    /// <summary>When it was attached (UTC, ISO 8601).</summary>
    public string AddedAt = "";
    /// <summary>The extracted text. Untrusted: anything can be written in a file.</summary>
    public string Text = "";
}

/// <summary>Where attached documents' text is kept: one file each under the user's own folder in the server's data folder. It is not in the output folder (which the file tools write to
/// and the web server serves), so neither the model nor a link can read or change it. A message holds only the id, so a chat's saved data stays small. A document no chat refers to is
/// removed by <see cref="OrphanedFileGC"/> after a day, and at once when the last chat holding it is deleted.</summary>
public static class ChatDocumentStore
{
    /// <summary>The documents folder of a user (null when the user has no name to make one from). Replaceable for tests.</summary>
    public static Func<User, string> RootFor = user =>
    {
        string key = Utilities.StrictFilenameClean(user?.UserID ?? "").Trim('.', ' ');
        return key.Length == 0 ? null : Path.Combine(Program.DataDir, "LLMAssistant", "documents", "users", key);
    };

    /// <summary>Whether text is a document id: hex only, so no part of a request becomes a path.</summary>
    public static bool IsId(string id) => id is { Length: 32 } && id.All(Uri.IsHexDigit);

    /// <summary>A document's file, or null for something that is not an id.</summary>
    public static string FileFor(string root, string id) => root is null || !IsId(id) ? null : Path.GetFullPath(Path.Combine(root, id + ".json"));

    /// <summary>Saves a new document, giving it an id.</summary>
    public static ChatDocument Save(string root, ChatDocument document)
    {
        document.Id = Guid.NewGuid().ToString("N");
        document.AddedAt = DateTime.UtcNow.ToString("o");
        document.Cost = (int)DocumentText.Cost(document.Text);
        Directory.CreateDirectory(root);
        string file = FileFor(root, document.Id);
        string temp = file + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(document), new UTF8Encoding(false));
        File.Move(temp, file, true);
        return document;
    }

    /// <summary>A saved document, or null when there is none (or it cannot be read).</summary>
    public static ChatDocument Read(string root, string id)
    {
        string file = FileFor(root, id);
        try
        {
            if (file is null || !File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }
            return JsonConvert.DeserializeObject<ChatDocument>(File.ReadAllText(file, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Logs.Warning($"[LLMAssistant] An attached document could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>Removes a document, if it is there.</summary>
    public static void Delete(string root, string id)
    {
        string file = FileFor(root, id);
        try
        {
            if (file is not null && File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logs.Warning($"[LLMAssistant] An attached document could not be deleted (the daily cleanup will try again): {ex.Message}");
        }
    }
}
