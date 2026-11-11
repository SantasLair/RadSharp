using System.Text.Json.Nodes;
using RadSharp.CodeEditing;
using RadSharp.Design.Code;
using RadSharp.Ide.Debug;

namespace RadSharp.Designer;

/// <summary>
/// The debugger in the IDE (milestone I6): F5 builds and starts the app under netcoredbg through the
/// <see cref="DebugController"/>; breakpoints toggle in the gutter's glyph lane or with F8 (F9 stays the live run, as
/// in the architecture), follow edits, sync live and persist per project in the session file; a stop opens the file at
/// the current statement and fills Call Stack, Locals and Watch; hovering an identifier while paused shows its value.
/// </summary>
/// <remarks>
/// Shortcuts: F5 Start Debugging / Continue, Shift+F5 Stop (the app of Ctrl+F5 too), Ctrl+Shift+F5 Restart, F10 Step
/// Over, F11 Step Into, Shift+F11 Step Out, Ctrl+Alt+P Pause (there is no Break key in RADSharp's key set), F8 Toggle
/// Breakpoint. F10 opens the menu bar while no session is paused (the registry only takes an enabled command's key).
/// </remarks>
public partial class DesignerForm
{
    /// <summary>The debugger's tool window ids.</summary>
    public static class DebugToolWindows
    {
        public const string CallStack = "callStack", Locals = "locals", Watch = "watch", Breakpoints = "breakpoints";
    }

    readonly BreakpointStore _breakpoints = new();
    DebugController _debugger = null!;
    CallStackWindow _callStackWindow = null!;
    LocalsWindow _localsWindow = null!;
    WatchWindow _watchWindow = null!;
    BreakpointsWindow _breakpointsWindow = null!;
    readonly Dictionary<CodeDocument, BreakpointDecorations> _debugMarks = [];
    readonly List<Control> _debugToolbar = [];
    JsonObject _savedBreakpoints = new();
    bool _loadingBreakpoints, _breakpointSavePosted, _debugWindowsShown;

    /// <summary>The debugger: state, commands and the paused context.</summary>
    public DebugController Debugger => _debugger;
    /// <summary>The project's breakpoints (persisted in the session file per project).</summary>
    public BreakpointStore Breakpoints => _breakpoints;
    public CallStackWindow CallStackView => _callStackWindow;
    public LocalsWindow LocalsView => _localsWindow;
    public WatchWindow WatchView => _watchWindow;
    public BreakpointsWindow BreakpointsView => _breakpointsWindow;

    /// <summary>The debugger's marks (breakpoint glyphs, current statement) in an open code document.</summary>
    public BreakpointDecorations? DebugMarksFor(CodeDocument document) => _debugMarks.GetValueOrDefault(document);

    bool DebugActive => _debugger?.IsActive == true;

    /// <summary>Creates the controller and the debug tool windows (before the commands are registered, so the View menu lists them).</summary>
    void InitDebugger()
    {
        _debugger = new DebugController(new DebugHost(this), _breakpoints);
        _debugger.StateChanged += OnDebugStateChanged;
        _debugger.ContextChanged += OnDebugContextChanged;
        _debugger.ExceptionStopped += OnDebugException;

        _callStackWindow = new CallStackWindow();
        _callStackWindow.FrameActivated += i => _ = _debugger.SelectFrameAsync(i);
        _localsWindow = new LocalsWindow();
        _localsWindow.Tree.LoadChildren = r => _debugger.GetVariablesAsync(r);
        _watchWindow = new WatchWindow { Evaluate = e => _debugger.EvaluateAsync(e, "watch") };
        _watchWindow.Tree.LoadChildren = r => _debugger.GetVariablesAsync(r);
        _breakpointsWindow = new BreakpointsWindow(_breakpoints)
        {
            Navigate = b => ShowSourceLine(b.File, b.Line, focus: true),
            DialogOwner = this,
            BaseDirectory = () => _project?.Directory,
        };

        // Call Stack on the right, the others below (beside Output); adding a window activates it, so the tabs that
        // were in front stay in front.
        var activeRight = _dock.Region(DockSide.Right).ActiveWindow;
        var activeBottom = _dock.Region(DockSide.Bottom).ActiveWindow;
        _dock.Add(new ToolWindow(DebugToolWindows.CallStack, "Call Stack", _callStackWindow), DockSide.Right);
        _dock.Add(new ToolWindow(DebugToolWindows.Locals, "Locals", _localsWindow), DockSide.Bottom);
        _dock.Add(new ToolWindow(DebugToolWindows.Watch, "Watch", _watchWindow), DockSide.Bottom);
        _dock.Add(new ToolWindow(DebugToolWindows.Breakpoints, "Breakpoints", _breakpointsWindow), DockSide.Bottom);
        if (activeRight != null) _dock.Region(DockSide.Right).Activate(activeRight);
        if (activeBottom != null) _dock.Region(DockSide.Bottom).Activate(activeBottom);

        _docs.TabsChanged += SyncDebugMarks;
        _breakpoints.Changed += OnBreakpointsChanged;
        _savedBreakpoints = IdeSession.Load(_paths.Session).Breakpoints ?? new JsonObject();
    }

