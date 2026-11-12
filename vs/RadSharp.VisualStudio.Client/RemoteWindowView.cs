using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using P = RadSharp.DesignHost.Protocol;

namespace RadSharp.VisualStudio.Client;

/// <summary>
/// Shows one host window and forwards input to it. The image is a <see cref="WriteableBitmap"/> in device pixels, drawn
/// 1:1 on the device pixel grid; the element's size (in device pixels, with the DPI scale) is sent to the host as
/// ResizeWindow. Attach it to a window with <see cref="Attach"/>.
/// </summary>
public class RemoteWindowView : FrameworkElement
{
    /// <summary>Fill behind the image (shown before the first frame and while resizing).</summary>
    public static readonly DependencyProperty BackgroundProperty = System.Windows.Controls.Panel.BackgroundProperty.AddOwner(
        typeof(RemoteWindowView), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    private const int WM_MOUSEHWHEEL = 0x020E;

    private readonly DispatcherTimer _resizeTimer;
    private WriteableBitmap? _bitmap;
    private DesignSession? _session;
    private HwndSource? _hookedSource;
    private int _sentW = -1, _sentH = -1;
    private double _sentScale = -1;
    private P.MouseButton[] _down = Array.Empty<P.MouseButton>(); // buttons the host believes are down

    /// <summary>Creates a detached view.</summary>
    public RemoteWindowView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        InputMethod.SetIsInputMethodEnabled(this, true);
        _resizeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
        _resizeTimer.Tick += (s, e) =>
        {
            _resizeTimer.Stop();
            SendSize();
        };
        SizeChanged += (s, e) => ScheduleSize();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Fill behind the image.</summary>
    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>The host window shown, or 0 when detached.</summary>
    public int WindowId { get; private set; }

    /// <summary>The session this view is attached to.</summary>
    public DesignSession? Session => _session;

    /// <summary>The pixels received so far (device pixels, Pbgra32), or null before the first frame.</summary>
    public WriteableBitmap? Bitmap => _bitmap;

    /// <summary>Frames applied since the last <see cref="Attach"/>.</summary>
    public int FrameCount { get; private set; }

    /// <summary>When false, a click does not take keyboard focus (used for popups such as menus, whose keys go to the owner).</summary>
    public bool FocusOnClick { get; set; } = true;

    /// <summary>
    /// Decides whether a key event goes to the host. Return false to let the key bubble on unhandled (for example Ctrl+S in
    /// Visual Studio). Keys that are forwarded are marked handled, except KeyDowns of keys that type text (so TextInput still follows). Null forwards every key RadSharp knows.
    /// </summary>
    public Func<KeyEventArgs, bool>? ShouldForwardKey { get; set; }

    /// <summary>A frame was applied to <see cref="Bitmap"/>.</summary>
    public event EventHandler? FrameReceived;

    /// <summary>Overrides the DPI scale (tests).</summary>
    internal double? DpiScaleOverride { get; set; }

    /// <summary>The current device-pixels-per-DIP scale.</summary>
    public double Scale => DpiScaleOverride ?? VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Shows host window <paramref name="windowId"/> of <paramref name="session"/>: sends AttachWindow and the current size.</summary>
    public void Attach(DesignSession session, int windowId)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        Detach();
        _down = Array.Empty<P.MouseButton>();
        _session = session;
        WindowId = windowId;
        FrameCount = 0;
        session.RegisterView(this, windowId);
        session.Send(new P.AttachWindow { WindowId = windowId });
        _sentW = _sentH = -1;
        _sentScale = -1;
        SendSize();
    }

    /// <summary>Stops showing the window. The last image stays until the next attach replaces it.</summary>
    public void Detach()
    {
        if (_session != null && WindowId != 0)
        {
            ReleaseHostButtons();
            if (IsKeyboardFocused) _session.Send(new P.FocusWindow { WindowId = WindowId, Focused = false });
            _session.UnregisterView(this, WindowId);
        }
        DetachInternal();
    }

    internal void DetachInternal()
    {
        _resizeTimer.Stop();
        _down = Array.Empty<P.MouseButton>(); // the host window is gone (or Detach already released): nothing to send
        if (IsMouseCaptured) ReleaseMouseCapture();
        _session = null;
        WindowId = 0;
        Cursor = null;
    }

