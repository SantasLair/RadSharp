using RadSharp.CodeEditing;
using RadSharp.Ide.Language;
using EditorTextEdit = RadSharp.CodeEditing.TextEdit;
using LsTextEdit = RadSharp.Ide.Language.TextEdit;

namespace RadSharp.Designer;

/// <summary>
/// One C# code document's language features, plugged into its <see cref="CodeEditor"/> through the editor's hooks:
/// colours (<see cref="ITokenSource"/>), squiggles and gutter glyphs (<see cref="IDecorationSource"/>), completion
/// (<see cref="ICompletionSource"/>), quick info on hover and signature help on <c>(</c> and <c>,</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Versions.</b> Every push of the buffer to the workspace is remembered as (workspace version, editor snapshot).
/// A result tagged with workspace version V was computed from the text of the latest push at or before V, so its
/// offsets belong to that snapshot; the editor maps them forward through the edits since (or the result is dropped
/// when no push matches).
/// </para>
/// <para>
/// <b>Colours.</b> Before the workspace is ready, and on lines edited since the last result, the editor's lexical
/// colouriser paints; elsewhere the newest semantic result does (the syntactic pass until the first semantic one).
/// Roslyn's classification names are the ones <see cref="CodeEditorColors.Tokens"/> uses, so they map directly.
/// </para>
/// </remarks>
public sealed class DocumentLanguage : ITokenSource, IDecorationSource, ICompletionSource, IDisposable
{
    readonly LanguageHost _host;
    readonly CodeEditor _editor;
    readonly CSharpTokenSource _lexical = new();
    readonly List<(long Version, TextSnapshot Snapshot)> _pushes = [];
    TextSnapshot? _pushed;
    CancellationTokenSource? _pushTimer, _classifyTimer, _classify, _hover, _signature;
    bool _disposed, _signaturePending;

    /// <summary>A classification result against the snapshot it was computed from (ranges sorted, not overlapping).</summary>
    sealed record Colours(TextSnapshot Snapshot, (int Start, int End, string Kind)[] Ranges);
    Colours? _syntax, _semantic;
    // The newest result's ranges mapped to a later snapshot, and the lines edited since (painted lexically).
    (Colours Source, TextSnapshot Snapshot, (int Start, int End, string Kind)[] Ranges, List<(int Start, int End)> Dirty, HashSet<int>? Lines)? _mapped;

    DecorationSet? _decorations;
    (TextSnapshot Snapshot, TextRange Span)? _signatureSpan;

    internal DocumentLanguage(LanguageHost host, CodeDocument document, CodeEditor editor)
    {
        _host = host;
        Document = document;
        _editor = editor;
        Path = document.Path;
        editor.TokenSource = this;
        editor.DecorationSources.Add(this);
        editor.CompletionSource = this;
        editor.TextChanged += OnTextChanged;
        editor.CaretMoved += OnCaretMoved;
        editor.Hover += OnHover;
        document.Moved += OnMoved;
    }

    public CodeDocument Document { get; }
    public CodeEditor Editor => _editor;
    public string Path { get; private set; }

    /// <summary>True once a semantic colour result arrived (tests wait for it).</summary>
    public bool HasSemanticColours => _semantic != null;

    /// <summary>The squiggles shown now (against <see cref="DecorationSet.Snapshot"/>).</summary>
    public DecorationSet? Decorations => _decorations;

    LanguageService? Service => _host.Service;

    internal void OnHostReady()
    {
        if (_disposed) return;
        Flush();
        RequestClassification(semantic: false);
        RequestClassification(semantic: true);
        if (_host.Diagnostics?.Latest is { } latest) OnDiagnostics(latest);
    }

    // ---- Pushing the buffer ----

    /// <summary>Pushes the buffer to the workspace if it changed since the last push; returns the snapshot the workspace has.</summary>
    internal TextSnapshot? Flush()
    {
        if (_disposed || _host.Workspace is not { } workspace) return null;
        var snapshot = _editor.Snapshot;
        if (snapshot == _pushed) return snapshot;
        long version = workspace.UpdateDocumentText(Path, snapshot.GetText());
        _pushed = snapshot;
        _pushes.Add((version, snapshot));
        if (_pushes.Count > 64) _pushes.RemoveRange(0, _pushes.Count - 64);
        return snapshot;
    }

