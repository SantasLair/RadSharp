using RadSharp.Drawing;

namespace RadSharp.CodeEditing.Tests;

/// <summary>The language-agnostic hooks: tokens, decorations, completion, hover and hints.</summary>
public class HookTests
{
    sealed class WordCompletion(params string[] words) : ICompletionSource
    {
        public int Requests;
        public CompletionRequest? Last;
        public TaskCompletionSource<CompletionList?>? Pending;

        public IReadOnlyCollection<char> TriggerCharacters => ['.'];

        public Task<CompletionList?> GetCompletionsAsync(CompletionRequest request, CancellationToken cancellationToken)
        {
            Requests++;
            Last = request;
            var snap = request.Snapshot;
            int start = request.Offset;
            while (start > 0 && char.IsLetterOrDigit(snap[start - 1])) start--;
            var list = new CompletionList(start, request.Offset - start, [.. words.Select(w => new CompletionItem(w, Kind: "method", Detail: "void " + w + "()"))]);
            if (Pending != null) return Pending.Task;
            return Task.FromResult<CompletionList?>(list);
        }
    }

    sealed class Squiggles(TextSnapshot snapshot, params Decoration[] items) : IDecorationSource
    {
        public DecorationSet? GetDecorations(TextSnapshot current) => new(snapshot, items);
        public event EventHandler<SourceChangedEventArgs>? Changed;
        public void Raise() => Changed?.Invoke(this, new SourceChangedEventArgs());
    }

    [Fact]
    public void Completion_opens_on_trigger_character_filters_and_commits()
    {
        var (f, ed) = EditorInputTests.Setup("obj");
        var source = new WordCompletion("ToString", "GetType", "GetHashCode", "Equals");
        ed.CompletionSource = source;
        f.Simulate.Key(Key.End);
        f.Simulate.Type(".");
        Assert.True(ed.IsCompletionOpen);
        Assert.Equal(CompletionTriggerKind.TriggerCharacter, source.Last!.Trigger);
        Assert.Equal(4, ed.CompletionItems.Count);
        f.Simulate.Type("g");
        f.Simulate.Type("e");
        f.Simulate.Type("t");
        Assert.Equal(["GetHashCode", "GetType"], ed.CompletionItems.Select(i => i.DisplayText));
        f.Simulate.Key(Key.Down);
        Assert.Equal(1, ed.SelectedCompletionIndex);
        using (RadSharp.Drawing.FormRenderer.RenderToBitmap(f)) { }
        f.Simulate.Key(Key.Enter);
        Assert.False(ed.IsCompletionOpen);
        Assert.Equal("obj.GetType", ed.Text);
        Assert.Equal(1, source.Requests);   // filtering is local
    }

    /// <summary>A source whose commit adds a line at the top and puts the caret inside the inserted call.</summary>
    sealed class UsingCompletion : ICompletionSource
    {
        public IReadOnlyCollection<char> TriggerCharacters => [];
        public Task<CompletionList?> GetCompletionsAsync(CompletionRequest request, CancellationToken cancellationToken)
        {
            int start = request.Offset;
            while (start > 0 && char.IsLetter(request.Snapshot[start - 1])) start--;
            return Task.FromResult<CompletionList?>(new CompletionList(start, request.Offset - start, [new CompletionItem("WriteLine")]));
        }
        public CompletionCommit? GetCommit(TextSnapshot snapshot, CompletionList list, CompletionItem item) =>
            new([TextEdit.Insert(0, Using), new TextEdit(list.ApplicableStart, list.ApplicableLength, "WriteLine()")],
                Using.Length + list.ApplicableStart + "WriteLine(".Length);

        public const string Using = "using X;" + "\u000A";
    }

    [Fact]
    public void Completion_commit_from_the_source_applies_several_edits_as_one_undo_step()
    {
        var (f, ed) = EditorInputTests.Setup("go ");
        ed.CompletionSource = new UsingCompletion();
        f.Simulate.Key(Key.End);
        f.Simulate.Type("Wr");
        f.Simulate.Key(Key.Space, KeyModifiers.Ctrl);
        Assert.True(ed.IsCompletionOpen);
        f.Simulate.Key(Key.Enter);
        Assert.Equal(UsingCompletion.Using + "go WriteLine()", ed.Text);
        Assert.Equal(ed.Text.Length - 1, ed.CaretOffset);
        Assert.True(ed.Undo());
        Assert.Equal("go Wr", ed.Text);
    }

