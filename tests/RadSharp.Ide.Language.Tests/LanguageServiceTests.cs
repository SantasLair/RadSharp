using System.Diagnostics;
using Xunit.Abstractions;

namespace RadSharp.Ide.Language.Tests;

public class LanguageServiceTests(ITestOutputHelper output)
{
    static readonly string SettingsForm = Samples.File("SettingsDialog", "SettingsForm.cs");
    static readonly string SettingsFormJson = Samples.File("SettingsDialog", "SettingsForm.radform.json");
    static readonly string SettingsMain = Samples.File("SettingsDialog", "MainForm.cs");
    static readonly string TileMain = Samples.File("TileEditor", "MainForm.cs");
    static readonly string TileMainJson = Samples.File("TileEditor", "MainForm.radform.json");

    // ---- Completion ----

    /// <summary>I2 done-when: a dropped control's field appears in completion without saving the form.</summary>
    [Fact]
    public async Task Dropped_controls_field_completes_before_saving()
    {
        var (workspace, language) = await Samples.OpenAsync("SettingsDialog");
        using var _ = workspace;
        var code = File.ReadAllText(SettingsForm).Replace("        edtUser.Text = s.User;", "        this.btn\n        edtUser.Text = s.User;");
        workspace.UpdateDocumentText(SettingsForm, code);
        int caret = Samples.After(code, "this.btn");

        var before = (await language.GetCompletionsAsync(SettingsForm, caret))!.Value;
        Assert.Contains(before.Items, i => i.DisplayText == "btnOk" && i.Kind == "Field");
        Assert.DoesNotContain(before.Items, i => i.DisplayText == "btnApply");

        // The designer drops a button; the shell hands over the unsaved form.
        workspace.UpdateFormText(SettingsFormJson, WorkspaceTests.WithButton(File.ReadAllText(SettingsFormJson), "btnApply"));
        var after = (await language.GetCompletionsAsync(SettingsForm, caret))!.Value;
        var item = Assert.Single(after.Items, i => i.DisplayText == "btnApply");
        Assert.Equal("Field", item.Kind);
        Assert.Equal("btnApply", after.Filter("btnA")[0].DisplayText);
        Assert.Equal(new TextRange(caret - 3, 3), after.Span);

        var detail = await language.ResolveCompletionAsync(after, item);
        Assert.Contains("Button", detail.Description);
        Assert.Equal("btnApply", Samples.Apply(code, detail.Edits).Substring(caret - 3, 8));
        Assert.DoesNotContain("btnApply", File.ReadAllText(SettingsFormJson));   // nothing was saved
    }

    [Fact]
    public async Task Completion_offers_members_and_snippets_by_context()
    {
        var (workspace, language) = await Samples.OpenAsync("SettingsDialog");
        using var _ = workspace;
        var code = File.ReadAllText(SettingsForm)
            .Replace("        edtUser.Text = s.User;", "        fo\n        edtUser.Text = s.User;")
            .Replace("    public SettingsForm() =>", "    pro\n    public SettingsForm() =>");
        workspace.UpdateDocumentText(SettingsForm, code);

        // Statement position: if/for/foreach, not prop.
        var statement = (await language.GetCompletionsAsync(SettingsForm, Samples.After(code, "        fo")))!.Value;
        Assert.Contains(statement.Items, i => i.DisplayText == "foreach" && i.IsSnippet);
        Assert.Contains(statement.Items, i => i.DisplayText == "if" && i.IsSnippet);
        Assert.DoesNotContain(statement.Items, i => i.DisplayText == "prop");
        Assert.Contains(statement.Items, i => i.DisplayText == "edtUser" && i.Kind == "Field");
        Assert.Contains(statement.Items, i => i.DisplayText == "Load" && i.Kind == "Method");

        var foreachItem = statement.Items.First(i => i.DisplayText == "foreach" && i.IsSnippet);
        var detail = await language.ResolveCompletionAsync(statement, foreachItem);
        var expanded = Samples.Apply(code, detail.Edits);
        Assert.Contains("        foreach (var item in collection)" + NewLine(code) + "        {", expanded);
        Assert.Equal(2, detail.Placeholders.Count);
        Assert.Equal("item", expanded.Substring(detail.Placeholders[0].Start, detail.Placeholders[0].Length));
        Assert.Equal("collection", expanded.Substring(detail.Placeholders[1].Start, detail.Placeholders[1].Length));
        Assert.Equal(detail.Placeholders[0].Start, detail.CaretPosition);

        // Member position: prop, not the statements.
        var member = (await language.GetCompletionsAsync(SettingsForm, Samples.After(code, "    pro")))!.Value;
        Assert.Contains(member.Items, i => i.DisplayText == "prop" && i.IsSnippet);
        Assert.DoesNotContain(member.Items, i => i.DisplayText == "foreach" && i.IsSnippet);
        var prop = await language.ResolveCompletionAsync(member, member.Items.First(i => i.DisplayText == "prop"));
        Assert.Contains("    public int MyProperty { get; set; }", Samples.Apply(code, prop.Edits));

        // Member access after a typed '.'.
        var dot = code.Replace("        fo\n", "        edtUser.\n");
        workspace.UpdateDocumentText(SettingsForm, dot);
        Assert.True(await language.ShouldTriggerCompletionAsync(SettingsForm, Samples.After(dot, "edtUser."), '.'));
        var members = (await language.GetCompletionsAsync(SettingsForm, Samples.After(dot, "edtUser."), '.'))!.Value;
        Assert.Contains(members.Items, i => i.DisplayText == "Text" && i.Kind == "Property");
        Assert.DoesNotContain(members.Items, i => i.IsSnippet);
    }