    private bool Attached => _session != null && WindowId != 0;

    private void Send(P.Message m)
    {
        if (Attached) _session!.Send(m);
    }

    // ---- Size and DPI ----

    private void ScheduleSize()
    {
        if (!Attached) return;
        if (_sentW <= 0 || _sentH <= 0)
        {
            SendSize(); // first real size: no reason to wait
            return;
        }
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    /// <summary>The element's size in device pixels.</summary>
    public (int Width, int Height) DeviceSize
    {
        get
        {
            double s = Scale;
            return ((int)Math.Round(ActualWidth * s), (int)Math.Round(ActualHeight * s));
        }
    }

    private void SendSize()
    {
        if (!Attached) return;
        var (w, h) = DeviceSize;
        double s = Scale;
        if (w <= 0 || h <= 0) return;
        if (w == _sentW && h == _sentH && Math.Abs(s - _sentScale) < 0.001) return;
        _sentW = w;
        _sentH = h;
        _sentScale = s;
        Send(new P.ResizeWindow { WindowId = WindowId, Width = w, Height = h, Scale = (float)s });
    }

    /// <inheritdoc />
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
        ScheduleSize();
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => new Size(0, 0);

    // ---- Frames ----

    /// <summary>The largest bitmap side and area a frame may ask for; anything bigger is a corrupt frame.</summary>
    private const long MaxFrameSide = 16384, MaxFrameArea = 64L * 1024 * 1024;

    /// <summary>Copies a frame's rectangle into the bitmap, creating or growing it as needed. A frame that is malformed or
    /// absurdly large is dropped: this runs on the UI thread and must never throw.</summary>
    internal void ApplyFrame(P.Frame f)
    {
        try
        {
            if (!ApplyFrameCore(f)) return;
            FrameCount++;
        }
#pragma warning disable CA1031 // never let a bad frame throw on the dispatcher
        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is OverflowException
            || ex is OutOfMemoryException || ex is NotSupportedException)
#pragma warning restore CA1031
        {
            System.Diagnostics.Debug.WriteLine("RemoteWindowView: dropped frame for window " + f?.WindowId + ": " + ex.Message);
            return;
        }
        FrameReceived?.Invoke(this, EventArgs.Empty);
    }

