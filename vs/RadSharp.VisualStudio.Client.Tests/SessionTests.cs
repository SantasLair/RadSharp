using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RadSharp.DesignHost.Protocol;
using Xunit;
using P = RadSharp.DesignHost.Protocol;

namespace RadSharp.VisualStudio.Client.Tests;

/// <summary>Session, view and presenter behaviour against the fake host, on an STA thread with a real WPF window.</summary>
public class SessionTests
{
    internal const int SurfaceId = 7;

    /// <summary>A connected session, an open document and a 200x100 DIP view at scale 1.5, attached to the surface.</summary>
    internal sealed class Rig : IDisposable
    {
        public Rig(double scale = 1.5)
        {
            Host = new FakeHost();
            var conn = new DesignHostConnection(Host.ClientStream, System.Windows.Threading.Dispatcher.CurrentDispatcher);
            conn.HandshakeAsync("tests").Wait();
            Session = new DesignSession(conn);
            View = new RemoteWindowView { Width = 200, Height = 100, DpiScaleOverride = scale };
            Window = new Window
            {
                Content = View,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -20000,
                Top = -20000,
            };
            Window.Show();
            Doc = Session.OpenDocument(@"C:\forms\Main.radform.json");
            Doc.WindowOpened += (s, e) =>
            {
                if (e.Window.Role == WindowRole.Surface) View.Attach(Session, e.Window.Id);
            };
            Host.WaitFor<OpenDocument>();
            Host.Send(new WindowOpened { WindowId = SurfaceId, DocId = Doc.Id, Role = WindowRole.Surface });
            Host.WaitFor<ResizeWindow>(r => r.WindowId == SurfaceId);
        }

        public FakeHost Host { get; }
        public DesignSession Session { get; }
        public DesignDocument Doc { get; }
        public RemoteWindowView View { get; }
        public Window Window { get; }

        public void Dispose()
        {
            Session.Dispose();
            Window.Close();
            Host.Dispose();
        }
    }

    internal static byte[] Fill(int w, int h, uint bgra)
    {
        var p = new byte[w * h * 4];
        for (int i = 0; i < p.Length; i += 4)
        {
            p[i] = (byte)bgra;
            p[i + 1] = (byte)(bgra >> 8);
            p[i + 2] = (byte)(bgra >> 16);
            p[i + 3] = (byte)(bgra >> 24);
        }
        return p;
    }

    internal static uint PixelAt(RemoteWindowView v, int x, int y)
    {
        var bmp = v.Bitmap!;
        var px = new byte[4];
        bmp.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return (uint)(px[0] | px[1] << 8 | px[2] << 16 | px[3] << 24);
    }

    [Fact]
    public void OpenDocument_then_WindowOpened_attaches_the_view_and_sends_its_device_pixel_size() => Ui.RunSta(() =>
    {
        using var rig = new Rig(scale: 1.5);
        var open = rig.Host.WaitFor<OpenDocument>();
        Assert.Equal(rig.Doc.Id, open.DocId);
        Assert.Equal(@"C:\forms\Main.radform.json", open.Path);
        rig.Host.WaitFor<AttachWindow>(a => a.WindowId == SurfaceId);
        var resize = rig.Host.WaitFor<ResizeWindow>(r => r.WindowId == SurfaceId);
        Assert.Equal(300, resize.Width); // 200 DIPs * 1.5
        Assert.Equal(150, resize.Height);
        Assert.Equal(1.5f, resize.Scale);
        Assert.Equal(SurfaceId, rig.View.WindowId);
        Assert.Same(rig.View, rig.Session.GetView(SurfaceId));
        Assert.Equal(SurfaceId, rig.Doc.GetWindow(WindowRole.Surface)!.Id);
    });

    [Fact]
    public void Resizing_the_view_sends_a_new_size() => Ui.RunSta(() =>
    {
        using var rig = new Rig(scale: 1.25);
        rig.View.Width = 320;
        rig.Host.WaitFor<ResizeWindow>(r => r.WindowId == SurfaceId && r.Width == 400);
        Assert.Equal(125, rig.Host.Find<ResizeWindow>(r => r.Width == 400)!.Height);
    });

