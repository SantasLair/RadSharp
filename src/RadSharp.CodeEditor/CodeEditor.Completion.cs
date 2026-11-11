using RadSharp.CodeEditing;
using RadSharp.Drawing;

namespace RadSharp;

public partial class CodeEditor
{
    // =====================================================================
    // Completion
    // =====================================================================

    sealed class CompletionSession
    {
        public required CompletionList List;
        /// <summary>The applicable span's start in the current snapshot (tracked through edits).</summary>
        public int Start;
        public List<CompletionItem> Filtered = [];
        public int Selected;
        public int Top;
    }

    CompletionSession? _completion;
    CancellationTokenSource? _completionCts;
    int _completionRequest;
    readonly object _pendingLock = new();
    (int Id, CompletionRequest Request, Task<CompletionList?> Task)? _pendingCompletion;

    const int CompletionRows = 10;

    /// <summary>True while the completion list is showing.</summary>
    public bool IsCompletionOpen => _completion != null;

    /// <summary>The filtered items in the open list (empty when closed), and the selected one's index.</summary>
    public IReadOnlyList<CompletionItem> CompletionItems => _completion?.Filtered ?? [];
    public int SelectedCompletionIndex => _completion?.Selected ?? -1;

    /// <summary>Asks the <see cref="CompletionSource"/> for completions at the caret (Ctrl+Space).</summary>
    public void ShowCompletion() => RequestCompletion(CompletionTriggerKind.Invoked, '\0');

    public void CloseCompletion()
    {
        _completionCts?.Cancel();
        _completionCts = null;
        _completionRequest++;
        if (_completion == null) return;
        _completion = null;
        Invalidate();
    }