    void RegisterDebugCommands()
    {
        var c = _commands;
        void Reg(string id, string title, Action run, string shortcut, Func<bool> enabled, Func<bool>? isChecked = null) =>
            c.Register(new Command(id, title, "Debug", run, shortcut, enabled, isChecked));
        Reg("Debug.Continue", "Continue", () => _ = _debugger.ContinueAsync(), "", () => _debugger.CanContinue);
        Reg("Debug.Restart", "Restart", () => _ = _debugger.RestartAsync(), "Ctrl+Shift+F5", () => _debugger.IsActive && !_building);
        Reg("Debug.Pause", "Break All", () => _ = _debugger.PauseAsync(), "Ctrl+Alt+P", () => _debugger.CanPause);
        Reg("Debug.StepOver", "Step Over", () => _ = _debugger.StepOverAsync(), "F10", () => _debugger.CanStep);
        Reg("Debug.StepInto", "Step Into", () => _ = _debugger.StepIntoAsync(), "F11", () => _debugger.CanStep);
        Reg("Debug.StepOut", "Step Out", () => _ = _debugger.StepOutAsync(), "Shift+F11", () => _debugger.CanStep);
        Reg("Debug.ToggleBreakpoint", "Toggle Breakpoint", () => ToggleBreakpointAtCaret(), "F8", () => _docs.Active is CodeDocument);
        Reg("Debug.DisableAllBreakpoints", "Disable All Breakpoints", () => _breakpointsWindow.ToggleAll(), "", () => _breakpoints.All.Count > 0);
        Reg("Debug.DeleteAllBreakpoints", "Delete All Breakpoints", () => _breakpoints.Clear(), "", () => _breakpoints.All.Count > 0);
        Reg("Debug.BreakOnAllExceptions", "Break When Any Exception Is Thrown", () => _debugger.BreakOnAllExceptions = !_debugger.BreakOnAllExceptions,
            "", () => true, () => _debugger.BreakOnAllExceptions);
        Reg("Debug.BreakOnUnhandledExceptions", "Break on Unhandled Exceptions", () => _debugger.BreakOnUnhandledExceptions = !_debugger.BreakOnUnhandledExceptions,
            "", () => true, () => _debugger.BreakOnUnhandledExceptions);
    }

    /// <summary>The Debug menu, and the debug group of the tool bar (shown only while debugging).</summary>
    void BuildDebugMenu(MenuItem debug)
    {
        void Cmd(MenuItem parent, string id, string? caption = null) => parent.Items.Add(_commands.CreateMenuItem(id, caption));
        Cmd(debug, "Run.StartDebugging", "Start Debugging / Continue");
        Cmd(debug, "Run.RunWithoutDebugging");
        Cmd(debug, "Run.Stop", "Stop Debugging");
        Cmd(debug, "Debug.Restart");
        Cmd(debug, "Debug.Pause");
        debug.AddSeparator();
        Cmd(debug, "Debug.StepOver");
        Cmd(debug, "Debug.StepInto");
        Cmd(debug, "Debug.StepOut");
        debug.AddSeparator();
        Cmd(debug, "Debug.ToggleBreakpoint");
        Cmd(debug, "Debug.DisableAllBreakpoints");
        Cmd(debug, "Debug.DeleteAllBreakpoints");
        debug.AddSeparator();
        Cmd(debug, "Debug.BreakOnUnhandledExceptions");
        Cmd(debug, "Debug.BreakOnAllExceptions");
        debug.AddSeparator();
        var windows = new MenuItem("Windows");
        windows.Opening += m => _commands.Refresh(m.Items);
        foreach (var id in new[] { DebugToolWindows.Breakpoints, DebugToolWindows.CallStack, DebugToolWindows.Locals, DebugToolWindows.Watch })
            Cmd(windows, $"View.ToolWindow.{id}");
        debug.Items.Add(windows);

        var sep = new ToolSeparator { Name = "sepDebug" };
        _debugToolbar.Add(sep);
        tbMain.Add(sep);
        sep.SetBounds(0, 0, sep.Px(9), sep.Px(24));
        foreach (var (name, caption, id, width) in new[]
        {
            ("btnDebugContinue", "Continue", "Debug.Continue", 72), ("btnDebugPause", "Pause", "Debug.Pause", 56),
            ("btnDebugStop", "Stop", "Run.Stop", 48), ("btnDebugRestart", "Restart", "Debug.Restart", 64),
            ("btnDebugStepOver", "Step Over", "Debug.StepOver", 76), ("btnDebugStepInto", "Step Into", "Debug.StepInto", 72),
            ("btnDebugStepOut", "Step Out", "Debug.StepOut", 68),
        })
        {
            var b = new ToolButton { Name = name, Caption = caption };
            b.SetBounds(0, 0, b.Px(width), b.Px(28));
            _commands.Bind(b, id);
            _debugToolbar.Add(b);
            tbMain.Add(b);
        }
        UpdateDebugToolbar();
    }