    private bool ApplyFrameCore(P.Frame f)
    {
        if (f == null || f.Pixels == null) return false;
        long fx = f.X, fy = f.Y, fw = f.Width, fh = f.Height;
        if (fw <= 0 || fh <= 0 || fx < 0 || fy < 0) return false;
        if (f.Pixels.LongLength < fw * fh * 4)
        {
            System.Diagnostics.Debug.WriteLine("RemoteWindowView: short frame for window " + f.WindowId);
            return false;
        }
        long needW = fx + fw, needH = fy + fh;
        if (needW > MaxFrameSide || needH > MaxFrameSide || needW * needH > MaxFrameArea) return false; // outside any real window
        double scale = _sentScale > 0 ? _sentScale : Scale;
        double dpi = 96.0 * scale;
        bool full = fx == 0 && fy == 0 && fw == _sentW && fh == _sentH;
        if (_bitmap == null
            || (full && (_bitmap.PixelWidth != fw || _bitmap.PixelHeight != fh || Math.Abs(_bitmap.DpiX - dpi) > 0.01)))
        {
            // A full frame (the host sends one after each resize) replaces the bitmap, since nothing old is kept. A first
            // frame that is only part of the window gets a bitmap of the requested size.
            long bw = full ? fw : Math.Max(needW, _sentW);
            long bh = full ? fh : Math.Max(needH, _sentH);
            if (bw > MaxFrameSide || bh > MaxFrameSide || bw * bh > MaxFrameArea) return false;
            _bitmap = new WriteableBitmap((int)bw, (int)bh, dpi, dpi, PixelFormats.Pbgra32, null);
            InvalidateVisual();
        }
        else if (needW > _bitmap.PixelWidth || needH > _bitmap.PixelHeight)
        {
            int gw = (int)Math.Max(needW, _bitmap.PixelWidth), gh = (int)Math.Max(needH, _bitmap.PixelHeight);
            var grown = new WriteableBitmap(gw, gh, _bitmap.DpiX, _bitmap.DpiY, PixelFormats.Pbgra32, null);
            var old = new byte[_bitmap.PixelWidth * _bitmap.PixelHeight * 4];
            _bitmap.CopyPixels(old, _bitmap.PixelWidth * 4, 0);
            grown.WritePixels(new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight), old, _bitmap.PixelWidth * 4, 0);
            _bitmap = grown;
            InvalidateVisual();
        }
        _bitmap.WritePixels(new Int32Rect((int)fx, (int)fy, (int)fw, (int)fh), f.Pixels, (int)fw * 4, 0);
        return true;
    }

    internal void ApplyCursor(string name) => Cursor = InputMapping.MapCursor(name);

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Background ?? Brushes.Transparent, null, bounds);
        var bmp = _bitmap;
        if (bmp == null) return;
        // Snap the image's origin to the device pixel grid so each bitmap pixel lands on exactly one screen pixel.
        var guidelines = new GuidelineSet(new[] { 0.0 }, new[] { 0.0 });
        dc.PushGuidelineSet(guidelines);
        dc.DrawImage(bmp, new Rect(0, 0, bmp.Width, bmp.Height));
        dc.Pop();
    }

    // ---- Mouse ----

    /// <summary>Converts a point in DIPs (relative to this element) to device pixels.</summary>
    public (int X, int Y) ToDevicePixels(Point dip)
    {
        double s = Scale;
        return ((int)Math.Floor(dip.X * s), (int)Math.Floor(dip.Y * s));
    }

    internal enum MouseKind { Move, Down, Up }

    /// <summary>Builds and sends a mouse message (the event handlers and tests go through here).</summary>
    internal P.MouseMessage? SendMouse(MouseKind kind, Point dip, P.MouseButton button, ModifierKeys mods, int clicks)
    {
        if (!Attached) return null;
        P.MouseMessage m = kind switch
        {
            MouseKind.Down => new P.MouseDown(),
            MouseKind.Up => new P.MouseUp(),
            _ => new P.MouseMove(),
        };
        var (x, y) = ToDevicePixels(dip);
        m.WindowId = WindowId;
        m.X = x;
        m.Y = y;
        m.Button = button;
        m.Mods = InputMapping.MapModifiers(mods);
        m.Clicks = clicks;
        if (kind == MouseKind.Down && Array.IndexOf(_down, button) < 0) _down = AppendButton(_down, button);
        else if (kind == MouseKind.Up) _down = Array.FindAll(_down, d => d != button);
        Send(m);
        return m;
    }

    private static P.MouseButton[] AppendButton(P.MouseButton[] a, P.MouseButton b)
    {
        var r = new P.MouseButton[a.Length + 1];
        a.CopyTo(r, 0);
        r[a.Length] = b;
        return r;
    }

    /// <summary>Sends MouseUp for every button the host still thinks is down (capture was lost, or the view detached mid-drag).</summary>
    internal void ReleaseHostButtons()
    {
        if (!Attached || _down.Length == 0) return;
        var pos = Mouse.GetPosition(this);
        foreach (var b in _down) SendMouse(MouseKind.Up, pos, b, Keyboard.Modifiers, 1);
        _down = Array.Empty<P.MouseButton>();
    }

    /// <inheritdoc />
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // Capture can be taken away mid-drag (Alt+Tab, a modal dialog, another element): the MouseUp then never reaches us.
        ReleaseHostButtons();
    }

    internal P.MouseWheel? SendWheel(Point dip, float dx, float dy, ModifierKeys mods)
    {
        if (!Attached) return null;
        var (x, y) = ToDevicePixels(dip);
        var m = new P.MouseWheel { WindowId = WindowId, X = x, Y = y, Dx = dx, Dy = dy, Mods = InputMapping.MapModifiers(mods) };
        Send(m);
        return m;
    }

    private static P.MouseButton PressedButton()
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed) return P.MouseButton.Left;
        if (Mouse.RightButton == MouseButtonState.Pressed) return P.MouseButton.Right;
        if (Mouse.MiddleButton == MouseButtonState.Pressed) return P.MouseButton.Middle;
        return P.MouseButton.Left;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!Attached) return;
        SendMouse(MouseKind.Move, e.GetPosition(this), PressedButton(), Keyboard.Modifiers, 0);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        // No leave message in the protocol: a move far outside clears hover state in the host.
        if (Attached && !IsMouseCaptured) SendMouse(MouseKind.Move, new Point(-100000, -100000), PressedButton(), Keyboard.Modifiers, 0);
    }

    /// <inheritdoc />
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Attached) return;
        var b = InputMapping.MapButton(e.ChangedButton);
        if (b == null) return;
        if (FocusOnClick && !IsKeyboardFocusWithin) Focus();
        CaptureMouse();
        SendMouse(MouseKind.Down, e.GetPosition(this), b.Value, Keyboard.Modifiers, e.ClickCount);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!Attached) return;
        var b = InputMapping.MapButton(e.ChangedButton);
        if (b == null) return;
        SendMouse(MouseKind.Up, e.GetPosition(this), b.Value, Keyboard.Modifiers, e.ClickCount);
        if (IsMouseCaptured && Mouse.LeftButton == MouseButtonState.Released && Mouse.RightButton == MouseButtonState.Released
            && Mouse.MiddleButton == MouseButtonState.Released)
            ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!Attached) return;
        SendWheel(e.GetPosition(this), 0, e.Delta / 120f, Keyboard.Modifiers);
        e.Handled = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource src && !ReferenceEquals(src, _hookedSource))
        {
            _hookedSource?.RemoveHook(WndProc);
            _hookedSource = src;
            src.AddHook(WndProc);
        }
        ScheduleSize();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _hookedSource?.RemoveHook(WndProc);
        _hookedSource = null;
    }

    // WPF has no horizontal wheel event; read WM_MOUSEHWHEEL from the window.
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEHWHEEL && Attached && (IsMouseOver || IsMouseCaptured))
        {
            int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            SendWheel(Mouse.GetPosition(this), delta / 120f, 0, Keyboard.Modifiers);
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ---- Keyboard ----

    /// <summary>Sends a key message for <paramref name="key"/> if it has a RadSharp name. Returns the message sent.</summary>
    internal P.KeyMessage? SendKey(bool down, Key key, ModifierKeys mods, bool repeat)
    {
        if (!Attached) return null;
        var name = InputMapping.MapKey(key);
        if (name == null) return null;
        P.KeyMessage m = down ? new P.KeyDown() : new P.KeyUp();
        m.WindowId = WindowId;
        m.Key = name;
        m.Mods = InputMapping.MapModifiers(mods);
        m.Repeat = repeat;
        Send(m);
        return m;
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        HandleKey(e, true);
    }

    /// <inheritdoc />
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        HandleKey(e, false);
    }

    private void HandleKey(KeyEventArgs e, bool down)
    {
        if (e.Handled || !Attached) return;
        var key = InputMapping.RealKey(e);
        if (InputMapping.MapKey(key) == null) return;
        if (ShouldForwardKey != null && !ShouldForwardKey(e)) return;
        var mods = Keyboard.Modifiers;
        SendKey(down, key, mods, e.IsRepeat);
        // WPF raises no TextInput for a handled KeyDown, so a key that types a character stays unhandled: the host gets the
        // KeyDown now and the text from OnTextInput. With Ctrl, Alt or Win held it is a shortcut (no text): handle it so it does
        // not bubble to Visual Studio. Ctrl+Alt together is AltGr on many layouts and does type text.
        bool altGr = (mods & (ModifierKeys.Control | ModifierKeys.Alt)) == (ModifierKeys.Control | ModifierKeys.Alt);
        bool shortcut = (mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0 && !altGr;
        if (down && InputMapping.ProducesText(key) && !shortcut) return;
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || !Attached) return;
        if (!InputMapping.IsPrintable(e.Text)) return;
        Send(new P.TextInput { WindowId = WindowId, Text = e.Text });
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (ReferenceEquals(e.NewFocus, this)) Send(new P.FocusWindow { WindowId = WindowId, Focused = true });
    }

    /// <inheritdoc />
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (ReferenceEquals(e.OldFocus, this)) Send(new P.FocusWindow { WindowId = WindowId, Focused = false });
    }
}