    [Fact]
    public void Completion_on_ctrl_space_and_typing_and_escape()
    {
        var (f, ed) = EditorInputTests.Setup("");
        var source = new WordCompletion("Console", "Convert");
        ed.CompletionSource = source;
        f.Simulate.Type("C");
        Assert.True(ed.IsCompletionOpen);
        Assert.Equal(CompletionTriggerKind.Typing, source.Last!.Trigger);
        f.Simulate.Key(Key.Escape);
        Assert.False(ed.IsCompletionOpen);
        f.Simulate.Key(Key.Space, KeyModifiers.Ctrl);
        Assert.True(ed.IsCompletionOpen);
        f.Simulate.Type("onv");
        f.Simulate.Key(Key.Tab);
        Assert.Equal("Convert", ed.Text);
        // A character that can't continue an identifier closes the list.
        f.Simulate.Key(Key.Space, KeyModifiers.Ctrl);
        f.Simulate.Type(" ");
        Assert.False(ed.IsCompletionOpen);
    }

    [Fact]
    public void Async_completion_results_arrive_on_tick_and_stale_ones_are_dropped()
    {
        var (f, ed) = EditorInputTests.Setup("x");
        var source = new WordCompletion("Alpha", "Beta") { Pending = new TaskCompletionSource<CompletionList?>() };
        ed.CompletionSource = source;
        f.Simulate.Key(Key.End);
        f.Simulate.Key(Key.Space, KeyModifiers.Ctrl);
        Assert.False(ed.IsCompletionOpen);
        var first = source.Pending;
        source.Pending = new TaskCompletionSource<CompletionList?>();
        f.Simulate.Key(Key.Space, KeyModifiers.Ctrl);       // supersedes the first request
        first.SetResult(new CompletionList(1, 0, [new CompletionItem("Stale")]));
        f.ProcessTick(10);
        Assert.False(ed.IsCompletionOpen);
        source.Pending.SetResult(new CompletionList(1, 0, [new CompletionItem("Fresh")]));
        f.ProcessTick(20);
        Assert.True(ed.IsCompletionOpen);
        Assert.Equal("Fresh", ed.CompletionItems.Single().DisplayText);
    }

    [Fact]
    public void Decorations_from_an_old_snapshot_follow_edits_and_show_messages_on_hover()
    {
        var (f, ed) = EditorInputTests.Setup("int x = y;");
        var squiggle = new Squiggles(ed.Snapshot, new Decoration(8, 1, DecorationKind.ErrorSquiggle, "The name 'y' does not exist"));
        ed.DecorationSources.Add(squiggle);
        ed.GoTo(0, 0);
        f.Simulate.Type("var ");                              // the source hasn't caught up: the range is mapped
        var deco = ed.CurrentDecorations().Single();
        Assert.Equal(12, deco.Start);
        Assert.Equal(13, deco.End);

        int hovered = -1;
        ed.Hover += (_, e) => hovered = e.Offset;
        var (x, y) = ed.PointOfOffset(12);
        f.Simulate.MoveTo(ed.AbsoluteLeft + x + 2, ed.AbsoluteTop + y + ed.LineHeight / 2);
        f.ProcessTick(f.Now + 100);
        Assert.Equal(-1, hovered);
        f.ProcessTick(f.Now + 600);
        Assert.Equal(12, hovered);
        Assert.Equal("The name 'y' does not exist", ed.HintText);
        f.Simulate.MoveTo(ed.AbsoluteLeft + 300, ed.AbsoluteTop + 200);
        Assert.False(ed.HintVisible);
    }