    void UpdateDebugToolbar()
    {
        foreach (var c in _debugToolbar) c.Visible = DebugActive;
        tbMain.Invalidate();
    }

    /// <summary>The title's state suffix while debugging.</summary>
    string DebugTitleSuffix => _debugger?.State switch
    {
        DebugState.Starting => " [Starting]",
        DebugState.Running => " [Running]",
        DebugState.Paused => " [Paused]",
        _ => "",
    };

    // ---- F5 ----

    /// <summary>F5: starts debugging (save, build, adapter, launch), or continues when paused.</summary>
    public async Task<bool> StartOrContinueDebuggingAsync()
    {
        if (_debugger.IsPaused) { await _debugger.ContinueAsync(); return true; }
        if (_debugger.IsActive || _project == null || _building || AppRunning) return false;
        _debugWindowsShown = false;
        return await _debugger.StartAsync();
    }

    void OnDebugStateChanged()
    {
        if (_debugger.State == DebugState.Design)
        {
            foreach (var marks in _debugMarks.Values) marks.SetStatement(null);
            _callStackWindow.SetFrames([], -1);
            _localsWindow.SetScopes([]);
            _watchWindow.Refresh();
        }
        foreach (var marks in _debugMarks.Values) marks.Invalidate();
        UpdateDebugToolbar();
        RefreshCommandState();
        UpdateTitle();
    }

    void OnDebugContextChanged()
    {
        var frames = _debugger.Frames;
        _callStackWindow.SetFrames(frames, _debugger.CurrentFrameIndex);
        _localsWindow.SetScopes(_debugger.Scopes);
        _watchWindow.Refresh();
        foreach (var marks in _debugMarks.Values) marks.SetStatement(null);
        RefreshCommandState();
        if (_debugger.CurrentFrame is not { } frame) return;
        if (!_debugWindowsShown)
        {
            _debugWindowsShown = true;
            _dock.Show(FindToolWindow(DebugToolWindows.Locals));
            _dock.Show(FindToolWindow(DebugToolWindows.CallStack));
        }
        if (frame.Source?.Path is not { } path || !File.Exists(path))
        {
            sbMain.SimpleText = $"Paused in {frame.Name} (no source)";
            return;
        }
        int top = frames.ToList().FindIndex(f => f.Source?.Path != null);
        if (ShowSourceLine(path, frame.Line, focus: true, column: Math.Max(1, frame.Column)) is { } doc && DebugMarksFor(doc) is { } m)
            m.SetStatement(frame.Line, top: _debugger.CurrentFrameIndex == top);
    }

    void OnDebugException(string text)
    {
        if (_docs.Active is CodeDocument doc && doc.Editor is CodeEditorAdapter { Editor: var editor } && DebugMarksFor(doc)?.StatementLine is { } line)
        {
            var snap = editor.Snapshot;
            int l = Math.Clamp(line - 1, 0, snap.LineCount - 1);
            var lineText = snap.GetLineText(l);
            editor.ShowHint(snap.GetLineStart(l) + (lineText.Length - lineText.TrimStart().Length), "Exception: " + text);
        }
        sbMain.SimpleText = "Exception: " + text;
    }

    /// <summary>Opens a file in the built-in editor at a line (the debugger always uses it, whatever the navigation setting).</summary>
    CodeDocument? ShowSourceLine(string file, int line, bool focus, int column = 1)
    {
        using var _ = JumpScope();   // opening the file and moving the caret is one navigation history entry
        var doc = OpenCodeFile(file);
        if (doc == null) return null;
        doc.GoTo(line, column);
        if (focus) doc.Focus();
        return doc;
    }

