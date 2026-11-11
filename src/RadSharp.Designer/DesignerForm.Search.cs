using RadSharp.CodeEditing;
using RadSharp.Design;
using RadSharp.Design.Code;

namespace RadSharp.Designer;

/// <summary>
/// Find in Files and Replace in Files (milestone I5): the <see cref="FindPanel"/> at the top of the Find Results window, a background search over
/// the open projects' files (open buffers and unsaved forms included, <c>bin</c>/<c>obj</c> skipped), results grouped by file with line previews,
/// and a replace that goes through the editor for open buffers (one undo step each) and to disk for the rest.
/// </summary>
/// <remarks>
/// A hit in a <c>.radform.json</c> opens the designer with the control that owns that region of the JSON selected
/// (<see cref="FormRegions"/>); a hit in code opens the file with the match selected.
/// </remarks>
public partial class DesignerForm
{
    FindPanel _findPanel = null!;
    CancellationTokenSource? _findCts;
    int _findSequence, _resultsSequence;
    readonly List<string> _replaceSkipped = [];
    IReadOnlyList<FileHits> _findInFilesResults = [];
    Action?[]? _findRowActions;

    /// <summary>The last Find in Files results, grouped by file.</summary>
    public IReadOnlyList<FileHits> FindInFilesResults => _findInFilesResults;

    /// <summary>The panel above the Find Results list.</summary>
    public FindPanel FindInFilesPanel => _findPanel;

    /// <summary>The confirmation text of the last Replace in Files (tests read it).</summary>
    public string LastReplaceConfirmation { get; private set; } = "";

    void InitSearch()
    {
        _findPanel = new FindPanel();
        pnlFindResults.Insert(0, _findPanel);
        _findPanel.FindRequested += o => _ = FindInFilesAsync(o);
        _findPanel.ReplaceRequested += (o, r) => _ = ReplaceInFilesAsync(o, r);
        _findPanel.StopRequested += () => _findCts?.Cancel();
    }

    /// <summary>Ctrl+Shift+F / Ctrl+Shift+H: shows the Find Results window with the panel focused (the selected text of the editor goes into the box).</summary>
    public void ShowFindInFiles(bool replace)
    {
        _dock.Show(FindToolWindow(ToolWindows.FindResults));
        if (_docs.Active is CodeDocument { Editor: CodeEditorAdapter a } && a.Editor.SelectedText is { Length: > 0 } selected && !selected.Contains('\n'))
            _findPanel.FindBox.Text = selected;
        (replace ? _findPanel.ReplaceBox : _findPanel.FindBox).Focus();
        if (!replace) _findPanel.FindBox.SelectAll();
    }

    // ---- Search ----

    /// <summary>The text a search sees for a file: an open buffer's text (unsaved edits included), an unsaved form's JSON, else null (read from disk).</summary>
    string? OpenText(string file) => FindDocument(file) switch
    {
        CodeDocument code => code.Text,
        FormDocument { IsDirty: true } form => form.Document.Save(),
        _ => null,
    };

