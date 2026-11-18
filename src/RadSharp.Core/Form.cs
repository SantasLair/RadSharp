using RadSharp.Drawing;

namespace RadSharp;

/// <summary>
/// The root of a form: owns input routing, focus, the popup layer and repaint tracking.
/// Its width/height in the form file are the design size.
/// </summary>
public class Form : Control
{
    bool _dirtyAll = true;
    Rect? _dirty;
    string _caption = "";

    Control? _hover, _capture, _focused;
    MouseButton _captureButton;
    int _pressX, _pressY;
    Control? _dragSource, _dragAcceptedTarget;
    const int DragThreshold = 4;
    readonly List<Control> _popups = [];
    readonly List<Control?> _popupOwners = [];
    bool _showFocusCues;

    public Form()
    {
        SetDefaultSize(640, 480);
        Simulate = new InputSimulator(this);
        Theme = Theme.Current;
    }

    [Published(Category = "Appearance")]
    public string Caption { get => _caption; set { _caption = value; CaptionChanged?.Invoke(this); } }

    /// <summary>Raised when <see cref="Caption"/> changes, so a host can retitle its window.</summary>
    public event Action<Form>? CaptionChanged;

    /// <summary>The title a host gives the OS window (and taskbar button): <see cref="Caption"/>, or <see cref="Application.Title"/> when the caption is empty.</summary>
    public string WindowTitle => Caption.Length > 0 ? Caption : Application.Title;

    /// <summary>Background colour; null uses the theme's.</summary>
    [Published(Category = "Appearance")] public Color? Color { get; set; }

    /// <summary>The form's size as designed (at 100 %); set when the form is loaded.</summary>
    public int DesignWidth { get; set; }
    public int DesignHeight { get; set; }

    /// <summary>The UI scale factor lengths were multiplied by when the form was loaded (or <see cref="ApplyScale"/> was called).</summary>
    public double Scale { get; set; } = 1;

    public Theme Theme { get; set; }

    /// <summary>
    /// The scale generated <c>InitializeComponent()</c> code loads forms at. Hosts set it at startup
    /// (the SDL host uses the display's content scale).
    /// </summary>
    public static double DefaultScale { get; set; } = 1;

    /// <summary>
    /// For a form class generated from a form file: the root class the file was designed as (e.g. "Form").
    /// The writer saves that class name and compares against its defaults, not the generated subclass's.
    /// </summary>
    protected virtual string? DesignedClassName => null;

    internal string? DesignedClass => DesignedClassName;

    // =====================================================================
    // Window: state, size limits, start position, border
    // =====================================================================

    WindowState _windowState;
    FormStartPosition _startPosition;
    bool _resizable = true;

    /// <summary>
    /// Whether the window is normal, maximised or minimised. Setting it while the window is open asks
    /// the host to change it; when the user maximises or restores the window, the host writes the new
    /// state back here (and raises <see cref="WindowStateChanged"/>).
    /// </summary>
    [Published(Category = "Window")]
    public WindowState WindowState
    {
        get => _windowState;
        set
        {
            if (_windowState == value) return;
            _windowState = value;
            WindowStateChanged?.Invoke(this);
        }
    }

    /// <summary>Raised when <see cref="WindowState"/> changes, from code or because the user changed the window.</summary>
    public event Action<Form>? WindowStateChanged;

    /// <summary>
    /// Host callback: the window's state was changed by the user or the OS. Updates <see cref="WindowState"/>
    /// without asking the host to apply it again.
    /// </summary>
    public void NotifyWindowStateChanged(WindowState state) => WindowState = state;

    Rect? _restoredBounds;

    /// <summary>
    /// The window's last normal (not maximised, not minimised) bounds in screen coordinates, in form pixels (the same
    /// units as <see cref="Control.Left"/>/<see cref="Control.Top"/>/<see cref="Control.Width"/>/<see cref="Control.Height"/>
    /// for <see cref="FormStartPosition.Manual"/>). It stays available while the window is maximised, so an app can
    /// save it. Until the host reports anything it is the form's own bounds.
    /// </summary>
    public Rect RestoredBounds => _restoredBounds ?? new Rect(Left, Top, Width, Height);

    /// <summary>Raised when <see cref="RestoredBounds"/> changes because the host reported a new normal window rect.</summary>
    public event Action<Form>? RestoredBoundsChanged;

    /// <summary>Host callback: the window's normal rect (screen coordinates, form pixels) was set or changed.</summary>
    public void NotifyRestoredBounds(Rect bounds)
    {
        if (_restoredBounds == bounds) return;
        _restoredBounds = bounds;
        RestoredBoundsChanged?.Invoke(this);
    }

    /// <summary>
    /// Where the window first appears (Delphi's <c>Position</c>). Read when the window is created; changing it
    /// afterwards has no effect. <see cref="FormStartPosition.Manual"/> uses <see cref="Control.Left"/> and
    /// <see cref="Control.Top"/> (form pixels, screen coordinates).
    /// </summary>
    [Published(Category = "Window")]
    public FormStartPosition StartPosition { get => _startPosition; set => _startPosition = value; }

    /// <summary>
    /// Whether the user can resize the window by its border. Turn it off for fixed-size dialogs; the host
    /// applies later changes to an open window. The window's minimum size is <see cref="Control.MinWidth"/> and
    /// <see cref="Control.MinHeight"/> (form pixels); 0 means no minimum.
    /// </summary>
    [Published(Category = "Window")]
    public bool Resizable
    {
        get => _resizable;
        set => _resizable = value;
    }

    /// <summary>
    /// Asks the OS to draw the window's title bar dark (on Windows 11 in the form's background colour) to match a dark
    /// theme. Read when the window is created; hosts without such a feature ignore it.
    /// </summary>
    [Published(Category = "Window")] public bool DarkTitleBar { get; set; }

    FormBorderStyle _borderStyle;
    string _iconSource = "";
    Picture? _icon;
    bool _iconLoaded;

    /// <summary>
    /// The window's frame (Delphi's <c>BorderStyle</c>): <see cref="FormBorderStyle.Sizable"/> (the default),
    /// <c>Single</c>, <c>Dialog</c>, <c>Tool</c> or <c>None</c>. Only Sizable and Tool windows can be resized, and
    /// only while <see cref="Resizable"/> is true (see <see cref="IsUserResizable"/>). A modal form left at Sizable
    /// shows as a dialog (see <see cref="EffectiveBorderStyle"/>). The host applies later changes to an open window,
    /// except to and from <c>Tool</c>, which is read when the window is created.
    /// </summary>
    [Published(Category = "Window")]
    public FormBorderStyle BorderStyle { get => _borderStyle; set => _borderStyle = value; }

