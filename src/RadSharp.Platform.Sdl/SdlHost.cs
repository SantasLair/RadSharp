using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RadSharp.Diagnostics;
using RadSharp.Drawing;
using RadSharp.Threading;
using SDL;
using SkiaSharp;
using static SDL.SDL3;

namespace RadSharp.Platform.Sdl;

/// <summary>
/// Runs forms in SDL3 windows. Each frame is painted by Skia on the CPU into a pixel buffer, and
/// only the dirty region is uploaded to an SDL texture and presented. Any number of forms can be
/// open; <see cref="Form.ShowModal"/> runs a nested loop while other windows stay painted but blocked.
/// </summary>
public static unsafe class SdlHost
{
    static bool _initialized;
    static readonly Dictionary<Form, FormWindow> Windows = [];
    static readonly List<Form> ModalStack = [];
    static readonly ConcurrentQueue<Action> MainThread = new();
    static SynchronizationContext? _uiContext;

    /// <summary>
    /// Initializes SDL, installs the platform services, and sets <see cref="Form.DefaultScale"/> to
    /// <see cref="Scale"/>, so forms created afterwards load at the monitor's scale. Call before creating forms.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        // We draw IME composition text inline ourselves; the OS draws the candidate list.
        SDL_SetHint("SDL_IME_IMPLEMENTED_UI", "composition");
        // Without this, the click that activates an inactive window is swallowed rather than
        // delivered as a normal mouse-down, so a button needs a second click to fire.
        SDL_SetHint(SDL_HINT_MOUSE_FOCUS_CLICKTHROUGH, "1");
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
            throw new InvalidOperationException($"SDL_Init failed: {SDL_GetError()}");
        _initialized = true;
        Form.DefaultScale = Scale;
        RadSharp.Text.Clipboard.Current = new SdlClipboard();
        // RADSHARP_LIVE_RESIZE=0 turns live resizing off (for diagnosing).
        _liveResize = Environment.GetEnvironmentVariable("RADSHARP_LIVE_RESIZE") != "0";
        if (_liveResize) SDL_AddEventWatch(&OnEventWatch, 0);
        // RADSHARP_THEME=dark switches to dark mode; default is light.
        if (Environment.GetEnvironmentVariable("RADSHARP_THEME") == "dark")
            Theme.Current = Theme.Dark;

        // Install UI-thread synchronization context so await works in event handlers.
        _uiContext = new UiSynchronizationContext(PostToUiThread);
        SynchronizationContext.SetSynchronizationContext(_uiContext);

