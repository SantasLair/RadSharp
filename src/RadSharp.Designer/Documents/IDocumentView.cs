using System.Text.Json.Nodes;

namespace RadSharp.Designer;

/// <summary>
/// A document open in the IDE's <see cref="DocumentArea"/>: a form in the designer (<see cref="FormDocument"/>),
/// a code file (<see cref="CodeDocument"/>), and later anything else with a tab (a project file, a diff).
/// The shell only talks to documents through this interface: it shows <see cref="View"/>, captions the tab from
/// <see cref="Title"/> and <see cref="IsDirty"/>, saves, asks <see cref="CanClose"/>, and persists
/// <see cref="SaveState"/> in the session.
/// </summary>
public interface IDocumentView
{
    /// <summary>What kind of document this is in the session file (<c>"form"</c>, <c>"code"</c>).</summary>
    string Kind { get; }

    /// <summary>The tab caption, without the dirty marker.</summary>
    string Title { get; }

    /// <summary>The file behind the document (full path), or null for an unsaved one.</summary>
    string? Path { get; }

    /// <summary>Has unsaved changes.</summary>
    bool IsDirty { get; }

    /// <summary>The control the document area shows for this document.</summary>
    Control View { get; }

    /// <summary>Raised when <see cref="Title"/> or <see cref="IsDirty"/> changed.</summary>
    event Action? StateChanged;

    /// <summary>Writes the document to <see cref="Path"/>. False if it couldn't.</summary>
    bool Save();

    /// <summary>A document's own veto on closing (busy, say). Unsaved changes are the shell's business: it asks
    /// once about all dirty documents before closing any, so this doesn't prompt.</summary>
    bool CanClose();

    /// <summary>Releases what the document holds. The shell has already taken <see cref="View"/> off screen.</summary>
    void Close();

    /// <summary>Gives the view keyboard focus.</summary>
    void Focus();

    /// <summary>The file's last write time when the document last loaded or saved it (to notice outside changes).</summary>
    DateTime DiskTimestamp { get; }

    /// <summary>Re-reads the file, dropping unsaved changes. False if it couldn't.</summary>
    bool Reload();

    /// <summary>What the session remembers beyond the path (carets, scroll); null for nothing.</summary>
    JsonObject? SaveState();

    /// <summary>Applies what <see cref="SaveState"/> returned in an earlier session.</summary>
    void RestoreState(JsonObject state);
}

/// <summary>The Edit menu's commands, for a document that supports them.</summary>
public interface IEditTarget
{
    bool CanUndo { get; }
    bool CanRedo { get; }
    void Undo();
    void Redo();
    void Cut();
    void Copy();
    void Paste();
    void Delete();
    void SelectAll();
}

/// <summary>Helpers shared by the documents.</summary>
internal static class DocumentFiles
{
    public static DateTime Stamp(string? path)
    {
        try { return path != null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default; }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
    }
}