    // ---- Breakpoints in the editor ----

    /// <summary>F8: toggles a breakpoint on the caret's line of the active code document.</summary>
    public bool ToggleBreakpointAtCaret()
    {
        if (_docs.Active is not CodeDocument doc) return false;
        return ToggleBreakpoint(doc.Path, doc.Editor.Caret.Line);
    }

    /// <summary>Adds a breakpoint at a 1-based line, or removes the one there. True if one exists now.</summary>
    public bool ToggleBreakpoint(string file, int line)
    {
        bool added = _breakpoints.Toggle(file, line);
        sbMain.SimpleText = added ? $"Breakpoint at {Path.GetFileName(file)}:{line}" : $"Breakpoint removed from {Path.GetFileName(file)}:{line}";
        return added;
    }

    /// <summary>Attaches the debugger's marks, gutter clicks and hover to code documents as they open; detaches closed ones.</summary>
    void SyncDebugMarks()
    {
        foreach (var doc in _debugMarks.Keys.Where(d => !_docs.Documents.Contains(d)).ToList())
        {
            _debugMarks[doc].Dispose();
            _debugMarks.Remove(doc);
        }
        foreach (var doc in _docs.Documents.OfType<CodeDocument>())
        {
            if (_debugMarks.ContainsKey(doc) || doc.Editor is not CodeEditorAdapter adapter) continue;
            var editor = adapter.Editor;
            var marks = new BreakpointDecorations(doc, editor, _breakpoints);
            _debugMarks[doc] = marks;
            editor.GutterClick += (_, e) =>
            {
                if (!_debugMarks.ContainsKey(doc) || e.Area != GutterArea.Glyphs || e.Button != MouseButton.Left) return;
                ToggleBreakpoint(doc.Path, e.Line + 1);
                e.Handled = true;
            };
            editor.Hover += (sender, e) =>
            {
                if (!_debugMarks.ContainsKey(doc) || !_debugger.IsPaused) return;
                int offset = e.Offset;
                DebugHoverAsync(doc, offset).ContinueWith(t => Application.Post(() =>
                {
                    if (t.IsCompletedSuccessfully && t.Result is { } hint) editor.ShowHoverHint(offset, hint.Start, hint.Text);
                }), TaskScheduler.Default);
            };
            doc.Moved += old => MoveBreakpoints(old, doc.Path);
        }
    }

    /// <summary>A renamed file keeps its breakpoints.</summary>
    void MoveBreakpoints(string oldPath, string newPath)
    {
        foreach (var b in _breakpoints.GetForFile(oldPath))
        {
            _breakpoints.Remove(b.Id);
            _breakpoints.Add(newPath, b.Line, b.Condition, b.HitCondition, b.Enabled);
        }
    }

    /// <summary>
    /// The debugger's hover while paused: the expression under <paramref name="offset"/> (an identifier with the member
    /// accesses before it, so <c>this.total</c> or <c>order.Lines</c>) evaluated in the selected frame. Null when not
    /// paused, not on an identifier, or the debugger can't evaluate it.
    /// </summary>
    public async Task<(int Start, string Text)?> DebugHoverAsync(CodeDocument doc, int offset)
    {
        if (!_debugger.IsPaused) return null;
        var text = doc.Text;
        if (ExpressionAt(text, offset) is not { } expr) return null;
        try
        {
            var result = await _debugger.EvaluateAsync(expr.Text, "hover").ConfigureAwait(false);
            return result == null ? null : (expr.Start, $"{expr.Text} = {result.Result}");
        }
        catch (Exception e) when (e is Ide.Debug.Protocol.DapRequestException or OperationCanceledException or Ide.Debug.Protocol.DapConnectionClosedException)
        {
            return null;
        }
    }

    /// <summary>The identifier at <paramref name="offset"/>, extended left over <c>a.b.</c> member accesses.</summary>
    public static (int Start, string Text)? ExpressionAt(string text, int offset)
    {
        static bool Ident(char c) => char.IsLetterOrDigit(c) || c == '_';
        if (offset < 0 || offset > text.Length) return null;
        int s = offset, e = offset;
        while (s > 0 && Ident(text[s - 1])) s--;
        while (e < text.Length && Ident(text[e])) e++;
        if (e == s || char.IsDigit(text[s])) return null;
        while (s > 1 && text[s - 1] == '.' && Ident(text[s - 2]))
        {
            int p = s - 1;
            while (p > 0 && Ident(text[p - 1])) p--;
            if (char.IsDigit(text[p])) break;
            s = p;
        }
        var expr = text[s..e];
        return expr is "var" or "new" or "return" or "if" or "else" or "for" or "foreach" or "while" or "using" or "namespace" or "class" or "void"
            or "int" or "string" or "bool" or "public" or "private" or "static" ? null : (s, expr);
    }

