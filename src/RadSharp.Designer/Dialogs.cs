using RadSharp.Design.Code;

namespace RadSharp.Designer;

/// <summary>Builds small dialogs in code at the current UI scale.</summary>
internal static class DialogLayout
{
    public static int S(int v) => (int)Math.Round(v * Form.DefaultScale, MidpointRounding.AwayFromZero);

    public static T Put<T>(Control parent, T c, int x, int y, int w, int h) where T : Control
    {
        c.SetBounds(S(x), S(y), S(w), S(h));
        parent.Add(c);
        return c;
    }

    public static void Setup(Form f, string caption, int w, int h, Form? owner)
    {
        f.Caption = caption;
        f.Owner = owner;
        f.Scale = Form.DefaultScale;
        f.SetBounds(0, 0, S(w), S(h));
        f.DesignWidth = f.Width;
        f.DesignHeight = f.Height;
    }
}

/// <summary>File › New Application: the project's name, where it goes, and where RADSharp packages come from.</summary>
public sealed class NewApplicationDialog : Form
{
    readonly Edit _name, _location, _packages;
    readonly Label _result;

    public NewApplicationDialog(DesignerSettings settings, Form? owner)
    {
        DialogLayout.Setup(this, "New Application", 560, 300, owner);
        DialogLayout.Put(this, new Label { Caption = "Name" }, 16, 20, 110, 24);
        _name = DialogLayout.Put(this, new Edit { Name = "edtName", Text = UniqueName(settings.ProjectsFolder) }, 130, 18, 410, 26);
        DialogLayout.Put(this, new Label { Caption = "Location" }, 16, 58, 110, 24);
        _location = DialogLayout.Put(this, new Edit { Name = "edtLocation", Text = settings.ProjectsFolder }, 130, 56, 316, 26);
        var browse = DialogLayout.Put(this, new Button { Name = "btnBrowse", Caption = "Browse…" }, 454, 56, 86, 26);
        DialogLayout.Put(this, new Label { Caption = "Packages" }, 16, 96, 110, 24);
        _packages = DialogLayout.Put(this, new Edit
        {
            Name = "edtPackages",
            Text = settings.EffectivePackageSource ?? "",
            Placeholder = "empty: nuget.org",
        }, 130, 94, 410, 26);
        _result = DialogLayout.Put(this, new Label { Name = "lblResult", FontColor = Drawing.Color.FromHex("#6B7280") }, 130, 132, 410, 24);
        DialogLayout.Put(this, new Label
        {
            Caption = "Creates the .csproj, Program.cs, MainForm and a solution file.",
            FontColor = Drawing.Color.FromHex("#6B7280"),
        }, 130, 160, 410, 24);
        DialogLayout.Put(this, new Button { Name = "btnOk", Caption = "Create", Default = true }, 356, 250, 90, 30).Click += _ => Accept();
        DialogLayout.Put(this, new Button { Name = "btnCancel", Caption = "Cancel", Cancel = true, ModalResult = ModalResult.Cancel }, 454, 250, 86, 30);

        browse.Click += _ =>
        {
            var d = new FolderDialog { Title = "Where to create the project", InitialDirectory = _location.Text };
            Add(d);
            if (d.Execute()) _location.Text = d.FileName;
            Remove(d);
        };
        _name.Change += _ => UpdateResult();
        _location.Change += _ => UpdateResult();
        UpdateResult();
        PerformLayout();
        Shown += _ => { _name.Focus(); _name.SelectAll(); };
    }

    static string UniqueName(string folder)
    {
        for (int n = 1; ; n++)
            if (!Directory.Exists(Path.Combine(folder, $"Project{n}"))) return $"Project{n}";
    }

    public string ProjectName => _name.Text.Trim();
    public string Location => _location.Text.Trim();
    public string ProjectFolder => Path.Combine(Location, ProjectName);
    public string? PackageSource => _packages.Text.Trim() is { Length: > 0 } p ? p : null;

    void UpdateResult() => _result.Caption = ProjectName.Length > 0 ? ProjectFolder : "";