    /// <summary>
    /// Searches the open projects' files on a background thread and lists the hits in Find Results. A new search cancels the running one.
    /// The task completes after the results are shown (or the search was cancelled or superseded).
    /// </summary>
    public Task FindInFilesAsync(FindOptions options)
    {
        _findCts?.Cancel();
        var cts = _findCts = new CancellationTokenSource();
        int sequence = ++_findSequence;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (options.ToRegex(out var error) == null)
        {
            _findPanel.Status = error ?? "";
            sbMain.SimpleText = error ?? "";
            done.SetResult();
            return done.Task;
        }
        var roots = Projects.Select(p => p.Directory).ToList();
        // Open buffers are read here, on the UI thread; the search itself only sees the snapshot.
        var open = _docs.Documents.Select(d => d.Path).OfType<string>()
            .Select(p => (Path: p, Text: OpenText(p))).Where(t => t.Text != null)
            .ToDictionary(t => t.Path, t => t.Text!, StringComparer.OrdinalIgnoreCase);

        _findPanel.Busy = true;
        _findPanel.Status = "Searching…";
        sbMain.SimpleText = $"Searching for \"{options.Text}\"…";
        var token = cts.Token;
        _ = Task.Run(() =>
        {
            List<FileHits>? hits = null;
            int files = 0;
            try
            {
                hits = FindInFiles.Search(options, FindInFiles.EnumerateFiles(roots, options.FileFilter, token).Select(f => { files++; return f; }),
                    f => open.GetValueOrDefault(f), token);
            }
            catch (OperationCanceledException) { }
            Post(() =>
            {
                try
                {
                    if (hits == null || token.IsCancellationRequested || sequence != _findSequence)
                    {
                        if (sequence == _findSequence) { _findPanel.Busy = false; _findPanel.Status = "Search stopped"; }
                        return;
                    }
                    _findPanel.Busy = false;
                    _resultsSequence = sequence;
                    ShowFindInFilesResults(options, hits, files);
                }
                finally { done.TrySetResult(); }
            });
        });
        return done.Task;
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread (inline when there is no platform host).</summary>
    static void Post(Action action)
    {
        if (PlatformHost.Current is { } host) host.PostToUiThread(action);
        else action();
    }

    void ShowFindInFilesResults(FindOptions options, IReadOnlyList<FileHits> hits, int filesSearched)
    {
        _findInFilesResults = hits;
        _findResults = [];   // not Find All References' list any more
        var items = new List<string>();
        var actions = new List<Action?>();
        foreach (var file in hits)
        {
            var first = file.Hits[0];
            items.Add($"{DisplayPath(file.File)}  ({file.Hits.Count})");
            actions.Add(() => OpenFindHit(first));
            foreach (var hit in file.Hits)
            {
                var h = hit;
                items.Add($"      {hit.Line,5}:  {hit.LineText}" + (hit.Owner is { Length: > 0 } owner ? $"   [{owner}]" : ""));
                actions.Add(() => OpenFindHit(h));
            }
        }
        _findRowActions = [.. actions];
        lstFindResults.Items = [.. items];
        lstFindResults.ItemIndex = -1;
        lstFindResults.Invalidate();
        int total = hits.Sum(f => f.Hits.Count);
        var summary = total == 0
            ? $"No results for \"{options.Text}\" ({filesSearched} files searched)"
            : $"{total} match(es) in {hits.Count} file(s)  ({filesSearched} files searched)";
        _findPanel.Status = total == 0 ? "No results" : $"{total} in {hits.Count} file(s)";
        sbMain.SimpleText = summary;
        _dock.Show(FindToolWindow(ToolWindows.FindResults));
    }

    string DisplayPath(string file)
    {
        var project = ProjectOf(file);
        return project != null ? Path.GetRelativePath(project.Directory, file) : file;
    }

    /// <summary>Shows one hit: a form file opens the designer with the owning control selected, a code file opens at the match.</summary>
    public void OpenFindHit(FindHit hit)
    {
        using var _ = JumpScope();
        if (hit.File.EndsWith(".radform.json", StringComparison.OrdinalIgnoreCase) && File.Exists(hit.File)
            && OpenFormFile(hit.File) is { } form)
        {
            var root = form.Document.Form;
            var control = hit.Owner is { Length: > 0 } name ? root.FindControl(name) : null;
            form.Document.Select(control == null || control == root ? null : control);
            form.Focus();
            sbMain.SimpleText = control is { } c && c != root ? $"{c.Name}: {ComponentRegistry.Of(c).Name} on {form.ClassName}" : form.ClassName;
            return;
        }
        if (!NavigateToCode(hit.File, hit.Line, hit.Column)) return;
        if (FindDocument(hit.File) is CodeDocument { Editor: CodeEditorAdapter a })
        {
            var snapshot = a.Editor.Snapshot;
            if (hit.Line - 1 < snapshot.LineCount)
            {
                int start = Math.Min(snapshot.Length, snapshot.GetLineStart(hit.Line - 1) + hit.Column - 1);
                a.Editor.Select(start, Math.Min(hit.Length, snapshot.Length - start));
            }
        }
    }

    // ---- Replace ----

    /// <summary>
    /// Replace in Files: finds the matches, asks for confirmation (listing the counts), then changes open buffers through the editor (one undo
    /// step per buffer, left unsaved) and writes the other files to disk. The task completes when it is done or was declined.
    /// </summary>
    public Task ReplaceInFilesAsync(FindOptions options, string replacement)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (options.ToRegex(out var error) is not { } regex)
        {
            _findPanel.Status = error ?? "";
            done.SetResult();
            return done.Task;
        }
        // Not awaited: the continuation must run on the UI thread (it shows a dialog and edits documents).
        var search = FindInFilesAsync(options);
        int sequence = _findSequence;   // FindInFilesAsync took its number synchronously
        _ = search.ContinueWith(_ => Post(() =>
        {
            bool searching = false;
            try
            {
                if (_resultsSequence != sequence)
                {
                    // The search was stopped, or a newer one replaced it: its results are not the ones to replace in.
                    var message = "Replace cancelled: the search it needed was stopped or replaced by another one.";
                    _findPanel.Status = message;
                    sbMain.SimpleText = message;
                    Output(message);
                    return;
                }
                searching = ReplaceFound(options, regex, replacement, () => done.TrySetResult());
            }
            finally { if (!searching) done.TrySetResult(); }
        }), TaskScheduler.Default);
        return done.Task;
    }

