using RadSharp.Design.Code;
using RadSharp.Drawing;

namespace RadSharp.Designer;

/// <summary>A one-line text prompt (names for new files and folders, rename). The text box is <c>edtValue</c>.</summary>
public sealed class PromptDialog : Form
{
    readonly Edit _value;

    public PromptDialog(string title, string label, string initial, Form? owner)
    {
        DialogLayout.Setup(this, title, 420, 150, owner);
        DialogLayout.Put(this, new Label { Caption = label }, 16, 16, 388, 24);
        _value = DialogLayout.Put(this, new Edit { Name = "edtValue", Text = initial }, 16, 44, 388, 26);
        DialogLayout.Put(this, new Button { Name = "btnOk", Caption = "OK", Default = true, ModalResult = ModalResult.Ok }, 218, 100, 90, 30);
        DialogLayout.Put(this, new Button { Name = "btnCancel", Caption = "Cancel", Cancel = true, ModalResult = ModalResult.Cancel }, 318, 100, 86, 30);
        PerformLayout();
        Shown += _ => { _value.Focus(); _value.SelectAll(); };
    }

    public string Value => _value.Text.Trim();
}

/// <summary>Pick one of a list of choices from a drop-down (the target folder of Move). The box is <c>cboChoice</c>.</summary>
public sealed class ChooseDialog : Form
{
    readonly ComboBox _choice;

    public ChooseDialog(string title, string label, IReadOnlyList<string> items, int selected, Form? owner)
    {
        DialogLayout.Setup(this, title, 420, 150, owner);
        DialogLayout.Put(this, new Label { Caption = label }, 16, 16, 388, 24);
        _choice = DialogLayout.Put(this, new ComboBox { Name = "cboChoice", Items = [.. items] }, 16, 44, 388, 26);
        _choice.ItemIndex = Math.Clamp(selected, 0, Math.Max(0, items.Count - 1));
        DialogLayout.Put(this, new Button { Name = "btnOk", Caption = "OK", Default = true, ModalResult = ModalResult.Ok }, 218, 100, 90, 30);
        DialogLayout.Put(this, new Button { Name = "btnCancel", Caption = "Cancel", Cancel = true, ModalResult = ModalResult.Cancel }, 318, 100, 86, 30);
        PerformLayout();
    }

    public int Selected => _choice.ItemIndex;
}

/// <summary>
/// Project › Properties: assembly name, version, application icon, root namespace, runtime identifiers and the
/// NativeAOT switch, edited in the .csproj through <see cref="CsprojEditor"/> (everything else in the file is kept).
/// </summary>
public sealed class ProjectPropertiesDialog : Form
{
    readonly RadProject _project;
    readonly CsprojEditor _editor;
    readonly ProjectProperties _original;
    readonly Edit _assembly, _version, _icon, _namespace, _rids;
    readonly CheckBox _aot;

    public ProjectPropertiesDialog(RadProject project, Form? owner)
    {
        _project = project;
        _editor = CsprojEditor.Load(project.ProjectFile);
        _original = _editor.Read();
        DialogLayout.Setup(this, $"Project Properties — {project.Name}", 560, 380, owner);
        int y = 16;
        Edit Row(string caption, string name, string? value, string placeholder)
        {
            DialogLayout.Put(this, new Label { Caption = caption }, 16, y + 2, 150, 24);
            var edit = DialogLayout.Put(this, new Edit { Name = name, Text = value ?? "", Placeholder = placeholder }, 170, y, 370, 26);
            y += 38;
            return edit;
        }
        _assembly = Row("Application name", "edtAssemblyName", _original.AssemblyName, project.Name);
        _version = Row("Version", "edtVersion", _original.Version, "1.0.0");
        _icon = Row("Application icon", "edtIcon", _original.ApplicationIcon, "app.ico (relative to the project)");
        _namespace = Row("Root namespace", "edtRootNamespace", _original.RootNamespace, project.RootNamespace);
        _rids = Row("Runtime identifiers", "edtRuntimeIdentifiers", _original.RuntimeIdentifiers, "win-x64;linux-x64;osx-arm64");
        _aot = DialogLayout.Put(this, new CheckBox { Name = "chkPublishAot", Caption = "Publish as NativeAOT (PublishAot)", Checked = _original.PublishAot == true }, 170, y, 370, 26);
        y += 40;
        DialogLayout.Put(this, new Label
        {
            Name = "lblNote",
            Caption = "Edits the .csproj; other elements, comments and layout are kept. Empty fields are removed.\nChanging the root namespace does not rewrite existing code.",
            FontColor = Drawing.Color.FromHex("#6B7280"),
        }, 16, y, 524, 44);
        DialogLayout.Put(this, new Button { Name = "btnOk", Caption = "OK", Default = true, ModalResult = ModalResult.Ok }, 354, 330, 90, 30);
        DialogLayout.Put(this, new Button { Name = "btnCancel", Caption = "Cancel", Cancel = true, ModalResult = ModalResult.Cancel }, 454, 330, 86, 30);
        PerformLayout();
    }