    static string NewLine(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    /// <summary>I2 done-when: completion within 150 ms on a warm workspace (the ceiling here is generous so CI noise doesn't flake it).</summary>
    [Fact]
    public async Task Completion_is_fast_on_a_warm_workspace()
    {
        var (workspace, language) = await Samples.OpenAsync("TileEditor");
        using var _ = workspace;
        var code = File.ReadAllText(TileMain);
        int position = Samples.After(code, "        vpMap.");
        await language.GetCompletionsAsync(TileMain, position, '.');   // warm up: compilation, MEF parts, JIT

        var times = new List<double>();
        for (int i = 0; i < 20; i++)
        {
            // Each round is a keystroke: a new version of the buffer, then a completion request.
            workspace.UpdateDocumentText(TileMain, code.Insert(position - "vpMap.".Length - 8, $"/*{i}*/"));
            int at = position + $"/*{i}*/".Length;
            var watch = Stopwatch.StartNew();
            var list = await language.GetCompletionsAsync(TileMain, at, '.');
            times.Add(watch.Elapsed.TotalMilliseconds);
            Assert.Contains(list!.Value.Items, item => item.DisplayText == "Invalidate");
        }
        times.Sort();
        double median = times[times.Count / 2], p90 = times[(int)(times.Count * 0.9)];
        output.WriteLine($"Warm completion over TileEditor/MainForm.cs: median {median:F1} ms, p90 {p90:F1} ms, max {times[^1]:F1} ms");
        Console.WriteLine($"[benchmark] warm completion: median {median:F1} ms, p90 {p90:F1} ms, max {times[^1]:F1} ms");
        Assert.True(median < 600, $"median {median:F1} ms");
    }

    // ---- Diagnostics ----

    /// <summary>I2 done-when: deleting a handler gives RAD1001 in the next diagnostics update.</summary>
    [Fact]
    public async Task Deleted_handler_gives_RAD1001_within_one_update()
    {
        var (workspace, language) = await Samples.OpenAsync("TileEditor");
        using var _ = workspace;
        using var monitor = new DiagnosticsMonitor(language, TimeSpan.FromMilliseconds(50));
        var initial = await monitor.WaitForAsync(workspace.Version).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.DoesNotContain(initial.Value, d => d.IsError);

        var updates = new List<long>();
        monitor.Updated += u => { lock (updates) updates.Add(u.Version); };
        var version = workspace.UpdateDocumentText(TileMain, File.ReadAllText(TileMain).Replace("    void mnuNew_Click(MenuItem sender) => NewMap();\n", "")
            .Replace("    void mnuNew_Click(MenuItem sender) => NewMap();\r\n", ""));
        var update = await monitor.WaitForAsync(version).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(version, update.Version);
        var missing = Assert.Single(update.Value, d => d.Id == "RAD1001");
        Assert.Equal(TileMainJson, missing.File, ignoreCase: true);
        Assert.Contains("mnuNew_Click", missing.Message);
        Assert.Equal(File.ReadAllLines(TileMainJson).ToList().FindIndex(l => l.Contains("\"mnuNew_Click\"")) + 1, missing.Line);
        lock (updates) Assert.Equal([version], updates);   // one update, for the version that has the edit

        // Per-file: the form file carries it, the code file has no error.
        Assert.Contains((await language.GetDiagnosticsAsync(TileMainJson)).Value, d => d.Id == "RAD1001");
        Assert.DoesNotContain((await language.GetDiagnosticsAsync(TileMain)).Value, d => d.IsError);
    }

    [Fact]
    public async Task Code_errors_have_locations_in_their_file()
    {
        var (workspace, language) = await Samples.OpenAsync("SettingsDialog");
        using var _ = workspace;
        var code = File.ReadAllText(SettingsForm).Replace("edtUser.Text = s.User;", "edtUser.Text = s.Missing;");
        workspace.UpdateDocumentText(SettingsForm, code);
        var error = Assert.Single((await language.GetDiagnosticsAsync(SettingsForm)).Value, d => d.IsError);
        Assert.Equal("CS1061", error.Id);
        Assert.Equal("Missing", code.Substring(error.Start, error.Length));
        Assert.Equal(code[..error.Start].Count(c => c == '\n') + 1, error.Line);
    }

    // ---- Navigation ----

    /// <summary>I2 done-when: go-to-definition targets, including a control field (its form file) and a type in another file.</summary>
    [Fact]
    public async Task Go_to_definition_targets()
    {
        var (workspace, language) = await Samples.OpenAsync("SettingsDialog");
        using var _ = workspace;
        var form = File.ReadAllText(SettingsForm);
        var main = File.ReadAllText(SettingsMain);

        // A record declared in MainForm.cs, used in SettingsForm.cs.
        var record = Assert.Single((await language.GoToDefinitionAsync(SettingsForm, Samples.After(form, "Load(Sett|ings s)"))).Value);
        Assert.Equal(("Source", SettingsMain), (record.Kind, record.File!));
        Assert.Equal(5, record.Line);
        Assert.Equal("Settings", main.Substring(record.Start, record.Length));

        // A control field: the control's name in the form file.
        var field = Assert.Single((await language.GoToDefinitionAsync(SettingsForm, Samples.After(form, "edtU|ser.Text = s.User"))).Value);
        Assert.Equal("Form", field.Kind);
        Assert.Equal(SettingsFormJson, field.File, ignoreCase: true);
        Assert.Equal(File.ReadAllLines(SettingsFormJson).ToList().FindIndex(l => l.Contains("\"name\": \"edtUser\"")) + 1, field.Line);

        // A method in the same file, from the caret at the end of the name.
        var method = Assert.Single((await language.GoToDefinitionAsync(SettingsMain, Samples.After(main, "        ShowSettings"))).Value);
        Assert.Equal(SettingsMain, method.File);
        Assert.Equal(main.Split('\n').ToList().FindIndex(l => l.Contains("void ShowSettings()")) + 1, method.Line);

        // A framework type: metadata.
        var metadata = Assert.Single((await language.GoToDefinitionAsync(SettingsForm, Samples.After(form, "Array.Index|Of"))).Value);
        Assert.Equal("Metadata", metadata.Kind);
        Assert.Null(metadata.File);
    }

    [Fact]
    public async Task Find_all_references_across_files()
    {
        var (workspace, language) = await Samples.OpenAsync("SettingsDialog");
        using var _ = workspace;
        var form = File.ReadAllText(SettingsForm);
        var refs = (await language.FindReferencesAsync(SettingsForm, Samples.After(form, "public void Lo|ad"))).Value;
        Assert.Contains(refs, r => r.IsDefinition && r.File == SettingsForm);
        Assert.Contains(refs, r => !r.IsDefinition && r.File == SettingsMain && r.LineText == "dialog.Load(_settings);");

        var field = (await language.FindReferencesAsync(SettingsForm, Samples.After(form, "edtU|ser.Text = s.User"))).Value;
        Assert.Equal(2, field.Count(r => !r.IsInGeneratedCode && !r.IsDefinition));   // Load and Result
        Assert.Contains(field, r => r.IsInGeneratedCode);                               // the declaration and InitializeComponent
    }

    // ---- Rename ----

    /// <summary>I2 done-when: a rename across two files.</summary>
    [Fact]
    public async Task Rename_across_two_files()
    {
        var (workspace, language) = await Samples.OpenAsync("SettingsDialog");
        using var _ = workspace;
        var form = File.ReadAllText(SettingsForm);
        var result = await language.RenameAsync(SettingsForm, Samples.After(form, "Load(Sett|ings s)"), "AppSettings");
        Assert.True(result.Success, result.Error);
        Assert.Equal([SettingsMain, SettingsForm], result.Documents.Select(d => d.File).Order(StringComparer.OrdinalIgnoreCase));
        var main = result.Documents.Single(d => d.File == SettingsMain);
        Assert.Contains("public sealed record AppSettings(", main.NewText);
        Assert.Contains("AppSettings _settings = new(", main.NewText);
        Assert.Contains("public void Load(AppSettings s)", result.Documents.Single(d => d.File == SettingsForm).NewText);
        Assert.Equal(main.NewText, Samples.Apply(File.ReadAllText(SettingsMain), main.Edits));
        Assert.Contains(result.Preview, p => p.File == SettingsForm && p.OldText == "public void Load(Settings s)" && p.NewText == "public void Load(AppSettings s)");
        Assert.DoesNotContain("AppSettings", File.ReadAllText(SettingsMain));   // nothing written

        workspace.ApplyEdits(result.Documents);
        Assert.DoesNotContain((await language.GetProjectDiagnosticsAsync()).Value, d => d.IsError);
        Assert.False((await language.RenameAsync(SettingsForm, Samples.After(form, "Load(Sett|ings s)"), "class")).Success);
    }

    /// <summary>The designer's rename: a control's field references and its Name_Event handlers, semantically.</summary>
    [Fact]
    public async Task Renaming_a_control_renames_its_field_uses_and_handlers()
    {
        var (workspace, language) = await Samples.OpenAsync("TileEditor");
        using var _ = workspace;
        var result = await language.RenameControlAsync(TileMainJson, "vpMap", "viewport");
        Assert.True(result.Success, result.Error);
        Assert.Contains(("vpMap_Draw", "viewport_Draw"), result.HandlerRenames);
        Assert.Contains(("vpMap_Wheel", "viewport_Wheel"), result.HandlerRenames);
        var code = Assert.Single(result.Documents).NewText;
        Assert.DoesNotContain("vpMap", code);
        Assert.Contains("viewport.Invalidate();", code);
        Assert.Contains("void viewport_Draw(", code);

        // The designer renames the control and its handlers in its document, then hands the form over.
        var json = File.ReadAllText(TileMainJson).Replace("\"vpMap", "\"viewport");
        workspace.ApplyEdits(result.Documents);
        workspace.UpdateFormText(TileMainJson, json);
        Assert.DoesNotContain((await language.GetProjectDiagnosticsAsync()).Value, d => d.IsError);
    }

    /// <summary>F2 on a control's field in code: the same control rename, with the form file's edits included.</summary>
    [Fact]
    public async Task Renaming_a_control_field_from_code_edits_the_form_too()
    {
        var (workspace, language) = await Samples.OpenAsync("SettingsDialog");
        using var _ = workspace;
        var main = File.ReadAllText(SettingsMain);
        var result = await language.RenameAsync(SettingsMain, Samples.After(main, "lblSumm|ary.Caption = \"Settings saved"), "lblStatus");
        Assert.True(result.Success, result.Error);
        var mainJson = Samples.File("SettingsDialog", "MainForm.radform.json");
        Assert.Contains("\"name\": \"lblStatus\"", result.Documents.Single(d => d.File.Equals(mainJson, StringComparison.OrdinalIgnoreCase)).NewText);
        Assert.Contains("lblStatus.Caption = \"Cancelled", result.Documents.Single(d => d.File == SettingsMain).NewText);

        workspace.ApplyEdits(result.Documents);
        Assert.DoesNotContain((await language.GetProjectDiagnosticsAsync()).Value, d => d.IsError);
    }

    [Fact]
    public async Task Renaming_a_handler_keeps_the_form_binding()
    {
        var (workspace, language) = await Samples.OpenAsync("SettingsDialog");
        using var _ = workspace;
        var main = File.ReadAllText(SettingsMain);
        var result = await language.RenameAsync(SettingsMain, Samples.After(main, "void btnSettings_Cl|ick"), "OpenSettings");
        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Documents, d => d.File.EndsWith("MainForm.radform.json") && d.NewText.Contains("\"onClick\": \"OpenSettings\""));
        workspace.ApplyEdits(result.Documents);
        Assert.DoesNotContain((await language.GetProjectDiagnosticsAsync()).Value, d => d.IsError);
    }