    /// <summary>
    /// The frame the host gives the window: <see cref="BorderStyle"/>, except that a modal form (<see cref="ShowModal"/>)
    /// left at <see cref="FormBorderStyle.Sizable"/> gets a dialog frame (no minimise or maximise button). Its
    /// resizing still follows <see cref="IsUserResizable"/>, so existing resizable dialogs stay resizable.
    /// </summary>
    public FormBorderStyle EffectiveBorderStyle =>
        IsModal && _borderStyle == FormBorderStyle.Sizable ? FormBorderStyle.Dialog : _borderStyle;

    /// <summary>Whether the user can resize the window by its border: <see cref="Resizable"/>, with a <c>Sizable</c> or <c>Tool</c> <see cref="BorderStyle"/>.</summary>
    public bool IsUserResizable => _resizable && _borderStyle is FormBorderStyle.Sizable or FormBorderStyle.Tool;

    /// <summary>
    /// Where the host places the window: <see cref="StartPosition"/>, except that a modal form left at
    /// <see cref="FormStartPosition.Default"/> is centred on its owner (<see cref="FormStartPosition.CenterOwner"/>).
    /// </summary>
    public FormStartPosition EffectiveStartPosition =>
        IsModal && _startPosition == FormStartPosition.Default ? FormStartPosition.CenterOwner : _startPosition;

    /// <summary>Keeps the window above all other (non-topmost) windows. The host applies later changes to an open window.</summary>
    [Published(Category = "Window")] public bool TopMost { get; set; }

    /// <summary>
    /// The window icon: a data URI, an embedded resource name or a file path, like <see cref="Image.PictureSource"/>.
    /// Empty uses the host's default icon.
    /// </summary>
    [Published("icon", Category = "Window")]
    public string IconSource
    {
        get => _iconSource;
        set { _iconSource = value ?? ""; _icon = null; _iconLoaded = false; }
    }

    /// <summary>The window icon: loaded from <see cref="IconSource"/>, or set from code. The host applies changes to an open window.</summary>
    public Picture? Icon
    {
        get
        {
            if (!_iconLoaded) { _icon = Picture.FromSource(_iconSource, GetType().Assembly); _iconLoaded = true; }
            return _icon;
        }
        set { _icon = value; _iconLoaded = true; }
    }

    /// <summary>
    /// The display the window opens on, for <see cref="FormStartPosition.Default"/> and
    /// <see cref="FormStartPosition.CenterScreen"/> (and <see cref="FormStartPosition.CenterOwner"/> without an owner):
    /// the window is centred on it, and maximised or full screen there if <see cref="WindowState"/> says so.
    /// <see cref="FormStartPosition.Manual"/> windows go where <see cref="Control.Left"/>/<see cref="Control.Top"/> say.
    /// Null (the default) leaves it to the host. Read when the window is created; ignored if the display is gone.
    /// </summary>
    public Display? StartDisplay { get; set; }

    /// <summary>
    /// The display the window is on now (the one with most of it); before the window opens, or after it closed, the one
    /// with most of <see cref="RestoredBounds"/>. Null if the host reports no displays.
    /// </summary>
    public Display? Display
    {
        get
        {
            var host = PlatformHost.Current;
            if (host == null) return null;
            return host.GetDisplayOf(this) ?? Screen.Nearest(host.GetDisplays(), RestoredBounds);
        }
    }

    /// <summary>
    /// Moves the window to <paramref name="display"/>, centred in its work area. A maximised or full-screen window
    /// stays so, on the new display. When the display has another scale the size is converted, so the window keeps its
    /// size in design units. If the window isn't open, it opens there (<see cref="StartDisplay"/>). Returns true if an
    /// open window was moved.
    /// </summary>
    public bool MoveToDisplay(Display display)
    {
        ArgumentNullException.ThrowIfNull(display);
        var r = RestoredBounds;
        double k = FollowsDisplayScale ? display.Scale / Scale : 1;
        var target = Screen.CenterIn(display.WorkArea, LayoutMath.Round(r.Width * k), LayoutMath.Round(r.Height * k));
        if (PlatformHost.Current?.MoveForm(this, target) == true) return true;
        StartDisplay = display;
        return false;
    }

    /// <summary>
    /// Whether the host rescales the form (<see cref="ChangeScale"/>) when its window moves to a display with another
    /// scale. On by default; it applies to forms that have a scale (loaded from a form file, <see cref="ApplyScale"/>,
    /// or <see cref="Scale"/> set by hand), not to code-built forms that were never scaled.
    /// </summary>
    public bool ScaleWithDisplay { get; set; } = true;

    /// <summary>True if the host should follow display scale changes for this form (see <see cref="ScaleWithDisplay"/>).</summary>
    public bool FollowsDisplayScale => ScaleWithDisplay && (_scaleApplied || Scale != 1);

    /// <summary>Raised when the host reports that the window moved to another display (see <see cref="Display"/>).</summary>
    public event Action<Form>? DisplayChanged;

    /// <summary>Host callback: the window is now mostly on another display.</summary>
    public void NotifyDisplayChanged() => DisplayChanged?.Invoke(this);

    /// <summary>Caption colour for the OS title bar (Windows 11), instead of the form colour. Read when the window is created.</summary>
    public Color? TitleBarColor { get; set; }

    /// <summary>Caption text colour for the OS title bar, instead of the theme text colour. Read when the window is created.</summary>
    public Color? TitleBarTextColor { get; set; }

    /// <summary>
    /// When true, the form sees key presses before the focused control (Delphi's <c>KeyPreview</c>): handle them in
    /// the form's <c>KeyDown</c>/<c>KeyUp</c> events (or by overriding <c>OnKeyDown</c>/<c>OnKeyUp</c>) and set
    /// <c>Handled</c> to stop them reaching the control. Not applied while a popup is open, or when the form itself
    /// has the focus (it gets the key anyway).
    /// </summary>
    [Published(Category = "Behavior")] public bool KeyPreview { get; set; }

    /// <summary>
    /// Scales every length in this form's tree (bounds, margins, padding, min/max sizes, a splitter's minimum
    /// size) by <paramref name="scale"/> once and records it as <see cref="Scale"/>, exactly as loading a form
    /// file does. Use it for forms built in code: lay the controls out at 100 %, add them all, then call this
    /// before the form is shown. <paramref name="scale"/> 0 uses <see cref="DefaultScale"/>. Controls added
    /// afterwards aren't scaled; give them <see cref="Control.Px"/> lengths or call <see cref="Control.ScaleLengths"/>
    /// on them first. Throws if the form was already scaled.
    /// </summary>
    public void ApplyScale(double scale = 0)
    {
        if (scale == 0) scale = DefaultScale;
        if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be positive.");
        if (_scaleApplied) throw new InvalidOperationException("The form's scale was already applied.");
        ApplyScaleCore(scale);
        PerformLayout();
    }