    /// <summary>The values as currently entered.</summary>
    public ProjectProperties Current() => new()
    {
        AssemblyName = _assembly.Text.Trim(),
        Version = _version.Text.Trim(),
        ApplicationIcon = _icon.Text.Trim(),
        RootNamespace = _namespace.Text.Trim(),
        RuntimeIdentifiers = _rids.Text.Trim(),
        PublishAot = _aot.Checked ? true : _original.PublishAot is null ? null : false,
    };

    /// <summary>Writes the entered values into the .csproj (nothing is written if nothing changed). Returns whether the file changed.</summary>
    public bool Save()
    {
        _editor.Apply(Current());
        if (!_editor.Changed) return false;
        _editor.Save();
        return true;
    }
}

/// <summary>
/// Project › Manage NuGet Packages: the project's installed packages (update, remove) and a nuget.org search (add).
/// Changes go through <c>dotnet add/remove package</c>, with its output sent to <c>log</c> (the IDE's Output window).
/// The search sends only the query text, with a generic user agent.
/// </summary>
public sealed class NuGetDialog : Form
{
    readonly RadProject _project;
    readonly NuGetService _nuget;
    readonly Action<string> _log;
    readonly ListBox _installed, _results;
    readonly Edit _query;
    readonly ComboBox _version;
    readonly CheckBox _prerelease;
    readonly Label _status;
    IReadOnlyList<PackageRef> _installedRefs = [];
    IReadOnlyList<PackageSearchResult> _found = [];
    bool _busy;