        PlatformHost.Current = new SdlPlatform();
    }

    /// <summary>
    /// The UI scale factor to load forms with: the primary monitor's content scale, or the
    /// RADSHARP_SCALE environment variable if set.
    /// </summary>
    public static double Scale
    {
        get
        {
            if (double.TryParse(Environment.GetEnvironmentVariable("RADSHARP_SCALE"),
                    System.Globalization.CultureInfo.InvariantCulture, out var forced) && forced > 0)
                return forced;
            Initialize();
            float s = SDL_GetDisplayContentScale(SDL_GetPrimaryDisplay());
            return s > 0 ? s : 1;
        }
    }

    /// <summary>
    /// Shows the main form and runs the event loop until it is closed; then closes everything. Returns
    /// <see cref="Application.ExitCode"/> (also stored in <see cref="Environment.ExitCode"/> when non-zero, so a void <c>Main</c> exits with it).
    /// </summary>
    public static int Run(Form mainForm)
    {
        _mainForm = mainForm;
        Initialize();
        ShowForm(mainForm, owner: null, modal: false);
        var screenshot = Environment.GetEnvironmentVariable("RADSHARP_SCREENSHOT");
        var resizeTo = Environment.GetEnvironmentVariable("RADSHARP_SCREENSHOT_RESIZE");   // "width,height" in points
        bool first = true;
        RunLoop(() =>
        {
            if (!string.IsNullOrEmpty(screenshot) && !first && Windows.TryGetValue(mainForm, out var w))
            {
                // Test hooks: RADSHARP_SCREENSHOT_RESIZE resizes the window and saves the back buffer at once,
                // without pumping events, so the picture shows what the event watch drew during the resize.
                if (resizeTo?.Split(',') is [var rw, var rh] && int.TryParse(rw, out var width) && int.TryParse(rh, out var height))
                    w.ResizeTo(width, height);
                w.SaveScreenshot(screenshot);
                CloseForm(mainForm);   // no CloseQuery prompts
            }
            first = false;
            return Windows.ContainsKey(mainForm);
        });
        foreach (var w in Windows.Values.ToList()) w.Dispose();
        Windows.Clear();
        PlatformHost.Current = null;
        if (_liveResize) SDL_RemoveEventWatch(&OnEventWatch, 0);
        SDL_Quit();
        _initialized = false;
        if (Application.ExitCode != 0) Environment.ExitCode = Application.ExitCode;
        return Application.ExitCode;
    }

    static bool _startupTraced;

    /// <summary>
    /// Opt-in start-up measurement: with RADSHARP_STARTUP_TRACE=1, the first presented frame prints
    /// "startup: N ms" (process start to first present) to stderr. Off by default; costs one flag check per frame.
    /// </summary>
    internal static void TraceFirstPresent()
    {
        if (_startupTraced) return;
        _startupTraced = true;
        if (Environment.GetEnvironmentVariable("RADSHARP_STARTUP_TRACE") != "1") return;
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Console.Error.WriteLine($"startup: {(DateTime.Now - self.StartTime).TotalMilliseconds:0} ms");
    }

    /// <summary>Posts an action to run on the UI thread during the next loop iteration.</summary>
    public static void PostToUiThread(Action action) => MainThread.Enqueue(action);

    /// <summary>Shows a form modally without blocking, calling onClosed when it closes.</summary>
    public static void ShowModalNonBlocking(Form form, Action onClosed)
    {
        var owner = form.Owner ?? ModalStack.LastOrDefault() ?? Windows.Keys.FirstOrDefault();
        ModalStack.Add(form);
        ShowForm(form, owner, modal: true);

        // Hook the Closed event to remove from modal stack and call onClosed
        void OnClosed(Control _)
        {
            form.Closed -= OnClosed;
            ModalStack.Remove(form);
            onClosed();
        }
        form.Closed += OnClosed;
    }

    internal static void ShowForm(Form form, Form? owner, bool modal)
    {
        if (Windows.TryGetValue(form, out var existing)) { existing.Raise(); return; }
        var ownerWindow = owner != null && Windows.TryGetValue(owner, out var ow) ? ow : null;
        var window = new FormWindow(form, ownerWindow, modal);
        Windows[form] = window;
        window.Show();
        form.NotifyShown();
    }

    internal static void RunModal(Form form)
    {
        var owner = form.Owner ?? ModalStack.LastOrDefault() ?? Windows.Keys.FirstOrDefault();
        ModalStack.Add(form);
        try
        {
            ShowForm(form, owner, modal: true);
            RunLoop(() => Windows.ContainsKey(form));
        }
        finally { ModalStack.Remove(form); }
    }

    internal static void CloseForm(Form form)
    {
        if (!Windows.Remove(form, out var window)) return;
        window.Dispose();
    }

    internal static bool OpenUrl(string url) => SDL_OpenURL(url);

    // ---- Monitors ----

    /// <summary>True when RADSHARP_SCALE forces the scale: forms then keep it on every monitor.</summary>
    internal static bool ScaleForced =>
        double.TryParse(Environment.GetEnvironmentVariable("RADSHARP_SCALE"), System.Globalization.CultureInfo.InvariantCulture, out var forced) && forced > 0;

    /// <summary>
    /// The monitors attached now, the primary one first. Bounds are SDL's (points) times the monitor's pixel density,
    /// so they are in the units of <see cref="Form.RestoredBounds"/> (on Windows both are pixels).
    /// </summary>
    internal static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        if (!_initialized) return list;
        var primary = SDL_GetPrimaryDisplay();
        using var ids = SDL_GetDisplays();
        for (int i = 0; ids != null && i < ids.Count; i++)
            if (MonitorFor(ids[i], primary) is { } d) list.Add(d);
        list.Sort((a, b) => b.IsPrimary.CompareTo(a.IsPrimary));   // stable enough: at most one primary
        return list;
    }

    internal static MonitorInfo? MonitorFor(SDL_DisplayID id, SDL_DisplayID primary)
    {
        if (id == 0) return null;
        SDL_Rect bounds, usable;
        if (!SDL_GetDisplayBounds(id, &bounds)) return null;
        if (!SDL_GetDisplayUsableBounds(id, &usable)) usable = bounds;
        float density = DensityOf(id);
        float scale = SDL_GetDisplayContentScale(id);
        static Rect R(SDL_Rect r, float k) => new((int)MathF.Round(r.x * k), (int)MathF.Round(r.y * k), (int)MathF.Round(r.w * k), (int)MathF.Round(r.h * k));
        return new MonitorInfo((uint)id, SDL_GetDisplayName(id) ?? $"Monitor {(uint)id}", R(bounds, density), R(usable, density),
            scale > 0 && float.IsFinite(scale) ? scale : 1, id == primary);
    }

    static float DensityOf(SDL_DisplayID id)
    {
        var mode = SDL_GetCurrentDisplayMode(id);
        return mode != null && mode->pixel_density > 0 ? mode->pixel_density : 1;
    }

    /// <summary>The usable bounds (points) of the monitor with this id, if it is still attached.</summary>
    internal static bool UsableBounds(uint id, out SDL_Rect area)
    {
        area = default;
        if (!_initialized) return false;
        SDL_Rect r;
        if (!SDL_GetDisplayUsableBounds((SDL_DisplayID)id, &r)) return false;
        area = r;
        return true;
    }

    /// <summary>The pixel density of the monitor a screen rect (in <see cref="GetMonitors"/> units) is on, to convert it to points.</summary>
    internal static float DensityAt(Rect rect)
    {
        var monitors = GetMonitors();
        return Screen.FromRectIn(monitors, rect) is { } d ? DensityOf((SDL_DisplayID)d.Id) : 1;
    }

    internal static MonitorInfo? MonitorOf(Form form) =>
        WindowOf(form) is { } w ? MonitorFor(w.DisplayId, SDL_GetPrimaryDisplay()) : null;

    internal static bool MoveForm(Form form, Rect bounds)
    {
        if (WindowOf(form) is not { } w) return false;
        w.MoveTo(bounds);
        return true;
    }

    // ---- Live resizing ----

    static bool _inWatch, _liveResize;

    /// <summary>How many frames were painted from inside the event watch (live resizing); for tests and diagnostics.</summary>
    public static int LiveFrames { get; private set; }

    /// <summary>
    /// Called by SDL as each event is generated — including from inside the OS's modal size/move loop
    /// (Windows) or live-resize tracking (macOS), when our own loop is blocked. For size changes and
    /// exposes we lay out and repaint the window right here, so forms reflow while the user drags their
    /// border (the same idea as Godot rendering from a timer inside the modal loop).
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static SDLBool OnEventWatch(nint userdata, SDL_Event* e)
    {
        var type = (SDL_EventType)e->type;
        if (type is not (SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED or SDL_EventType.SDL_EVENT_WINDOW_EXPOSED)) return true;
        if (_inWatch || !SDL_IsMainThread()) return true;
        _inWatch = true;
        try
        {
            var id = e->window.windowID;
            foreach (var w in Windows.Values)
            {
                if (w.Id != id || !w.CanPaintNow) continue;
                w.PaintNow();
                LiveFrames++;
                break;
            }
        }
        catch (Exception ex)
        {
            // An exception must not unwind into native code; the next normal frame repaints anyway.
            System.Diagnostics.Debug.WriteLine("RadSharp live resize: " + ex);
        }
        finally { _inWatch = false; }
        return true;   // watches can't drop events; the event still reaches the queue
    }

    /// <summary>
    /// Asks every open form to close (each may veto through CloseQuery): modal forms newest first, then the other windows,
    /// the main form last. Used for the OS quit request and for <see cref="Application.Terminate()"/>.
    /// </summary>
    static void CloseAllForms()
    {
        var order = new List<Form>(Enumerable.Reverse(ModalStack));
        foreach (var f in Windows.Keys)
            if (!order.Contains(f) && f != _mainForm) order.Add(f);
        if (_mainForm != null) order.Add(_mainForm);
        foreach (var f in order)
            if (Windows.ContainsKey(f)) f.Close();
    }

    static Form? _mainForm;

    /// <summary>Processes events for all windows until <paramref name="keepRunning"/> returns false.</summary>
    internal static void RunLoop(Func<bool> keepRunning)
    {
        SDL_Event e;
        bool idleWantsMore = false;
        while (keepRunning())
        {
            // Application.Terminate(): ask every form to close, like the OS quit request (CloseQuery may veto).
            if (Application.ConsumeTerminateRequest())
            {
                CloseAllForms();
                if (!keepRunning()) break;
            }
            // Compute next wake timeout from form deadlines (timers, tooltips, etc.)
            ulong now = SDL_GetTicks();
            int timeout = 16; // default 60 Hz
            double? nearest = null;
            foreach (var w in Windows.Values)
            {
                var deadline = w.Form.NextWakeMs(now);
                if (deadline.HasValue && (!nearest.HasValue || deadline.Value < nearest.Value))
                    nearest = deadline.Value;
            }
            if (nearest.HasValue)
                timeout = Math.Max(1, Math.Min(60000, (int)Math.Ceiling(nearest.Value - now)));
            if (idleWantsMore) timeout = 0;   // an Idle handler asked to be called again at once (Done = false)

            if (SDL_WaitEventTimeout(&e, timeout))
            {
                do Dispatch(ref e);
                while (SDL_PollEvent(&e));
            }
            while (MainThread.TryDequeue(out var action)) Guard.Run(action);
            // Input queue drained: Application.Idle (before painting, so what it changes is drawn in this pass).
            idleWantsMore = Application.RaiseIdle();
            now = SDL_GetTicks();
            foreach (var w in Windows.Values.ToList())
            {
                w.UpdateCursor();
                w.Tick(now);
                w.RenderIfDirty();
            }
        }
    }

    static void Dispatch(ref SDL_Event e)
    {
        var type = (SDL_EventType)e.type;
        if (type == SDL_EventType.SDL_EVENT_QUIT)
        {
            // Closing the last window, Cmd+Q...: ask every form, modal ones first.
            CloseAllForms();
            return;
        }
        if (!HasWindow(type)) return;
        var id = e.window.windowID;
        var window = Windows.Values.FirstOrDefault(w => w.Id == id);
        if (window == null) return;
        // While a modal form is open, other windows get no input (they still repaint).
        if (ModalStack.Count > 0 && window.Form != ModalStack[^1] && IsInput(type)) return;
        window.HandleEvent(e);
    }

    static bool HasWindow(SDL_EventType t) =>
        t is >= SDL_EventType.SDL_EVENT_WINDOW_FIRST and <= SDL_EventType.SDL_EVENT_WINDOW_LAST
          or SDL_EventType.SDL_EVENT_KEY_DOWN or SDL_EventType.SDL_EVENT_KEY_UP
          or SDL_EventType.SDL_EVENT_TEXT_EDITING or SDL_EventType.SDL_EVENT_TEXT_INPUT
          or SDL_EventType.SDL_EVENT_MOUSE_MOTION or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN
          or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP or SDL_EventType.SDL_EVENT_MOUSE_WHEEL
          or SDL_EventType.SDL_EVENT_DROP_BEGIN or SDL_EventType.SDL_EVENT_DROP_FILE or SDL_EventType.SDL_EVENT_DROP_COMPLETE;

    static bool IsInput(SDL_EventType t) =>
        t is SDL_EventType.SDL_EVENT_KEY_DOWN or SDL_EventType.SDL_EVENT_KEY_UP
          or SDL_EventType.SDL_EVENT_TEXT_EDITING or SDL_EventType.SDL_EVENT_TEXT_INPUT
          or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN or SDL_EventType.SDL_EVENT_MOUSE_WHEEL
          or SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED
          or SDL_EventType.SDL_EVENT_DROP_BEGIN or SDL_EventType.SDL_EVENT_DROP_FILE or SDL_EventType.SDL_EVENT_DROP_COMPLETE;

    internal static FormWindow? WindowOf(Form? form) => form != null && Windows.TryGetValue(form, out var w) ? w : null;

    // ---- File dialogs ----

    sealed class DialogRequest(Action<FileResult[]?> done, List<nint> allocations)
    {
        public readonly Action<FileResult[]?> Done = done;
        public readonly List<nint> Allocations = allocations;
    }

    internal static void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done)
    {
        var allocations = new List<nint>();
        nint Utf8(string s) { var p = Marshal.StringToCoTaskMemUTF8(s); allocations.Add(p); return p; }

        var filters = options.ParseFilter();
        var filterArray = (SDL_DialogFileFilter*)NativeMemory.Alloc((nuint)Math.Max(1, filters.Count), (nuint)sizeof(SDL_DialogFileFilter));
        allocations.Add((nint)filterArray);
        for (int i = 0; i < filters.Count; i++)
        {
            // SDL wants "png;jpg" (no "*."), and "*" for everything.
            var patterns = string.Join(";", filters[i].Patterns.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim() is "*.*" or "*" ? "*" : p.Trim().TrimStart('*').TrimStart('.')));
            filterArray[i] = new SDL_DialogFileFilter { name = (byte*)Utf8(filters[i].Name), pattern = (byte*)Utf8(patterns) };
        }

        var request = new DialogRequest(done, allocations);
        var handle = GCHandle.Alloc(request);
        var ownerWindow = WindowOf(owner);
        SDL_Window* window = ownerWindow != null ? ownerWindow.Handle : null;
        byte* location = options.InitialDirectory.Length > 0 || options.FileName.Length > 0
            ? (byte*)Utf8(options.FileName.Length > 0 ? Path.Combine(options.InitialDirectory, options.FileName) : options.InitialDirectory)
            : null;

        switch (kind)
        {
            case FileDialogKind.Open:
                SDL_ShowOpenFileDialog(&OnDialogDone, GCHandle.ToIntPtr(handle), window, filterArray, filters.Count, location, options.AllowMultiple);
                break;
            case FileDialogKind.Save:
                SDL_ShowSaveFileDialog(&OnDialogDone, GCHandle.ToIntPtr(handle), window, filterArray, filters.Count, location);
                break;
            case FileDialogKind.Folder:
                SDL_ShowOpenFolderDialog(&OnDialogDone, GCHandle.ToIntPtr(handle), window, location, options.AllowMultiple);
                break;
        }
    }

    /// <summary>SDL may call this on another thread; the result is handed to the UI thread.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnDialogDone(nint userdata, byte** files, int filter)
    {
        var handle = GCHandle.FromIntPtr(userdata);
        var request = (DialogRequest)handle.Target!;
        handle.Free();
        FileResult[]? result = null;
        if (files != null && files[0] != null)
        {
            var list = new List<FileResult>();
            for (int i = 0; files[i] != null; i++)
            {
                var path = Marshal.PtrToStringUTF8((nint)files[i]) ?? "";
                list.Add(new FileResult(path,
                    () => File.OpenRead(path),
                    () => File.Create(path)));
            }
            result = [.. list];
        }
        // The dialog is gone, so its filter strings can go: the first allocation is the
        // NativeMemory filter array, the rest are CoTaskMem UTF-8 strings.
        NativeMemory.Free((void*)request.Allocations[0]);
        foreach (var p in request.Allocations.Skip(1)) Marshal.FreeCoTaskMem(p);
        PostToUiThread(() => request.Done(result));
    }
}