    [Fact]
    public void Frames_are_blitted_into_the_bitmap_at_their_rectangle() => Ui.RunSta(() =>
    {
        using var rig = new Rig(scale: 1.5);
        const uint A = 0xFF302010, B = 0x80402000; // B is premultiplied half-transparent
        rig.Host.Send(new Frame { WindowId = SurfaceId, X = 0, Y = 0, Width = 300, Height = 150, Pixels = Fill(300, 150, A) });
        rig.Host.Send(new Frame { WindowId = SurfaceId, X = 5, Y = 6, Width = 2, Height = 3, Pixels = Fill(2, 3, B) });
        Ui.WaitUntil(() => rig.View.FrameCount == 2);

        var bmp = rig.View.Bitmap!;
        Assert.Equal(300, bmp.PixelWidth);
        Assert.Equal(150, bmp.PixelHeight);
        Assert.Equal(PixelFormats.Pbgra32, bmp.Format);
        Assert.Equal(96 * 1.5, bmp.DpiX, 3); // so the bitmap is 200 DIPs wide: one bitmap pixel per device pixel
        Assert.Equal(A, PixelAt(rig.View, 0, 0));
        Assert.Equal(B, PixelAt(rig.View, 5, 6));
        Assert.Equal(B, PixelAt(rig.View, 6, 8));
        Assert.Equal(A, PixelAt(rig.View, 4, 6));
        Assert.Equal(A, PixelAt(rig.View, 7, 6));
        Assert.Equal(A, PixelAt(rig.View, 5, 9));
        Assert.Equal(A, PixelAt(rig.View, 299, 149));
    });

    [Fact]
    public void A_full_frame_at_a_new_size_replaces_the_bitmap() => Ui.RunSta(() =>
    {
        using var rig = new Rig(scale: 1.0);
        rig.Host.Send(new Frame { WindowId = SurfaceId, Width = 200, Height = 100, Pixels = Fill(200, 100, 0xFF0000FF) });
        Ui.WaitUntil(() => rig.View.FrameCount == 1);
        rig.View.Width = 150;
        rig.Host.WaitFor<ResizeWindow>(r => r.Width == 150);
        rig.Host.Send(new Frame { WindowId = SurfaceId, Width = 150, Height = 100, Pixels = Fill(150, 100, 0xFF00FF00) });
        Ui.WaitUntil(() => rig.View.FrameCount == 2);
        Assert.Equal(150, rig.View.Bitmap!.PixelWidth);
        Assert.Equal(0xFF00FF00, PixelAt(rig.View, 149, 99));
    });

    [Fact]
    public void Frames_for_other_windows_are_ignored() => Ui.RunSta(() =>
    {
        // (Frames whose pixel buffer doesn't match Width x Height are rejected by the protocol reader itself, so they
        // never reach the view; see Frame.Read in RadSharp.DesignHost.Protocol/Messages.cs.)
        using var rig = new Rig();
        rig.Host.Send(new Frame { WindowId = 99, Width = 2, Height = 2, Pixels = Fill(2, 2, 1) });
        rig.Host.Send(new Log { Level = 0, Text = "sync" });
        bool synced = false;
        rig.Session.Log += (s, e) => synced |= e.Text == "sync";
        Ui.WaitUntil(() => synced);
        Assert.Equal(0, rig.View.FrameCount);
        Assert.Null(rig.View.Bitmap);
    });

