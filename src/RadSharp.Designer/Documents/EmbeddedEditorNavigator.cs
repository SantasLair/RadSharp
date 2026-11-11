using RadSharp.Design.Code;

namespace RadSharp.Designer;

/// <summary>
/// An <see cref="ICodeNavigator"/> that opens the file as a <see cref="CodeDocument"/> inside the IDE and puts
/// the caret on the line and column (the default; see <see cref="IdeSettings.CodeNavigation"/>).
/// </summary>
public sealed class EmbeddedEditorNavigator(Func<string, CodeDocument?> open) : ICodeNavigator
{
    public bool Navigate(string file, int line, int column)
    {
        if (open(file) is not { } doc) return false;
        doc.GoTo(line, column);
        doc.Focus();
        return true;
    }
}