/// <summary>The platform services RadSharp's core uses, backed by SDL.</summary>
internal sealed class SdlPlatform : IPlatformServices
{
    public void ShowForm(Form form) => SdlHost.ShowForm(form, form.Owner, modal: false);
    public void RunModal(Form form) => SdlHost.RunModal(form);
    public void CloseForm(Form form) => SdlHost.CloseForm(form);
    public void ProcessEventsUntil(Func<bool> done) => SdlHost.RunLoop(() => !done());
    public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done) =>
        SdlHost.ShowFileDialog(kind, owner, options, done);
    public void PostToUiThread(Action action) => SdlHost.PostToUiThread(action);
    public void ShowModal(Form form, Action onClosed) => SdlHost.ShowModalNonBlocking(form, onClosed);
    public bool OpenUrl(string url) => SdlHost.OpenUrl(url);
    public IReadOnlyList<MonitorInfo> GetMonitors() => SdlHost.GetMonitors();
    public MonitorInfo? GetMonitorOf(Form form) => SdlHost.MonitorOf(form);
    public bool MoveForm(Form form, Rect bounds) => SdlHost.MoveForm(form, bounds);
}

/// <summary>One SDL window showing one form.</summary>
internal sealed unsafe class FormWindow : IDisposable
{
    readonly Form _form;
    readonly SDL_Window* _window;
    readonly SDL_Renderer* _renderer;
    SDL_Texture* _texture;
    SKBitmap? _backBuffer;
    readonly List<string> _pendingDrops = [];
    float _lastDropX, _lastDropY;

