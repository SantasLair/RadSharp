using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RadSharp.Drawing;
using SDL;
using SkiaSharp;
using static SDL.SDL3;

namespace RadSharp.Platform.Sdl;

/// <summary>
/// Runs forms in SDL3 windows. Each frame is painted by Skia on the CPU into a pixel buffer, and
/// only the dirty region is uploaded to an SDL texture and presented. Any number of forms can be
/// open; <see cref="Form.ShowModal"/> runs a nested loop while other windows stay painted but blocked.
/// </summary>
public static unsafe class Application
{
    static bool _initialized;
    static readonly Dictionary<Form, FormWindow> Windows = [];
    static readonly List<Form> ModalStack = [];
    static readonly ConcurrentQueue<Action> MainThread = new();

    /// <summary>
    /// Initializes SDL, installs the platform services, and sets <see cref="Form.DefaultScale"/> to
    /// <see cref="Scale"/>, so forms created afterwards load at the display's scale. Call before creating forms.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        // We draw IME composition text inline ourselves; the OS draws the candidate list.
        SDL_SetHint("SDL_IME_IMPLEMENTED_UI", "composition");
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
            throw new InvalidOperationException($"SDL_Init failed: {SDL_GetError()}");
        _initialized = true;
        Form.DefaultScale = Scale;
        RadSharp.Text.Clipboard.Current = new SdlClipboard();
        PlatformHost.Current = new SdlPlatform();
    }

    /// <summary>
    /// The UI scale factor to load forms with: the primary display's content scale, or the
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

    /// <summary>Shows the main form and runs the event loop until it is closed; then closes everything.</summary>
    public static void Run(Form mainForm)
    {
        Initialize();
        ShowForm(mainForm, owner: null, modal: false);
        var screenshot = Environment.GetEnvironmentVariable("RADSHARP_SCREENSHOT");
        bool first = true;
        RunLoop(() =>
        {
            if (!string.IsNullOrEmpty(screenshot) && !first && Windows.TryGetValue(mainForm, out var w))
            {
                w.SaveScreenshot(screenshot);
                mainForm.Close();
            }
            first = false;
            return Windows.ContainsKey(mainForm);
        });
        foreach (var w in Windows.Values.ToList()) w.Dispose();
        Windows.Clear();
        PlatformHost.Current = null;
        SDL_Quit();
        _initialized = false;
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread during the next loop iteration.</summary>
    public static void Post(Action action) => MainThread.Enqueue(action);

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

    /// <summary>Processes events for all windows until <paramref name="keepRunning"/> returns false.</summary>
    internal static void RunLoop(Func<bool> keepRunning)
    {
        SDL_Event e;
        while (keepRunning())
        {
            if (SDL_WaitEventTimeout(&e, 16))
            {
                do Dispatch(ref e);
                while (SDL_PollEvent(&e));
            }
            while (MainThread.TryDequeue(out var action)) action();
            ulong now = SDL_GetTicks();
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
            foreach (var f in Windows.Keys.Reverse().ToList()) f.Close();
            return;
        }
        if (!HasWindow(type)) return;
        var id = e.window.windowID;
        var window = Windows.Values.FirstOrDefault(w => w.Id == id);
        if (window == null) return;
        // While a modal form is open, other windows get no input (they still repaint).
        if (ModalStack.Count > 0 && window.Form != ModalStack[^1] && IsInput(type)) return;
        window.HandleEvent(ref e);
    }

    static bool HasWindow(SDL_EventType t) =>
        t is >= SDL_EventType.SDL_EVENT_WINDOW_FIRST and <= SDL_EventType.SDL_EVENT_WINDOW_LAST
          or SDL_EventType.SDL_EVENT_KEY_DOWN or SDL_EventType.SDL_EVENT_KEY_UP
          or SDL_EventType.SDL_EVENT_TEXT_EDITING or SDL_EventType.SDL_EVENT_TEXT_INPUT
          or SDL_EventType.SDL_EVENT_MOUSE_MOTION or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN
          or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP or SDL_EventType.SDL_EVENT_MOUSE_WHEEL;

    static bool IsInput(SDL_EventType t) =>
        t is SDL_EventType.SDL_EVENT_KEY_DOWN or SDL_EventType.SDL_EVENT_KEY_UP
          or SDL_EventType.SDL_EVENT_TEXT_EDITING or SDL_EventType.SDL_EVENT_TEXT_INPUT
          or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN or SDL_EventType.SDL_EVENT_MOUSE_WHEEL
          or SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED;

    internal static FormWindow? WindowOf(Form? form) => form != null && Windows.TryGetValue(form, out var w) ? w : null;

    // ---- File dialogs ----

    sealed class DialogRequest(Action<string[]?> done, List<nint> allocations)
    {
        public readonly Action<string[]?> Done = done;
        public readonly List<nint> Allocations = allocations;
    }

    internal static void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<string[]?> done)
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
        string[]? result = null;
        if (files != null && files[0] != null)
        {
            var list = new List<string>();
            for (int i = 0; files[i] != null; i++) list.Add(Marshal.PtrToStringUTF8((nint)files[i]) ?? "");
            result = [.. list];
        }
        // The dialog is gone, so its filter strings can go: the first allocation is the
        // NativeMemory filter array, the rest are CoTaskMem UTF-8 strings.
        NativeMemory.Free((void*)request.Allocations[0]);
        foreach (var p in request.Allocations.Skip(1)) Marshal.FreeCoTaskMem(p);
        MainThread.Enqueue(() => request.Done(result));
    }
}