    /// <summary>Pushes every pending buffer and form of the project (a request must see the designer's latest form), then returns this buffer's pushed snapshot.</summary>
    TextSnapshot? FlushAll()
    {
        _host.Flush();
        return Flush();
    }

    /// <summary>The editor snapshot whose text the workspace had at <paramref name="version"/>, or null if unknown.</summary>
    internal TextSnapshot? SnapshotAt(long version)
    {
        for (int i = _pushes.Count - 1; i >= 0; i--)
            if (_pushes[i].Version <= version) return _pushes[i].Snapshot;
        return null;
    }

    void OnTextChanged(Control sender, TextChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.Reason == TextChangeReason.Edit && e.Changes.Count == 1 && e.Changes[0].OldText.Length == 0
            && e.Changes[0].NewText is "(" or "()" or ",")
            _signaturePending = true;
        if (!_host.IsReady) return;
        _host.Debounce(ref _pushTimer, LanguageHost.PushDelay, () => { Flush(); ScheduleClassify(); });
    }

    void OnMoved(string oldPath)
    {
        if (_host.Workspace is { } workspace) workspace.CloseDocument(oldPath);
        Path = Document.Path;
        _pushed = null;
        _pushes.Clear();
        if (_host.IsReady) OnHostReady();
    }

    // ---- Colours ----

    internal void ScheduleClassify()
    {
        if (_disposed) return;
        _host.Debounce(ref _classifyTimer, LanguageHost.ClassifyDelay, () => RequestClassification(semantic: true));
    }

    void RequestClassification(bool semantic)
    {
        if (Flush() is not { } snapshot) return;
        if (semantic) { _classify?.Cancel(); _classify = new CancellationTokenSource(); }
        var cancel = semantic ? _classify!.Token : CancellationToken.None;
        var path = Path;
        _host.Request(
            (service, token) => semantic ? service.ClassifyAsync(path, cancel: token) : service.ClassifySyntaxAsync(path, cancel: token),
            result => OnClassified(result, semantic), cancel);
    }

    void OnClassified(Versioned<IReadOnlyList<ClassifiedRange>> result, bool semantic)
    {
        if (SnapshotAt(result.Version) is not { } snapshot) return;
        var existing = semantic ? _semantic : _syntax;
        if (existing != null && existing.Snapshot.Version > snapshot.Version) return;   // stale
        var ranges = new List<(int, int, string)>(result.Value.Count);
        int last = 0;
        foreach (var r in result.Value.OrderBy(r => r.Start))
        {
            int start = Math.Max(r.Start, last), end = r.Start + r.Length;
            if (end <= start) continue;
            ranges.Add((start, end, r.Kind));
            last = end;
        }
        var colours = new Colours(snapshot, [.. ranges]);
        if (semantic) _semantic = colours; else _syntax = colours;
        _mapped = null;
        Changed?.Invoke(this, new SourceChangedEventArgs(snapshot));
    }

    public event EventHandler<SourceChangedEventArgs>? Changed;

    public IReadOnlyList<TokenSpan>? GetTokens(TextSnapshot snapshot, int line)
    {
        var colours = _semantic ?? _syntax;
        if (colours == null || colours.Snapshot.Buffer != snapshot.Buffer || colours.Snapshot.Version > snapshot.Version)
            return _lexical.GetTokens(snapshot, line);
        var mapped = Map(colours, snapshot);
        if (mapped.Lines != null && mapped.Lines.Contains(line)) return _lexical.GetTokens(snapshot, line);

        int lineStart = snapshot.GetLineStart(line), lineEnd = snapshot.GetLineEnd(line);
        var ranges = mapped.Ranges;
        int lo = 0, hi = ranges.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (ranges[mid].End <= lineStart) lo = mid + 1; else hi = mid;
        }
        var spans = new List<TokenSpan>();
        for (int i = lo; i < ranges.Length && ranges[i].Start < lineEnd; i++)
        {
            int s = Math.Max(ranges[i].Start, lineStart), e = Math.Min(ranges[i].End, lineEnd);
            if (e > s) spans.Add(new TokenSpan(s - lineStart, e - s, ranges[i].Kind));
        }
        return spans;
    }

    /// <summary>The result's ranges in <paramref name="current"/>'s coordinates, walked forward from the last mapping (one step per edit).</summary>
    (Colours Source, TextSnapshot Snapshot, (int Start, int End, string Kind)[] Ranges, List<(int Start, int End)> Dirty, HashSet<int>? Lines) Map(Colours colours, TextSnapshot current)
    {
        if (_mapped is { } m && m.Source == colours && m.Snapshot == current) return m;
        TextSnapshot from;
        (int Start, int End, string Kind)[] ranges;
        List<(int Start, int End)> dirty;
        if (_mapped is { } prev && prev.Source == colours && prev.Snapshot.Version <= current.Version)
            (from, ranges, dirty) = (prev.Snapshot, prev.Ranges, prev.Dirty);
        else
            (from, ranges, dirty) = (colours.Snapshot, colours.Ranges, []);

        for (var s = from; s != current && s.Next != null && s.ChangesToNext is { } changes; s = s.Next)
        {
            var next = new List<(int, int, string)>(ranges.Length);
            foreach (var r in ranges)
            {
                int a = TextSnapshot.TranslateThrough(changes, r.Start, PointTrackingMode.Positive);
                int b = TextSnapshot.TranslateThrough(changes, r.End, PointTrackingMode.Negative);
                if (b > a) next.Add((a, b, r.Kind));
            }
            ranges = [.. next];
            dirty = [.. dirty.Select(d => (TextSnapshot.TranslateThrough(changes, d.Start, PointTrackingMode.Negative),
                TextSnapshot.TranslateThrough(changes, d.End, PointTrackingMode.Positive)))];
            foreach (var c in changes) dirty.Add((c.NewStart, c.NewEnd));
        }
        HashSet<int>? lines = null;
        if (dirty.Count > 0)
        {
            lines = [];
            foreach (var (start, end) in dirty)
            {
                int l0 = current.GetLineFromOffset(start), l1 = current.GetLineFromOffset(end);
                for (int l = l0; l <= l1; l++) lines.Add(l);
            }
        }
        var result = (colours, current, ranges, dirty, lines);
        _mapped = result;
        return result;
    }

    // ---- Squiggles ----

    internal void OnDiagnostics(Versioned<IReadOnlyList<DiagnosticInfo>> update)
    {
        if (_disposed || SnapshotAt(update.Version) is not { } snapshot) return;
        var items = new List<Decoration>();
        var glyphs = new Dictionary<int, (Decoration D, int Rank)>();
        foreach (var d in update.Value)
        {
            if (d.IsInGeneratedCode || !LanguageHost.SamePath(d.File, Path)) continue;
            var (kind, glyph, rank) = d.Severity switch
            {
                "error" => (DecorationKind.ErrorSquiggle, GutterGlyph.Error, 3),
                "warning" => (DecorationKind.WarningSquiggle, GutterGlyph.Warning, 2),
                _ => (DecorationKind.InfoSquiggle, GutterGlyph.Info, 1),
            };
            int start = Math.Clamp(d.Start, 0, snapshot.Length);
            int length = Math.Clamp(Math.Max(1, d.Length), 0, snapshot.Length - start);
            items.Add(new Decoration(start, length, kind, $"{d.Id}: {d.Message}"));
            int line = snapshot.GetLineFromOffset(start);
            if (!glyphs.TryGetValue(line, out var g) || g.Rank < rank)
                glyphs[line] = (new Decoration(snapshot.GetLineStart(line), 0, DecorationKind.GutterGlyph, Glyph: glyph), rank);
        }
        // The form's generator messages (RAD1001: a handler the form binds is missing) belong to this code too: they
        // squiggle the class name, since that's where the missing member would go.
        if (Document.FormFile is { } formFile && ClassNameSpan(snapshot, RadSharp.Design.Code.RadProject.FormClassName(formFile)) is { } cls)
            foreach (var d in update.Value.Where(d => LanguageHost.SamePath(d.File, formFile) && d.Id.StartsWith("RAD", StringComparison.Ordinal)))
            {
                var kind = d.IsError ? DecorationKind.ErrorSquiggle : d.Severity == "warning" ? DecorationKind.WarningSquiggle : DecorationKind.InfoSquiggle;
                items.Add(new Decoration(cls.Start, cls.Length, kind, $"{d.Id}: {d.Message} ({System.IO.Path.GetFileName(formFile)}, line {d.Line})"));
                int line = snapshot.GetLineFromOffset(cls.Start);
                int rank = d.IsError ? 3 : 2;
                if (!glyphs.TryGetValue(line, out var g) || g.Rank < rank)
                    glyphs[line] = (new Decoration(snapshot.GetLineStart(line), 0, DecorationKind.GutterGlyph, Glyph: d.IsError ? GutterGlyph.Error : GutterGlyph.Warning), rank);
            }
        items.AddRange(glyphs.Values.Select(g => g.D));
        _decorations = new DecorationSet(snapshot, [.. items.OrderBy(i => i.Start)]);
        Changed?.Invoke(this, new SourceChangedEventArgs(snapshot));
    }

    public DecorationSet? GetDecorations(TextSnapshot current) => _decorations;

    /// <summary>Where <c>class <paramref name="name"/></c> names the class in the text.</summary>
    static (int Start, int Length)? ClassNameSpan(TextSnapshot snapshot, string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(snapshot.GetText(), @"\bclass\s+(" + System.Text.RegularExpressions.Regex.Escape(name) + @")\b");
        return match.Success ? (match.Groups[1].Index, match.Groups[1].Length) : null;
    }

    /// <summary>The squiggle messages at an offset of the current text.</summary>
    string? DiagnosticAt(int offset)
    {
        if (_decorations is not { } set) return null;
        var current = _editor.Snapshot;
        var messages = new List<string>();
        foreach (var d in set.Items)
        {
            if (d.Kind is DecorationKind.GutterGlyph or DecorationKind.Highlight || d.Message == null) continue;
            int s = set.Snapshot.TranslateOffset(d.Start, current, PointTrackingMode.Negative);
            int e = set.Snapshot.TranslateOffset(d.End, current, PointTrackingMode.Positive);
            if (offset >= s && offset <= Math.Max(e, s + 1)) messages.Add(d.Message);
        }
        return messages.Count == 0 ? null : string.Join("\n", messages.Distinct());
    }

    // ---- Completion ----

    public IReadOnlyCollection<char> TriggerCharacters { get; } = ['.'];

    sealed record CompletionTag(CompletionResult Result, CompletionEntry Entry, TextSnapshot Snapshot);

    public Task<CompletionList?> GetCompletionsAsync(CompletionRequest request, CancellationToken cancellationToken)
    {
        if (Service is not { } service || FlushAll() is not { } snapshot || snapshot != request.Snapshot)
            return Task.FromResult<CompletionList?>(null);
        char? typed = request.Trigger == CompletionTriggerKind.Invoked ? null : request.TriggerCharacter;
        var path = Path;
        return Task.Run(async () =>
        {
            Versioned<CompletionResult>? result;
            try { result = await service.GetCompletionsAsync(path, request.Offset, typed, cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (LanguageHost.IsExpected(e)) { return null; }
            if (result == null) return null;
            var list = result.Value;
            IReadOnlyList<CompletionItem> items =
            [
                .. list.Items.Select(e => new CompletionItem(e.DisplayText, e.FilterText, e.Kind,
                    e.InlineDescription.Length > 0 ? e.InlineDescription : null, e.FilterText, e.SortText) { Tag = new CompletionTag(list, e, snapshot) }),
            ];
            return (CompletionList?)new CompletionList(list.Span.Start, list.Span.Length, items);
        }, cancellationToken);
    }

    // The commit details of the selected item are resolved in the background as soon as it is selected (ItemSelected), so
    // committing finds them ready. If they aren't (Enter right after the list opened), the commit waits a moment at most.
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<CompletionEntry, Task<CompletionDetail>> _details = new();

    /// <summary>How long a commit waits for details that aren't resolved yet before it inserts the plain text.</summary>
    public static TimeSpan CommitWait { get; set; } = TimeSpan.FromMilliseconds(150);

    Task<CompletionDetail>? DetailFor(CompletionItem item)
    {
        if (item.Tag is not CompletionTag tag || Service is not { } service) return null;
        return _details.GetValue(tag.Entry, _ => Task.Run(() => service.ResolveCompletionAsync(tag.Result, tag.Entry)));
    }

    /// <summary>The list's selection moved to <paramref name="item"/>: starts resolving what committing it needs.</summary>
    public void ItemSelected(CompletionList list, CompletionItem item) => DetailFor(item);

    /// <summary>Commits through Roslyn: the item's edits (and any <c>using</c> it adds) and the caret it asks for.</summary>
    public CompletionCommit? GetCommit(TextSnapshot snapshot, CompletionList list, CompletionItem item)
    {
        if (item.Tag is not CompletionTag tag || DetailFor(item) is not { } task) return null;
        CompletionDetail detail;
        try
        {
            // Ready: use it. Not ready: a short wait, then the plain insert text (the UI thread never stalls for seconds).
            if (!task.IsCompleted && !task.Wait(CommitWait)) return null;
            detail = task.Result;
        }
        catch (Exception e) when (LanguageHost.IsExpected(e)) { return null; }
        if (detail.Edits.Count == 0) return null;

        var from = tag.Snapshot;
        int spanStart = tag.Result.Span.Start, caret = _editor.CaretOffset;
        var edits = detail.Edits.OrderBy(e => e.Start).ToList();
        var mapped = new List<EditorTextEdit>(edits.Count);
        foreach (var e in edits)
        {
            int s = from.TranslateOffset(e.Start, snapshot, PointTrackingMode.Negative);
            int end = from.TranslateOffset(e.Start + e.Length, snapshot, PointTrackingMode.Positive);
            // The characters typed since the list opened are part of the word the item replaces.
            if (e.Start <= spanStart && e.Start + e.Length >= spanStart) end = Math.Max(end, caret);
            mapped.Add(new EditorTextEdit(s, Math.Max(0, end - s), e.NewText));
        }
        int? newCaret = detail.CaretPosition is { } p ? MapAfterEdits(p, from, snapshot, edits, mapped) : null;
        return new CompletionCommit(mapped, newCaret);
    }

    /// <summary>Maps an offset in "<paramref name="from"/> with <paramref name="edits"/> applied" to the text after <paramref name="mapped"/> is applied to <paramref name="current"/>.</summary>
    static int MapAfterEdits(int position, TextSnapshot from, TextSnapshot current, List<LsTextEdit> edits, List<EditorTextEdit> mapped)
    {
        int deltaOld = 0, deltaNew = 0;
        for (int i = 0; i < edits.Count; i++)
        {
            int newStart = edits[i].Start + deltaOld;
            if (position < newStart) break;
            if (position <= newStart + edits[i].NewText.Length) return mapped[i].Start + deltaNew + (position - newStart);
            deltaOld += edits[i].NewText.Length - edits[i].Length;
            deltaNew += mapped[i].NewText.Length - mapped[i].Length;
        }
        return from.TranslateOffset(Math.Clamp(position - deltaOld, 0, from.Length), current) + deltaNew;
    }

    // ---- Hover: quick info, plus the squiggle's message ----

    void OnHover(Control sender, CodeEditorHoverEventArgs e)
    {
        if (Service == null || _host.QuickInfoSuppressed?.Invoke() == true || FlushAll() is not { } snapshot) return;
        _hover?.Cancel();
        _hover = new CancellationTokenSource();
        int offset = e.Offset;
        var path = Path;
        _host.Request((service, token) => service.GetQuickInfoAsync(path, offset, token), result =>
        {
            if (_editor.Snapshot != snapshot) return;
            var diagnostic = DiagnosticAt(offset);
            var info = result.Value;
            if (info == null || info.Text.Length == 0) return;   // the editor already shows a squiggle's own message
            var text = diagnostic == null ? info.Text : info.Text + "\n\n" + diagnostic;
            _editor.ShowHoverHint(offset, info.Span.Start, text);
        }, _hover.Token);
    }

    // ---- Signature help ----

    void OnCaretMoved(Control sender)
    {
        if (_disposed) return;
        if (_signaturePending) { _signaturePending = false; ShowSignatureHelp(); return; }
        if (_signatureSpan is { } sig)
        {
            var current = _editor.Snapshot;
            int start = sig.Snapshot.TranslateOffset(sig.Span.Start, current, PointTrackingMode.Negative);
            int end = sig.Snapshot.TranslateOffset(sig.Span.End, current, PointTrackingMode.Positive);
            int caret = _editor.CaretOffset;
            if (caret <= start || caret > end) { _signatureSpan = null; _editor.HideHint(); }
        }
    }

    /// <summary>Shows the overloads of the call around the caret (on <c>(</c> and <c>,</c>, or Ctrl+Shift+Space).</summary>
    public Task ShowSignatureHelp()
    {
        if (Service == null || FlushAll() is not { } snapshot) return Task.CompletedTask;
        _signature?.Cancel();
        _signature = new CancellationTokenSource();
        int caret = _editor.CaretOffset;
        var path = Path;
        return _host.Request((service, token) => service.GetSignatureHelpAsync(path, caret, token), result =>
        {
            if (result.Value is not { } help || help.Signatures.Count == 0 || SnapshotAt(result.Version) is not { } at) return;
            _signatureSpan = (at, help.ApplicableSpan);
            _editor.ShowHint(at.TranslateOffset(help.ApplicableSpan.Start, _editor.Snapshot, PointTrackingMode.Negative), SignatureText(help), above: true);
        }, _signature.Token);
    }

    /// <summary>The active overload with its current parameter in brackets, the overload count, and the parameter's documentation.</summary>
    public static string SignatureText(SignatureHelpResult help)
    {
        var sig = help.Signatures[Math.Clamp(help.ActiveSignature, 0, help.Signatures.Count - 1)];
        var label = sig.Label;
        ParameterInfo? param = help.ActiveParameter >= 0 && help.ActiveParameter < sig.Parameters.Count ? sig.Parameters[help.ActiveParameter] : null;
        if (param is { LabelLength: > 0 } p && p.LabelStart + p.LabelLength <= label.Length)
            label = label[..p.LabelStart] + "[" + label.Substring(p.LabelStart, p.LabelLength) + "]" + label[(p.LabelStart + p.LabelLength)..];
        var text = help.Signatures.Count > 1 ? $"{label}   ({help.ActiveSignature + 1} of {help.Signatures.Count})" : label;
        if (sig.Documentation.Length > 0) text += "\n" + sig.Documentation;
        if (param is { Documentation.Length: > 0 }) text += $"\n{param.Name}: {param.Documentation}";
        return text;
    }

    // ---- Applying service edits to this buffer ----

    /// <summary>
    /// Applies edits computed against workspace version <paramref name="version"/> to the buffer as one undo step,
    /// mapped through what was typed since. False if the version is unknown (the result is too old to place).
    /// </summary>
    public bool ApplyServiceEdits(long version, IReadOnlyList<LsTextEdit> edits)
    {
        if (SnapshotAt(version) is not { } from) return false;
        var current = _editor.Snapshot;
        var mapped = edits.OrderBy(e => e.Start).Select(e =>
        {
            int s = from.TranslateOffset(e.Start, current, PointTrackingMode.Negative);
            int end = from.TranslateOffset(e.Start + e.Length, current, PointTrackingMode.Positive);
            return new EditorTextEdit(s, Math.Max(0, end - s), e.NewText);
        }).ToList();
        if (mapped.Count > 0) _editor.ApplyEdits(mapped);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var cts in new[] { _pushTimer, _classifyTimer, _classify, _hover, _signature }) cts?.Cancel();
        _editor.TextChanged -= OnTextChanged;
        _editor.CaretMoved -= OnCaretMoved;
        _editor.Hover -= OnHover;
        Document.Moved -= OnMoved;
        if (_editor.TokenSource == this) _editor.TokenSource = null;
        if (_editor.TokenSource == null) _editor.Language = "csharp";
        _editor.DecorationSources.Remove(this);
        if (_editor.CompletionSource == this) _editor.CompletionSource = null;
        _host.Workspace?.CloseDocument(Path);
    }
}