    public FormWindow(Form form, FormWindow? owner, bool modal)
    {
        _form = form;
        // Window sizes are in points; with high pixel density a point can be several pixels.
        var flags = SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY | SDL_WindowFlags.SDL_WINDOW_HIDDEN;
        var style = form.EffectiveBorderStyle;
        if (form.IsUserResizable) flags |= SDL_WindowFlags.SDL_WINDOW_RESIZABLE;
        if (style == FormBorderStyle.None) flags |= SDL_WindowFlags.SDL_WINDOW_BORDERLESS;
        if (style == FormBorderStyle.Tool) flags |= SDL_WindowFlags.SDL_WINDOW_UTILITY;
        if (form.TopMost) flags |= SDL_WindowFlags.SDL_WINDOW_ALWAYS_ON_TOP;
        _window = SDL_CreateWindow(form.WindowTitle, form.Width, form.Height, flags);
        if (_window == null) throw new InvalidOperationException($"SDL_CreateWindow failed: {SDL_GetError()}");
        float density = SDL_GetWindowPixelDensity(_window);
        if (density > 1) SDL_SetWindowSize(_window, (int)MathF.Round(form.Width / density), (int)MathF.Round(form.Height / density));
        if (owner != null)
        {
            SDL_SetWindowParent(_window, owner._window);
            if (modal) SDL_SetWindowModal(_window, true);
        }
        Id = SDL_GetWindowID(_window);
        _appliedResizable = form.IsUserResizable;
        _appliedStyle = style;
        _appliedTopMost = form.TopMost;
        ApplyDialogFrame();
        ApplyIcon();
        ApplyMinSize();
        _logicalW = form.Width / form.Scale;
        _logicalH = form.Height / form.Scale;
        PlaceWindow(owner);
        // On a monitor with another scale than the form was loaded at (a second monitor, a remembered placement),
        // rescale now, before the first frame, and centre again at the new size.
        if (SyncScaleToDisplay() && _form.EffectiveStartPosition != FormStartPosition.Manual) PlaceWindow(owner);
        RecordRestoredBounds();   // before ApplyWindowState maximises the window
        if (form.DarkTitleBar && OperatingSystem.IsWindows())
            Win32Chrome.ApplyDarkTitleBar(Hwnd, form.TitleBarColor ?? form.Color ?? form.Theme.FormBackground, form.TitleBarTextColor ?? form.Theme.Text);
        ApplyWindowState();

        _renderer = SDL_CreateRenderer(_window, (string?)null);
        if (_renderer == null) throw new InvalidOperationException($"SDL_CreateRenderer failed: {SDL_GetError()}");
        SDL_SetRenderVSync(_renderer, 1);

        form.CaptionChanged += OnCaptionChanged;
        Application.TitleChanged += OnTitleChanged;
        SDL_StartTextInput(_window);
        SyncSizeToWindow();
    }