    [Fact]
    public void Mouse_input_is_converted_from_DIPs_to_device_pixels() => Ui.RunSta(() =>
    {
        using var rig = new Rig(scale: 1.5);
        rig.View.SendMouse(RemoteWindowView.MouseKind.Down, new Point(10.5, 20.2), P.MouseButton.Right,
            ModifierKeys.Control | ModifierKeys.Shift, 2);
        var down = rig.Host.WaitFor<MouseDown>();
        Assert.Equal(SurfaceId, down.WindowId);
        Assert.Equal(15, down.X); // floor(10.5 * 1.5)
        Assert.Equal(30, down.Y); // floor(20.2 * 1.5)
        Assert.Equal(P.MouseButton.Right, down.Button);
        Assert.Equal(Modifiers.Ctrl | Modifiers.Shift, down.Mods);
        Assert.Equal(2, down.Clicks);

        rig.View.SendMouse(RemoteWindowView.MouseKind.Move, new Point(-2, 199.9), P.MouseButton.Left, ModifierKeys.Alt | ModifierKeys.Windows, 0);
        var move = rig.Host.WaitFor<MouseMove>();
        Assert.Equal(-3, move.X);
        Assert.Equal(299, move.Y);
        Assert.Equal(Modifiers.Alt | Modifiers.Meta, move.Mods);

        rig.View.SendMouse(RemoteWindowView.MouseKind.Up, new Point(1, 1), P.MouseButton.Right, ModifierKeys.None, 1);
        Assert.Equal(P.MouseButton.Right, rig.Host.WaitFor<MouseUp>().Button);

        rig.View.SendWheel(new Point(4, 4), 0, -240 / 120f, ModifierKeys.Control);
        var wheel = rig.Host.WaitFor<MouseWheel>();
        Assert.Equal(6, wheel.X);
        Assert.Equal(-2f, wheel.Dy);
        Assert.Equal(0f, wheel.Dx);
        Assert.Equal(Modifiers.Ctrl, wheel.Mods);
    });

    [Fact]
    public void Keys_are_sent_as_RadSharp_key_names_and_unmapped_keys_are_dropped() => Ui.RunSta(() =>
    {
        using var rig = new Rig();
        Assert.NotNull(rig.View.SendKey(true, Key.A, ModifierKeys.Control, false));
        var a = rig.Host.WaitFor<P.KeyDown>(k => k.Key == "A");
        Assert.Equal(Modifiers.Ctrl, a.Mods);
        Assert.False(a.Repeat);

        rig.View.SendKey(false, Key.Return, ModifierKeys.None, false);
        rig.Host.WaitFor<P.KeyUp>(k => k.Key == "Enter");
        rig.View.SendKey(true, Key.Down, ModifierKeys.Shift, true);
        Assert.True(rig.Host.WaitFor<P.KeyDown>(k => k.Key == "Down").Repeat);

        Assert.Null(rig.View.SendKey(true, Key.LeftShift, ModifierKeys.Shift, false));
        Assert.Null(rig.View.SendKey(true, Key.VolumeUp, ModifierKeys.None, false));
    });

    [Fact]
    public void Cursor_messages_set_the_WPF_cursor() => Ui.RunSta(() =>
    {
        using var rig = new Rig();
        rig.Host.Send(new P.Cursor { WindowId = SurfaceId, CursorName = "SizeNWSE" });
        Ui.WaitUntil(() => rig.View.Cursor == Cursors.SizeNWSE);
        rig.Host.Send(new P.Cursor { WindowId = SurfaceId, CursorName = "NoSuchCursor" });
        Ui.WaitUntil(() => rig.View.Cursor == Cursors.Arrow);
    });

    [Fact]
    public void Document_messages_reach_the_document_and_the_session() => Ui.RunSta(() =>
    {
        using var rig = new Rig();
        int stateEvents = 0;
        NavigateToCodeEventArgs? nav = null;
        HostErrorEventArgs? docError = null, globalError = null;
        rig.Doc.StateChanged += (s, e) => stateEvents++;
        rig.Doc.NavigateToCode += (s, e) => nav = e;
        rig.Doc.Error += (s, e) => docError = e;
        rig.Session.Error += (s, e) => globalError = e;

        rig.Host.Send(new DocumentState { DocId = rig.Doc.Id, Dirty = true, CanUndo = true, HasSelection = true });
        rig.Host.Send(new NavigateToCode { DocId = rig.Doc.Id, File = @"C:\forms\Main.cs", Line = 42 });
        rig.Host.Send(new Error { DocId = 0, Text = "global" });
        Ui.WaitUntil(() => globalError != null);

        Assert.Equal(1, stateEvents);
        Assert.True(rig.Doc.IsDirty);
        Assert.True(rig.Doc.CanUndo);
        Assert.False(rig.Doc.CanRedo);
        Assert.True(rig.Doc.HasSelection);
        Assert.Equal(@"C:\forms\Main.cs", nav!.File);
        Assert.Equal(42, nav.Line);
        Assert.Same(rig.Doc, nav.Document);
        Assert.Null(docError);
        Assert.Null(globalError!.Document);
        Assert.Equal("global", globalError.Text);
    });

