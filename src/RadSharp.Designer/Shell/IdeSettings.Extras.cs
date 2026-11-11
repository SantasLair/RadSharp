using System.Text.Json.Nodes;
using RadSharp.CodeEditing;

namespace RadSharp.Designer;

/// <summary>Editor, theme, autosave and welcome-page preferences (I9), kept in <c>ide.json</c> beside the code navigation choice.</summary>
public sealed partial class IdeSettings
{
    /// <summary>The editor colour theme id (<see cref="EditorThemes"/>); <c>auto</c> follows the IDE's light/dark.</summary>
    public string EditorTheme { get; set; } = EditorThemes.Auto;
    public string EditorFontFamily { get; set; } = CodeEditorFonts.Family;
    public double EditorFontSize { get; set; } = 13;
    public bool Ligatures { get; set; }
    public int TabSize { get; set; } = 4;
    public bool InsertSpaces { get; set; } = true;
    public bool ShowLineNumbers { get; set; } = true;
    public bool HighlightCurrentLine { get; set; } = true;

    /// <summary>Switch the IDE between light and dark with the operating system.</summary>
    public bool FollowOsTheme { get; set; }

    /// <summary>Save a modified document when its editor loses focus (off by default).</summary>
    public bool AutosaveOnFocusLoss { get; set; }
    /// <summary>Save modified documents after this many seconds without edits; 0 = off (the default).</summary>
    public int AutosaveIdleSeconds { get; set; }

    /// <summary>Projects pinned to the top of the welcome page, in pin order.</summary>
    public List<string> PinnedProjects { get; } = [];
    /// <summary>When each project was last opened (UTC), by full path.</summary>
    public Dictionary<string, DateTime> LastOpened { get; } = new(StringComparer.OrdinalIgnoreCase);

    void LoadExtras(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject r) return;
            if (r["editorTheme"]?.GetValue<string>() is { Length: > 0 } th) EditorTheme = th;
            if (r["editorFontFamily"]?.GetValue<string>() is { Length: > 0 } ff) EditorFontFamily = ff;
            if (r["editorFontSize"]?.GetValue<double>() is { } fs) EditorFontSize = Math.Clamp(fs, 6, 48);
            Ligatures = r["ligatures"]?.GetValue<bool>() ?? Ligatures;
            if (r["tabSize"]?.GetValue<int>() is { } ts) TabSize = Math.Clamp(ts, 1, 16);
            InsertSpaces = r["insertSpaces"]?.GetValue<bool>() ?? InsertSpaces;
            ShowLineNumbers = r["showLineNumbers"]?.GetValue<bool>() ?? ShowLineNumbers;
            HighlightCurrentLine = r["highlightCurrentLine"]?.GetValue<bool>() ?? HighlightCurrentLine;
            FollowOsTheme = r["followOsTheme"]?.GetValue<bool>() ?? false;
            AutosaveOnFocusLoss = r["autosaveOnFocusLoss"]?.GetValue<bool>() ?? false;
            AutosaveIdleSeconds = Math.Max(0, r["autosaveIdleSeconds"]?.GetValue<int>() ?? 0);
            if (r["pinnedProjects"] is JsonArray pins)
                foreach (var p in pins) if (p?.GetValue<string>() is { Length: > 0 } s && !PinnedProjects.Contains(s)) PinnedProjects.Add(s);
            if (r["lastOpened"] is JsonObject lo)
                foreach (var (k, v) in lo)
                    if (v?.GetValue<string>() is { } t && DateTime.TryParse(t, null, System.Globalization.DateTimeStyles.RoundtripKind, out var when))
                        LastOpened[k] = when.ToUniversalTime();
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException or InvalidOperationException or FormatException) { }
    }

    void SaveExtras(JsonObject root)
    {
        root["editorTheme"] = EditorTheme;
        root["editorFontFamily"] = EditorFontFamily;
        root["editorFontSize"] = EditorFontSize;
        root["ligatures"] = Ligatures;
        root["tabSize"] = TabSize;
        root["insertSpaces"] = InsertSpaces;
        root["showLineNumbers"] = ShowLineNumbers;
        root["highlightCurrentLine"] = HighlightCurrentLine;
        root["followOsTheme"] = FollowOsTheme;
        root["autosaveOnFocusLoss"] = AutosaveOnFocusLoss;
        root["autosaveIdleSeconds"] = AutosaveIdleSeconds;
        root["pinnedProjects"] = new JsonArray([.. PinnedProjects.Select(p => (JsonNode?)p)]);
        var lo = new JsonObject();
        foreach (var (k, v) in LastOpened) lo[k] = v.ToString("o");
        root["lastOpened"] = lo;
    }
}

