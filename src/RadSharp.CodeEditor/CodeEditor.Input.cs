using RadSharp.CodeEditing;
using RadSharp.Text;

namespace RadSharp;

public partial class CodeEditor
{
    enum DragMode2 { None, Text, Words, Lines, Box, GutterLines }
    enum BarDrag { None, Vertical, Horizontal }

    DragMode2 _dragging;
    BarDrag _barDrag;
    int _dragOrigin, _dragOriginEnd;     // the clicked word/line range, for word and line drags

    string _composition = "";
    int _compCursor;

    /// <summary>True while an input method is composing text at the primary caret.</summary>
    public bool IsComposing => _composition.Length > 0;

    /// <summary>The caret line's view with the composition spliced in at the caret; <paramref name="compStart"/> is its display index.</summary>
    LineView CompositionView(int line, out int compStart)
    {
        var snap = Snapshot;
        var text = snap.GetLineText(line);
        int col = Math.Clamp(CaretOffset - snap.GetLineStart(line), 0, text.Length);
        var view = BuildLineView(string.Concat(text.AsSpan(0, col), _composition, text.AsSpan(col)), TextFont);
        compStart = view.ToDisplay(col);
        return view;
    }

    // =====================================================================
    // Text input and IME
    // =====================================================================

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled) return;
        e.Handled = true;
        bool wasComposing = IsComposing;
        _composition = "";
        if (wasComposing) Invalidate();
        if (ReadOnly) return;
        _box = null;
        TypeText(e.Text);
    }

    protected override void OnTextComposition(TextCompositionEventArgs e)
    {
        base.OnTextComposition(e);
        if (ReadOnly) return;
        _composition = e.Text ?? "";
        _compCursor = Math.Clamp(e.Cursor, 0, _composition.Length);
        ResetBlink();
        ScrollToCaret();
        UpdateTextInputArea();
        Invalidate();
    }

    // =====================================================================
    // Keyboard
    // =====================================================================

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (IsComposing)
        {
            // The input method owns editing keys while it composes.
            if (e.Key is Key.Backspace or Key.Delete or Key.Enter or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.Escape or Key.Tab)
                e.Handled = true;
            return;
        }
        if (_completion != null && HandleCompletionKey(e)) { e.Handled = true; return; }
        bool box = e.Modifiers == (KeyModifiers.Shift | KeyModifiers.Alt) && e.Key is Key.Up or Key.Down or Key.Left or Key.Right;
        if (!box && e.Key is not (Key.Unknown or Key.Alt)) _box = null;
        e.Handled = HandleKey(e);
        if (e.Handled && e.Key is not (Key.Escape)) MaybeHideHintOnKey(e);
    }

    bool HandleKey(KeyEventArgs e)
    {
        bool mac = Shortcut.CommandModifier == KeyModifiers.Meta;
        bool shift = e.Shift;
        var mods = e.Modifiers & ~KeyModifiers.Shift;
        var wordMod = mac ? KeyModifiers.Alt : KeyModifiers.Ctrl;
        var cmd = Shortcut.CommandModifier;
        int page = Math.Max(1, VisibleLineCount - 1);

        switch (e.Key)
        {
            case Key.Left or Key.Right:
            {
                bool left = e.Key == Key.Left;
                if (e.Modifiers == (KeyModifiers.Shift | KeyModifiers.Alt) && !mac) { ExtendBox(0, left ? -1 : 1); return true; }
                if (mac && mods == KeyModifiers.Meta) { MoveCarets(s => left ? SmartHome(s.Caret) : Snapshot.GetLineEnd(Snapshot.GetLineFromOffset(s.Caret)), shift); return true; }
                if (mods == wordMod) { MoveHorizontal(left, word: true, shift); return true; }
                if (mods != KeyModifiers.None) return false;
                MoveHorizontal(left, word: false, shift);
                return true;
            }
            case Key.Up or Key.Down:
            {
                bool up = e.Key == Key.Up;
                if (e.Modifiers == (KeyModifiers.Shift | KeyModifiers.Alt)) { ExtendBox(up ? -1 : 1, 0); return true; }
                if (mods == KeyModifiers.Alt && !shift) { MoveLines(up); return true; }
                if (mods == KeyModifiers.Ctrl && !mac && !shift) { ScrollY = _scrollY + (up ? -LineHeight : LineHeight); return true; }
                if (mac && mods == KeyModifiers.Meta) { MoveCarets(_ => up ? 0 : Snapshot.Length, shift); return true; }
                if (mods != KeyModifiers.None) return false;
                MoveVertical(up ? -1 : 1, shift);
                return true;
            }
            case Key.PageUp or Key.PageDown:
            {
                if (mods != KeyModifiers.None) return false;
                bool up = e.Key == Key.PageUp;
                int delta = page * LineHeight;
                _scrollY += up ? -delta : delta;
                ClampScroll();
                MoveVertical(up ? -page : page, shift);
                return true;
            }
            case Key.Home or Key.End:
            {
                bool home = e.Key == Key.Home;
                if (mods == KeyModifiers.Ctrl) { MoveCarets(_ => home ? 0 : Snapshot.Length, shift); return true; }
                if (mods != KeyModifiers.None) return false;
                MoveCarets(s => home ? SmartHome(s.Caret) : Snapshot.GetLineEnd(Snapshot.GetLineFromOffset(s.Caret)), shift);
                return true;
            }
            case Key.Backspace:
                if (ReadOnly) return true;
                if (mac && mods == KeyModifiers.Meta)
                {
                    MoveCarets(s => Snapshot.GetLineStart(Snapshot.GetLineFromOffset(s.Caret)), true);
                    DeleteBackward();
                }
                else DeleteBackward(word: mods == wordMod);
                return true;
            case Key.Delete:
                if (mods == KeyModifiers.None && shift) { CutToClipboard(); return true; }
                if (ReadOnly) return true;
                DeleteForward(word: mods == wordMod);
                return true;
            case Key.Enter when mods == KeyModifiers.None:
                if (!ReadOnly) InsertNewLine();
                return true;
            case Key.Tab when mods == KeyModifiers.None:
                if (ReadOnly) return false;
                if (shift) Outdent(); else Indent();
                return true;
            case Key.Escape when mods == KeyModifiers.None && !shift:
                if (HintVisible) { HideHint(); return true; }
                if (FindBarVisible) { HideFind(); return true; }
                if (_sels.Count > 1) { ClearSecondarySelections(); return true; }
                if (!_sels[_primary].IsEmpty) { MoveCarets(s => s.Caret, false); return true; }
                return false;
            case Key.Insert when mods == KeyModifiers.Ctrl && !shift: CopyToClipboard(); return true;
            case Key.Insert when mods == KeyModifiers.None && shift: PasteFromClipboard(); return true;
            case Key.Space when mods == KeyModifiers.Ctrl && !shift: ShowCompletion(); return true;
            case Key.F3 when mods == KeyModifiers.None:
                if (shift) FindPrevious(); else FindNext();
                return true;
        }

        if (mods == cmd)
        {
            switch (e.Key)
            {
                case Key.A when !shift: SelectAll(); return true;
                case Key.C when !shift: CopyToClipboard(); return true;
                case Key.X when !shift: CutToClipboard(); return true;
                case Key.V when !shift: PasteFromClipboard(); return true;
                case Key.Z: if (shift) Redo(); else Undo(); return true;
                case Key.Y when !mac && !shift: Redo(); return true;
                case Key.Slash when !shift: ToggleLineComment(); return true;
                case Key.D when !shift: SelectNextOccurrence(); return true;
                case Key.D when shift: DuplicateLines(); return true;
                case Key.F when !shift: ShowFind(replace: false); return true;
                case Key.H when !shift: ShowFind(replace: true); return true;
                case Key.LeftBracket when shift: CollapseFold(Snapshot.GetLineFromOffset(CaretOffset)); return true;
                case Key.RightBracket when shift: ExpandFold(Snapshot.GetLineFromOffset(CaretOffset)); return true;
            }
        }
        if (e.Modifiers == (KeyModifiers.Shift | KeyModifiers.Alt) && e.Key == Key.I) { AddCaretsToLineEnds(); return true; }
        return false;
    }

    // =====================================================================
    // Mouse
    // =====================================================================

    enum Zone { Text, Gutter, VBar, HBar, None }

    Zone ZoneAt(int x, int y)
    {
        var area = TextArea;
        if (ShowVBar && VBarRect is var v && x >= v.X && y >= v.Y && y < v.Y + v.Height) return Zone.VBar;
        if (ShowHBar && HBarRect is var h && y >= h.Y && x >= h.X && x < h.X + h.Width) return Zone.HBar;
        if (x < area.X && x >= Border && y < area.Y + area.Height) return Zone.Gutter;
        if (x >= area.X && y >= area.Y && x < area.X + area.Width && y < area.Y + area.Height) return Zone.Text;
        return Zone.None;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (CompletionMouseDown(e)) return;
        if (e.Button != MouseButton.Left)
        {
            return;
        }
        if (_hintFromHover) HideHint();
        var zone = ZoneAt(e.X, e.Y);
        switch (zone)
        {
            case Zone.VBar:
            {
                SyncScrollAxes();
                _barDrag = BarDrag.Vertical;
                var bar = VBarRect;
                if (_vDriver.Press(e.Y - bar.Y, bar.Height, Px(16), TextArea.Height)) { _scrollY = _vAxis.Position; Invalidate(); }
                return;
            }
            case Zone.HBar:
            {
                SyncScrollAxes();
                _barDrag = BarDrag.Horizontal;
                var bar = HBarRect;
                if (_hDriver.Press(e.X - bar.X, bar.Width, Px(16), TextArea.Width)) { _scrollX = _hAxis.Position; Invalidate(); }
                return;
            }
            case Zone.Gutter:
                GutterMouseDown(e);
                return;
            case Zone.None:
                return;
        }

        _buffer.BreakUndoCoalescing();
        int offset = OffsetAtPoint(e.X, e.Y);
        var snap = Snapshot;
        bool alt = e.Modifiers.HasFlag(KeyModifiers.Alt);
        bool shiftDown = e.Modifiers.HasFlag(KeyModifiers.Shift);
        if (e.Clicks >= 3)
        {
            int line = snap.GetLineFromOffset(offset);
            _dragOrigin = snap.GetLineStart(line);
            _dragOriginEnd = snap.GetLineEndIncludingBreak(line);
            SetSelectionsCore([new Sel(_dragOrigin, _dragOriginEnd)], 0);
            _dragging = DragMode2.Lines;
            return;
        }
        if (e.Clicks == 2)
        {
            var p = snap.GetPosition(offset);
            var (a, b) = WordAt(snap.GetLineText(p.Line), p.Column);
            int ls = snap.GetLineStart(p.Line);
            _dragOrigin = ls + a;
            _dragOriginEnd = ls + b;
            var list = alt ? new List<Sel>(_sels) { new(ls + a, ls + b) } : [new Sel(ls + a, ls + b)];
            if (alt) list.RemoveAll(s => s.IsEmpty && s.Caret == offset);
            SetSelectionsCore(list, list.Count - 1);
            _dragging = DragMode2.Words;
            return;
        }
        if (alt && shiftDown)
        {
            BeginBox(e.X, e.Y);
            return;
        }
        if (alt)
        {
            // Alt+click adds a caret (or removes one that's already there); dragging on makes a column selection.
            var existing = _sels.FindIndex(s => s.IsEmpty && s.Caret == offset);
            if (existing >= 0 && _sels.Count > 1)
            {
                var list = _sels.ToList();
                list.RemoveAt(existing);
                SetSelectionsCore(list, list.Count - 1);
            }
            else AddSelection(TextSelection.At(offset));
            _boxPressPoint = (e.X, e.Y);
            _dragging = DragMode2.Box;
            _box = null;
            return;
        }
        _box = null;
        if (shiftDown)
        {
            var p = _sels[_primary];
            SetSelectionsCore([new Sel(p.Anchor, offset)], 0);
        }
        else SetSelectionsCore([new Sel(offset, offset)], 0);
        _dragging = DragMode2.Text;
    }

    (int X, int Y)? _boxPressPoint;

    void BeginBox(int x, int y)
    {
        var area = TextArea;
        int line = LineAtY(y);
        float fx = Math.Max(0, x - area.X - TextPadding + _scrollX);
        var anchor = _box is { } b ? (b.AnchorLine, b.AnchorX) : (line, fx);
        _box = (anchor.Item1, anchor.Item2, line, fx);
        _dragging = DragMode2.Box;
        ApplyBox();
    }

    void GutterMouseDown(MouseEventArgs e)
    {
        int line = LineAtY(e.Y);
        int x = e.X - Border;
        GutterArea area = x < GlyphLaneWidth ? GutterArea.Glyphs : x < GlyphLaneWidth + NumbersWidth ? GutterArea.LineNumbers : GutterArea.Folding;
        if (_showGlyphMargin == false && _showLineNumbers && x < NumbersWidth) area = GutterArea.LineNumbers;
        if (area == GutterArea.Folding) ToggleFold(line);
        var args = new CodeEditorGutterClickEventArgs(line, area, e.Button, e.Modifiers);
        GutterClick?.Invoke(this, args);
        if (args.Handled || area != GutterArea.LineNumbers) return;
        // A click on a line number selects the line; dragging extends by lines.
        var snap = Snapshot;
        _dragOrigin = snap.GetLineStart(line);
        _dragOriginEnd = snap.GetLineEndIncludingBreak(line);
        SetSelectionsCore([new Sel(_dragOrigin, _dragOriginEnd)], 0);
        _dragging = DragMode2.GutterLines;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_barDrag == BarDrag.Vertical)
        {
            if (_vDriver.Move(e.Y - VBarRect.Y, VBarRect.Height, Px(16))) { _scrollY = _vAxis.Position; Invalidate(); }
            return;
        }
        if (_barDrag == BarDrag.Horizontal)
        {
            if (_hDriver.Move(e.X - HBarRect.X, HBarRect.Width, Px(16))) { _scrollX = _hAxis.Position; Invalidate(); }
            return;
        }

        var zone = ZoneAt(e.X, e.Y);
        Cursor = zone == Zone.Text && !OverCompletion(e.X, e.Y) ? Cursor.IBeam : Cursor.Default;
        TrackHover(e.X, e.Y, zone);

        switch (_dragging)
        {
            case DragMode2.Text:
            {
                int offset = OffsetAtPoint(e.X, e.Y);
                var p = _sels[_primary];
                SetSelectionsCore([new Sel(p.Anchor, offset)], 0);
                ScrollToCaret();
                break;
            }
            case DragMode2.Words:
            {
                var snap = Snapshot;
                int offset = OffsetAtPoint(e.X, e.Y);
                var pos = snap.GetPosition(offset);
                var (a, b) = WordAt(snap.GetLineText(pos.Line), pos.Column);
                int ls = snap.GetLineStart(pos.Line);
                var sel = offset < _dragOrigin ? new Sel(_dragOriginEnd, ls + a) : new Sel(_dragOrigin, Math.Max(_dragOriginEnd, ls + b));
                SetSelectionsCore([sel], 0);
                ScrollToCaret();
                break;
            }
            case DragMode2.Lines or DragMode2.GutterLines:
            {
                var snap = Snapshot;
                int line = LineAtY(e.Y);
                int ls = snap.GetLineStart(line), le = snap.GetLineEndIncludingBreak(line);
                var sel = ls < _dragOrigin ? new Sel(_dragOriginEnd, ls) : new Sel(_dragOrigin, le);
                SetSelectionsCore([sel], 0);
                ScrollToCaret();
                break;
            }
            case DragMode2.Box:
            {
                if (_box == null && _boxPressPoint is { } press)
                {
                    // Alt+drag past a few pixels turns the new caret into a column selection.
                    if (Math.Abs(e.X - press.X) < Px(3) && Math.Abs(e.Y - press.Y) < Px(3)) break;
                    var area = TextArea;
                    _box = (LineAtY(press.Y), Math.Max(0, press.X - area.X - TextPadding + _scrollX), LineAtY(press.Y), 0);
                }
                if (_box is { } b)
                {
                    var area = TextArea;
                    _box = (b.AnchorLine, b.AnchorX, LineAtY(e.Y), Math.Max(0, e.X - area.X - TextPadding + _scrollX));
                    ApplyBox();
                }
                break;
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_barDrag != BarDrag.None)
        {
            _barDrag = BarDrag.None;
            _vDriver.Release();
            _hDriver.Release();
            Invalidate();
            return;
        }
        _dragging = DragMode2.None;
        _boxPressPoint = null;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled) return;
        if (CompletionWheel(e)) { e.Handled = true; return; }
        int beforeX = _scrollX, beforeY = _scrollY;
        bool horizontal = e.Modifiers.HasFlag(KeyModifiers.Shift) || e.DeltaX != 0;
        if (horizontal)
        {
            float d = e.DeltaX != 0 ? -e.DeltaX : e.DeltaY;
            _scrollX -= (int)Math.Round(d * CharWidth * 6);
        }
        else _scrollY -= (int)Math.Round(e.DeltaY * LineHeight * 3);
        ClampScroll();
        if (_scrollX != beforeX || _scrollY != beforeY)
        {
            if (_hintFromHover) HideHint();
            Invalidate();
            UpdateTextInputArea();
            e.Handled = true;
        }
    }

    // =====================================================================
    // Context menu
    // =====================================================================

    PopupMenu? _defaultContextMenu;
    MenuItem? _mnuUndo, _mnuRedo, _mnuCut, _mnuCopy, _mnuPaste, _mnuComment;

    /// <summary>The built-in Undo/Redo/Cut/Copy/Paste/Select All/Toggle Comment/Find menu, unless a <c>PopupMenu</c> replaces it.</summary>
    public override PopupMenu? ContextMenu
    {
        get
        {
            if (base.ContextMenu is { } explicitMenu) return explicitMenu;
            if (_defaultContextMenu == null)
            {
                var menu = new PopupMenu();
                _mnuUndo = menu.Add("Undo", _ => Undo(), "Mod+Z");
                _mnuRedo = menu.Add("Redo", _ => Redo(), "Mod+Shift+Z");
                menu.Items.Add(new MenuItem { Caption = "-", Separator = true });
                _mnuCut = menu.Add("Cut", _ => CutToClipboard(), "Mod+X");
                _mnuCopy = menu.Add("Copy", _ => CopyToClipboard(), "Mod+C");
                _mnuPaste = menu.Add("Paste", _ => PasteFromClipboard(), "Mod+V");
                menu.Items.Add(new MenuItem { Caption = "-", Separator = true });
                menu.Add("Select All", _ => SelectAll(), "Mod+A");
                _mnuComment = menu.Add("Toggle Comment", _ => ToggleLineComment(), "Mod+/");
                menu.Add("Find…", _ => ShowFind(false), "Mod+F");
                _defaultContextMenu = menu;
            }
            _mnuUndo!.Enabled = !ReadOnly && CanUndo;
            _mnuRedo!.Enabled = !ReadOnly && CanRedo;
            _mnuCut!.Enabled = !ReadOnly;
            _mnuCopy!.Enabled = true;
            _mnuPaste!.Enabled = !ReadOnly && Clipboard.GetText()?.Length > 0;
            _mnuComment!.Enabled = !ReadOnly && LineCommentPrefix.Length > 0;
            return _defaultContextMenu;
        }
        set => base.ContextMenu = value;
    }
}