    [Fact]
    public void Commands_activate_reload_and_close_send_their_messages() => Ui.RunSta(() =>
    {
        using var rig = new Rig();
        rig.Doc.Activate();
        rig.Doc.Command(DesignCommands.Undo);
        rig.Doc.Reload();
        rig.Host.WaitFor<ActivateDocument>(m => m.DocId == rig.Doc.Id);
        Assert.Equal("Undo", rig.Host.WaitFor<Command>().Name);
        rig.Host.WaitFor<ReloadDocument>(m => m.DocId == rig.Doc.Id);
        rig.Doc.Close();
        rig.Host.WaitFor<CloseDocument>(m => m.DocId == rig.Doc.Id);
        Assert.True(rig.Doc.IsClosed);
        Assert.Equal(0, rig.View.WindowId); // its surface went with it
    });

    [Fact]
    public void SaveAsync_completes_when_the_host_answers() => Ui.RunSta(() =>
    {
        using var rig = new Rig();
        Task<DocumentSavedEventArgs> task = rig.Doc.SaveAsync(@"C:\forms\Copy.radform.json");
        var save = rig.Host.WaitFor<SaveDocument>();
        Assert.Equal(@"C:\forms\Copy.radform.json", save.Path);
        Assert.False(task.IsCompleted);
        rig.Host.Send(new DocumentSaved { DocId = rig.Doc.Id, Ok = false, Error = "disk full" });
        Ui.WaitUntil(() => task.IsCompleted);
        Assert.False(task.Result.Ok);
        Assert.Equal("disk full", task.Result.Error);
    });

    [Fact]
    public void A_popup_is_shown_at_its_owner_offset_and_closed_by_the_host() => Ui.RunSta(() =>
    {
        using var rig = new Rig(scale: 1.5);
        rig.Host.Send(new WindowOpened
        {
            WindowId = 20, DocId = rig.Doc.Id, Role = WindowRole.Popup, OwnerWindowId = SurfaceId,
            X = 30, Y = 45, Width = 90, Height = 60,
        });
        rig.Host.WaitFor<AttachWindow>(a => a.WindowId == 20);
        var resize = rig.Host.WaitFor<ResizeWindow>(r => r.WindowId == 20);
        Assert.Equal(90, resize.Width);
        Assert.Equal(60, resize.Height);
        var popup = rig.Session.Presenter.GetPopup(20)!;
        Assert.True(popup.IsOpen);
        Assert.Same(rig.View, popup.PlacementTarget);
        Assert.Equal(20, popup.HorizontalOffset, 3); // 30 px / 1.5
        Assert.Equal(30, popup.VerticalOffset, 3);
        Assert.False(rig.Session.Presenter.GetView(20)!.FocusOnClick);

        rig.Host.Send(new WindowMoved { WindowId = 20, X = 60, Y = 0, Width = 90, Height = 60 });
        Ui.WaitUntil(() => Math.Abs(popup.HorizontalOffset - 40) < 0.001);

        rig.Host.Send(new WindowClosed { WindowId = 20 });
        Ui.WaitUntil(() => !popup.IsOpen);
        Assert.Null(rig.Session.Presenter.GetPopup(20));
        Assert.Null(rig.Session.GetWindow(20));
    });

    [Fact]
    public void Closing_a_dialog_asks_the_host_and_the_host_closes_it() => Ui.RunSta(() =>
    {
        using var rig = new Rig(scale: 1.0);
        rig.Host.Send(new WindowOpened
        {
            WindowId = 21, DocId = rig.Doc.Id, Role = WindowRole.Dialog, OwnerWindowId = SurfaceId,
            X = 10, Y = 10, Width = 120, Height = 80, Title = "Items", Modal = false,
        });
        rig.Host.WaitFor<ResizeWindow>(r => r.WindowId == 21 && r.Width == 120 && r.Height == 80);
        var dialog = rig.Session.Presenter.GetDialog(21)!;
        Assert.True(dialog.IsVisible);
        Assert.Equal("Items", dialog.Title);
        Assert.Same(rig.Window, dialog.Owner);

        dialog.Close(); // the user clicks X: the client asks, the host decides
        rig.Host.WaitFor<CloseWindow>(c => c.WindowId == 21);
        Assert.True(dialog.IsVisible);

        rig.Host.Send(new WindowClosed { WindowId = 21 });
        Ui.WaitUntil(() => !dialog.IsVisible);
        Assert.Null(rig.Session.Presenter.GetDialog(21));
    });