    /// <summary>
    /// Gives the form the scale <paramref name="scale"/> whether or not it is scaled yet (Delphi's <c>ChangeScale</c>):
    /// a dialog that should match its owner window calls <c>ChangeScale(owner.Scale)</c>, and the SDL host calls it
    /// when the window moves to a display with another scale. Every length (bounds, margins, padding, min/max sizes,
    /// anchor distances, a subclass's own lengths) is recomputed from the 100 % value it was scaled from, so the
    /// layout is the same as loading at <paramref name="scale"/> in the first place and 100 % → 150 % → 100 % gives
    /// back exactly the original layout. A length set from code since is in the old scale's pixels: it is converted
    /// to 100 % (<c>pixels / Scale</c>) and scaled from there, so it comes back exactly when the old scale returns.
    /// Open popups are closed. A form not scaled yet is scaled as by <see cref="ApplyScale"/>.
    /// <paramref name="scale"/> 0 uses <see cref="DefaultScale"/>. Raises <see cref="ScaleChanged"/> when the scale
    /// changes.
    /// </summary>
    public void ChangeScale(double scale = 0)
    {
        if (scale == 0) scale = DefaultScale;
        if (!(scale > 0) || !double.IsFinite(scale)) throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be positive.");
        if (!_scaleApplied)
        {
            // A form whose Scale was set by hand (built in pixels at that scale, like a message box) is at that scale.
            if (Scale == 1) { ApplyScale(scale); return; }
            _scaleApplied = true;
            DesignWidth = DesignWidth > 0 ? DesignWidth : LayoutMath.Round(Width / Scale);
            DesignHeight = DesignHeight > 0 ? DesignHeight : LayoutMath.Round(Height / Scale);
        }
        if (scale == Scale) return;
        var change = new ScaleChange(Scale, scale);
        CloseAllPopups();   // sized and placed at the old scale
        RescaleTree(change.From, change.To);
        Scale = scale;
        PerformLayout();
        InvalidateAll();
        ScaleChanged?.Invoke(this, change);
    }

    /// <summary>
    /// Raised after <see cref="ChangeScale"/> moved the form to another scale (for example because its window was
    /// dragged to a display with another scale), once the form was laid out again. <see cref="ScaleChange.Apply"/>
    /// rescales lengths the application keeps itself.
    /// </summary>
    public event Action<Form, ScaleChange>? ScaleChanged;

    /// <summary>True once the form has a scale (<see cref="ApplyScale"/>, <see cref="ChangeScale"/> or loading a form file).</summary>
    public bool IsScaled => _scaleApplied;

    /// <summary>The scaling step shared with the form-file reader (which may reload into the same form).</summary>
    internal void ApplyScaleCore(double scale)
    {
        _scaleApplied = true;
        DesignWidth = Width;
        DesignHeight = Height;
        ResetLengthScale();   // the tree is at 100 % now (a reload into the same form starts over)
        if (scale != 1) ScaleLengths(scale);
        Scale = scale;
    }

    bool _scaleApplied;

    /// <summary>Feeds synthetic input through the same code paths as real input (for tests).</summary>
    public InputSimulator Simulate { get; }

    /// <summary>Resizes the form (e.g. to its window) and lays it out.</summary>
    public void Resize(int width, int height)
    {
        SetBounds(Left, Top, width, height);
        PerformLayout();
    }

    /// <summary>Lays out the form and every open popup.</summary>
    public new void PerformLayout()
    {
        base.PerformLayout();
        foreach (var p in _popups) p.PerformLayout();
    }

    // =====================================================================
    // Time, text input area, tooltips
    // =====================================================================

    readonly List<Control> _tickers = [];
    // The hint under the pointer (control, region within it, text), when it was first seen, and whether a
    // click or key press dismissed it (it comes back only once the pointer moves to a different hint).
    Control? _tipControl;
    Rect _tipRegion;
    string _tipText = "";
    int _tipX, _tipY;
    bool _tipDismissed;
    double _tooltipStartTime;
    ToolTipPopup? _tooltipPopup;
    const double TooltipDelayMs = 1000;

    /// <summary>The host's clock in milliseconds, as of the last <see cref="ProcessTick"/>.</summary>
    public double Now { get; private set; }

    /// <summary>Registers a control for <see cref="Control.OnTick"/> calls (caret blink, timers).</summary>
    public void AddTicker(Control control)
    {
        if (!_tickers.Contains(control)) _tickers.Add(control);
    }

    public void RemoveTicker(Control control) => _tickers.Remove(control);

    List<Timer>? _timers;

    /// <summary>Called when controls are added or removed anywhere on the form.</summary>
    internal void TreeChanged() => _timers = null;

    /// <summary>Called by the host on every loop iteration with its monotonic clock.</summary>
    public void ProcessTick(double nowMs)
    {
        Now = nowMs;
        foreach (var t in _tickers.ToArray()) t.OnTick(nowMs);
        _timers ??= [.. Descendants().OfType<Timer>()];
        foreach (var t in _timers.ToArray()) t.Tick(nowMs);

        // Show the pending tooltip once the pointer has rested on its hint long enough.
        if (_tipText.Length > 0 && !_tipDismissed && _tooltipPopup == null && nowMs - _tooltipStartTime >= TooltipDelayMs)
            ShowTooltip();
    }

    /// <summary>Computes the next deadline (in ms) when the host should wake up: timer deadlines, tooltip delay, etc. Returns null if no deadline.</summary>
    public double? NextWakeMs(double nowMs)
    {
        double? nearest = null;

        // Check timer deadlines
        _timers ??= [.. Descendants().OfType<Timer>()];
        foreach (var t in _timers)
        {
            var deadline = t.GetNextDeadlineMs(nowMs);
            if (deadline.HasValue && (!nearest.HasValue || deadline.Value < nearest.Value))
                nearest = deadline.Value;
        }

        // Check tooltip deadline
        if (_tipText.Length > 0 && !_tipDismissed && _tooltipPopup == null)
        {
            var tooltipDeadline = _tooltipStartTime + TooltipDelayMs;
            if (!nearest.HasValue || tooltipDeadline < nearest.Value)
                nearest = tooltipDeadline;
        }

        return nearest;
    }

    /// <summary>
    /// Re-reads the hint under the pointer (<see cref="Control.GetHintAt"/>). A new control, region or text closes
    /// the tooltip and starts the delay again — or, if a tooltip was showing, shows the new one at once, as
    /// moving between toolbar buttons does. No hint, or a pressed mouse button, removes it.
    /// </summary>
    void UpdateTip(int x, int y)
    {
        var c = _capture == null ? _hover : null;
        string text = "";
        Rect region = default;
        if (c != null) text = c.GetHintAt(x - c.AbsoluteLeft, y - c.AbsoluteTop, out region) ?? "";
        if (text.Length == 0)
        {
            ClearTip();
            return;
        }
        _tipX = x;
        _tipY = y;
        if (c == _tipControl && region == _tipRegion && text == _tipText) return;
        bool wasShowing = _tooltipPopup != null;
        CloseTooltip();
        _tipControl = c;
        _tipRegion = region;
        _tipText = text;
        _tipDismissed = false;
        _tooltipStartTime = wasShowing ? Now - TooltipDelayMs : Now;
    }