    public SDL_WindowID Id { get; }
    public Form Form => _form;
    internal SDL_Window* Handle => _window;

    nint Hwnd => OperatingSystem.IsWindows()
        ? (nint)SDL_GetPointerProperty(SDL_GetWindowProperties(_window), SDL_PROP_WINDOW_WIN32_HWND_POINTER, 0)
        : 0;

    void OnCaptionChanged(Form f) => SDL_SetWindowTitle(_window, f.WindowTitle);
    void OnTitleChanged() { if (!_disposed) SDL_SetWindowTitle(_window, _form.WindowTitle); }

    readonly Dictionary<Cursor, nint> _cursors = [];
    Cursor _currentCursor = Cursor.Default;

    float Density => Math.Max(1f, SDL_GetWindowPixelDensity(_window));
    int Px(float points) => (int)MathF.Floor(points * Density);

    // ---- Window state, minimum size, start position, frame, scale ----

    WindowState _appliedState;
    bool _appliedResizable, _appliedTopMost;
    FormBorderStyle _appliedStyle;
    Picture? _appliedIcon;
    int _appliedMinW = -1, _appliedMinH = -1;
    // The normal window's client size in design units (pixels / form scale): what it keeps when the scale changes.
    double _logicalW, _logicalH;

    /// <summary>Form pixels to window points.</summary>
    int Points(int pixels) => (int)MathF.Round(pixels / Density);

    void ApplyMinSize()
    {
        int w = Math.Max(0, _form.MinWidth), h = Math.Max(0, _form.MinHeight);
        if (w == _appliedMinW && h == _appliedMinH) return;
        _appliedMinW = w;
        _appliedMinH = h;
        SDL_SetWindowMinimumSize(_window, w == 0 ? 0 : Math.Max(1, Points(w)), h == 0 ? 0 : Math.Max(1, Points(h)));
    }

    void ApplyWindowState()
    {
        var wanted = _form.WindowState;
        if (wanted == _appliedState) return;
        var previous = _appliedState;
        _appliedState = wanted;
        if (previous == WindowState.FullScreen)
        {
            SDL_SetWindowFullscreen(_window, false);
            SDL_SyncWindow(_window);   // let it finish before asking for anything else
        }
        switch (wanted)
        {
            case WindowState.FullScreen: SDL_SetWindowFullscreen(_window, true); break;
            case WindowState.Maximized: SDL_MaximizeWindow(_window); break;
            case WindowState.Minimized: SDL_MinimizeWindow(_window); break;
            default:
                // Leaving full screen already gives the normal window back; restoring on top of that can make SDL
                // go back to full screen while it is still leaving it. Restore only a maximised or minimised window.
                if (previous != WindowState.FullScreen
                    || (SDL_GetWindowFlags(_window) & (SDL_WindowFlags.SDL_WINDOW_MAXIMIZED | SDL_WindowFlags.SDL_WINDOW_MINIMIZED)) != 0)
                    SDL_RestoreWindow(_window);
                break;
        }
    }

    /// <summary>A dialog frame has no minimise or maximise button (Windows only; SDL resets the style when it changes the frame).</summary>
    void ApplyDialogFrame()
    {
        if (_appliedStyle == FormBorderStyle.Dialog && OperatingSystem.IsWindows()) Win32Chrome.RemoveMinMaxButtons(Hwnd);
    }

    void ApplyIcon()
    {
        var icon = _form.Icon;
        if (ReferenceEquals(icon, _appliedIcon)) return;
        _appliedIcon = icon;
        if (icon == null || icon.Width == 0 || icon.Height == 0) return;   // SDL has no "back to the default icon"
        var rgba = icon.ToRgba();
        var format = BitConverter.IsLittleEndian ? SDL_PixelFormat.SDL_PIXELFORMAT_ABGR8888 : SDL_PixelFormat.SDL_PIXELFORMAT_RGBA8888;
        fixed (byte* p = rgba)
        {
            var surface = SDL_CreateSurfaceFrom(icon.Width, icon.Height, format, (nint)p, icon.Width * 4);
            if (surface == null) return;
            SDL_SetWindowIcon(_window, surface);   // SDL copies the pixels
            SDL_DestroySurface(surface);
        }
    }

    /// <summary>Applies the form's window properties that may change while the window is open.</summary>
    void SyncWindowProperties()
    {
        SyncScaleToDisplay();
        if (_disposed) return;
        ApplyMinSize();
        ApplyWindowState();
        if (_form.IsUserResizable != _appliedResizable)
        {
            _appliedResizable = _form.IsUserResizable;
            SDL_SetWindowResizable(_window, _appliedResizable);
        }
        var style = _form.EffectiveBorderStyle;
        if (style != _appliedStyle)
        {
            if ((style == FormBorderStyle.None) != (_appliedStyle == FormBorderStyle.None))
                SDL_SetWindowBordered(_window, style != FormBorderStyle.None);
            _appliedStyle = style;   // Tool is read when the window is created
        }
        ApplyDialogFrame();   // also after SDL rebuilt the style itself (resizable, bordered, full screen); cheap when unchanged
        if (_form.TopMost != _appliedTopMost)
        {
            _appliedTopMost = _form.TopMost;
            SDL_SetWindowAlwaysOnTop(_window, _appliedTopMost);
        }
        ApplyIcon();
    }