    private sealed class FakeBuffers : ICodeBufferProvider
    {
        public bool Throw { get; set; }
        public string? Applied { get; private set; }
        public string? LastBaseHash { get; private set; }
        public int AppliedLine { get; private set; }

        public bool TryGetText(string file, out string text)
        {
            if (Throw) throw new InvalidOperationException("buffer locked");
            text = file.EndsWith("Open.cs", StringComparison.Ordinal) ? "// unsaved buffer" : "";
            return text.Length > 0;
        }

        public void ApplyEdit(DesignDocument? document, string file, string text, int line, string baseHash)
        {
            LastBaseHash = baseHash;
            Applied = text;
            AppliedLine = line;
        }
    }

    [Fact]
    public void Code_requests_are_always_answered() => Ui.RunSta(() =>
    {
        using var rig = new Rig();
        // Default provider: nothing open, so the host reads from disk.
        rig.Host.Send(new CodeRequest { RequestId = 1, DocId = rig.Doc.Id, File = @"C:\forms\Main.cs" });
        var r1 = rig.Host.WaitFor<CodeResponse>(r => r.RequestId == 1);
        Assert.False(r1.Found);

        var buffers = new FakeBuffers();
        rig.Session.CodeBuffers = buffers;
        rig.Host.Send(new CodeRequest { RequestId = 2, DocId = rig.Doc.Id, File = @"C:\forms\Open.cs" });
        var r2 = rig.Host.WaitFor<CodeResponse>(r => r.RequestId == 2);
        Assert.True(r2.Found);
        Assert.Equal("// unsaved buffer", r2.Text);

        buffers.Throw = true;
        rig.Host.Send(new CodeRequest { RequestId = 3, DocId = rig.Doc.Id, File = @"C:\forms\Open.cs" });
        Assert.False(rig.Host.WaitFor<CodeResponse>(r => r.RequestId == 3).Found);

        buffers.Throw = false;
        rig.Host.Send(new ApplyCodeEdit { DocId = rig.Doc.Id, File = @"C:\forms\Open.cs", Text = "class X {}", Line = 5, BaseHash = "abc" });
        Ui.WaitUntil(() => buffers.Applied != null);
        Assert.Equal("class X {}", buffers.Applied);
        Assert.Equal(5, buffers.AppliedLine);
        Assert.Equal("abc", buffers.LastBaseHash);
    });

    [Fact]
    public void The_default_provider_writes_the_edit_to_disk_and_navigates() => Ui.RunSta(() =>
    {
        using var rig = new Rig();
        var file = Path.Combine(Path.GetTempPath(), "radsharp-codeedit-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllBytes(file, new byte[] { 0xEF, 0xBB, 0xBF, (byte)'x' });
        try
        {
            NavigateToCodeEventArgs? nav = null;
            rig.Doc.NavigateToCode += (s, e) => nav = e;
            rig.Host.Send(new ApplyCodeEdit { DocId = rig.Doc.Id, File = file, Text = "partial class Main { void Click() { } }", Line = 3, BaseHash = CodeHash.Compute("x") });
            Ui.WaitUntil(() => nav != null);
            Assert.Equal(file, nav!.File);
            Assert.Equal(3, nav.Line);
            var bytes = File.ReadAllBytes(file);
            Assert.Equal(0xEF, bytes[0]); // BOM kept
            Assert.Equal("partial class Main { void Click() { } }", File.ReadAllText(file));
        }
        finally { File.Delete(file); }
    });

    [Fact]
    public void Host_crash_raises_Disconnected_and_detaches_views() => Ui.RunSta(() =>
    {
        using var rig = new Rig();
        DisconnectedEventArgs? args = null;
        rig.Session.Disconnected += (s, e) => args = e;
        rig.Host.Kill();
        Ui.WaitUntil(() => args != null);
        Assert.False(rig.Session.IsConnected);
        Assert.Equal(0, rig.View.WindowId);
        Assert.Empty(rig.Doc.Windows);
        Assert.False(rig.Doc.SaveAsync().Result.Ok); // fails at once instead of hanging
    });
}