/// <summary>The platform services RADSharp's core uses, backed by SDL.</summary>
internal sealed class SdlPlatform : IPlatformServices
{
    public void ShowForm(Form form) => Application.ShowForm(form, form.Owner, modal: false);
    public void RunModal(Form form) => Application.RunModal(form);
    public void CloseForm(Form form) => Application.CloseForm(form);
    public void ProcessEventsUntil(Func<bool> done) => Application.RunLoop(() => !done());
    public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<string[]?> done) =>
        Application.ShowFileDialog(kind, owner, options, done);
}

/// <summary>One SDL window showing one form.</summary>
internal sealed unsafe class FormWindow : IDisposable
{
    readonly Form _form;
    readonly SDL_Window* _window;
    readonly SDL_Renderer* _renderer;
    SDL_Texture* _texture;
    SKBitmap? _backBuffer;

    public FormWindow(Form form, FormWindow? owner, bool modal)
    {
        _form = form;
        // Window sizes are in points; with high pixel density a point can be several pixels.
        var flags = SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY | SDL_WindowFlags.SDL_WINDOW_HIDDEN;
        _window = SDL_CreateWindow(form.Caption, form.Width, form.Height, flags);
        if (_window == null) throw new InvalidOperationException($"SDL_CreateWindow failed: {SDL_GetError()}");
        float density = SDL_GetWindowPixelDensity(_window);
        if (density > 1) SDL_SetWindowSize(_window, (int)MathF.Round(form.Width / density), (int)MathF.Round(form.Height / density));
        if (owner != null)
        {
            SDL_SetWindowParent(_window, owner._window);
            if (modal) SDL_SetWindowModal(_window, true);
        }
        Id = SDL_GetWindowID(_window);

        _renderer = SDL_CreateRenderer(_window, (string?)null);
        if (_renderer == null) throw new InvalidOperationException($"SDL_CreateRenderer failed: {SDL_GetError()}");
        SDL_SetRenderVSync(_renderer, 1);

        form.CaptionChanged += OnCaptionChanged;
        SDL_StartTextInput(_window);
        SyncSizeToWindow();
    }

    public SDL_WindowID Id { get; }
    public Form Form => _form;
    internal SDL_Window* Handle => _window;

    void OnCaptionChanged(Form f) => SDL_SetWindowTitle(_window, f.Caption);

    readonly Dictionary<Cursor, nint> _cursors = [];
    Cursor _currentCursor = Cursor.Default;