    // ---- Persistence: per project, in the session file ----

    string? BreakpointKey => _solutionFile ?? _project?.ProjectFile;

    /// <summary>What the session file keeps: every project's breakpoints (read back from the file until the debugger is set up).</summary>
    JsonObject? BreakpointsForSession => _debugger != null ? _savedBreakpoints : IdeSession.Load(_paths.Session).Breakpoints;

    void OnBreakpointsChanged(object? sender, BreakpointsChangedEventArgs e)
    {
        // Verdicts and hit counts (session threads) only repaint; user changes (UI thread) are saved too.
        Application.Post(() =>
        {
            foreach (var marks in _debugMarks.Values) marks.Invalidate();
            RefreshCommandState();
        });
        if (e.Kind == BreakpointChangeKind.StateChanged || _loadingBreakpoints || BreakpointKey is not { } key) return;
        _savedBreakpoints[key] = SerializeBreakpoints(_breakpoints.All);
        if (_breakpointSavePosted) return;
        _breakpointSavePosted = true;
        Application.Post(() => { _breakpointSavePosted = false; SaveSession(); });
    }

    static JsonArray SerializeBreakpoints(IEnumerable<SourceBreakpoint> breakpoints)
    {
        var list = new JsonArray();
        foreach (var b in breakpoints)
        {
            var o = new JsonObject { ["file"] = b.File, ["line"] = b.Line };
            if (!b.Enabled) o["enabled"] = false;
            if (b.Condition != null) o["condition"] = b.Condition;
            if (b.HitCondition != null) o["hitCondition"] = b.HitCondition;
            list.Add(o);
        }
        return list;
    }

    /// <summary>After a project (or solution) opened: its breakpoints from the session file.</summary>
    void RestoreBreakpoints()
    {
        _loadingBreakpoints = true;
        try
        {
            _breakpoints.Clear();
            if (BreakpointKey is not { } key || _savedBreakpoints[key] is not JsonArray list) return;
            foreach (var o in list.OfType<JsonObject>())
            {
                try
                {
                    if (o["file"]?.GetValue<string>() is not { Length: > 0 } file || o["line"]?.GetValue<int>() is not { } line || line < 1) continue;
                    _breakpoints.Add(file, line, o["condition"]?.GetValue<string>(), o["hitCondition"]?.GetValue<string>(), o["enabled"]?.GetValue<bool>() ?? true);
                }
                catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException) { }
            }
        }
        finally { _loadingBreakpoints = false; }
        _breakpointsWindow.Refresh();
    }

    /// <summary>The project closes: the session ends (the debuggee is killed) and its breakpoints are put away.</summary>
    void ResetDebugState()
    {
        if (_debugger == null) return;
        _ = _debugger.StopAsync();
        _loadingBreakpoints = true;
        try { _breakpoints.Clear(); }
        finally { _loadingBreakpoints = false; }
    }

    // ---- The controller's view of the IDE ----

    sealed class DebugHost(DesignerForm form) : IDebugHost
    {
        public void Output(string line) => form.Output(line);
        public void Status(string text) => form.sbMain.SimpleText = text;
        public Task<BuildResult?> BuildAsync() => form.BuildProjectAsync();
        public void CancelBuild() => form.CancelBuild();

        public DebugLaunchOptions LaunchOptionsFor(string outputFile)
        {
            var project = form._project!;
            var config = LaunchSettings.Load(project);
            var args = DebugController.SplitArguments(config.Arguments);
            bool dll = outputFile.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            return new DebugLaunchOptions
            {
                Program = dll ? "dotnet" : outputFile,
                Args = dll ? [outputFile, .. args] : [.. args],
                WorkingDirectory = config.ResolveWorkingDirectory(project),
                Environment = config.Environment.Count == 0 ? null : config.Environment.ToDictionary(p => p.Key, p => (string?)p.Value),
            };
        }

        public void OfferRunWithoutDebugging(string reason)
        {
            form.Output(reason);
            form.sbMain.SimpleText = "The debugger could not start";
            if (Dialogs.MessageDlg(reason + "\n\nRun without debugging (Ctrl+F5) instead?", MessageType.Warning, MessageButtons.YesNo, form) == ModalResult.Yes)
                _ = form.RunWithoutDebuggingAsync();
        }
    }
}