    [Fact]
    public void Hover_handler_answers_with_show_hint()
    {
        var (f, ed) = EditorInputTests.Setup("Console.WriteLine();");
        ed.Hover += (_, e) => ed.ShowHint(e.Offset, "class System.Console");
        var (x, y) = ed.PointOfOffset(2);
        f.Simulate.MoveTo(ed.AbsoluteLeft + x, ed.AbsoluteTop + y + 4);
        f.ProcessTick(1000);
        Assert.Equal("class System.Console", ed.HintText);
        f.Simulate.Key(Key.Escape);
        Assert.False(ed.HintVisible);
        // A programmatic hint (signature help) stays while typing.
        ed.ShowHint(ed.CaretOffset, "WriteLine(string value)", above: true);
        f.Simulate.Type("a");
        Assert.True(ed.HintVisible);
    }

    [Fact]
    public void Token_source_colours_lines_against_the_snapshot()
    {
        var src = new CSharpTokenSource();
        var b = new TextBuffer("/* start\n still comment */ int x = 42; // done\n@\"verbatim\n\"\"end\" + $\"{x}\";");
        var l1 = src.GetTokens(b.Current, 1)!;
        Assert.Equal(Classifications.Comment, l1[0].Classification);
        Assert.Contains(l1, t => t.Classification == Classifications.Keyword && t.Start == 18);
        Assert.Contains(l1, t => t.Classification == Classifications.Number);
        Assert.Equal(Classifications.Comment, l1[^1].Classification);
        var l3 = src.GetTokens(b.Current, 3)!;
        Assert.Equal(Classifications.VerbatimString, l3[0].Classification);
        b.Delete(0, 2);                                      // no longer a block comment
        var again = src.GetTokens(b.Current, 1)!;
        Assert.NotEqual(Classifications.Comment, again[0].Classification);
    }

    [Fact]
    public void Colors_map_classifications_with_fallback()
    {
        Assert.Equal(CodeEditorColors.Dark.Tokens[Classifications.Keyword], CodeEditorColors.Dark.ColorOf("keyword"));
        Assert.Equal(CodeEditorColors.Light.Tokens[Classifications.String], CodeEditorColors.Light.ColorOf("string - escape character"));
        Assert.Null(CodeEditorColors.Light.ColorOf("something unknown"));
        var (f, ed) = EditorInputTests.Setup("x");
        Assert.Same(CodeEditorColors.Light, ed.EffectiveColors);
        f.Theme = Theme.Dark;
        Assert.Same(CodeEditorColors.Dark, ed.EffectiveColors);
    }

    [Fact]
    public void Bundled_font_is_registered_without_ligatures()
    {
        CodeEditorFonts.EnsureRegistered();
        var plain = CodeEditorFonts.Face();
        var lig = CodeEditorFonts.Face(ligatures: true);
        Assert.True(plain.IsBundled);
        Assert.Equal(CodeEditorFonts.Family, plain.Name);
        var font = new RenderFont(plain, 13);
        // Monospace: every ASCII character has the same advance, and "=>" is two separate glyphs.
        Assert.Equal(TextLayout.Get("i", font).Width, TextLayout.Get("W", font).Width, 3);
        Assert.Equal(2, plain.Shape("=>").Glyphs.Length);
        Assert.NotEqual(plain.Shape("=>").Glyphs, lig.Shape("=>").Glyphs);
        Assert.NotNull(FontFace.Resolve(CodeEditorFonts.Family, true, false));
    }
}

public class FormFileTests
{
    [Fact]
    public void CodeEditor_loads_from_a_form_file_with_its_published_properties()
    {
        const string json = """
        {
          "format": "radform", "version": "1.0",
          "root": { "class": "Form", "name": "frm", "width": 400, "height": 300, "children": [
            { "class": "CodeEditor", "name": "edCode", "align": "client", "tabSize": 2, "lineCommentPrefix": "#",
              "language": "plain", "showLineNumbers": false, "fontSize": 15, "text": "a\nb" } ] }
        }
        """;
        var (form, warnings) = FormReader.Load(json);
        Assert.Empty(warnings);
        var ed = Assert.IsType<RadSharp.CodeEditor>(form.FindControl("edCode"));
        Assert.Equal(2, ed.TabSize);
        Assert.Equal("#", ed.LineCommentPrefix);
        Assert.Null(ed.TokenSource);
        Assert.False(ed.ShowLineNumbers);
        Assert.Equal(15, ed.FontSize);
        Assert.Equal(2, ed.LineCount);
    }
}