    void ClearTip()
    {
        CloseTooltip();
        _tipControl = null;
        _tipText = "";
        _tipDismissed = false;
    }

    /// <summary>Closes the tooltip because of input (click, key, wheel, drag); it stays away until the hint changes.</summary>
    void DismissTooltip()
    {
        CloseTooltip();
        if (_tipText.Length > 0) _tipDismissed = true;
    }

    void ShowTooltip()
    {
        if (_tooltipPopup != null || _tipControl == null) return;
        var popup = ToolTipPopup.Create(this, _tipText);
        // Just below and to the right of the pointer; above it if there's no room underneath.
        int x = _tipX + Px(12);
        int y = _tipY + Px(20);
        if (y + popup.Height > Height) y = Math.Max(0, _tipY - popup.Height - Px(6));
        _tooltipPopup = popup;
        OpenPopup(popup, x, y, _tipControl);
    }

    void CloseTooltip()
    {
        if (_tooltipPopup is not { } popup) return;
        _tooltipPopup = null;
        ClosePopup(popup);
    }

    // =====================================================================
    // Windows: Show, ShowModal, Close
    // =====================================================================

    ModalResult _modalResult;

    /// <summary>The form that owns this one (a dialog's parent window).</summary>
    public Form? Owner { get; set; }

    public bool IsModal { get; private set; }

    /// <summary>Setting a result other than None on a modal form closes it (as a dialog button does).</summary>
    public ModalResult ModalResult
    {
        get => _modalResult;
        set
        {
            _modalResult = value;
            if (IsModal && value != ModalResult.None) Close();
        }
    }

    [Published("onCloseQuery")] public event CloseQueryEvent? CloseQuery;
    [Published("onClose")] public event NotifyEvent? Closed;
    [Published("onShow")] public event NotifyEvent? Shown;

    /// <summary>Shows the form in its own window and returns immediately.</summary>
    public void Show() => PlatformHost.Require().ShowForm(this);

    /// <summary>Queues an action to run on the UI thread (from any thread).</summary>
    public void Queue(Action action) => Application.Queue(action);

    /// <summary>Shows the form as a modal dialog synchronously; returns how it was closed (Cancel if closed from its title bar). Desktop-only.</summary>
    public ModalResult ShowModal()
    {
        _modalResult = ModalResult.None;
        IsModal = true;
        try { PlatformHost.Require().RunModal(this); }
        finally { IsModal = false; }
        return _modalResult;
    }

    /// <summary>Shows the form as a modal dialog asynchronously; returns how it was closed (Cancel if closed from its title bar).</summary>
    public Task<ModalResult> ShowModalAsync()
    {
        var tcs = new TaskCompletionSource<ModalResult>();
        _modalResult = ModalResult.None;
        IsModal = true;

        void OnClosed(Control _)
        {
            Closed -= OnClosed;
            IsModal = false;
            tcs.SetResult(_modalResult);
        }

        Closed += OnClosed;
        PlatformHost.Require().ShowModal(this, () => { });
        return tcs.Task;
    }

    /// <summary>Asks <see cref="CloseQuery"/> handlers, then closes the window. Returns false if a handler refused.</summary>
    public bool Close()
    {
        var e = new CloseQueryEventArgs();
        CloseQuery?.Invoke(this, e);
        if (!e.CanClose) return false;
        if (IsModal && _modalResult == ModalResult.None) _modalResult = ModalResult.Cancel;
        CloseAllPopups();
        PlatformHost.Current?.CloseForm(this);
        Closed?.Invoke(this);
        return true;
    }

    /// <summary>Called by the host once the form's window is visible.</summary>
    public void NotifyShown() => Shown?.Invoke(this);

    /// <summary>
    /// Where text is being entered (form pixels): the host positions the IME candidate window there.
    /// Set by the focused text control; null when no text control has focus.
    /// </summary>
    public Rect? TextInputArea { get; set; }

    protected override void Paint(Canvas canvas) =>
        canvas.FillRect(new Rect(0, 0, Width, Height), Color ?? canvas.Theme.FormBackground);


    // =====================================================================
    // Repaint tracking (form coordinates)
    // =====================================================================

    public void InvalidateAll() => _dirtyAll = true;

    public void InvalidateRect(Rect r)
    {
        if (_dirtyAll || r.Width <= 0 || r.Height <= 0) return;
        _dirty = _dirty is { } d ? Union(d, r) : r;
    }

    /// <summary>Returns the area needing a repaint since the last call, clipped to the form, and clears it.</summary>
    public bool TryTakeDirtyRect(out Rect rect)
    {
        var full = new Rect(0, 0, Width, Height);
        Rect? result = _dirtyAll ? full : _dirty is { } d ? Intersect(d, full) : null;
        _dirtyAll = false;
        _dirty = null;
        rect = result ?? default;
        return result is { Width: > 0, Height: > 0 };
    }

    static Rect Union(Rect a, Rect b)
    {
        int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new Rect(x, y, Math.Max(a.X + a.Width, b.X + b.Width) - x, Math.Max(a.Y + a.Height, b.Y + b.Height) - y);
    }

    static Rect? Intersect(Rect a, Rect b)
    {
        int x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        int r = Math.Min(a.X + a.Width, b.X + b.Width), bt = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return r > x && bt > y ? new Rect(x, y, r - x, bt - y) : null;
    }

    // =====================================================================
    // Focus (spec, Appendix C)
    // =====================================================================

    /// <summary>The control with keyboard focus, or null.</summary>
    public Control? FocusedControl => _focused;

    /// <summary>
    /// True after keyboard navigation (Tab), false after a mouse press: controls draw focus rings
    /// only while this is set.
    /// </summary>
    public bool ShowFocusCues
    {
        get => _showFocusCues;
        private set { if (_showFocusCues == value) return; _showFocusCues = value; _focused?.Invalidate(); }
    }

    /// <summary>Moves focus to <paramref name="control"/> (null clears it). Returns false if it can't take focus.</summary>
    public bool SetFocus(Control? control)
    {
        if (control != null && (control.OwningForm != this || !control.IsEffectivelyVisible || !control.IsEffectivelyEnabled))
            return false;
        if (control == _focused) return true;
        var old = _focused;
        _focused = control;
        old?.OnLostFocus();
        control?.OnGotFocus();
        return true;
    }

