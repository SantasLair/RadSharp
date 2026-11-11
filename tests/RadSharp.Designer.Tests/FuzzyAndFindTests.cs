using RadSharp.Designer;

namespace RadSharp.Tests;

/// <summary>The pure parts of I5: the fuzzy matcher, the Find in Files engine and the navigation history.</summary>
public class FuzzyAndFindTests
{
    // ---- Fuzzy matcher ----

    [Fact]
    public void Fuzzy_matches_subsequences_case_insensitively_and_rejects_the_rest()
    {
        Assert.NotNull(FuzzyMatcher.Find("sva", "Save All"));
        Assert.NotNull(FuzzyMatcher.Find("SAVE", "save all"));
        Assert.Null(FuzzyMatcher.Find("vas", "Save All"));     // order matters
        Assert.Null(FuzzyMatcher.Find("xyz", "Save All"));
        Assert.Null(FuzzyMatcher.Find("toolong", "short"));
        var empty = FuzzyMatcher.Find("", "anything");
        Assert.NotNull(empty);
        Assert.Equal(0, empty!.Value.Score);
        Assert.Empty(empty.Value.Indices);
    }

    [Fact]
    public void Fuzzy_prefers_word_starts_and_contiguous_runs()
    {
        // Word starts beat a scattered match.
        Assert.True(FuzzyMatcher.Score("sa", "Save All") > FuzzyMatcher.Score("sa", "Disable"));
        // Contiguous beats gapped.
        Assert.True(FuzzyMatcher.Score("save", "Save All") > FuzzyMatcher.Score("save", "Sxaxvxe"));
        // Camel-case humps and path segments count as word starts.
        var hump = FuzzyMatcher.Find("fcb", "FormCodeBehind")!.Value;
        Assert.Equal([0, 4, 8], hump.Indices);
        Assert.True(FuzzyMatcher.Score("pf", "src/Project/File.cs") > FuzzyMatcher.Score("pf", "src/xpxxfx.cs"));
        // A prefix beats the same letters later in the text.
        Assert.True(FuzzyMatcher.Score("run", "Run Without Debugging") > FuzzyMatcher.Score("run", "Build and Run"));
        // Exact case is a tie-breaker.
        Assert.True(FuzzyMatcher.Score("Save", "Save") > FuzzyMatcher.Score("save", "Save"));
    }

    [Fact]
    public void Fuzzy_ranks_a_list_best_first_and_keeps_ties_in_order()
    {
        string[] items = ["Close Project", "Open Project…", "Project Properties", "Save All"];
        var ranked = FuzzyMatcher.Rank("proj", items, s => s).Select(r => r.Item).ToList();
        Assert.Equal("Project Properties", ranked[0]);   // the word start at the beginning wins
        Assert.DoesNotContain("Save All", ranked);
        Assert.Equal(["Open Project…", "Close Project"], ranked.Skip(1).ToArray());   // the earlier word start scores higher
        Assert.Equal(["a x", "b x", "c x"], FuzzyMatcher.Rank("x", new[] { "a x", "b x", "c x" }, s => s).Select(r => r.Item).ToArray());   // equal scores keep the original order
    }

    // ---- Find in Files engine ----

    static FindOptions O(string text, bool matchCase = false, bool word = false, bool regex = false, string filter = "") =>
        new(text, matchCase, word, regex, filter);

    [Fact]
    public void Plain_case_and_whole_word_search()
    {
        const string text = "int Count;\nint count2;\nvar COUNT = Count + counter;\n";
        int Hits(FindOptions o) => FindInFiles.FindInText(o.ToRegex(out _)!, "a.cs", text).Count;

        Assert.Equal(5, Hits(O("count")));                              // ignores case, matches inside words
        Assert.Equal(2, Hits(O("count", matchCase: true)));             // counter and count2
        Assert.Equal(3, Hits(O("count", word: true)));                  // Count, COUNT, Count (not count2/counter)
        Assert.Equal(2, Hits(O("Count", matchCase: true, word: true)));
        Assert.Equal(1, Hits(O("Count + counter")));                    // a literal, not a pattern
        Assert.Equal(0, Hits(O("a.b")));

        var hit = FindInFiles.FindInText(O("count2").ToRegex(out _)!, "a.cs", text).Single();
        Assert.Equal(2, hit.Line);
        Assert.Equal(5, hit.Column);
        Assert.Equal("int count2;", hit.LineText);
        Assert.Equal("count2", text.Substring(hit.Start, hit.Length));
    }

