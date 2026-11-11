using RadSharp.Drawing;

namespace RadSharp.CodeEditing;

/// <summary>
/// The code editor's colours: the view (background, gutter, selection, squiggles...) and a dictionary from
/// classification name (<see cref="Classifications"/>) to text colour. <see cref="Light"/> and <see cref="Dark"/>
/// are the defaults; the editor picks one from the form's theme unless <see cref="CodeEditor.Colors"/> is set.
/// Copy one with <c>with</c> and a new <see cref="Tokens"/> dictionary to make a custom scheme.
/// </summary>
public sealed record CodeEditorColors
{
    public Color Background { get; init; }
    public Color Text { get; init; }
    public Color GutterBackground { get; init; }
    public Color GutterBorder { get; init; }
    public Color LineNumber { get; init; }
    public Color CurrentLineNumber { get; init; }
    public Color CurrentLine { get; init; }
    public Color Selection { get; init; }
    public Color SelectionInactive { get; init; }
    public Color Caret { get; init; }
    public Color SecondaryCaret { get; init; }
    public Color ErrorSquiggle { get; init; }
    public Color WarningSquiggle { get; init; }
    public Color InfoSquiggle { get; init; }
    public Color Highlight { get; init; }
    public Color FindMatch { get; init; }
    public Color FindMatchCurrent { get; init; }
    public Color BracketMatch { get; init; }
    public Color FoldMarker { get; init; }
    public Color CollapsedText { get; init; }
    public Color Breakpoint { get; init; }
    public Color CurrentStatement { get; init; }
    public Color Bookmark { get; init; }
    public Color PopupBackground { get; init; }
    public Color PopupBorder { get; init; }
    public Color PopupText { get; init; }
    public Color PopupSelection { get; init; }
    public Color PopupDetail { get; init; }
    public Color PopupMatch { get; init; }

    /// <summary>Classification name → text colour.</summary>
    public IReadOnlyDictionary<string, Color> Tokens { get; init; } = new Dictionary<string, Color>();