    /// <summary>Visible, enabled tab stops in Tab order: depth-first; among siblings, tabOrder ≥ 0 ascending, then -1 in child order.</summary>
    public List<Control> GetTabOrderList()
    {
        var list = new List<Control>();
        Collect(this, list);
        return list;

        static void Collect(Control parent, List<Control> list)
        {
            var ordered = parent.Children
                .Select((c, i) => (c, i))
                .OrderBy(t => t.c.TabOrder < 0 ? 1 : 0)
                .ThenBy(t => t.c.TabOrder < 0 ? t.i : t.c.TabOrder)
                .ThenBy(t => t.i);
            foreach (var (c, _) in ordered)
            {
                if (!c.IsShown || !c.Enabled) continue;
                if (c.IncludeInTabOrder) list.Add(c);
                Collect(c, list);
            }
        }
    }

    /// <summary>Moves focus to the next (or previous) tab stop, wrapping around.</summary>
    public void SelectNext(bool forward = true)
    {
        var list = GetTabOrderList();
        if (list.Count == 0) return;
        int i = _focused == null ? -1 : list.IndexOf(_focused);
        int next = i < 0 ? (forward ? 0 : list.Count - 1) : (i + (forward ? 1 : -1) + list.Count) % list.Count;
        SetFocus(list[next]);
    }

    /// <summary>
    /// Hiding, disabling or removing a control clears hover, capture and focus inside it, and closes
    /// popups it (or anything inside it) opened or contains.
    /// </summary>
    internal void ControlBecameUnavailable(Control control)
    {
        if (_hover != null && _hover.IsSelfOrDescendantOf(control)) { var h = _hover; _hover = null; h.SetHovered(false); }
        if (_capture != null && _capture.IsSelfOrDescendantOf(control)) _capture = null;
        if (_focused != null && _focused.IsSelfOrDescendantOf(control)) SetFocus(null);
        if (_tipControl != null && _tipControl.IsSelfOrDescendantOf(control)) ClearTip();
        for (int i = _popups.Count - 1; i >= 0 && i < _popups.Count; i--)
        {
            var owner = _popupOwners[i];
            if (_popups[i].IsSelfOrDescendantOf(control) || (owner != null && owner.IsSelfOrDescendantOf(control)))
                ClosePopupAt(i);
        }
    }

    // =====================================================================
    // Popup layer
    // =====================================================================

    /// <summary>Open popups, bottom to top.</summary>
    public IReadOnlyList<Control> Popups => _popups;

    public bool IsPopupOpen(Control popup) => _popups.Contains(popup);

    /// <summary>
    /// Shows <paramref name="popup"/> above the form at (<paramref name="x"/>, <paramref name="y"/>) in form
    /// coordinates, moved if needed to fit inside the form. <paramref name="owner"/> is the control that
    /// opened it; hiding, disabling or removing the owner closes the popup.
    /// </summary>
    public void OpenPopup(Control popup, int x, int y, Control? owner = null)
    {
        if (popup.Parent != null) throw new InvalidOperationException("A popup must not have a parent.");
        // The tooltip stays the topmost popup: closing it closes every popup above it, so a popup opened from
        // code (a timer, a posted callback) while it shows would otherwise vanish with it.
        if (popup != _tooltipPopup) DismissTooltip();
        if (_popups.Contains(popup)) ClosePopup(popup);
        x = Math.Max(0, Math.Min(x, Width - popup.Width));
        y = Math.Max(0, Math.Min(y, Height - popup.Height));
        popup.PopupForm = this;
        popup.SetBounds(x, y, popup.Width, popup.Height);
        _popups.Add(popup);
        _popupOwners.Add(owner);
        popup.PerformLayout();
        InvalidateAll();
    }

    /// <summary>Closes <paramref name="popup"/> and every popup opened above it.</summary>
    public void ClosePopup(Control popup)
    {
        int i = _popups.IndexOf(popup);
        if (i >= 0) ClosePopupAt(i);
    }

    public void CloseAllPopups()
    {
        if (_popups.Count > 0) ClosePopupAt(0);
    }

    void ClosePopupAt(int index)
    {
        for (int i = _popups.Count - 1; i >= index; i--)
        {
            var p = _popups[i];
            _popups.RemoveAt(i);
            _popupOwners.RemoveAt(i);
            if (p == _tooltipPopup) _tooltipPopup = null;
            if (_hover != null && _hover.IsSelfOrDescendantOf(p)) { var h = _hover; _hover = null; h.SetHovered(false); }
            if (_capture != null && _capture.IsSelfOrDescendantOf(p)) _capture = null;
            if (_focused != null && _focused.IsSelfOrDescendantOf(p)) SetFocus(null);
            p.PopupForm = null;
            p.OnPopupClosed();
        }
        InvalidateAll();
    }

    // =====================================================================
    // Input routing (spec, Appendix C). Coordinates are form pixels.
    // =====================================================================

    /// <summary>The cursor for the control under the pointer (or the one capturing the mouse); while an
    /// in-app drag is on, it instead shows whether the current position would accept a drop.</summary>
    public Cursor CurrentCursor => _dragSource != null
        ? (_dragAcceptedTarget != null ? Cursor.Default : Cursor.NotAllowed)
        : (_capture ?? _hover)?.Cursor ?? Cursor.Default;

    /// <summary>The deepest control at a form point: open popups first (topmost first), then the form.</summary>
    public Control HitTestForm(int x, int y)
    {
        for (int i = _popups.Count - 1; i >= 0; i--)
        {
            var p = _popups[i];
            if (p == _tooltipPopup) continue;   // a tooltip never takes the pointer
            if (x >= p.Left && y >= p.Top && x < p.Left + p.Width && y < p.Top + p.Height)
                return p.HitTest(x - p.Left, y - p.Top);
        }
        return HitTest(x, y);
    }

    bool InAnyPopup(Control c) => _popups.Any(p => p != _tooltipPopup && c.IsSelfOrDescendantOf(p));

    static MouseEventArgs Local(Control c, int x, int y, MouseButton b, KeyModifiers m, int clicks = 1, PointerKind pointer = PointerKind.Mouse) =>
        new(x - c.AbsoluteLeft, y - c.AbsoluteTop, b, m, clicks, pointer);

    void UpdateHover(int x, int y)
    {
        var hit = HitTestForm(x, y);
        // While the mouse is captured, only the capturing control can be hovered.
        Control? hover = _capture == null ? hit : hit.IsSelfOrDescendantOf(_capture) ? _capture : null;
        if (hover != null && !hover.IsEffectivelyEnabled) hover = null;
        if (hover == _hover) return;
        var old = _hover;
        _hover = hover;
        old?.SetHovered(false);
        hover?.SetHovered(true);
    }

