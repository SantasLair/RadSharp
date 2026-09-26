using RadSharp.Drawing;
using SDL;
using SkiaSharp;
using static SDL.SDL3;

namespace RadSharp.Platform.Sdl;

/// <summary>
/// Runs forms in SDL3 windows. Each frame is painted by Skia on the CPU into a pixel buffer, and
/// only the dirty region is uploaded to an SDL texture and presented.
/// </summary>
public static unsafe class Application
{
    static bool _initialized;

    /// <summary>
    /// Initializes SDL and sets <see cref="Form.DefaultScale"/> to <see cref="Scale"/>, so forms
    /// created afterwards (their generated <c>InitializeComponent()</c>) load at the display's scale.
    /// Call before creating forms.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
            throw new InvalidOperationException($"SDL_Init failed: {SDL_GetError()}");
        _initialized = true;
        Form.DefaultScale = Scale;
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

    /// <summary>Shows <paramref name="form"/> in a window and runs the event loop until it is closed.</summary>
    public static void Run(Form form)
    {
        Initialize();
        using var window = new FormWindow(form);
        window.Show();
        var screenshot = Environment.GetEnvironmentVariable("RADSHARP_SCREENSHOT");

        SDL_Event e;
        bool running = true;
        while (running)
        {
            if (SDL_WaitEventTimeout(&e, 50))
            {
                do
                {
                    if (!window.HandleEvent(ref e)) running = false;
                } while (SDL_PollEvent(&e));
                window.UpdateCursor();
            }
            window.RenderIfDirty();

            if (!string.IsNullOrEmpty(screenshot))
            {
                window.SaveScreenshot(screenshot);
                running = false;
            }
        }
        SDL_Quit();
        _initialized = false;
    }
}

/// <summary>One SDL window showing one form.</summary>
internal sealed unsafe class FormWindow : IDisposable
{
    readonly Form _form;
    readonly SDL_Window* _window;
    readonly SDL_Renderer* _renderer;
    SDL_Texture* _texture;
    SKBitmap? _backBuffer;

    public FormWindow(Form form)
    {
        _form = form;
        // Window sizes are in points; with high pixel density a point can be several pixels.
        _window = SDL_CreateWindow(form.Caption, form.Width, form.Height,
            SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY | SDL_WindowFlags.SDL_WINDOW_HIDDEN);
        if (_window == null) throw new InvalidOperationException($"SDL_CreateWindow failed: {SDL_GetError()}");
        float density = SDL_GetWindowPixelDensity(_window);
        if (density > 1) SDL_SetWindowSize(_window, (int)MathF.Round(form.Width / density), (int)MathF.Round(form.Height / density));

        _renderer = SDL_CreateRenderer(_window, (string?)null);
        if (_renderer == null) throw new InvalidOperationException($"SDL_CreateRenderer failed: {SDL_GetError()}");
        SDL_SetRenderVSync(_renderer, 1);

        form.CaptionChanged += f => SDL_SetWindowTitle(_window, f.Caption);
        SDL_StartTextInput(_window);
        SyncSizeToWindow();
    }

    readonly Dictionary<Cursor, nint> _cursors = [];
    Cursor _currentCursor = Cursor.Default;

    float Density => Math.Max(1f, SDL_GetWindowPixelDensity(_window));
    int Px(float points) => (int)MathF.Floor(points * Density);

    public void Show() => SDL_ShowWindow(_window);

    /// <summary>Handles one event; returns false when the window should close.</summary>
    public bool HandleEvent(ref SDL_Event e)
    {
        switch ((SDL_EventType)e.type)
        {
            case SDL_EventType.SDL_EVENT_QUIT:
            case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                return false;
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
            case SDL_EventType.SDL_EVENT_TEXT_INPUT:
                _form.ProcessTextInput(System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)e.text.text) ?? "");
                break;
        }
        return true;
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
        if (_texture != null) SDL_DestroyTexture(_texture);
        foreach (var c in _cursors.Values) SDL_DestroyCursor((SDL_Cursor*)c);
        SDL_StopTextInput(_window);
        SDL_DestroyRenderer(_renderer);
        SDL_DestroyWindow(_window);
        _backBuffer?.Dispose();
    }
}
