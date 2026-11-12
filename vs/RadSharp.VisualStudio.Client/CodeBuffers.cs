using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RadSharp.VisualStudio.Client;

/// <summary>
/// Where the host's code reads and edits go. The host never writes .cs files itself: it asks for a file's current text
/// (CodeRequest) and sends back the whole new text when it inserts an event-handler stub (ApplyCodeEdit). Called on the
/// UI thread. Visual Studio supplies one that reads and edits open editor buffers.
/// </summary>
public interface ICodeBufferProvider
{
    /// <summary>The current text of <paramref name="file"/> when it is open in an editor (possibly unsaved). Return false
    /// (with <paramref name="text"/> empty) to let the host read the file from disk.</summary>
    bool TryGetText(string file, out string text);

    /// <summary>
    /// Replaces <paramref name="file"/>'s text with <paramref name="text"/> (as one undo unit in an editor) and shows the
    /// 1-based <paramref name="line"/>. <paramref name="baseHash"/> is the host's <c>ApplyCodeEdit.BaseHash</c>: the
    /// implementation must first hash the text the edit would replace (open buffer, else the file on disk) with
    /// <see cref="CodeHash.Matches"/> and, when it does not match, change nothing and throw
    /// <see cref="CodeEditConflictException"/>.
    /// </summary>
    void ApplyEdit(DesignDocument? document, string file, string text, int line, string baseHash);
}

/// <summary>The code file changed between the host's CodeRequest and its ApplyCodeEdit, so the edit was not applied.</summary>
public sealed class CodeEditConflictException : Exception
{
    /// <summary>The message shown to the user.</summary>
    public const string DefaultMessage = "The code file changed while the handler was being added; try again.";

    /// <summary>Creates the exception with <see cref="DefaultMessage"/>.</summary>
    public CodeEditConflictException() : base(DefaultMessage) { }
}

/// <summary>The hash the protocol uses to detect that a code file changed under an edit.</summary>
public static class CodeHash
{
    /// <summary>Lowercase hex SHA-256 of the UTF-8 bytes of <paramref name="text"/> (the host's <c>BaseHash</c>).</summary>
    public static string Compute(string text)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(new UTF8Encoding(false).GetBytes(text ?? ""));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>True when <paramref name="baseHash"/> is the hash of <paramref name="currentText"/>.</summary>
    public static bool Matches(string currentText, string? baseHash) =>
        !string.IsNullOrEmpty(baseHash) && string.Equals(Compute(currentText), baseHash, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The default provider (used by the harness): no editor buffers; edits are written to disk and then reported as
/// <see cref="DesignSession.NavigateToCode"/>.</summary>
public class FileCodeBufferProvider : ICodeBufferProvider
{
    private readonly DesignSession _session;

    public FileCodeBufferProvider(DesignSession session) => _session = session ?? throw new ArgumentNullException(nameof(session));

    public virtual bool TryGetText(string file, out string text)
    {
        text = "";
        return false;
    }

    public virtual void ApplyEdit(DesignDocument? document, string file, string text, int line, string baseHash)
    {
        // Read the file the way the host does when it has no buffer (File.ReadAllText).
        var current = File.Exists(file) ? File.ReadAllText(file) : "";
        if (!CodeHash.Matches(current, baseHash))
            throw new CodeEditConflictException();
        FileText.WritePreservingEncoding(file, text);
        _session.RaiseNavigateToCode(document, file, line);
    }
}
