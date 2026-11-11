using RadSharp.Drawing;

namespace RadSharp.CodeEditing;

// Language-agnostic extension points. None of them knows about C#: the editor asks them for colours, squiggles,
// fold ranges and completions against a snapshot, and they tell it when their answers changed. Sources may
// compute on a background thread, but must raise their events on the UI thread (marshal with Application.Post).

/// <summary>A coloured span within one line: <see cref="Start"/> and <see cref="Length"/> are columns of that line.</summary>
public readonly record struct TokenSpan(int Start, int Length, string Classification)
{
    public int End => Start + Length;
}

/// <summary>Raised by a source when its results changed, so the editor repaints (and asks again).</summary>
public sealed class SourceChangedEventArgs(TextSnapshot? snapshot = null, int firstLine = 0, int lastLine = int.MaxValue) : EventArgs
{
    /// <summary>The snapshot the new results are for, if known.</summary>
    public TextSnapshot? Snapshot { get; } = snapshot;
    public int FirstLine { get; } = firstLine;
    public int LastLine { get; } = lastLine;
}

/// <summary>
/// Syntax colouring. The editor asks for the lines it is about to paint. Classifications are free-form strings
/// (<see cref="Classifications"/> has common ones, which match Roslyn's names) that the editor maps to colours
/// through <see cref="CodeEditorColors.Tokens"/>; an unknown classification paints in the default text colour.
/// </summary>
public interface ITokenSource
{
    /// <summary>
    /// The spans of <paramref name="line"/> in <paramref name="snapshot"/>, sorted and non-overlapping; gaps paint in
    /// the default colour. A slow (background) source may return spans computed for an older version of the line
    /// (they are clipped to the line), or null while it has nothing, then raise <see cref="Changed"/> when it does.
    /// Called on the UI thread while painting, so it must be quick.
    /// </summary>
    IReadOnlyList<TokenSpan>? GetTokens(TextSnapshot snapshot, int line);

    event EventHandler<SourceChangedEventArgs>? Changed;
}

public enum DecorationKind
{
    ErrorSquiggle,
    WarningSquiggle,
    InfoSquiggle,
    /// <summary>A background fill behind the range (a reference highlight, a search hit, a debugger's current statement).</summary>
    Highlight,
    /// <summary>A glyph in the gutter's glyph lane on the range's first line (see <see cref="Decoration.Glyph"/>).</summary>
    GutterGlyph,
}

public enum GutterGlyph { None, Breakpoint, BreakpointDisabled, CurrentStatement, Error, Warning, Info, Bookmark }

/// <summary>
/// A squiggle, highlight or gutter glyph over [<see cref="Start"/>, <see cref="Start"/> + <see cref="Length"/>) of
/// the snapshot it was reported against. <see cref="Message"/> is shown as a hint when the pointer rests on it.
/// <see cref="Color"/> overrides the theme's colour for the kind.
/// </summary>
public readonly record struct Decoration(int Start, int Length, DecorationKind Kind, string? Message = null, Color? Color = null, GutterGlyph Glyph = GutterGlyph.None)
{
    public int End => Start + Length;
}

/// <summary>A source's decorations, all against one <see cref="Snapshot"/>; the editor maps them to the current text.</summary>
public sealed record DecorationSet(TextSnapshot Snapshot, IReadOnlyList<Decoration> Items);

/// <summary>Squiggles (diagnostics), range highlights and gutter glyphs (breakpoints).</summary>
public interface IDecorationSource
{
    /// <summary>
    /// The decorations to show for <paramref name="current"/> (the editor's latest snapshot). Returning a set
    /// computed against an older snapshot is fine: the editor maps the ranges forward through the edits since.
    /// Called on the UI thread while painting; return a cached result.
    /// </summary>
    DecorationSet? GetDecorations(TextSnapshot current);

    event EventHandler<SourceChangedEventArgs>? Changed;
}

/// <summary>A foldable block: lines <see cref="StartLine"/> + 1 to <see cref="EndLine"/> hide when it's collapsed.</summary>
public readonly record struct FoldRange(int StartLine, int EndLine, string? CollapsedText = null);