    [Fact]
    public void Regex_search_and_bad_patterns()
    {
        const string text = "foo(1)\nfoo(22)\nbar(3)\n";
        var re = O(@"foo\((\d+)\)", regex: true).ToRegex(out var error)!;
        Assert.Null(error);
        Assert.Equal(2, FindInFiles.FindInText(re, "a.cs", text).Count);
        Assert.Equal("foo[1]\nfoo[22]\nbar(3)\n", FindInFiles.ReplaceInText(re, text, "foo[$1]", literal: false));
        // Whole word applies to a regex too.
        Assert.Single(FindInFiles.FindInText(O(@"\d+", regex: true, word: true).ToRegex(out _)!, "a.cs", "x1 22 y3"));

        Assert.Null(O("(unclosed", regex: true).ToRegex(out error));
        Assert.NotNull(error);
        Assert.Null(O("").ToRegex(out error));
        Assert.NotNull(error);
        // A literal search escapes regex characters, and a literal replacement keeps its $ signs.
        var lit = O("a.b").ToRegex(out _)!;
        Assert.Equal("x$1y a_b", FindInFiles.ReplaceInText(lit, "xa.by a_b", "$1", literal: true));
    }

    [Fact]
    public void File_filter_globs_include_exclude_and_skip_build_folders()
    {
        var root = Directory.CreateTempSubdirectory("radsharp-find-").FullName;
        void Put(string rel, string text = "needle")
        {
            var p = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        Put("A.cs"); Put("B.radform.json"); Put("notes.md"); Put("sub/C.cs"); Put("sub/D.txt");
        Put("bin/Debug/X.cs"); Put("obj/Y.cs"); Put("sub/obj/Z.cs"); Put("image.png", "needle\0binary");

        List<string> Rel(string filter) =>
            [.. FindInFiles.EnumerateFiles([root], filter).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal)];

        Assert.Equal(["A.cs", "B.radform.json", "notes.md", "sub/C.cs", "sub/D.txt"], Rel(""));     // text files only, no bin/obj
        Assert.Equal(["A.cs", "sub/C.cs"], Rel("*.cs"));
        Assert.Equal(["A.cs", "B.radform.json", "sub/C.cs"], Rel("*.cs; *.radform.json"));
        Assert.Equal(["A.cs", "notes.md", "sub/C.cs", "sub/D.txt"], Rel("!*.json"));
        Assert.Equal(["sub/C.cs"], Rel("sub/*.cs"));
        Assert.Equal(["sub/C.cs", "sub/D.txt"], Rel("sub/**"));
        Assert.Equal(["image.png"], Rel("*.png"));   // an explicit filter reaches any extension...

        var hits = FindInFiles.Search(O("needle"), FindInFiles.EnumerateFiles([root], "*"));
        Assert.DoesNotContain(hits, h => h.File.EndsWith("image.png"));                  // ...but binary content is skipped
        Assert.Equal(5, hits.Count);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Search_uses_the_override_text_for_open_buffers_and_can_be_cancelled()
    {
        var root = Directory.CreateTempSubdirectory("radsharp-find-").FullName;
        var a = Path.Combine(root, "A.cs");
        var b = Path.Combine(root, "B.cs");
        File.WriteAllText(a, "on disk");
        File.WriteAllText(b, "on disk");
        var hits = FindInFiles.Search(O("buffer"), [a, b], f => f == a ? "an unsaved buffer" : null);
        Assert.Equal(a, Assert.Single(hits).File);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => FindInFiles.Search(O("disk"), [a, b], cancel: cts.Token));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void A_hit_in_a_form_file_names_the_control_that_owns_its_region()
    {
        const string json = """
            {
              "class": "Form",
              "name": "frmMain",
              "children": [
                {
                  "class": "Button",
                  "caption": "Save",
                  "margin": { "l": 4 },
                  "name": "btnSave",
                  "events": { "onClick": "btnSave_Click" }
                },
                { "class": "Panel", "name": "pnlBody", "children": [ { "class": "Label", "name": "lblTitle", "caption": "Save" } ] }
              ]
            }
            """;
        string Owner(string marker) => FormRegions.OwnerOf(json, json.IndexOf(marker, StringComparison.Ordinal));
        Assert.Equal("btnSave", Owner("\"Save\""));          // the caption, before the name appears in the object
        Assert.Equal("btnSave", Owner("\"l\""));             // inside a nested object without a name
        Assert.Equal("btnSave", Owner("btnSave_Click"));     // an event entry
        Assert.Equal("lblTitle", Owner("lblTitle"));
        Assert.Equal("pnlBody", Owner("\"Panel\""));
        Assert.Equal("frmMain", Owner("\"children\""));      // the form's own region

        var re = O("Save", matchCase: true).ToRegex(out _)!;
        var hits = FindInFiles.FindInText(re, "Main.radform.json", json);
        Assert.Equal(["btnSave", "btnSave", "btnSave", "lblTitle"], hits.Select(h => h.Owner ?? "").ToArray());   // the caption, the name, the handler name, the label caption
    }

    // ---- Navigation history ----

    static NavLocation Cs(string file, int line) => NavLocation.InCode("/p/" + file, line, 1);

    [Fact]
    public void History_records_places_and_updates_the_current_one_on_small_moves()
    {
        var h = new NavigationHistory();
        h.Capture(Cs("a.cs", 1));
        h.Capture(Cs("a.cs", 8));                 // 7 lines: the same place
        Assert.Single(h.Entries);
        Assert.Equal(8, h.Current!.Line);
        h.Capture(Cs("a.cs", 40));                // far: a new place
        h.Capture(Cs("b.cs", 1));                 // another file: a new place
        h.Capture(NavLocation.InForm("/p/F.radform.json", "btnOk"));
        h.Capture(NavLocation.InForm("/p/F.radform.json", "btnOk"));   // the same selection
        h.Capture(NavLocation.InForm("/p/F.radform.json", "edtName"));
        Assert.Equal(5, h.Entries.Count);

        Assert.Equal("btnOk", h.Back()!.Control);
        Assert.Equal("b.cs", Path.GetFileName(h.Back()!.Path));
        Assert.Equal(40, h.Back()!.Line);
        Assert.Equal(8, h.Back()!.Line);
        Assert.Null(h.Back());                    // at the start
        Assert.False(h.CanGoBack);
        Assert.Equal(40, h.Forward()!.Line);
        Assert.True(h.CanGoForward);
    }

    [Fact]
    public void History_drops_forward_entries_on_a_new_place_and_is_bounded()
    {
        var h = new NavigationHistory(capacity: 5);
        for (int i = 1; i <= 8; i++) h.Capture(Cs($"f{i}.cs", 1));
        Assert.Equal(5, h.Entries.Count);
        Assert.Equal("f4.cs", Path.GetFileName(h.Entries[0].Path));   // the oldest went

        h.Back(); h.Back();
        h.Capture(Cs("new.cs", 1));
        Assert.False(h.CanGoForward);
        Assert.Equal(4, h.Entries.Count);
        Assert.Equal("new.cs", Path.GetFileName(h.Current!.Path));
    }

    [Fact]
    public void A_jump_is_one_entry_however_many_events_it_raises_and_navigating_is_not_recorded()
    {
        var h = new NavigationHistory();
        h.Capture(Cs("a.cs", 1));
        h.BeginJump();
        h.Capture(Cs("b.cs", 1));       // the document activated...
        h.Capture(Cs("b.cs", 200));     // ...and the caret moved
        h.EndJump();
        Assert.Equal(2, h.Entries.Count);
        Assert.Equal(200, h.Current!.Line);

        h.BeginJump();
        h.Capture(Cs("b.cs", 205));     // a jump within 10 lines is still its own place
        h.EndJump();
        Assert.Equal(3, h.Entries.Count);

        h.Navigating = true;
        h.Capture(Cs("zzz.cs", 1));
        h.Navigating = false;
        Assert.Equal(3, h.Entries.Count);
    }

    [Fact]
    public void Going_back_skips_and_forgets_places_that_no_longer_exist()
    {
        var h = new NavigationHistory();
        h.Capture(Cs("a.cs", 1)); h.Capture(Cs("gone.cs", 1)); h.Capture(Cs("gone2.cs", 1)); h.Capture(Cs("d.cs", 1));
        bool Exists(NavLocation l) => !Path.GetFileName(l.Path).StartsWith("gone");
        Assert.Equal("a.cs", Path.GetFileName(h.Back(Exists)!.Path));
        Assert.Equal(2, h.Entries.Count);
        Assert.Equal("d.cs", Path.GetFileName(h.Forward(Exists)!.Path));
    }
}