    /// <summary>The colour for a classification, or null for the default text colour.</summary>
    public Color? ColorOf(string classification)
    {
        if (Tokens.TryGetValue(classification, out var c)) return c;
        // "keyword - control" falls back to "keyword", "string - verbatim" to "string", and so on.
        int dash = classification.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && Tokens.TryGetValue(classification[..dash], out c)) return c;
        return null;
    }

    static Color H(string hex) => Color.FromHex(hex);

    public static CodeEditorColors Light { get; } = new()
    {
        Background = H("#FFFFFF"),
        Text = H("#1F2328"),
        GutterBackground = H("#F7F8FA"),
        GutterBorder = H("#E3E6EA"),
        LineNumber = H("#9AA1AC"),
        CurrentLineNumber = H("#1F2328"),
        CurrentLine = H("#F3F6FB"),
        Selection = H("#C9DAFB"),
        SelectionInactive = H("#E3E6EA"),
        Caret = H("#1F2328"),
        SecondaryCaret = H("#5A6270"),
        ErrorSquiggle = H("#E5484D"),
        WarningSquiggle = H("#C99A06"),
        InfoSquiggle = H("#2F6FEB"),
        Highlight = H("#E8EEF9"),
        FindMatch = H("#FCE8B2"),
        FindMatchCurrent = H("#F7C55E"),
        BracketMatch = H("#D7E2F5"),
        FoldMarker = H("#8A929E"),
        CollapsedText = H("#6A737D"),
        Breakpoint = H("#E5484D"),
        CurrentStatement = H("#F2C744"),
        Bookmark = H("#2F6FEB"),
        PopupBackground = H("#FFFFFF"),
        PopupBorder = H("#B8BEC8"),
        PopupText = H("#1F2328"),
        PopupSelection = H("#DCE5FB"),
        PopupDetail = H("#6A737D"),
        PopupMatch = H("#2F6FEB"),
        Tokens = new Dictionary<string, Color>
        {
            [Classifications.Keyword] = H("#0033B3"),
            [Classifications.ControlKeyword] = H("#8B2BB9"),
            [Classifications.Comment] = H("#5E8B57"),
            ["xml doc comment"] = H("#5E8B57"),
            [Classifications.String] = H("#A31515"),
            [Classifications.VerbatimString] = H("#A31515"),
            [Classifications.Number] = H("#1750EB"),
            [Classifications.Operator] = H("#1F2328"),
            [Classifications.Punctuation] = H("#1F2328"),
            [Classifications.Preprocessor] = H("#7A7A7A"),
            [Classifications.PreprocessorText] = H("#7A7A7A"),
            [Classifications.ClassName] = H("#1F7A8C"),
            [Classifications.RecordClassName] = H("#1F7A8C"),
            [Classifications.StructName] = H("#1F7A8C"),
            [Classifications.InterfaceName] = H("#2B8A5C"),
            [Classifications.EnumName] = H("#1F7A8C"),
            [Classifications.DelegateName] = H("#1F7A8C"),
            [Classifications.TypeParameterName] = H("#1F7A8C"),
            [Classifications.NamespaceName] = H("#1F2328"),
            [Classifications.MethodName] = H("#795E26"),
            [Classifications.ExtensionMethodName] = H("#795E26"),
            [Classifications.PropertyName] = H("#1F2328"),
            [Classifications.FieldName] = H("#871094"),
            [Classifications.EventName] = H("#1F2328"),
            [Classifications.ConstantName] = H("#871094"),
            [Classifications.EnumMemberName] = H("#871094"),
            [Classifications.LocalName] = H("#1F2328"),
            [Classifications.ParameterName] = H("#1F2328"),
            [Classifications.ExcludedCode] = H("#9AA1AC"),
        },
    };

    public static CodeEditorColors Dark { get; } = new()
    {
        Background = H("#1E1E1E"),
        Text = H("#D4D4D4"),
        GutterBackground = H("#1E1E1E"),
        GutterBorder = H("#333333"),
        LineNumber = H("#6E7681"),
        CurrentLineNumber = H("#D4D4D4"),
        CurrentLine = H("#262A30"),
        Selection = H("#264F78"),
        SelectionInactive = H("#3A3D41"),
        Caret = H("#E0E0E0"),
        SecondaryCaret = H("#A0A0A0"),
        ErrorSquiggle = H("#F14C4C"),
        WarningSquiggle = H("#CCA700"),
        InfoSquiggle = H("#3794FF"),
        Highlight = H("#343A46"),
        FindMatch = H("#613214"),
        FindMatchCurrent = H("#9E6A03"),
        BracketMatch = H("#3B4252"),
        FoldMarker = H("#8B949E"),
        CollapsedText = H("#8B949E"),
        Breakpoint = H("#E51400"),
        CurrentStatement = H("#FFCC00"),
        Bookmark = H("#3794FF"),
        PopupBackground = H("#252526"),
        PopupBorder = H("#454545"),
        PopupText = H("#D4D4D4"),
        PopupSelection = H("#04395E"),
        PopupDetail = H("#9DA5B4"),
        PopupMatch = H("#3794FF"),
        Tokens = new Dictionary<string, Color>
        {
            [Classifications.Keyword] = H("#569CD6"),
            [Classifications.ControlKeyword] = H("#C586C0"),
            [Classifications.Comment] = H("#6A9955"),
            ["xml doc comment"] = H("#6A9955"),
            [Classifications.String] = H("#CE9178"),
            [Classifications.VerbatimString] = H("#CE9178"),
            [Classifications.Number] = H("#B5CEA8"),
            [Classifications.Operator] = H("#D4D4D4"),
            [Classifications.Punctuation] = H("#D4D4D4"),
            [Classifications.Preprocessor] = H("#9B9B9B"),
            [Classifications.PreprocessorText] = H("#9B9B9B"),
            [Classifications.ClassName] = H("#4EC9B0"),
            [Classifications.RecordClassName] = H("#4EC9B0"),
            [Classifications.StructName] = H("#86C691"),
            [Classifications.InterfaceName] = H("#B8D7A3"),
            [Classifications.EnumName] = H("#B8D7A3"),
            [Classifications.DelegateName] = H("#4EC9B0"),
            [Classifications.TypeParameterName] = H("#B8D7A3"),
            [Classifications.NamespaceName] = H("#D4D4D4"),
            [Classifications.MethodName] = H("#DCDCAA"),
            [Classifications.ExtensionMethodName] = H("#DCDCAA"),
            [Classifications.PropertyName] = H("#D4D4D4"),
            [Classifications.FieldName] = H("#9CDCFE"),
            [Classifications.EventName] = H("#D4D4D4"),
            [Classifications.ConstantName] = H("#4FC1FF"),
            [Classifications.EnumMemberName] = H("#4FC1FF"),
            [Classifications.LocalName] = H("#9CDCFE"),
            [Classifications.ParameterName] = H("#9CDCFE"),
            [Classifications.ExcludedCode] = H("#6E7681"),
        },
    };
}