/// <summary>Where the gutter offers fold markers.</summary>
public interface IFoldingSource
{
    /// <summary>The fold ranges of <paramref name="snapshot"/> (or of an older one, whose lines the editor then uses as they are), or null when not ready.</summary>
    IReadOnlyList<FoldRange>? GetFoldRanges(TextSnapshot snapshot);

    event EventHandler<SourceChangedEventArgs>? Changed;
}

public enum CompletionTriggerKind
{
    /// <summary>Ctrl+Space, or <see cref="CodeEditor.ShowCompletion"/>.</summary>
    Invoked,
    /// <summary>One of <see cref="ICompletionSource.TriggerCharacters"/> was typed.</summary>
    TriggerCharacter,
    /// <summary>The first letter of an identifier was typed (when <see cref="CodeEditor.CompleteWhileTyping"/> is on).</summary>
    Typing,
}

/// <summary>What the editor asks a completion source: the text and caret, and what triggered the request.</summary>
public sealed record CompletionRequest(TextSnapshot Snapshot, int Offset, CompletionTriggerKind Trigger, char TriggerCharacter = '\0');

/// <summary>
/// One completion. <see cref="InsertText"/> (default: <see cref="DisplayText"/>) replaces the list's applicable
/// span. <see cref="FilterText"/> (default: <see cref="DisplayText"/>) is matched against what the user typed.
/// <see cref="Kind"/> is free-form ("method", "keyword", "class"...) and picks the icon colour.
/// </summary>
public sealed record CompletionItem(string DisplayText, string? InsertText = null, string? Kind = null, string? Detail = null, string? FilterText = null, string? SortText = null)
{
    public object? Tag { get; init; }
}

/// <summary>
/// The answer to a <see cref="CompletionRequest"/>: items, and the span of the request's snapshot they replace
/// (usually the identifier part before the caret; empty after a trigger character).
/// </summary>
public sealed record CompletionList(int ApplicableStart, int ApplicableLength, IReadOnlyList<CompletionItem> Items);

/// <summary>Completion (IntelliSense). The editor owns the list UI: it filters as you type, commits with Enter or Tab and closes with Esc.</summary>
public interface ICompletionSource
{
    /// <summary>Characters that open the list when typed ('.' for member access, for example).</summary>
    IReadOnlyCollection<char> TriggerCharacters { get; }

    /// <summary>
    /// Computes completions. May run as long as it likes off the UI thread; the editor cancels <paramref name="cancellationToken"/>
    /// when the request is superseded (another keystroke, Esc), and ignores results for a request it cancelled.
    /// </summary>
    Task<CompletionList?> GetCompletionsAsync(CompletionRequest request, CancellationToken cancellationToken);

    /// <summary>Called when an item is committed, to adjust the edit (snippets, adding parentheses); return null to insert <see cref="CompletionItem.InsertText"/>.</summary>
    TextEdit? GetCommitEdit(TextSnapshot snapshot, CompletionList list, CompletionItem item) => null;
}

/// <summary>Common classification names (the same strings Roslyn's classifier uses, so its results map directly).</summary>
public static class Classifications
{
    public const string Keyword = "keyword";
    public const string ControlKeyword = "keyword - control";
    public const string Comment = "comment";
    public const string XmlDocComment = "xml doc comment - text";
    public const string String = "string";
    public const string VerbatimString = "string - verbatim";
    public const string Char = "string";
    public const string Number = "number";
    public const string Operator = "operator";
    public const string Punctuation = "punctuation";
    public const string Identifier = "identifier";
    public const string Preprocessor = "preprocessor keyword";
    public const string PreprocessorText = "preprocessor text";
    public const string ClassName = "class name";
    public const string StructName = "struct name";
    public const string InterfaceName = "interface name";
    public const string EnumName = "enum name";
    public const string DelegateName = "delegate name";
    public const string TypeParameterName = "type parameter name";
    public const string RecordClassName = "record class name";
    public const string NamespaceName = "namespace name";
    public const string MethodName = "method name";
    public const string ExtensionMethodName = "extension method name";
    public const string PropertyName = "property name";
    public const string FieldName = "field name";
    public const string EventName = "event name";
    public const string ConstantName = "constant name";
    public const string EnumMemberName = "enum member name";
    public const string LocalName = "local name";
    public const string ParameterName = "parameter name";
    public const string ExcludedCode = "excluded code";
    public const string StaticSymbol = "static symbol";
}