    // ---- Classification, quick info, signature help, formatting, code fixes ----

    [Fact]
    public async Task Classification_syntactic_then_semantic()
    {
        var (workspace, language) = await Samples.OpenAsync("SettingsDialog");
        using var _ = workspace;
        var code = File.ReadAllText(SettingsForm);
        string KindAt(IReadOnlyList<ClassifiedRange> spans, string marker) =>
            spans.First(s => s.Start == Samples.After(code, marker)).Kind;

        var syntax = (await language.ClassifySyntaxAsync(SettingsForm)).Value;
        Assert.Equal("keyword", KindAt(syntax, "|public partial class"));
        Assert.Equal("class name", KindAt(syntax, "partial class |SettingsForm"));
        Assert.Equal("identifier", KindAt(syntax, "        |edtUser.Text"));   // a field, but only the semantic pass knows
        Assert.StartsWith("xml doc comment", KindAt(syntax, "|/// <summary>"));

        var semantic = (await language.ClassifyAsync(SettingsForm)).Value;
        Assert.Equal("field name", KindAt(semantic, "        |edtUser.Text"));
        Assert.Equal("property name", KindAt(semantic, "edtUser.|Text"));
        Assert.Equal("record class name", KindAt(semantic, "Load(|Settings s)"));

        // A range: only spans that overlap it.
        int start = Samples.After(code, "|    public void Load");
        var ranged = (await language.ClassifyAsync(SettingsForm, new TextRange(start, 40))).Value;
        Assert.All(ranged, s => Assert.True(s.Start + s.Length > start && s.Start < start + 40));
    }