    bool IsFullScreenNow => (SDL_GetWindowFlags(_window) & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0;

    /// <summary>The window's state as SDL's flags say now (full screen wins over maximised).</summary>
    WindowState OsState()
    {
        var flags = SDL_GetWindowFlags(_window);
        if ((flags & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0) return WindowState.FullScreen;
        if ((flags & SDL_WindowFlags.SDL_WINDOW_MINIMIZED) != 0) return WindowState.Minimized;
        return (flags & SDL_WindowFlags.SDL_WINDOW_MAXIMIZED) != 0 ? WindowState.Maximized : WindowState.Normal;
    }

    /// <summary>
    /// The OS reported a state change (user maximised, restored or minimised the window). If code asked for another
    /// state that isn't applied yet, the event is older than that request (it can arrive late, e.g. from entering full
    /// screen): the request wins and the next tick applies it.
    /// </summary>
    void OnWindowStateEvent(WindowState state)
    {
        bool requestPending = _form.WindowState != _appliedState;
        _appliedState = state;   // already in effect; don't ask the OS again
        ApplyDialogFrame();      // SDL rebuilds the window style when it leaves full screen
        if (!requestPending) _form.NotifyWindowStateChanged(state);
    }

    /// <summary>
    /// Whether the window is neither maximised, minimised nor full screen. On Windows the real OS state decides, because
    /// SDL's flags can lag behind the move/resize events sent while the window is being maximised.
    /// </summary>
    bool IsNormalState()
    {
        if (Hwnd is var hwnd and not 0)
            return !Win32Chrome.IsMaximizedOrMinimized(hwnd)
                && (SDL_GetWindowFlags(_window) & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) == 0;
        return (SDL_GetWindowFlags(_window) & (SDL_WindowFlags.SDL_WINDOW_MAXIMIZED | SDL_WindowFlags.SDL_WINDOW_MINIMIZED | SDL_WindowFlags.SDL_WINDOW_FULLSCREEN)) == 0;
    }

    /// <summary>
    /// Tells the form the window's normal rect (client-area origin and size, in form pixels) while the window is
    /// neither maximised, minimised nor full screen.
    /// </summary>
    void RecordRestoredBounds()
    {
        if (!IsNormalState()) return;
        int x, y, w, h;
        SDL_GetWindowPosition(_window, &x, &y);
        SDL_GetWindowSize(_window, &w, &h);
        _form.NotifyRestoredBounds(new Rect(Px(x), Px(y), Px(w), Px(h)));
    }

    /// <summary>The UI scale of the monitor the window is on: what the form should be at (the host loads forms at the monitor's content scale).</summary>
    double MonitorScale
    {
        get
        {
            float s = SDL_GetDisplayContentScale(SDL_GetDisplayForWindow(_window));
            return s > 0 && float.IsFinite(s) ? s : _form.Scale;
        }
    }

    /// <summary>
    /// If the window is now on a monitor with another scale than the form's, rescales the form there
    /// (<see cref="Form.ChangeScale"/>, drift-free) and, for a normal window, resizes it so it keeps its size in design
    /// units: Windows already does that while the user drags a resizable window across, so usually nothing is left to
    /// do; a fixed-size window, or one moved from code, is resized here. Returns true if the scale changed.
    /// </summary>
    bool SyncScaleToDisplay()
    {
        if (!_form.FollowsMonitorScale || SdlHost.ScaleForced) return false;
        double scale = MonitorScale;
        if (Math.Abs(scale - _form.Scale) < 1e-6) return false;
        bool normal = IsNormalState();
        _form.ChangeScale(scale);
        if (_disposed) return true;   // a ScaleChanged handler closed the form: the window is gone
        if (normal && _logicalW > 0 && _logicalH > 0)
        {
            int wantW = Math.Max(1, (int)Math.Round(_logicalW * scale)), wantH = Math.Max(1, (int)Math.Round(_logicalH * scale));
            int w, h;
            SDL_GetWindowSizeInPixels(_window, &w, &h);
            if (Math.Abs(w - wantW) > 1 || Math.Abs(h - wantH) > 1) SDL_SetWindowSize(_window, Points(wantW), Points(wantH));
        }
        _appliedMinW = _appliedMinH = -1;   // the minimum size is in form pixels: apply it at the new scale
        ApplyMinSize();
        if (_renderer != null) SyncSizeToWindow();   // (null while the constructor runs; it syncs at its end)
        _form.InvalidateAll();
        return true;
    }

    /// <summary>Moves and resizes the window's normal rect to <paramref name="bounds"/> (screen coordinates, form pixels).</summary>
    public void MoveTo(Rect bounds)
    {
        var wanted = _form.WindowState;
        if (_appliedState != WindowState.Normal || !IsNormalState())
        {
            if ((SDL_GetWindowFlags(_window) & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0) SDL_SetWindowFullscreen(_window, false);
            SDL_RestoreWindow(_window);
            _appliedState = WindowState.Normal;   // Tick gives the form its state back, on the new monitor
        }
        float density = SdlHost.DensityAt(bounds);
        SDL_SetWindowPosition(_window, (int)MathF.Round(bounds.X / density), (int)MathF.Round(bounds.Y / density));
        // The size is meant for the monitor the window lands on.
        double scale = _form.FollowsMonitorScale && !SdlHost.ScaleForced ? MonitorScale : _form.Scale;
        _logicalW = bounds.Width / scale;
        _logicalH = bounds.Height / scale;
        if (!SyncScaleToDisplay())
            SDL_SetWindowSize(_window, (int)MathF.Round(bounds.Width / density), (int)MathF.Round(bounds.Height / density));
        if (_disposed) return;
        SyncSizeToWindow();
        RecordRestoredBounds();
        // Maximise (or go full screen) again, now on the new monitor. The RESTORED event queued above is read after
        // this, when SDL's flags already say maximised, so it doesn't overwrite the form's state.
        if (wanted is WindowState.Maximized or WindowState.FullScreen && _form.WindowState != wanted) _form.WindowState = wanted;
        ApplyWindowState();
    }

    /// <summary>The monitor the window is on.</summary>
    public SDL_DisplayID DisplayId => SDL_GetDisplayForWindow(_window);

    /// <summary>Positions the (still hidden) window per <see cref="Form.EffectiveStartPosition"/> and <see cref="Form.StartMonitor"/>.</summary>
    void PlaceWindow(FormWindow? owner)
    {
        int w, h;
        SDL_GetWindowSize(_window, &w, &h);
        SDL_Rect area = default;
        bool onMonitor = _form.StartMonitor is { } start && SdlHost.UsableBounds(start.Id, out area);
        switch (_form.EffectiveStartPosition)
        {
            case FormStartPosition.CenterOwner when owner != null:
                int ox, oy, ow, oh;
                SDL_GetWindowPosition(owner._window, &ox, &oy);
                SDL_GetWindowSize(owner._window, &ow, &oh);
                SDL_SetWindowPosition(_window, ox + (ow - w) / 2, oy + (oh - h) / 2);
                break;
            case FormStartPosition.Manual:
                SDL_SetWindowPosition(_window, Points(_form.Left), Points(_form.Top));
                break;
            case FormStartPosition.CenterScreen:
            case FormStartPosition.CenterOwner:
                if (onMonitor) SDL_SetWindowPosition(_window, area.x + (area.w - w) / 2, area.y + Math.Max(0, (area.h - h) / 2));
                else SDL_SetWindowPosition(_window, unchecked((int)SDL_WINDOWPOS_CENTERED), unchecked((int)SDL_WINDOWPOS_CENTERED));
                break;
            default:
                if (onMonitor) SDL_SetWindowPosition(_window, area.x + (area.w - w) / 2, area.y + Math.Max(0, (area.h - h) / 2));
                break;
        }
    }

    public void Show() => SDL_ShowWindow(_window);
    public void Raise() => SDL_RaiseWindow(_window);

    // By value, not ref: several branches close over e inside Guard.Run(() => ...), which a ref
    // parameter can't do (CS1628), and SDL_Event is copied once per input event either way.
    public void HandleEvent(SDL_Event e)
    {
        switch ((SDL_EventType)e.type)
        {
            case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                Guard.Run(() => _form.Close());
                break;
            case SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
                Guard.Run(SyncSizeToWindow);
                break;
            case SDL_EventType.SDL_EVENT_WINDOW_EXPOSED:
                Guard.Run(() => _form.InvalidateAll());
                break;
            case SDL_EventType.SDL_EVENT_WINDOW_MOVED:
            case SDL_EventType.SDL_EVENT_WINDOW_RESIZED:
                Guard.Run(RecordRestoredBounds);
                break;
            case SDL_EventType.SDL_EVENT_WINDOW_MAXIMIZED:
                Guard.Run(() => OnWindowStateEvent(IsFullScreenNow ? WindowState.FullScreen : WindowState.Maximized));
                break;
            case SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED:
                Guard.Run(() => OnWindowStateEvent(WindowState.Minimized));
                break;
            case SDL_EventType.SDL_EVENT_WINDOW_RESTORED:
                // Read the state now: this event may be older than a later change (e.g. maximised -> full screen).
                Guard.Run(() => OnWindowStateEvent(OsState()));
                Guard.Run(RecordRestoredBounds);
                break;
            case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST:
                Guard.Run(_form.ProcessFocusLost);
                break;
            case SDL_EventType.SDL_EVENT_WINDOW_ENTER_FULLSCREEN:
                Guard.Run(() => OnWindowStateEvent(OsState()));
                break;
            case SDL_EventType.SDL_EVENT_WINDOW_LEAVE_FULLSCREEN:
                Guard.Run(() => OnWindowStateEvent(OsState()));
                Guard.Run(RecordRestoredBounds);
                break;
            // SDL 3.5 has no separate pixel-density event: a density change arrives as these two.
            case SDL_EventType.SDL_EVENT_WINDOW_DISPLAY_SCALE_CHANGED:
                Guard.Run(() => SyncScaleToDisplay());
                break;
            case SDL_EventType.SDL_EVENT_WINDOW_DISPLAY_CHANGED:
                Guard.Run(() => SyncScaleToDisplay());
                Guard.Run(_form.NotifyMonitorChanged);
                break;
            case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                Guard.Run(() => _form.ProcessMouseMove(Px(e.motion.x), Px(e.motion.y), SdlInput.Modifiers(SDL_GetModState()), SdlInput.Pointer((uint)e.motion.which)));
                break;
            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                if (SdlInput.Button(e.button.button) is { } button)
                {
                    var mods = SdlInput.Modifiers(SDL_GetModState());
                    var pointer = SdlInput.Pointer((uint)e.button.which);
                    if (e.button.down)
                        Guard.Run(() => _form.ProcessMouseDown(Px(e.button.x), Px(e.button.y), button, mods, e.button.clicks, pointer));
                    else
                        Guard.Run(() => _form.ProcessMouseUp(Px(e.button.x), Px(e.button.y), button, mods, pointer));
                }
                break;
            case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                float flip = e.wheel.direction == SDL_MouseWheelDirection.SDL_MOUSEWHEEL_FLIPPED ? -1 : 1;
                Guard.Run(() => _form.ProcessMouseWheel(Px(e.wheel.mouse_x), Px(e.wheel.mouse_y), e.wheel.x * flip, e.wheel.y * flip,
                    SdlInput.Modifiers(SDL_GetModState())));
                break;
            case SDL_EventType.SDL_EVENT_KEY_DOWN:
                Guard.Run(() => _form.ProcessKeyDown(SdlInput.Key(e.key.key), SdlInput.Modifiers(e.key.mod), e.key.repeat));
                break;
            case SDL_EventType.SDL_EVENT_KEY_UP:
                Guard.Run(() => _form.ProcessKeyUp(SdlInput.Key(e.key.key), SdlInput.Modifiers(e.key.mod)));
                break;
            case SDL_EventType.SDL_EVENT_TEXT_EDITING:
                Guard.Run(() => _form.ProcessTextComposition(Marshal.PtrToStringUTF8((nint)e.edit.text) ?? "",
                    Math.Max(0, e.edit.start), Math.Max(0, e.edit.length)));
                break;
            case SDL_EventType.SDL_EVENT_TEXT_INPUT:
                Guard.Run(() => _form.ProcessTextInput(Marshal.PtrToStringUTF8((nint)e.text.text) ?? ""));
                break;
            case SDL_EventType.SDL_EVENT_DROP_BEGIN:
                _pendingDrops.Clear();
                break;
            case SDL_EventType.SDL_EVENT_DROP_FILE:
                if (Marshal.PtrToStringUTF8((nint)e.drop.data) is { Length: > 0 } path) _pendingDrops.Add(path);
                _lastDropX = e.drop.x;
                _lastDropY = e.drop.y;
                break;
            case SDL_EventType.SDL_EVENT_DROP_COMPLETE:
                if (_pendingDrops.Count > 0)
                {
                    var paths = _pendingDrops.ToArray();
                    _pendingDrops.Clear();
                    Guard.Run(() => _form.ProcessFilesDropped(paths, Px(_lastDropX), Px(_lastDropY)));
                }
                break;
        }
    }

    Rect? _textInputArea;

    /// <summary>Advances the form's clock and keeps the IME candidate window next to the caret.</summary>
    public void Tick(ulong nowMs)
    {
        if (_disposed) return;   // closed by another window's tick earlier in this loop iteration
        Guard.Run(() =>
        {
            SyncWindowProperties();
            if (_disposed) return;
            _form.ProcessTick(nowMs);
            if (_disposed) return;   // a timer closed the form
            var area = _form.TextInputArea;
            if (area == _textInputArea) return;
            _textInputArea = area;
            if (area is { } a)
            {
                float d = Density;
                var rect = new SDL_Rect { x = (int)(a.X / d), y = (int)(a.Y / d), w = Math.Max(1, (int)(a.Width / d)), h = (int)(a.Height / d) };
                SDL_SetTextInputArea(_window, &rect, 0);
            }
        });
    }

    /// <summary>Shows the cursor of the control under the pointer.</summary>
    public void UpdateCursor()
    {
        var wanted = _form.CurrentCursor;
        if (wanted == _currentCursor) return;
        _currentCursor = wanted;
        if (!_cursors.TryGetValue(wanted, out var handle))
        {
            handle = (nint)SDL_CreateSystemCursor(SdlInput.SystemCursor(wanted));
            _cursors[wanted] = handle;
        }
        SDL_SetCursor((SDL_Cursor*)handle);
    }

    bool _busy, _disposed;

    /// <summary>
    /// False while this window is resizing or rendering (the event watch may be called from inside SDL
    /// calls made by that code, and must not replace the back buffer under it) or once it is disposed.
    /// </summary>
    public bool CanPaintNow => !_busy && !_disposed;

    void SyncSizeToWindow()
    {
        bool outer = !_busy;
        _busy = true;
        try { SyncSizeToWindowCore(); }
        finally { if (outer) _busy = false; }
    }

    void SyncSizeToWindowCore()
    {
        int w, h;
        SDL_GetWindowSizeInPixels(_window, &w, &h);
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        // While the scale matches the monitor, the window's size in design units is what a later scale change keeps.
        if (IsNormalState() && (!_form.FollowsMonitorScale || Math.Abs(MonitorScale - _form.Scale) < 1e-6))
        {
            _logicalW = w / _form.Scale;
            _logicalH = h / _form.Scale;
        }
        if (_backBuffer != null && _backBuffer.Width == w && _backBuffer.Height == h)
        {
            // Same window size, but the form may have been rescaled (a maximised window moved to another monitor).
            if (_form.Width != w || _form.Height != h) { _form.Resize(w, h); _form.InvalidateAll(); }
            return;
        }

        _backBuffer?.Dispose();
        _backBuffer = new SKBitmap(FormRenderer.FrameInfo(w, h));
        if (_texture != null) SDL_DestroyTexture(_texture);
        _texture = SDL_CreateTexture(_renderer, SDL_PixelFormat.SDL_PIXELFORMAT_ARGB8888,
            SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING, w, h);
        SDL_SetTextureScaleMode(_texture, SDL_ScaleMode.SDL_SCALEMODE_NEAREST);

        _form.Resize(w, h);
        _form.InvalidateAll();
    }

    public void RenderIfDirty()
    {
        bool outer = !_busy;
        _busy = true;
        try { RenderIfDirtyCore(); }
        finally { if (outer) _busy = false; }
    }

    void RenderIfDirtyCore()
    {
        if (_disposed) return;   // closed from inside this loop iteration (a timer, a queued action)
        _form.PerformLayout();
        if (_backBuffer == null || !_form.TryTakeDirtyRect(out var dirty)) return;

        using (var canvas = new SKCanvas(_backBuffer))
            FormRenderer.Render(_form, canvas, dirty);

        var rect = new SDL_Rect { x = dirty.X, y = dirty.Y, w = dirty.Width, h = dirty.Height };
        nint pixels = _backBuffer.GetPixels() + dirty.Y * _backBuffer.RowBytes + dirty.X * 4;
        SDL_UpdateTexture(_texture, &rect, pixels, _backBuffer.RowBytes);
        SDL_RenderClear(_renderer);
        SDL_RenderTexture(_renderer, _texture, null, null);
        SDL_RenderPresent(_renderer);
        SdlHost.TraceFirstPresent();
    }

    /// <summary>Takes the window's current size, lays the form out and presents a frame immediately.</summary>
    public void PaintNow()
    {
        SyncSizeToWindow();
        _form.InvalidateAll();
        RenderIfDirty();
    }

    /// <summary>Resizes the window (in points); SDL's event watch repaints it before this returns.</summary>
    public void ResizeTo(int width, int height) => SDL_SetWindowSize(_window, width, height);

    public void SaveScreenshot(string path)
    {
        if (_backBuffer != null) File.WriteAllBytes(path, FormRenderer.EncodePng(_backBuffer));
    }

    public void Dispose()
    {
        _disposed = true;
        _form.CaptionChanged -= OnCaptionChanged;
        Application.TitleChanged -= OnTitleChanged;
        if (_texture != null) SDL_DestroyTexture(_texture);
        foreach (var c in _cursors.Values) SDL_DestroyCursor((SDL_Cursor*)c);
        SDL_StopTextInput(_window);
        SDL_DestroyRenderer(_renderer);
        SDL_DestroyWindow(_window);
        _backBuffer?.Dispose();
    }
}