/// <summary>Editor colour themes: Light and Dark, and a high-contrast pair, as <see cref="CodeEditorColors"/>.</summary>
public static class EditorThemes
{
    public const string Auto = "auto", Light = "light", Dark = "dark", HighContrastLight = "hc-light", HighContrastDark = "hc-dark";

    public static IReadOnlyList<(string Id, string Name)> All { get; } =
    [
        (Auto, "Match the IDE"), (Light, "Light"), (Dark, "Dark"),
        (HighContrastLight, "High Contrast Light"), (HighContrastDark, "High Contrast Dark"),
    ];

    static RadSharp.Drawing.Color H(string hex) => RadSharp.Drawing.Color.FromHex(hex);

    static readonly CodeEditorColors HcDark = CodeEditorColors.Dark with
    {
        Background = H("#000000"), Text = H("#FFFFFF"), GutterBackground = H("#000000"), GutterBorder = H("#6FC3DF"),
        LineNumber = H("#FFFFFF"), CurrentLineNumber = H("#FFFF00"), CurrentLine = H("#1A1A1A"), Selection = H("#0F4A85"),
        SelectionInactive = H("#333333"), Caret = H("#FFFFFF"), ErrorSquiggle = H("#FF5555"), WarningSquiggle = H("#FFFF00"),
        InfoSquiggle = H("#6FC3DF"), PopupBackground = H("#000000"), PopupBorder = H("#6FC3DF"), PopupText = H("#FFFFFF"),
        PopupSelection = H("#0F4A85"), PopupDetail = H("#D0D0D0"), PopupMatch = H("#FFFF00"), FindMatch = H("#7A5A00"),
        FindMatchCurrent = H("#B07A00"), BracketMatch = H("#3A3A3A"),
        Tokens = new Dictionary<string, RadSharp.Drawing.Color>(CodeEditorColors.Dark.Tokens)
        {
            [Classifications.Keyword] = H("#6FC3FF"), [Classifications.ControlKeyword] = H("#FF8AF0"),
            [Classifications.Comment] = H("#7CE08A"), ["xml doc comment"] = H("#7CE08A"),
            [Classifications.String] = H("#FFB38A"), [Classifications.VerbatimString] = H("#FFB38A"),
            [Classifications.Number] = H("#D4F0B0"), [Classifications.MethodName] = H("#FFFF99"),
        },
    };

    static readonly CodeEditorColors HcLight = CodeEditorColors.Light with
    {
        Background = H("#FFFFFF"), Text = H("#000000"), GutterBackground = H("#FFFFFF"), GutterBorder = H("#000000"),
        LineNumber = H("#000000"), CurrentLineNumber = H("#000000"), CurrentLine = H("#E6E6E6"), Selection = H("#9BC4FF"),
        SelectionInactive = H("#CCCCCC"), Caret = H("#000000"), ErrorSquiggle = H("#B00000"), WarningSquiggle = H("#7A5A00"),
        InfoSquiggle = H("#0033AA"), PopupBorder = H("#000000"), PopupDetail = H("#303030"), PopupMatch = H("#0033AA"),
        Tokens = new Dictionary<string, RadSharp.Drawing.Color>(CodeEditorColors.Light.Tokens)
        {
            [Classifications.Keyword] = H("#0000C0"), [Classifications.ControlKeyword] = H("#7A0080"),
            [Classifications.Comment] = H("#005A00"), ["xml doc comment"] = H("#005A00"),
            [Classifications.String] = H("#8A0000"), [Classifications.VerbatimString] = H("#8A0000"),
            [Classifications.Number] = H("#0A2A9A"), [Classifications.MethodName] = H("#5A3A00"),
        },
    };

    /// <summary>The colours of a theme id; null for <see cref="Auto"/> (the editor then follows the IDE theme).</summary>
    public static CodeEditorColors? Colors(string id) => id switch
    {
        Light => CodeEditorColors.Light,
        Dark => CodeEditorColors.Dark,
        HighContrastLight => HcLight,
        HighContrastDark => HcDark,
        _ => null,
    };
}