    [Fact]
    public async Task Quick_info_and_signature_help()
    {
        var (workspace, language) = await Samples.OpenAsync("SettingsDialog");
        using var _ = workspace;
        var main = File.ReadAllText(SettingsMain);
        var info = (await language.GetQuickInfoAsync(SettingsMain, Samples.After(main, "ShowSett|ings();"))).Value;
        Assert.NotNull(info);
        Assert.Contains("void MainForm.ShowSettings()", info.Text);

        var form = File.ReadAllText(SettingsForm);
        var call = form.Replace("        edtUser.Text = s.User;", "        Array.IndexOf(cboTheme.Items, \n        edtUser.Text = s.User;");
        workspace.UpdateDocumentText(SettingsForm, call);
        var help = (await language.GetSignatureHelpAsync(SettingsForm, Samples.After(call, "Array.IndexOf(cboTheme.Items, "))).Value;
        Assert.NotNull(help);
        Assert.True(help.Signatures.Count > 1);
        Assert.Equal(1, help.ActiveParameter);
        var signature = help.Signatures[help.ActiveSignature];
        Assert.StartsWith("int Array.IndexOf", signature.Label);
        var p = signature.Parameters[1];
        Assert.Equal(p.Display, signature.Label.Substring(p.LabelStart, p.LabelLength));

        Assert.Null((await language.GetSignatureHelpAsync(SettingsForm, Samples.After(call, "public void Lo"))).Value);
    }

