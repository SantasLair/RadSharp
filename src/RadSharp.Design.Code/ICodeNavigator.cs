namespace RadSharp.Design.Code;

/// <summary>
/// Where "go to this place in the code" ends up: the one seam between a designer and a code editor
/// (ide-plan.md, "Coexistence", rule 4). <see cref="CodeIntegration.EnsureHandler"/> gives a file and a line;
/// a navigator shows it — in an external IDE (<see cref="ExternalEditorNavigator"/>), in an editor embedded in
/// the designer, or (for the Visual Studio host) as a <c>NavigateToCode</c> message. Hosts are free to ignore it.
/// </summary>
public interface ICodeNavigator
{
    /// <summary>Shows <paramref name="file"/> with the caret at 1-based <paramref name="line"/> and
    /// <paramref name="column"/>. Returns false if it couldn't (the editor failed to start, say).</summary>
    bool Navigate(string file, int line, int column);
}

/// <summary>
/// An <see cref="ICodeNavigator"/> that opens the file in the user's IDE through <see cref="ExternalEditor"/>
/// (VS Code, Visual Studio, Rider or a custom command, as chosen in <see cref="DesignerSettings.Editor"/>).
/// External editors take a line but not a column, so the column is ignored.
/// </summary>
public sealed class ExternalEditorNavigator(DesignerSettings settings, Func<RadProject?>? project = null) : ICodeNavigator
{
    /// <summary>The display name of the editor used by the last <see cref="Navigate"/> call.</summary>
    public string LastEditorName { get; private set; } = "";

    /// <summary>The configured editor's executable (if detected) and display name.</summary>
    public (string? Executable, string Name) Editor()
    {
        var found = ExternalEditor.Detect().FirstOrDefault(e => e.Kind == settings.Editor);
        return (found?.Executable, found?.Name ?? settings.Editor.ToString());
    }

    public bool Navigate(string file, int line, int column)
    {
        var (exe, name) = Editor();
        LastEditorName = name;
        return ExternalEditor.Open(settings.Editor, exe, settings.CustomEditorCommand, project?.Invoke(), file, line);
    }
}