    void RequestCompletion(CompletionTriggerKind trigger, char ch)
    {
        if (_completionSource is not { } source) return;
        _completionCts?.Cancel();
        var cts = new CancellationTokenSource();
        _completionCts = cts;
        int id = ++_completionRequest;
        var request = new CompletionRequest(Snapshot, CaretOffset, trigger, ch);
        Task<CompletionList?> task;
        try { task = source.GetCompletionsAsync(request, cts.Token); }
        catch (Exception) { return; }
        if (task.IsCompleted) { OnCompletionResult(id, request, task); return; }
        // Results come back through the UI thread's context when the host installed one; otherwise the next tick picks them up.
        var context = SynchronizationContext.Current as RadSharp.Threading.UiSynchronizationContext;
        task.ContinueWith(t =>
        {
            if (context != null) context.Post(_ => OnCompletionResult(id, request, t), null);
            else lock (_pendingLock) _pendingCompletion = (id, request, t);   // picked up on the next tick
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Delivers a completion result that finished on another thread while no synchronization context was installed.</summary>
    void PollCompletion()
    {
        (int, CompletionRequest, Task<CompletionList?>)? pending;
        lock (_pendingLock) { pending = _pendingCompletion; _pendingCompletion = null; }
        if (pending is { } p) OnCompletionResult(p.Item1, p.Item2, p.Item3);
    }

    void OnCompletionResult(int id, CompletionRequest request, Task<CompletionList?> task)
    {
        if (id != _completionRequest || task.Status != TaskStatus.RanToCompletion || task.Result is not { } list) return;
        if (request.Snapshot.Buffer != _buffer) return;
        var snap = Snapshot;
        int start = request.Snapshot.TranslateOffset(Math.Clamp(list.ApplicableStart, 0, request.Snapshot.Length), snap, PointTrackingMode.Negative);
        if (CaretOffset < start || snap.GetLineFromOffset(start) != snap.GetLineFromOffset(CaretOffset)) return;
        _completion = new CompletionSession { List = list, Start = start };
        Refilter();
    }

    void UpdateCompletionAfterChange(TextChangedEventArgs e)
    {
        if (_completion == null) return;
        _completion.Start = TextSnapshot.TranslateThrough(e.Changes, _completion.Start, PointTrackingMode.Negative);
        // The editor's own edits refilter once the carets have moved; anyone else's edit refilters now.
        if (!_applyingOwnEdit) Refilter();
    }

    void UpdateCompletionAfterCaretMove()
    {
        if (_completion != null) Refilter();
    }

    static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    void Refilter()
    {
        var s = _completion;
        if (s == null) return;
        var snap = Snapshot;
        int caret = CaretOffset;
        if (caret < s.Start || s.Start > snap.Length || snap.GetLineFromOffset(s.Start) != snap.GetLineFromOffset(caret) || _sels.Count == 0 || !_sels[_primary].IsEmpty)
        {
            CloseCompletion();
            return;
        }
        var typed = snap.GetText(s.Start, caret - s.Start);
        if (typed.Any(c => !IsIdentPart(c))) { CloseCompletion(); return; }
        s.Filtered = Filter(s.List.Items, typed);
        if (s.Filtered.Count == 0) { CloseCompletion(); return; }
        s.Selected = 0;
        s.Top = 0;
        Invalidate();
    }

    /// <summary>Items matching what was typed: prefix matches first (exact case first), then subsequence matches.</summary>
    static List<CompletionItem> Filter(IReadOnlyList<CompletionItem> items, string typed)
    {
        var scored = new List<(CompletionItem Item, int Score)>();
        foreach (var item in items)
        {
            var f = item.FilterText ?? item.DisplayText;
            int score;
            if (typed.Length == 0) score = 1;
            else if (f.StartsWith(typed, StringComparison.Ordinal)) score = 4;
            else if (f.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) score = 3;
            else if (IsSubsequence(typed, f)) score = 1;
            else continue;
            scored.Add((item, score));
        }
        return [.. scored.OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.SortText ?? x.Item.DisplayText, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Item)];
    }

    static bool IsSubsequence(string needle, string hay)
    {
        int j = 0;
        foreach (char c in hay)
            if (j < needle.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(needle[j])) j++;
        return j == needle.Length;
    }

    /// <summary>After a single typed character: open, re-request or leave the list.</summary>
    void OnCharTyped(char ch)
    {
        if (_completionSource is not { } source || _sels.Count == 0) return;
        if (source.TriggerCharacters.Contains(ch)) { RequestCompletion(CompletionTriggerKind.TriggerCharacter, ch); return; }
        if (_completion != null || !CompleteWhileTyping) return;
        if (!(char.IsLetter(ch) || ch == '_')) return;
        var snap = Snapshot;
        int caret = CaretOffset;
        // Only at the start of an identifier.
        if (caret >= 2 && IsIdentPart(snap[caret - 2])) return;
        RequestCompletion(CompletionTriggerKind.Typing, ch);
    }

    bool HandleCompletionKey(KeyEventArgs e)
    {
        var s = _completion!;
        if (e.Modifiers != KeyModifiers.None) return false;
        int count = s.Filtered.Count;
        switch (e.Key)
        {
            case Key.Down: SelectCompletion(s.Selected + 1 >= count ? 0 : s.Selected + 1); return true;
            case Key.Up: SelectCompletion(s.Selected - 1 < 0 ? count - 1 : s.Selected - 1); return true;
            case Key.PageDown: SelectCompletion(Math.Min(count - 1, s.Selected + CompletionRows - 1)); return true;
            case Key.PageUp: SelectCompletion(Math.Max(0, s.Selected - CompletionRows + 1)); return true;
            case Key.Enter or Key.Tab: CommitCompletion(); return true;
            case Key.Escape: CloseCompletion(); return true;
        }
        return false;
    }

    void SelectCompletion(int index)
    {
        var s = _completion!;
        s.Selected = Math.Clamp(index, 0, Math.Max(0, s.Filtered.Count - 1));
        if (s.Selected < s.Top) s.Top = s.Selected;
        if (s.Selected >= s.Top + CompletionRows) s.Top = s.Selected - CompletionRows + 1;
        Invalidate();
    }

    /// <summary>Inserts the selected completion item (Enter or Tab).</summary>
    public void CommitCompletion()
    {
        var s = _completion;
        if (s == null || s.Filtered.Count == 0) return;
        var item = s.Filtered[s.Selected];
        var snap = Snapshot;
        int caret = CaretOffset;
        var list = new CompletionList(s.Start, caret - s.Start, s.List.Items);
        CloseCompletion();
        if (ReadOnly) return;
        CompletionCommit? commit = null;
        try { commit = _completionSource?.GetCommit(snap, list, item); }
        catch (Exception) { commit = null; }
        if (commit is { Edits.Count: > 0 }) { ApplyCommit(commit); return; }
        TextEdit? custom = null;
        try { custom = _completionSource?.GetCommitEdit(snap, list, item); }
        catch (Exception) { custom = null; }
        _buffer.BreakUndoCoalescing();
        var insert = item.InsertText ?? item.DisplayText;
        var edits = new List<(TextEdit, int, int)>();
        if (custom is { } c)
            edits.Add((c, c.NewText.Length, c.NewText.Length));
        else
        {
            // Every caret whose typed prefix matches gets the same completion.
            var typed = snap.GetText(s.Start, caret - s.Start);
            foreach (var sel in _sels)
            {
                int st = sel.Caret - typed.Length;
                if (sel.IsEmpty && st >= 0 && snap.GetText(st, typed.Length) == typed)
                    edits.Add((new TextEdit(st, typed.Length, insert), insert.Length, insert.Length));
                else edits.Add((new TextEdit(sel.Caret, 0, ""), 0, 0));
            }
        }
        ApplyOwn(edits, UndoCoalescing.None);
        _buffer.BreakUndoCoalescing();
    }

    /// <summary>Applies a source's <see cref="CompletionCommit"/> as one undo step and places the caret.</summary>
    void ApplyCommit(CompletionCommit commit)
    {
        _buffer.BreakUndoCoalescing();
        var before = SelectionState();
        _applyingOwnEdit = true;
        TextChangedEventArgs? args;
        try { args = _buffer.Apply(commit.Edits, UndoCoalescing.None, before); }
        finally { _applyingOwnEdit = false; }
        int caret;
        if (commit.Caret is { } c) caret = c;
        else
        {
            var last = commit.Edits.OrderBy(e => e.Start).Last();
            caret = args == null ? last.Start : TextSnapshot.TranslateThrough(args.Changes, last.End, PointTrackingMode.Positive);
        }
        caret = Math.Clamp(caret, 0, Snapshot.Length);
        SetSelectionsCore([new Sel(caret, caret)], 0, raise: false);
        if (args != null) _buffer.SetUndoStateAfter(SelectionState());
        _buffer.BreakUndoCoalescing();
        ScrollToCaret();
        RaiseCaretMoved();
    }

    Rect? CompletionRect
    {
        get
        {
            var s = _completion;
            if (s == null || s.Filtered.Count == 0) return null;
            var font = TextFont;
            int rowH = LineHeight + Px(4);
            int rows = Math.Min(CompletionRows, s.Filtered.Count);
            float widest = 0;
            foreach (var item in s.Filtered.Take(200)) widest = Math.Max(widest, font.MeasureWidth(item.DisplayText));
            int w = Math.Clamp((int)widest + Px(48), Px(220), Math.Max(Px(220), Px(480)));
            w = Math.Min(w, Math.Max(Px(120), Width - 2 * Border));
            int h = rows * rowH + 2;
            var (x, y) = PointOfOffset(s.Start);
            x -= Px(22);
            int below = y + LineHeight + Px(2);
            int top = below + h <= Height - Border || y - h - Px(2) < 0 ? below : y - h - Px(2);
            x = Math.Clamp(x, Border, Math.Max(Border, Width - Border - w));
            return new Rect(x, top, w, h);
        }
    }

    bool OverCompletion(int x, int y) => CompletionRect is { } r && x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height;

    bool CompletionMouseDown(MouseEventArgs e)
    {
        if (_completion == null) return false;
        if (CompletionRect is { } r && OverCompletion(e.X, e.Y))
        {
            int rowH = LineHeight + Px(4);
            int index = _completion.Top + (e.Y - r.Y - 1) / rowH;
            if (index < _completion.Filtered.Count) SelectCompletion(index);
            if (e.Clicks >= 2) CommitCompletion();
            return true;
        }
        CloseCompletion();
        return false;
    }

    bool CompletionWheel(MouseWheelEventArgs e)
    {
        if (_completion == null || !OverCompletion(e.X, e.Y)) return false;
        var s = _completion;
        s.Top = Math.Clamp(s.Top - (int)Math.Round(e.DeltaY * 3), 0, Math.Max(0, s.Filtered.Count - CompletionRows));
        Invalidate();
        return true;
    }

    void PaintCompletion(Canvas canvas, CodeEditorColors colors)
    {
        if (CompletionRect is not { } r) return;
        var s = _completion!;
        var font = TextFont;
        var ui = canvas.DefaultFont;
        int rowH = LineHeight + Px(4);
        canvas.FillRect(new Rect(r.X + Px(2), r.Y + Px(2), r.Width, r.Height), new Color(0, 0, 0, 28));
        canvas.FillRect(r, colors.PopupBackground);
        canvas.DrawBorder(r, colors.PopupBorder, 1);
        canvas.Save();
        canvas.ClipRect(new Rect(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2));
        var typed = Snapshot.GetText(s.Start, Math.Max(0, CaretOffset - s.Start));
        for (int i = s.Top, row = 0; i < s.Filtered.Count && row < CompletionRows; i++, row++)
        {
            var item = s.Filtered[i];
            int y = r.Y + 1 + row * rowH;
            if (i == s.Selected) canvas.FillRect(new Rect(r.X + 1, y, r.Width - 2, rowH), colors.PopupSelection);
            // Kind badge: a coloured square with the kind's initial.
            int badge = rowH - Px(8);
            var badgeRect = new Rect(r.X + Px(5), y + (rowH - badge) / 2, badge, badge);
            var kindColor = KindColor(item.Kind, colors);
            canvas.FillRoundRect(badgeRect, Px(2), kindColor);
            if (!string.IsNullOrEmpty(item.Kind))
                canvas.DrawText(char.ToUpperInvariant(item.Kind[0]).ToString(), ui.WithSize(ui.Size * 0.8f), colors.PopupBackground, badgeRect, TextAlign.Center, VerticalAlign.Center);
            float tx = badgeRect.X + badge + Px(6);
            float baseline = y + (rowH - LineHeight) / 2f + font.Ascent;
            var display = item.DisplayText;
            int match = typed.Length > 0 && display.StartsWith(typed, StringComparison.OrdinalIgnoreCase) ? typed.Length : 0;
            if (match > 0)
            {
                canvas.DrawText(display[..match], font, colors.PopupMatch, tx, baseline);
                canvas.DrawText(display[match..], font, colors.PopupText, tx + font.MeasureWidth(display[..match]), baseline);
            }
            else canvas.DrawText(display, font, colors.PopupText, tx, baseline);
            if (i == s.Selected && !string.IsNullOrEmpty(item.Detail))
            {
                float dw = ui.MeasureWidth(item.Detail);
                float dx = r.X + r.Width - Px(8) - dw;
                if (dx > tx + font.MeasureWidth(display) + Px(12))
                    canvas.DrawText(item.Detail, ui, colors.PopupDetail, dx, y + (rowH - ui.LineHeight) / 2f + ui.Ascent);
            }
        }
        canvas.Restore();
    }

    static Color KindColor(string? kind, CodeEditorColors colors) => (kind ?? "").ToLowerInvariant() switch
    {
        "method" or "function" or "extensionmethod" => Color.FromHex("#8250DF"),
        "property" or "field" or "event" => Color.FromHex("#2F6FEB"),
        "class" or "struct" or "record" or "type" => Color.FromHex("#1F7A8C"),
        "interface" => Color.FromHex("#2B8A5C"),
        "enum" or "enummember" or "constant" => Color.FromHex("#B35900"),
        "keyword" => Color.FromHex("#6A737D"),
        "snippet" => Color.FromHex("#9A6700"),
        "local" or "parameter" or "variable" => Color.FromHex("#0969DA"),
        "namespace" => Color.FromHex("#57606A"),
        _ => colors.FoldMarker,
    };

    // =====================================================================
    // Hints (quick info, signature help) and hover
    // =====================================================================

    (int Offset, string Text, bool Above)? _hint;
    bool _hintFromHover;
    bool _inHoverEvent;
    (int X, int Y)? _hoverPoint;
    int _hoverOffset = -1;
    double _hoverTime;
    bool _hoverRaised;

    /// <summary>How long the pointer must rest on text before <see cref="Hover"/> is raised, in milliseconds.</summary>
    public double HoverDelayMs { get; set; } = 500;

    public bool HintVisible => _hint != null;
    public string? HintText => _hint?.Text;

    /// <summary>
    /// Shows a hint box (quick info, signature help, a diagnostic) at <paramref name="offset"/>: below its line, or
    /// above it with <paramref name="above"/>. Line breaks in <paramref name="text"/> are kept; long lines wrap.
    /// It stays until <see cref="HideHint"/>, Esc, or (for a hint shown from <see cref="Hover"/>) the pointer moving on.
    /// </summary>
    public void ShowHint(int offset, string text, bool above = false)
    {
        _hint = (Math.Clamp(offset, 0, Snapshot.Length), text ?? "", above);
        _hintFromHover = _inHoverEvent;
        Invalidate();
    }

    public void ShowHint(TextPosition position, string text, bool above = false) => ShowHint(Snapshot.GetOffset(position), text, above);

    /// <summary>
    /// Answers a <see cref="Hover"/> later (quick info that took a moment): shows the hint as if it had been shown from
    /// the event, so it goes away when the pointer moves on, but only while the pointer still rests where it was
    /// raised. Returns false (and shows nothing) if the pointer has moved since.
    /// </summary>
    public bool ShowHoverHint(int hoverOffset, int hintOffset, string text)
    {
        if (_hoverPoint == null || !_hoverRaised || _hoverOffset != hoverOffset) return false;
        _inHoverEvent = true;
        try { ShowHint(hintOffset, text); }
        finally { _inHoverEvent = false; }
        return true;
    }

    public void HideHint()
    {
        if (_hint == null) return;
        _hint = null;
        _hintFromHover = false;
        Invalidate();
    }

    void MaybeHideHintOnKey(KeyEventArgs e)
    {
        if (_hintFromHover) HideHint();
    }

    void TrackHover(int x, int y, Zone zone)
    {
        if (zone != Zone.Text || _dragging != DragMode2.None || OverCompletion(x, y))
        {
            _hoverPoint = null;
            _hoverOffset = -1;
            if (_hintFromHover) HideHint();
            return;
        }
        int offset = OffsetAtPoint(x, y);
        if (offset == _hoverOffset && _hoverPoint != null) return;
        if (_hintFromHover && _hint is { } h)
        {
            // Keep a hover hint while the pointer stays on the same word.
            var snap = Snapshot;
            var p = snap.GetPosition(h.Offset);
            var (a, b) = WordAt(snap.GetLineText(p.Line), p.Column);
            int ls = snap.GetLineStart(p.Line);
            if (offset < ls + a || offset > ls + b) HideHint();
        }
        _hoverOffset = offset;
        _hoverPoint = (x, y);
        _hoverTime = OwningForm?.Now ?? 0;
        _hoverRaised = false;
    }

    void TickHover(double now)
    {
        if (_hoverPoint is not { } pt || _hoverRaised || now - _hoverTime < HoverDelayMs) return;
        _hoverRaised = true;
        var snap = Snapshot;
        int offset = _hoverOffset;
        var pos = snap.GetPosition(offset);
        // Only over the text itself, not the empty space after a line.
        var view = GetLineView(pos.Line);
        var area = TextArea;
        if (pt.X - area.X - TextPadding + _scrollX > view.Layout.Width + CharWidth) return;
        _inHoverEvent = true;
        try { Hover?.Invoke(this, new CodeEditorHoverEventArgs(offset, pos, pt.X, pt.Y)); }
        finally { _inHoverEvent = false; }
        if (_hint != null) return;
        // No one answered: show a decoration's message (a diagnostic) if the pointer is on one.
        foreach (var (s, e, d) in CurrentDecorations())
        {
            if (s > offset) break;
            if (d.Message is { Length: > 0 } msg && offset >= s && offset <= Math.Max(e, s + 1) && d.Kind != DecorationKind.Highlight)
            {
                _inHoverEvent = true;
                try { ShowHint(s, msg); }
                finally { _inHoverEvent = false; }
                return;
            }
        }
    }

    void PaintHint(Canvas canvas, CodeEditorColors colors)
    {
        if (_hint is not { } h || h.Text.Length == 0) return;
        var ui = canvas.DefaultFont;
        int pad = Px(6);
        float maxW = Math.Min(Px(480), Math.Max(Px(80), Width - 2 * pad - 2 * Border));
        var lines = TextLayout.WrapLines(h.Text, ui, maxW);
        float widest = lines.Max(l => ui.MeasureWidth(l));
        int lineH = (int)Math.Ceiling(ui.LineHeight);
        int w = (int)Math.Ceiling(widest) + 2 * pad, hh = lines.Count * lineH + 2 * pad;
        var (x, y) = PointOfOffset(h.Offset);
        int below = y + LineHeight + Px(2), above = y - hh - Px(2);
        int top = h.Above ? (above >= 0 ? above : below) : (below + hh <= Height || above < 0 ? below : above);
        x = Math.Clamp(x, Border, Math.Max(Border, Width - Border - w));
        var r = new Rect(x, top, w, hh);
        canvas.FillRect(new Rect(r.X + Px(2), r.Y + Px(2), r.Width, r.Height), new Color(0, 0, 0, 28));
        canvas.FillRect(r, colors.PopupBackground);
        canvas.DrawBorder(r, colors.PopupBorder, 1);
        for (int i = 0; i < lines.Count; i++)
            canvas.DrawText(lines[i], ui, colors.PopupText, r.X + pad, r.Y + pad + i * lineH + ui.Ascent);
    }
}