    [Fact]
    public async Task Format_document_and_selection()
    {
        var (workspace, language) = await Samples.OpenAsync("SettingsDialog");
        using var _ = workspace;
        var original = File.ReadAllText(SettingsForm);
        var messy = original.Replace("        edtUser.Text = s.User;", "  edtUser.Text   =s.User;").Replace("chkAutosave.Checked = s.Autosave;", "chkAutosave.Checked=s.Autosave;");
        workspace.UpdateDocumentText(SettingsForm, messy);

        var all = (await language.FormatAsync(SettingsForm)).Value;
        Assert.Equal(original, Samples.Apply(messy, all));

        int start = Samples.After(messy, "|  edtUser.Text");
        var selection = (await language.FormatAsync(SettingsForm, new TextRange(start, "  edtUser.Text   =s.User;".Length))).Value;
        var formatted = Samples.Apply(messy, selection);
        Assert.Contains("        edtUser.Text = s.User;", formatted);
        Assert.Contains("chkAutosave.Checked=s.Autosave;", formatted);   // outside the selection
    }

    [Fact]
    public async Task Code_fixes_add_using_implement_interface_generate_method()
    {
        var (workspace, language) = await Samples.OpenAsync("SettingsDialog");
        using var _ = workspace;
        var code = File.ReadAllText(SettingsForm)
            .Replace("        edtUser.Text = s.User;", "        var sb = new StringBuilder();\n        Recalculate(42);\n        edtUser.Text = s.User;")
            + "\nclass Disposer : IDisposable { }\n";
        workspace.UpdateDocumentText(SettingsForm, code);

        async Task<CodeFixInfo> Fix(string marker, string category)
        {
            int at = Samples.After(code, marker);
            var fixes = (await language.GetCodeFixesAsync(SettingsForm, new TextRange(at, 0))).Value;
            return fixes.First(f => f.Category == category);
        }

        var addUsing = await Fix("new StringBu|ilder", "AddUsing");
        Assert.Contains("using System.Text;", Assert.Single(addUsing.Edits).NewText);

        var implement = await Fix("Disposer : IDisp|osable", "ImplementInterface");
        Assert.Contains("public void Dispose()", Assert.Single(implement.Edits).NewText);

        var generate = await Fix("Recalc|ulate(42)", "GenerateMethod");
        Assert.Matches(@"void Recalculate\(int \w+\)", Assert.Single(generate.Edits).NewText);
    }