    public void ProcessMouseMove(int x, int y, KeyModifiers modifiers = KeyModifiers.None, PointerKind pointer = PointerKind.Mouse)
    {
        SyncAlt(modifiers);
        ProcessMouseMoveCore(x, y, modifiers, pointer);
    }

    void ProcessMouseMoveCore(int x, int y, KeyModifiers modifiers, PointerKind pointer)
    {
        PerformLayout();
        if (_dragSource != null) { _dragAcceptedTarget = BubbleDragOver(_dragSource, x, y); return; }
        UpdateHover(x, y);
        UpdateTip(x, y);
        var target = _capture ?? _hover;
        if (target == null || !target.IsEffectivelyEnabled) return;
        // Automatic drag: once the press moves past a small threshold, start dragging instead of
        // delivering further ordinary moves to the source (Delphi's dmAutomatic).
        if (_capture != null && _capture.DragMode == DragMode.Automatic
            && (Math.Abs(x - _pressX) > DragThreshold || Math.Abs(y - _pressY) > DragThreshold))
        {
            BeginDrag(_capture);
            _dragAcceptedTarget = BubbleDragOver(_dragSource!, x, y);
            return;
        }
        target.OnMouseMove(Local(target, x, y, MouseButton.Left, modifiers, 1, pointer));
    }

    /// <summary>Starts an in-app drag from <paramref name="source"/> (Delphi's <c>BeginDrag</c>): further
    /// mouse movement raises <see cref="Control.OnDragOver"/> on whatever's under the pointer instead of
    /// the source's own <c>OnMouseMove</c>, until the button is released over a control that accepted it.
    /// Call this from a control's own input handling for <see cref="DragMode.Manual"/>; automatic mode
    /// calls it for you.</summary>
    public void BeginDrag(Control source)
    {
        if (_dragSource != null) return;
        DismissTooltip();
        _dragSource = source;
        _dragAcceptedTarget = null;
    }

    /// <summary>Bubbles <c>OnDragOver</c> from the control under (x, y) up through its parents (like
    /// <see cref="ProcessMouseWheel"/>) until one accepts or handles it. Returns the accepting control, if
    /// any. Shared by in-app dragging and <see cref="ProcessFilesDropped"/>, so one control's <c>OnDragOver</c>
    /// can handle both the same way — check <c>e.IsFileDrop</c> to tell them apart.</summary>
    Control? BubbleDragOver(Control source, int x, int y, IReadOnlyList<string>? files = null)
    {
        for (Control? c = HitTestForm(x, y); c != null; c = c.Parent)
        {
            if (!c.IsEffectivelyEnabled) continue;
            var e = new DragEventArgs(source, x - c.AbsoluteLeft, y - c.AbsoluteTop) { Files = files };
            c.OnDragOver(e);
            if (e.Accept) return c;
            if (e.Handled) break;
        }
        return null;
    }

    public void ProcessMouseDown(int x, int y, MouseButton button, KeyModifiers modifiers = KeyModifiers.None, int clicks = 1, PointerKind pointer = PointerKind.Mouse)
    {
        _altHeld = (modifiers & KeyModifiers.Alt) != 0;   // the host reports the real modifier state: heals a missed Alt release
        ProcessMouseDownCore(x, y, button, modifiers, clicks, pointer);
        UpdateAccelerators();
    }

    void ProcessMouseDownCore(int x, int y, MouseButton button, KeyModifiers modifiers, int clicks, PointerKind pointer)
    {
        _altAlone = false;
        PerformLayout();
        // A click is never swallowed by a tooltip: it closes it (the tooltip isn't a "press outside" popup).
        DismissTooltip();
        var hit = HitTestForm(x, y);

        // A press outside every popup closes them all and is consumed.
        if (_popups.Count > 0 && !InAnyPopup(hit))
        {
            CloseAllPopups();
            UpdateHover(x, y);
            return;
        }
        if (_capture != null) return;  // another button is already down
        if (!hit.IsEffectivelyEnabled) return;

        ShowFocusCues = false;
        if (hit.TabStop) SetFocus(hit);
        _capture = hit;
        _captureButton = button;
        _pressX = x; _pressY = y;
        UpdateHover(x, y);
        hit.OnMouseDown(Local(hit, x, y, button, modifiers, clicks, pointer));

        // Context menus open after the press was delivered (so a list selects the row first).
        if (button == MouseButton.Right)
        {
            for (Control? c = hit; c != null; c = c.Parent)
            {
                if (c.ContextMenu is not { } menu) continue;
                _capture = null;
                menu.Popup(this, x, y, c);
                break;
            }
        }
    }

    public void ProcessMouseUp(int x, int y, MouseButton button, KeyModifiers modifiers = KeyModifiers.None, PointerKind pointer = PointerKind.Mouse)
    {
        PerformLayout();
        if (_dragSource != null)
        {
            if (button != _captureButton) return;
            var source = _dragSource;
            var accepted = _dragAcceptedTarget;
            _dragSource = null;
            _dragAcceptedTarget = null;
            _capture = null;
            accepted?.OnDragDrop(new DragEventArgs(source, x - accepted.AbsoluteLeft, y - accepted.AbsoluteTop));
            UpdateHover(x, y);
            return;
        }
        if (_capture == null || button != _captureButton) return;
        var target = _capture;
        _capture = null;
        target.OnMouseUp(Local(target, x, y, button, modifiers, 1, pointer));
        // A click fires on a left release only if the pointer is still over the captured control.
        if (button == MouseButton.Left && target.OwningForm == this && target.IsEffectivelyEnabled
            && HitTestForm(x, y).IsSelfOrDescendantOf(target))
            target.OnClick();
        UpdateHover(x, y);
    }

    /// <summary>Wheel events bubble from the control under the pointer up through its parents until handled.</summary>
    public void ProcessMouseWheel(int x, int y, float deltaX, float deltaY, KeyModifiers modifiers = KeyModifiers.None)
    {
        PerformLayout();
        DismissTooltip();
        for (Control? c = HitTestForm(x, y); c != null; c = c.Parent)
        {
            if (!c.IsEffectivelyEnabled) continue;
            var e = new MouseWheelEventArgs(x - c.AbsoluteLeft, y - c.AbsoluteTop, deltaX, deltaY, modifiers);
            c.OnMouseWheel(e);
            if (e.Handled) break;
        }
    }

    // ---- Mnemonics (spec, Appendix C) ----

    bool _altHeld, _accelShown;

    /// <summary>
    /// True while mnemonic letters are drawn underlined: Alt is held, or the menu bar is in keyboard mode
    /// (after a lone Alt or F10). Only meaningful while <see cref="Mnemonics.Enabled"/>.
    /// </summary>
    public bool ShowAccelerators => _altHeld || FindMainMenu(this) is { InMenuMode: true };

