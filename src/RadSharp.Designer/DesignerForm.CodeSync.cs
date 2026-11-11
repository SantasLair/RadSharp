using RadSharp.CodeEditing;
using RadSharp.Design;
using RadSharp.Drawing;
using RadSharp.Ide.Language;

namespace RadSharp.Designer;

/// <summary>
/// Structure and code in step (milestone I5).
/// <list type="bullet">
/// <item>Selecting a control (in the Structure window or on the surface) highlights, in the form's open code-behind, the control's field
/// declaration (when the file has one: the generated part of the class holds it otherwise), its handler methods, and, lighter, the places that
/// use the field.</item>
/// <item>Placing the caret inside a handler method outlines the controls wired to it on the design surface
/// (<see cref="DesignSurface.HighlightedControls"/>).</item>
/// </list>
/// Both read the file's syntax tree (<see cref="LanguageService.GetDocumentSymbols"/>), so they need no workspace.
/// </summary>
public partial class DesignerForm
{
    static readonly Color HandlerColor = new(245, 158, 11, 80), FieldColor = new(59, 130, 246, 80), UseColor = new(59, 130, 246, 36);

    readonly Dictionary<CodeDocument, ControlHighlightSource> _highlightSources = [];
    readonly Dictionary<FormDocument, string> _syncSignatures = [];
    readonly Dictionary<CodeDocument, (TextSnapshot Snapshot, IReadOnlyList<DocumentSymbol> Symbols)> _symbolCache = [];

    /// <summary>The highlights the code editor of <paramref name="code"/> shows for the selected control, for tests.</summary>
    public IReadOnlyList<Decoration> CodeHighlightsOf(CodeDocument code) =>
        _highlightSources.TryGetValue(code, out var source) && source.Current is { } set ? set.Items : [];

    /// <summary>The form open for a code-behind file, if there is one.</summary>
    FormDocument? FormOf(CodeDocument code) => code.FormFile is { } f ? FindDocument(f) as FormDocument : null;

    void HookCodeSync(IDocumentView doc)
    {
        switch (doc)
        {
            case CodeDocument { Editor: CodeEditorAdapter adapter } code when code.IsCSharp && code.FormFile != null:
                var source = new ControlHighlightSource();
                _highlightSources[code] = source;
                adapter.Editor.DecorationSources.Add(source);
                if (FormOf(code) is { } owner) UpdateCodeHighlights(owner);
                break;
            case FormDocument form:
                if (FindDocument(form.CodeFile) is CodeDocument) UpdateCodeHighlights(form);
                break;
        }
    }

    void OnCodeDocumentClosed(CodeDocument code)
    {
        if (_highlightSources.Remove(code, out var source) && code.Editor is CodeEditorAdapter a) a.Editor.DecorationSources.Remove(source);
        _symbolCache.Remove(code);
        if (FormOf(code) is { } form) form.Surface.HighlightedControls = [];
    }

    /// <summary>A change to the form that could alter a selected control's handlers (assigning one, renaming) refreshes the code highlights.</summary>
    void OnFormChangedForSync(FormDocument form)
    {
        var signature = SignatureOf(form);
        if (_syncSignatures.TryGetValue(form, out var last) && last == signature) return;
        UpdateCodeHighlights(form);
    }

    static string SignatureOf(FormDocument form) =>
        string.Join("|", form.Document.Selection.Select(c => c.Name + ":" + string.Join(",", c.EventHandlers.Values)));

    // ---- Control selected: highlight in code ----

    /// <summary>Highlights, in the open code-behind of <paramref name="form"/>, what belongs to the selected controls.</summary>
    void UpdateCodeHighlights(FormDocument form)
    {
        _syncSignatures[form] = SignatureOf(form);
        if (FindDocument(form.CodeFile) is not CodeDocument { Editor: CodeEditorAdapter adapter } code || !_highlightSources.TryGetValue(code, out var source)) return;
        var editor = adapter.Editor;
        var snapshot = editor.Snapshot;
        var items = new List<Decoration>();
        var selection = form.Document.Selection.Where(c => c != form.Document.Form).ToList();
        if (selection.Count > 0)
        {
            var text = editor.Text;
            var symbols = SymbolsOf(code, snapshot, text);
            foreach (var control in selection)
            {
                var handlers = control.EventHandlers.Where(h => h.Value.Length > 0).ToList();
                // The field's declaration, when this file has one.
                foreach (var field in symbols.Where(s => s.Kind == "Field" && s.Name == control.Name))
                    items.Add(WholeLine(snapshot, field.Line, FieldColor, $"Field {control.Name}"));
                // Handler methods.
                foreach (var (ev, name) in handlers)
                    foreach (var method in symbols.Where(s => s.Kind == "Method" && s.Name == name))
                        items.Add(WholeLine(snapshot, method.Line, HandlerColor, $"{control.Name}.{ev} handler"));
                // Uses of the field, lighter.
                foreach (var span in LanguageService.FindIdentifierSpans(text, control.Name))
                    items.Add(new Decoration(span.Start, span.Length, DecorationKind.Highlight, null, UseColor));
            }
        }
        source.Set(snapshot, items);
    }

    static Decoration WholeLine(TextSnapshot snapshot, int line1, Color color, string message)
    {
        int line = Math.Clamp(line1 - 1, 0, snapshot.LineCount - 1);
        int start = snapshot.GetLineStart(line);
        return new Decoration(start, snapshot.GetLineLength(line), DecorationKind.Highlight, message, color);
    }

    IReadOnlyList<DocumentSymbol> SymbolsOf(CodeDocument code, TextSnapshot snapshot, string text)
    {
        if (_symbolCache.TryGetValue(code, out var cached) && cached.Snapshot == snapshot) return cached.Symbols;
        var symbols = LanguageService.GetDocumentSymbols(text);
        _symbolCache[code] = (snapshot, symbols);
        return symbols;
    }

    // ---- Caret in a handler: outline the controls on the surface ----

    /// <summary>The controls of the form wired to the handler method the caret of <paramref name="code"/> is in (none when it is not in one).</summary>
    public IReadOnlyList<Control> ControlsWiredToCaret(CodeDocument code)
    {
        if (FormOf(code) is not { } form || code.Editor is not CodeEditorAdapter adapter || !code.IsCSharp) return [];
        var editor = adapter.Editor;
        var symbols = SymbolsOf(code, editor.Snapshot, editor.Text);
        int caret = editor.CaretOffset;
        var method = symbols.Where(s => s.Kind == "Method" && s.Contains(caret)).OrderByDescending(s => s.FullStart).FirstOrDefault();
        if (method == null) return [];
        return [.. form.Document.Form.Descendants().Where(c => c.EventHandlers.Values.Contains(method.Name))];
    }

    void UpdateSurfaceHighlights(CodeDocument code)
    {
        if (FormOf(code) is not { } form) return;
        var wired = ControlsWiredToCaret(code);
        if (wired.Count == 0 && form.Surface.HighlightedControls.Count == 0) return;
        form.Surface.HighlightedControls = wired;
    }
}