    // ---- Navigate to (I5) ----

    [Fact]
    public async Task Symbol_search_finds_types_members_and_control_fields_by_subsequence()
    {
        var (workspace, language) = await Samples.OpenAsync("SettingsDialog");
        using var _ = workspace;

        var types = (await language.SearchSymbolsAsync("SettingsForm")).Value;
        var type = Assert.Single(types, s => s.Name == "SettingsForm" && s.Kind == "Class");
        Assert.Equal("Source", type.Location.Kind);
        Assert.Equal(SettingsForm, type.Location.File, ignoreCase: true);

        // A method, best match first.
        var load = (await language.SearchSymbolsAsync("Load", max: 5)).Value;
        Assert.Equal("Load", load[0].Name);
        Assert.Equal("Method", load[0].Kind);
        Assert.Equal("SettingsForm", load[0].Container);

        // A control field lives in generated code: it leads to the control in the form file.
        var field = Assert.Single((await language.SearchSymbolsAsync("edtUser")).Value, s => s.Name == "edtUser");
        Assert.Equal("Form", field.Location.Kind);
        Assert.EndsWith(".radform.json", field.Location.File, StringComparison.OrdinalIgnoreCase);

        Assert.Empty((await language.SearchSymbolsAsync("zzzNoSuchSymbol")).Value);
        Assert.Empty((await language.SearchSymbolsAsync("")).Value);
        Assert.True((await language.SearchSymbolsAsync("e", max: 3)).Value.Count <= 3);
    }

    [Fact]
    public void Document_symbols_list_the_declarations_of_a_file_in_order()
    {
        var text = File.ReadAllText(SettingsForm);
        var symbols = LanguageService.GetDocumentSymbols(text);
        Assert.Equal(["SettingsForm", "SettingsForm", "Load", "Result"], symbols.Select(s => s.Name).ToArray());
        Assert.Equal(["Class", "Constructor", "Method", "Property"], symbols.Select(s => s.Kind).ToArray());
        var load = symbols.Single(s => s.Name == "Load");
        Assert.Equal("SettingsForm", load.Container);
        Assert.Equal("Load", text.Substring(load.Start, load.Length));

        var more = LanguageService.GetDocumentSymbols("namespace N; enum E { A, B } struct S { int f, g; event System.Action Ev; } interface I { void M(); }");
        Assert.Equal(["E", "A", "B", "S", "f", "g", "Ev", "I", "M"], more.Select(s => s.Name).ToArray());
        Assert.Equal("N", more[0].Container);
        Assert.Equal("Struct", more.Single(s => s.Name == "S").Kind);
    }
}