    /// <summary>A pointer event carries the real modifier state: if Alt is no longer down (its key-up went to another window), forget it.</summary>
    void SyncAlt(KeyModifiers modifiers)
    {
        if (!_altHeld || (modifiers & KeyModifiers.Alt) != 0) return;
        _altHeld = false;
        _altAlone = false;
        UpdateAccelerators();
    }

    /// <summary>
    /// The host's window lost keyboard focus (Alt+Tab, a modal window opened by a mnemonic): its key-ups will go elsewhere,
    /// so forget Alt (stops stuck underlines and a stale lone-Alt menu toggle).
    /// </summary>
    public void ProcessFocusLost()
    {
        _altHeld = false;
        _altAlone = false;
        UpdateAccelerators();
    }

    void UpdateAccelerators()
    {
        bool now = ShowAccelerators;
        if (now == _accelShown) return;
        _accelShown = now;
        InvalidateAll();
    }

    /// <summary>
    /// Alt+<paramref name="key"/> (a letter or digit): finds the shown, enabled controls on this form (only the
    /// active page of a page control) whose caption has that mnemonic, in tab order. One match is activated; several
    /// take focus in turn without activating (the next after the one holding focus). Menu bar access keys are tried
    /// first (<see cref="ProcessKeyDown"/> offers them as shortcuts before this runs), as in Windows Forms and the VCL.
    /// Returns true if a control took the key.
    /// </summary>
    public bool ProcessMnemonic(Key key)
    {
        if (!Mnemonics.Enabled || MainMenu.LetterOf(key) is not { } letter) return false;
        PerformLayout();
        var matches = new List<Control>();
        CollectMnemonics(this, letter, matches);
        if (matches.Count == 0) return false;
        ShowFocusCues = true;
        if (matches.Count == 1) { matches[0].ActivateMnemonic(); return true; }
        int at = _focused == null ? -1 : matches.FindIndex(c => c.HoldsMnemonicFocus(_focused));
        matches[(at + 1) % matches.Count].FocusMnemonic();
        return true;
    }

    static void CollectMnemonics(Control parent, char letter, List<Control> list)
    {
        var ordered = parent.Children
            .Select((c, i) => (c, i))
            .OrderBy(t => t.c.TabOrder < 0 ? 1 : 0)
            .ThenBy(t => t.c.TabOrder < 0 ? t.i : t.c.TabOrder)
            .ThenBy(t => t.i);
        foreach (var (c, _) in ordered)
        {
            if (!c.Visible || !c.Enabled) continue;
            // A page-control tab is reachable while its sheet is not the shown one; everything else must be shown.
            bool shown = c.IsShown;
            if (!shown && c is not TabSheet) continue;
            if (c.MnemonicCaption is { } caption && Mnemonics.KeyOf(caption) == letter && c.CanTakeMnemonic) list.Add(c);
            if (shown) CollectMnemonics(c, letter, list);
        }
    }

    // Alt pressed and released with nothing in between toggles the menu bar's keyboard mode.
    bool _altAlone;

    /// <summary>The first shown <see cref="MainMenu"/> in the control tree, or null.</summary>
    MainMenu? FindMainMenu(Control c)
    {
        foreach (var child in c.Children)
        {
            if (!child.IsShown) continue;
            if (child is MainMenu m) return m;
            if (FindMainMenu(child) is { } found) return found;
        }
        return null;
    }

    public void ProcessKeyDown(Key key, KeyModifiers modifiers = KeyModifiers.None, bool repeat = false)
    {
        _altHeld = key == Key.Alt || (modifiers & KeyModifiers.Alt) != 0;
        ProcessKeyDownCore(key, modifiers, repeat);
        UpdateAccelerators();
    }

    void ProcessKeyDownCore(Key key, KeyModifiers modifiers, bool repeat)
    {
        if (key == Key.Alt) _altAlone = !repeat || _altAlone;
        else _altAlone = false;
        PerformLayout();
        DismissTooltip();
        var e = new KeyEventArgs(key, modifiers, repeat);
        if (KeyPreview && _popups.Count == 0 && _focused != null && _focused != this)
        {
            OnKeyDown(e);
            if (e.Handled) return;
        }
        bool plainTab = key == Key.Tab && (modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Meta)) == 0;

        // Tab closes all popups, then moves focus (unless the focused control wants tabs).
        if (plainTab && !(_popups.Count == 0 && _focused is { AcceptsTab: true }))
        {
            CloseAllPopups();
            ShowFocusCues = true;
            SelectNext(forward: !e.Shift);
            return;
        }

        // While a popup is open, keys go to the topmost popup; Escape closes it if unhandled.
        if (_popups.Count > 0)
        {
            var top = _popups[^1];
            var target = _focused != null && _focused.IsSelfOrDescendantOf(top) ? _focused : top;
            target.OnKeyDown(e);
            if (!e.Handled && key == Key.Escape) ClosePopup(top);
            return;
        }

        var focusTarget = _focused ?? this;
        if (focusTarget.IsEffectivelyEnabled) focusTarget.OnKeyDown(e);
        if (e.Handled) return;