    string? Problem()
    {
        if (ProjectName.Length == 0 || !(char.IsLetter(ProjectName[0]) || ProjectName[0] == '_')
            || !ProjectName.All(c => char.IsLetterOrDigit(c) || c is '_' or '.'))
            return "The name must start with a letter and use letters, digits, '_' or '.'.";
        if (Location.Length == 0) return "Choose a location.";
        if (Directory.Exists(ProjectFolder) && Directory.EnumerateFileSystemEntries(ProjectFolder).Any())
            return $"{ProjectFolder} already exists and isn't empty.";
        if (PackageSource is { } p && !Directory.Exists(p)) return $"The package folder {p} doesn't exist.";
        return null;
    }

    void Accept()
    {
        if (Problem() is { } problem) { Dialogs.ShowMessage(problem, this); return; }
        ModalResult = ModalResult.Ok;
    }
}

/// <summary>Tools › Options: the code editor (Unity-style choice of detected IDEs) and project defaults.</summary>
public sealed class OptionsDialog : Form
{
    readonly DesignerSettings _settings;
    readonly ComboBox _editor;
    readonly Edit _custom, _folder, _packages;
    readonly List<EditorKind> _kinds = [];

    public OptionsDialog(DesignerSettings settings, Form? owner)
    {
        _settings = settings;
        DialogLayout.Setup(this, "Options", 600, 320, owner);
        var detected = ExternalEditor.Detect();
        var names = new List<string>();
        foreach (var e in detected) { _kinds.Add(e.Kind); names.Add(e.Name); }
        foreach (var kind in new[] { EditorKind.VSCode, EditorKind.VisualStudio, EditorKind.Rider })
            if (!_kinds.Contains(kind)) { _kinds.Add(kind); names.Add(kind switch { EditorKind.VSCode => "Visual Studio Code", EditorKind.VisualStudio => "Visual Studio", _ => "JetBrains Rider" } + " (not found)"); }
        _kinds.Add(EditorKind.Custom);
        names.Add("Custom command");

        DialogLayout.Put(this, new Label { Caption = "Code editor", FontBold = true }, 16, 16, 300, 24);
        _editor = DialogLayout.Put(this, new ComboBox { Name = "cboEditor", Items = [.. names] }, 16, 44, 300, 26);
        _editor.ItemIndex = Math.Max(0, _kinds.IndexOf(settings.Editor));
        DialogLayout.Put(this, new Label { Caption = "Custom command ({file} {line} {folder} {solution})" }, 16, 80, 560, 24);
        _custom = DialogLayout.Put(this, new Edit { Name = "edtCustom", Text = settings.CustomEditorCommand, Placeholder = "\"C:\\\\Tools\\\\editor.exe\" \"{file}\" -n{line}" }, 16, 106, 568, 26);
        DialogLayout.Put(this, new Label { Caption = "New projects go in" }, 16, 146, 180, 24);
        _folder = DialogLayout.Put(this, new Edit { Name = "edtFolder", Text = settings.ProjectsFolder }, 200, 144, 384, 26);
        DialogLayout.Put(this, new Label { Caption = "RADSharp packages" }, 16, 182, 180, 24);
        _packages = DialogLayout.Put(this, new Edit { Name = "edtPackages", Text = settings.PackageSource, Placeholder = "auto-detect, then nuget.org" }, 200, 180, 384, 26);
        DialogLayout.Put(this, new Label
        {
            Caption = "VS Code and Rider open handlers at their line; Visual Studio opens the file.",
            FontColor = Drawing.Color.FromHex("#6B7280"),
        }, 16, 222, 568, 24);
        DialogLayout.Put(this, new Button { Name = "btnOk", Caption = "OK", Default = true, ModalResult = ModalResult.Ok }, 404, 272, 86, 30);
        DialogLayout.Put(this, new Button { Name = "btnCancel", Caption = "Cancel", Cancel = true, ModalResult = ModalResult.Cancel }, 498, 272, 86, 30);
        PerformLayout();
    }

    public void Apply()
    {
        _settings.Editor = _kinds[Math.Max(0, _editor.ItemIndex)];
        _settings.CustomEditorCommand = _custom.Text.Trim();
        if (_folder.Text.Trim().Length > 0) _settings.ProjectsFolder = _folder.Text.Trim();
        _settings.PackageSource = _packages.Text.Trim();
    }
}