    public NuGetDialog(RadProject project, NuGetService nuget, Action<string> log, Form? owner)
    {
        _project = project;
        _nuget = nuget;
        _log = log;
        DialogLayout.Setup(this, $"NuGet Packages — {project.Name}", 780, 560, owner);
        DialogLayout.Put(this, new Label { Caption = "Installed", FontBold = true }, 16, 12, 300, 24);
        _installed = DialogLayout.Put(this, new ListBox { Name = "lstInstalled" }, 16, 40, 748, 130);
        DialogLayout.Put(this, new Button { Name = "btnUpdate", Caption = "Update to latest" }, 16, 178, 140, 28).Click += async _ => await UpdateSelectedAsync();
        DialogLayout.Put(this, new Button { Name = "btnRemove", Caption = "Remove" }, 164, 178, 100, 28).Click += async _ => await RemoveSelectedAsync();

        DialogLayout.Put(this, new Label { Caption = "Search nuget.org", FontBold = true }, 16, 222, 300, 24);
        _query = DialogLayout.Put(this, new Edit { Name = "edtSearch", Placeholder = "package name or keywords" }, 16, 250, 480, 26);
        _prerelease = DialogLayout.Put(this, new CheckBox { Name = "chkPrerelease", Caption = "Include prerelease" }, 506, 250, 160, 26);
        DialogLayout.Put(this, new Button { Name = "btnSearch", Caption = "Search", Default = false }, 674, 248, 90, 28).Click += async _ => await SearchAsync();
        _results = DialogLayout.Put(this, new ListBox { Name = "lstResults" }, 16, 284, 748, 170);
        DialogLayout.Put(this, new Label { Caption = "Version" }, 16, 466, 60, 26).Layout = VerticalAlign.Center;
        _version = DialogLayout.Put(this, new ComboBox { Name = "cboVersion" }, 80, 466, 200, 26);
        DialogLayout.Put(this, new Button { Name = "btnAdd", Caption = "Add to project" }, 292, 464, 130, 28).Click += async _ => await AddSelectedAsync();
        _status = DialogLayout.Put(this, new Label { Name = "lblStatus", FontColor = Drawing.Color.FromHex("#6B7280") }, 16, 500, 640, 24);
        DialogLayout.Put(this, new Button { Name = "btnClose", Caption = "Close", Cancel = true, ModalResult = ModalResult.Cancel }, 674, 504, 90, 30);
        _results.Change += _ => FillVersions();
        _query.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); } };
        RefreshInstalled();
        PerformLayout();
    }

    public IReadOnlyList<PackageRef> Installed => _installedRefs;
    public IReadOnlyList<PackageSearchResult> Results => _found;
    public string Query { get => _query.Text; set => _query.Text = value; }
    public string Status => _status.Caption;

    /// <summary>Re-reads the installed list from the .csproj.</summary>
    public void RefreshInstalled()
    {
        _installedRefs = NuGetService.InstalledPackages(_project.ProjectFile);
        _installed.Items = [.. _installedRefs.Select(p => p.Version is { } v ? $"{p.Id}   {v}" : p.Id)];
        if (_installed.Items.Length > 0 && _installed.ItemIndex < 0) _installed.ItemIndex = 0;
    }

    public void SelectInstalled(string id) => _installed.ItemIndex = _installedRefs.ToList().FindIndex(p => p.Id == id);

    public void SelectResult(int index) { _results.ItemIndex = index; FillVersions(); }

    PackageRef? SelectedInstalled => _installed.ItemIndex >= 0 && _installed.ItemIndex < _installedRefs.Count ? _installedRefs[_installed.ItemIndex] : null;
    PackageSearchResult? SelectedResult => _results.ItemIndex >= 0 && _results.ItemIndex < _found.Count ? _found[_results.ItemIndex] : null;

    void FillVersions()
    {
        if (SelectedResult is not { } r) { _version.Items = []; return; }
        var versions = r.Versions.Count > 0 ? r.Versions.Reverse().ToList() : [r.Version];
        _version.Items = [.. versions];
        _version.ItemIndex = 0;   // the newest
    }

    void Say(string text) => Application.Post(() => { _status.Caption = text; Invalidate(); });

    public async Task SearchAsync()
    {
        var q = _query.Text.Trim();
        if (q.Length == 0) { Say("Type something to search for."); return; }
        Say("Searching nuget.org…");
        try
        {
            var found = await _nuget.SearchAsync(q, _prerelease.Checked);
            Application.Post(() =>
            {
                _found = found;
                _results.Items = [.. found.Select(r => $"{r.Id}   {r.Version}   ({Downloads(r.TotalDownloads)} downloads)   {FirstLine(r.Description)}")];
                _results.ItemIndex = found.Count > 0 ? 0 : -1;
                FillVersions();
                _status.Caption = found.Count == 0 ? "No packages found." : $"{found.Count} package(s).";
            });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Say("The search failed: " + e.Message);
        }
    }

    static string Downloads(long n) => n >= 1_000_000_000 ? $"{n / 1e9:0.#}B" : n >= 1_000_000 ? $"{n / 1e6:0.#}M" : n >= 1_000 ? $"{n / 1e3:0.#}K" : n.ToString();

    static string FirstLine(string s)
    {
        var line = s.Split('\n')[0].Trim();
        return line.Length > 80 ? line[..80] + "…" : line;
    }

    public Task AddSelectedAsync()
    {
        if (SelectedResult is not { } r) { Say("Search and select a package first."); return Task.CompletedTask; }
        var version = _version.ItemIndex >= 0 && _version.ItemIndex < _version.Items.Length ? _version.Items[_version.ItemIndex] : r.Version;
        return Run($"Adding {r.Id} {version}", () => _nuget.AddAsync(_project, r.Id, version, _log, prerelease: false));
    }

    public Task UpdateSelectedAsync() =>
        SelectedInstalled is { } p ? Run($"Updating {p.Id}", () => _nuget.UpdateAsync(_project, p.Id, _log))
            : Say2("Select an installed package first.");

    public Task RemoveSelectedAsync() =>
        SelectedInstalled is { } p ? Run($"Removing {p.Id}", () => _nuget.RemoveAsync(_project, p.Id, _log))
            : Say2("Select an installed package first.");

    Task Say2(string text) { Say(text); return Task.CompletedTask; }

    async Task Run(string what, Func<Task<int>> action)
    {
        if (_busy) return;
        _busy = true;
        Say(what + "…");
        int exit;
        try { exit = await action(); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log(what + " failed: " + e.Message);
            Say(what + " failed: " + e.Message);
            _busy = false;
            return;
        }
        Application.Post(() =>
        {
            _busy = false;
            RefreshInstalled();
            _status.Caption = exit == 0 ? what + ": done." : $"{what}: failed (exit code {exit}); see Output.";
            Changed?.Invoke();
        });
    }

    /// <summary>Raised after a package command finished (the IDE re-reads the project).</summary>
    public event Action? Changed;
}