        // Menu bar access keys and shortcuts come first (see ProcessMnemonic for the precedence), then control mnemonics.
        if (IsShortcutCandidate(e) && OfferShortcut(this, e)) return;
        if (modifiers == KeyModifiers.Alt && ProcessMnemonic(key)) { e.Handled = true; return; }
        if (key is Key.Enter or Key.Escape && modifiers == KeyModifiers.None) OfferDialogKey(this, e);
    }

    public void ProcessKeyUp(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        _altHeld = key != Key.Alt && (modifiers & KeyModifiers.Alt) != 0;
        ProcessKeyUpCore(key, modifiers);
        UpdateAccelerators();
    }

    void ProcessKeyUpCore(Key key, KeyModifiers modifiers)
    {
        DismissTooltip();
        if (key == Key.Alt && _altAlone)
        {
            _altAlone = false;
            if (FindMainMenu(this) is { } bar) { bar.ToggleMenuMode(); return; }
        }
        var e = new KeyEventArgs(key, modifiers);
        if (KeyPreview && _popups.Count == 0 && _focused != null && _focused != this)
        {
            OnKeyUp(e);
            if (e.Handled) return;
        }
        var target = _popups.Count > 0 ? _popups[^1] : _focused ?? this;
        if (target.IsEffectivelyEnabled) target.OnKeyUp(e);
    }

    /// <summary>Typed text goes to the focused control; it is discarded while a popup is open.</summary>
    public void ProcessTextInput(string text)
    {
        DismissTooltip();
        if (string.IsNullOrEmpty(text) || TextTarget() is not { } target) return;
        if (target.IsEffectivelyEnabled) target.OnTextInput(new TextInputEventArgs(text));
    }

    /// <summary>An IME composition update for the focused control (discarded while a popup is open).</summary>
    public void ProcessTextComposition(string text, int cursor, int selectionLength = 0)
    {
        DismissTooltip();
        if (TextTarget() is not { } target) return;
        if (target.IsEffectivelyEnabled) target.OnTextComposition(new TextCompositionEventArgs(text, cursor, selectionLength));
    }

    /// <summary>
    /// One or more files were dropped onto this form from the desktop (the host's file manager). Delivered
    /// through the same <see cref="Control.OnDragOver"/>/<see cref="Control.OnDragDrop"/> a control already
    /// uses for in-app drags (<see cref="DragEventArgs.Files"/> is set, <see cref="DragEventArgs.IsFileDrop"/>
    /// is true), bubbling from whatever's directly under the drop up to the form — so a specific control (an
    /// image viewer, say) can accept files dropped on *it* without hand-checking coordinates, and the form
    /// itself still sees the drop if nothing more specific wants it. The host calls this once per drop, even
    /// when the desktop delivered it as several files in a row.
    /// </summary>
    public void ProcessFilesDropped(IReadOnlyList<string> paths, int x, int y)
    {
        if (paths.Count == 0) return;
        PerformLayout();
        if (BubbleDragOver(this, x, y, paths) is { } target)
            target.OnDragDrop(new DragEventArgs(this, x - target.AbsoluteLeft, y - target.AbsoluteTop) { Files = paths });
    }

    /// <summary>
    /// Where typed text goes: the focused control; while popups are open, only a focused control inside
    /// the topmost popup (an inline editor), otherwise the text is discarded.
    /// </summary>
    Control? TextTarget()
    {
        if (_popups.Count == 0) return _focused ?? this;
        var top = _popups[^1];
        if (_focused == null) return null;
        return _focused.IsSelfOrDescendantOf(top) || (top.PopupTextTarget != null && _focused == top.PopupTextTarget) ? _focused : null;
    }

    static bool IsShortcutCandidate(KeyEventArgs e) =>
        (e.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Meta)) != 0
        || e.Key is >= Key.F1 and <= Key.F12;

    static bool OfferShortcut(Control c, KeyEventArgs e)
    {
        if (!c.IsShown || !c.Enabled) return false;
        if (c.ProcessShortcut(e)) { e.Handled = true; return true; }
        foreach (var child in c.Children)
            if (OfferShortcut(child, e)) return true;
        return false;
    }

    static bool OfferDialogKey(Control c, KeyEventArgs e)
    {
        if (!c.IsShown || !c.Enabled) return false;
        if (c.ProcessDialogKey(e)) { e.Handled = true; return true; }
        foreach (var child in c.Children)
            if (OfferDialogKey(child, e)) return true;
        return false;
    }
}

/// <summary>Synthetic input for tests; it goes through exactly the same routines as real input.</summary>
public sealed class InputSimulator(Form form)
{
    public void MoveTo(int x, int y, KeyModifiers modifiers = KeyModifiers.None, PointerKind pointer = PointerKind.Mouse) =>
        form.ProcessMouseMove(x, y, modifiers, pointer);

    public void Press(int x, int y, MouseButton button = MouseButton.Left, KeyModifiers modifiers = KeyModifiers.None, int clicks = 1,
        PointerKind pointer = PointerKind.Mouse)
    {
        form.ProcessMouseMove(x, y, modifiers, pointer);
        form.ProcessMouseDown(x, y, button, modifiers, clicks, pointer);
    }

    /// <summary>A finger tap: what the host delivers for a touch (move, left press, left release), flagged <see cref="PointerKind.Touch"/>.</summary>
    public void Tap(int x, int y, PointerKind pointer = PointerKind.Touch) => Click(x, y, pointer: pointer);

    /// <summary>A double click: two press/release pairs, the second reporting two clicks.</summary>
    public void DoubleClick(int x, int y)
    {
        Click(x, y);
        Press(x, y, clicks: 2);
        Release(x, y);
    }

    /// <summary>An IME composition update, e.g. <c>Compose("にほ", 2)</c>; <c>Compose("", 0)</c> ends it.</summary>
    public void Compose(string text, int cursor) => form.ProcessTextComposition(text, cursor);

    public void Release(int x, int y, MouseButton button = MouseButton.Left, KeyModifiers modifiers = KeyModifiers.None,
        PointerKind pointer = PointerKind.Mouse) =>
        form.ProcessMouseUp(x, y, button, modifiers, pointer);

    public void Click(int x, int y, MouseButton button = MouseButton.Left, KeyModifiers modifiers = KeyModifiers.None,
        PointerKind pointer = PointerKind.Mouse)
    {
        Press(x, y, button, modifiers, pointer: pointer);
        Release(x, y, button, modifiers, pointer);
    }

    /// <summary>Clicks the centre of a control.</summary>
    public void Click(Control control, MouseButton button = MouseButton.Left, PointerKind pointer = PointerKind.Mouse)
    {
        form.PerformLayout();
        Click(control.AbsoluteLeft + control.Width / 2, control.AbsoluteTop + control.Height / 2, button, pointer: pointer);
    }

    public void Drag(int x1, int y1, int x2, int y2, int steps = 4, MouseButton button = MouseButton.Left, PointerKind pointer = PointerKind.Mouse)
    {
        Press(x1, y1, button, pointer: pointer);
        for (int i = 1; i <= steps; i++)
            form.ProcessMouseMove(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps, KeyModifiers.None, pointer);
        Release(x2, y2, button, pointer: pointer);
    }

    public void Wheel(int x, int y, float deltaY, float deltaX = 0) => form.ProcessMouseWheel(x, y, deltaX, deltaY);

    /// <summary>Presses a key and keeps it down (see <see cref="KeyUp"/>); e.g. <c>KeyDown(Key.Alt, KeyModifiers.Alt)</c> shows mnemonic underlines.</summary>
    public void KeyDown(Key key, KeyModifiers modifiers = KeyModifiers.None) => form.ProcessKeyDown(key, modifiers);

    /// <summary>Releases a key pressed with <see cref="KeyDown"/>.</summary>
    public void KeyUp(Key key, KeyModifiers modifiers = KeyModifiers.None) => form.ProcessKeyUp(key, modifiers);

    public void Key(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        form.ProcessKeyDown(key, modifiers);
        form.ProcessKeyUp(key, modifiers);
    }

    /// <summary>Presses a shortcut given as text, e.g. <c>"Mod+S"</c>.</summary>
    public void Key(string shortcut)
    {
        var s = Shortcut.Parse(shortcut);
        Key(s.Key, s.Modifiers);
    }

    public void Type(string text) => form.ProcessTextInput(text);
}