    /// <summary>Confirms and applies the replacement to the current results; true if it started a refreshing search that will call <paramref name="finished"/>.</summary>
    bool ReplaceFound(FindOptions options, System.Text.RegularExpressions.Regex regex, string replacement, Action finished)
    {
        var hits = _findInFilesResults;
        if (hits.Count == 0) return false;

        int total = hits.Sum(f => f.Hits.Count);
        var openFiles = hits.Where(f => FindDocument(f.File) != null).ToList();
        var diskFiles = hits.Except(openFiles).ToList();
        LastReplaceConfirmation =
            $"Replace {total} occurrence(s) of \"{options.Text}\" with \"{replacement}\" in {hits.Count} file(s)?\n\n" +
            $"{openFiles.Count} open file(s) change in the editor (one undo step each, not saved).\n" +
            $"{diskFiles.Count} other file(s) are written to disk and can't be undone here.";
        if (Dialogs.MessageDlg(LastReplaceConfirmation, MessageType.Confirmation, MessageButtons.YesNo, this) != ModalResult.Yes) return false;

        int changed = 0, replaced = 0;
        _replaceSkipped.Clear();
        foreach (var file in hits)
        {
            int n = ReplaceInFile(file.File, regex, replacement, literal: !options.UseRegex);
            if (n > 0) { changed++; replaced += n; }
        }
        RefreshGit();
        UpdateTitle();
        var summary = $"Replaced {replaced} occurrence(s) in {changed} file(s)"
            + (_replaceSkipped.Count > 0 ? $"; skipped {_replaceSkipped.Count} file(s) (see Output)" : "");
        Output(summary);
        // The list described the old text; show what the search finds now (normally nothing).
        _ = FindInFilesAsync(options).ContinueWith(_ => Post(() =>
        {
            _findPanel.Status = summary;
            sbMain.SimpleText = summary;
            finished();
        }), TaskScheduler.Default);
        return true;
    }

    /// <summary>Replaces in one file, in the editor if it is open (a code buffer: one undo step; a form: an undoable change) and on disk otherwise. The number of matches changed.</summary>
    int ReplaceInFile(string file, System.Text.RegularExpressions.Regex regex, string replacement, bool literal)
    {
        try { return ReplaceInFileCore(file, regex, replacement, literal); }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            Skip(file, "the pattern took too long to match");
            return 0;
        }
    }

    void Skip(string file, string why)
    {
        _replaceSkipped.Add(file);
        Output($"Skipped {file}: {why}.");
    }

    int ReplaceInFileCore(string file, System.Text.RegularExpressions.Regex regex, string replacement, bool literal)
    {
        switch (FindDocument(file))
        {
            case CodeDocument code:
            {
                if (code.ReadOnly) { Skip(file, "it is open read-only"); return 0; }
                var text = code.Text;
                int n = FindInFiles.CountStrict(regex, text);
                if (n > 0) code.ApplyText(FindInFiles.ReplaceInText(regex, text, replacement, literal));
                return n;
            }
            case FormDocument form:
            {
                var text = OpenText(file) ?? FindInFiles.ReadText(file) ?? "";
                int n = FindInFiles.CountStrict(regex, text);
                if (n == 0) return 0;
                var updated = FindInFiles.ReplaceInText(regex, text, replacement, literal);
                try { form.Document.ReplaceWithJson(updated); }   // one undo step, left unsaved
                catch (FormFileException e)
                {
                    Output($"{Path.GetFileName(file)}: the replacement would make the form file invalid ({e.Message}); left as it was.");
                    return 0;
                }
                return n;
            }
            default:
            {
                var content = FindInFiles.ReadContent(file);
                if (content == null) return 0;
                if (content.IsLossy) { Skip(file, "it is not valid UTF-8 and has no byte order mark, so rewriting it could corrupt it"); return 0; }
                var text = content.Text;
                int n = FindInFiles.CountStrict(regex, text);
                if (n == 0) return 0;
                try { content.Write(file, FindInFiles.ReplaceInText(regex, text, replacement, literal)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Output($"Could not write {file}: {e.Message}");
                    return 0;
                }
                try { LanguageHostFor(file)?.Workspace?.ReloadFromDisk(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                return n;
            }
        }
    }
}