    float Density => Math.Max(1f, SDL_GetWindowPixelDensity(_window));
    int Px(float points) => (int)MathF.Floor(points * Density);

    public void Show() => SDL_ShowWindow(_window);
    public void Raise() => SDL_RaiseWindow(_window);

    public void HandleEvent(ref SDL_Event e)
    {
        switch ((SDL_EventType)e.type)
        {
            case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                _form.Close();
                break;
            case SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
                SyncSizeToWindow();
                break;
            case SDL_EventType.SDL_EVENT_WINDOW_EXPOSED:
                _form.InvalidateAll();
                break;
            case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                _form.ProcessMouseMove(Px(e.motion.x), Px(e.motion.y), SdlInput.Modifiers(SDL_GetModState()));
                break;
            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                if (SdlInput.Button(e.button.button) is { } button)
                {
                    var mods = SdlInput.Modifiers(SDL_GetModState());
                    if (e.button.down) _form.ProcessMouseDown(Px(e.button.x), Px(e.button.y), button, mods, e.button.clicks);
                    else _form.ProcessMouseUp(Px(e.button.x), Px(e.button.y), button, mods);
                }
                break;
            case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                float flip = e.wheel.direction == SDL_MouseWheelDirection.SDL_MOUSEWHEEL_FLIPPED ? -1 : 1;
                _form.ProcessMouseWheel(Px(e.wheel.mouse_x), Px(e.wheel.mouse_y), e.wheel.x * flip, e.wheel.y * flip,
                    SdlInput.Modifiers(SDL_GetModState()));
                break;
            case SDL_EventType.SDL_EVENT_KEY_DOWN:
                _form.ProcessKeyDown(SdlInput.Key(e.key.key), SdlInput.Modifiers(e.key.mod), e.key.repeat);
                break;
            case SDL_EventType.SDL_EVENT_KEY_UP:
                _form.ProcessKeyUp(SdlInput.Key(e.key.key), SdlInput.Modifiers(e.key.mod));
                break;
            case SDL_EventType.SDL_EVENT_TEXT_EDITING:
                _form.ProcessTextComposition(Marshal.PtrToStringUTF8((nint)e.edit.text) ?? "",
                    Math.Max(0, e.edit.start), Math.Max(0, e.edit.length));
                break;
            case SDL_EventType.SDL_EVENT_TEXT_INPUT:
                _form.ProcessTextInput(Marshal.PtrToStringUTF8((nint)e.text.text) ?? "");
                break;
        }
    }

    Rect? _textInputArea;

    /// <summary>Advances the form's clock and keeps the IME candidate window next to the caret.</summary>
    public void Tick(ulong nowMs)
    {
        _form.ProcessTick(nowMs);
        var area = _form.TextInputArea;
        if (area == _textInputArea) return;
        _textInputArea = area;
        if (area is { } a)
        {
            float d = Density;
            var rect = new SDL_Rect { x = (int)(a.X / d), y = (int)(a.Y / d), w = Math.Max(1, (int)(a.Width / d)), h = (int)(a.Height / d) };
            SDL_SetTextInputArea(_window, &rect, 0);
        }
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

    void SyncSizeToWindow()
    {
        int w, h;
        SDL_GetWindowSizeInPixels(_window, &w, &h);
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        if (_backBuffer != null && _backBuffer.Width == w && _backBuffer.Height == h) return;

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
    }

    public void SaveScreenshot(string path)
    {
        if (_backBuffer != null) File.WriteAllBytes(path, FormRenderer.EncodePng(_backBuffer));
    }

    public void Dispose()
    {
        _form.CaptionChanged -= OnCaptionChanged;
        if (_texture != null) SDL_DestroyTexture(_texture);
        foreach (var c in _cursors.Values) SDL_DestroyCursor((SDL_Cursor*)c);
        SDL_StopTextInput(_window);
        SDL_DestroyRenderer(_renderer);
        SDL_DestroyWindow(_window);
        _backBuffer?.Dispose();
    }
}
