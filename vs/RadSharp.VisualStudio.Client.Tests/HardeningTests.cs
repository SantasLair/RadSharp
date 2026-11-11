using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using RadSharp.DesignHost.Protocol;
using Xunit;
using P = RadSharp.DesignHost.Protocol;

namespace RadSharp.VisualStudio.Client.Tests;

/// <summary>Regression tests for the review findings: edit safety, typing, capture, frame limits, coalescing, saves.</summary>
public class HardeningTests
{
    private const int SurfaceId = SessionTests.SurfaceId;

    // ---- BaseHash / code edits ----

    [Fact]
    public void CodeHash_is_lowercase_hex_sha256_of_the_utf8_text()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", CodeHash.Compute("abc"));
        var text = "class Ü { } // 😀";
        using var sha = SHA256.Create();
        var expected = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        Assert.Equal(expected, CodeHash.Compute(text));
        Assert.True(CodeHash.Matches(text, expected));
        Assert.False(CodeHash.Matches(text + " ", expected));
        Assert.False(CodeHash.Matches(text, ""));
    }

    private static string TempCs() => Path.Combine(Path.GetTempPath(), "radsharp-hardening-" + Guid.NewGuid().ToString("N") + ".cs");

    [Fact]
    public void A_stale_BaseHash_applies_nothing_and_reports_an_error() => Ui.RunSta(() =>
    {
        using var rig = new SessionTests.Rig();
        var file = TempCs();
        File.WriteAllText(file, "original", new UTF8Encoding(false));
        try
        {
            string? error = null;
            NavigateToCodeEventArgs? nav = null;
            rig.Session.Error += (s, e) => error = e.Text;
            rig.Doc.NavigateToCode += (s, e) => nav = e;
            rig.Host.Send(new ApplyCodeEdit { DocId = rig.Doc.Id, File = file, Text = "edited", Line = 1, BaseHash = CodeHash.Compute("what the host saw") });
            Ui.WaitUntil(() => error != null);
            Assert.Equal("The code file changed while the handler was being added; try again.", error);
            Assert.Equal("original", File.ReadAllText(file));
            Assert.Null(nav);
        }
        finally { File.Delete(file); }
    });

    [Fact]
    public void A_matching_BaseHash_applies_the_edit() => Ui.RunSta(() =>
    {
        using var rig = new SessionTests.Rig();
        var file = TempCs();
        File.WriteAllText(file, "original", Encoding.Unicode);
        try
        {
            NavigateToCodeEventArgs? nav = null;
            rig.Doc.NavigateToCode += (s, e) => nav = e;
            rig.Host.Send(new ApplyCodeEdit { DocId = rig.Doc.Id, File = file, Text = "edited", Line = 2, BaseHash = CodeHash.Compute("original") });
            Ui.WaitUntil(() => nav != null);
            Assert.Equal("edited", File.ReadAllText(file));
        }
        finally { File.Delete(file); }
    });

    [Theory]
    [InlineData("utf8bom")]
    [InlineData("utf8")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    public void File_write_keeps_the_encoding(string kind)
    {
        Encoding enc = kind switch
        {
            "utf8bom" => new UTF8Encoding(true),
            "utf8" => new UTF8Encoding(false),
            "utf16le" => new UnicodeEncoding(false, true),
            _ => new UnicodeEncoding(true, true),
        };
        var path = TempCs();
        try
        {
            File.WriteAllText(path, "old", enc);
            FileText.WritePreservingEncoding(path, "new ü");
            var expected = enc.GetPreamble().Concat(enc.GetBytes("new ü")).ToArray();
            Assert.Equal(expected, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_new_file_is_utf8_without_bom()
    {
        var path = TempCs();
        try
        {
            FileText.WritePreservingEncoding(path, "x");
            Assert.Equal(new byte[] { (byte)'x' }, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    // ---- Typing through real WPF routing ----

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);

    [Fact]
    public void A_typed_letter_reaches_the_host_as_KeyDown_and_TextInput() => Ui.RunSta(() =>
    {
        using var rig = new SessionTests.Rig();
        var source = (HwndSource)PresentationSource.FromVisual(rig.View)!;
        SetFocus(source.Handle);
        Keyboard.Focus(rig.View);
        Ui.WaitUntil(() => rig.View.IsKeyboardFocused, 2000, "keyboard focus");

        // What Windows delivers for pressing A: WM_KEYDOWN, then the WM_CHAR that TranslateMessage derives from it.
        SendMessage(source.Handle, 0x0100, (IntPtr)0x41, (IntPtr)0x001E0001);
        SendMessage(source.Handle, 0x0102, (IntPtr)'a', (IntPtr)0x001E0001);
        SendMessage(source.Handle, 0x0101, (IntPtr)0x41, unchecked((IntPtr)(int)0xC01E0001));

        var down = rig.Host.WaitFor<P.KeyDown>(k => k.Key == "A");
        Assert.False(down.Repeat);
        var text = rig.Host.WaitFor<P.TextInput>();
        Assert.Equal("a", text.Text);
        rig.Host.WaitFor<P.KeyUp>(k => k.Key == "A");
    });

    [Fact]
    public void Text_keys_stay_unhandled_but_other_keys_are_handled() => Ui.RunSta(() =>
    {
        using var rig = new SessionTests.Rig();
        KeyEventArgs Raise(Key key)
        {
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(rig.View)!, 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
            rig.View.RaiseEvent(e);
            return e;
        }
        Assert.False(Raise(Key.A).Handled);
        Assert.False(Raise(Key.Space).Handled);
        Assert.False(Raise(Key.OemComma).Handled);
        Assert.False(Raise(Key.D5).Handled);
        Assert.True(Raise(Key.Enter).Handled);
        Assert.True(Raise(Key.Back).Handled);
        Assert.True(Raise(Key.Left).Handled);
        Assert.True(Raise(Key.F5).Handled);
        rig.Host.WaitFor<P.KeyDown>(k => k.Key == "Enter");
    });

    // ---- Lost capture ----

    [Fact]
    public void Losing_mouse_capture_sends_MouseUp_for_buttons_that_are_down() => Ui.RunSta(() =>
    {
        using var rig = new SessionTests.Rig();
        rig.View.SendMouse(RemoteWindowView.MouseKind.Down, new Point(10, 10), P.MouseButton.Left, ModifierKeys.None, 1);
        rig.View.SendMouse(RemoteWindowView.MouseKind.Down, new Point(10, 10), P.MouseButton.Right, ModifierKeys.None, 1);
        rig.View.SendMouse(RemoteWindowView.MouseKind.Up, new Point(10, 10), P.MouseButton.Right, ModifierKeys.None, 1);
        rig.Host.WaitFor<P.MouseUp>(m => m.Button == P.MouseButton.Right);
        rig.Host.Clear();

        rig.View.CaptureMouse();
        rig.View.ReleaseMouseCapture(); // OnLostMouseCapture
        var up = rig.Host.WaitFor<P.MouseUp>(m => m.Button == P.MouseButton.Left);
        Assert.Equal(SurfaceId, up.WindowId);
        Thread.Sleep(100);
        Assert.DoesNotContain(rig.Host.All<P.MouseUp>(), m => m.Button == P.MouseButton.Right); // already up: not repeated

        rig.Host.Clear();
        rig.View.ReleaseHostButtons(); // nothing left down
        Thread.Sleep(100);
        Assert.Empty(rig.Host.All<P.MouseUp>());
    });

    [Fact]
    public void Detaching_mid_drag_sends_MouseUp() => Ui.RunSta(() =>
    {
        using var rig = new SessionTests.Rig();
        rig.View.SendMouse(RemoteWindowView.MouseKind.Down, new Point(5, 5), P.MouseButton.Left, ModifierKeys.None, 1);
        rig.Host.Clear();
        rig.View.Detach();
        rig.Host.WaitFor<P.MouseUp>(m => m.Button == P.MouseButton.Left);
    });

    // ---- Frame hardening ----

    [Fact]
    public void Frames_outside_any_real_window_are_dropped_without_throwing() => Ui.RunSta(() =>
    {
        using var rig = new SessionTests.Rig();
        rig.Host.Send(new Frame { WindowId = SurfaceId, X = 0, Y = 0, Width = 2, Height = 2, Pixels = SessionTests.Fill(2, 2, 0xFF112233) });
        Ui.WaitUntil(() => rig.View.FrameCount == 1);
        int good = rig.View.FrameCount;

        // X + Width overflows int arithmetic; the others are far beyond the cap. All must simply be ignored.
        rig.Host.Send(new Frame { WindowId = SurfaceId, X = int.MaxValue, Y = 0, Width = 1, Height = 1, Pixels = new byte[4] });
        rig.Host.Send(new Frame { WindowId = SurfaceId, X = 0, Y = 0, Width = 40000, Height = 1, Pixels = new byte[40000 * 4] });
        rig.Host.Send(new Frame { WindowId = SurfaceId, X = 0, Y = int.MaxValue - 1, Width = 2, Height = 1, Pixels = new byte[8] });
        rig.Host.Send(new Frame { WindowId = SurfaceId, X = 1, Y = 1, Width = 1, Height = 1, Pixels = SessionTests.Fill(1, 1, 0xFF445566) });
        Ui.WaitUntil(() => rig.View.FrameCount == good + 1);
        Assert.Equal(0xFF445566u, SessionTests.PixelAt(rig.View, 1, 1));
        Assert.True(rig.Session.IsConnected);
    });

    // ---- Coalescing ----

    private static (DesignHostConnection Conn, List<Message> Got) Connect(FakeHost host)
    {
        var conn = new DesignHostConnection(host.ClientStream, Dispatcher.CurrentDispatcher);
        conn.HandshakeAsync("tests").Wait();
        var got = new List<Message>();
        conn.MessageReceived += (s, e) => got.Add(e.Message);
        conn.StartReading();
        return (conn, got);
    }

    private static void WaitRead(DesignHostConnection conn, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (conn.MessagesRead < count)
        {
            Assert.True(DateTime.UtcNow < deadline, "reader did not read everything");
            Thread.Sleep(5); // deliberately not pumping the dispatcher: this is the slow-UI-thread case
        }
    }

    private static Frame F(int window, int x, int y, int w, int h, byte fill = 0) =>
        new Frame { WindowId = window, X = x, Y = y, Width = w, Height = h, Pixels = Enumerable.Repeat(fill, w * h * 4).ToArray() };

    [Fact]
    public void A_backlog_of_frames_is_coalesced_without_losing_pixels_or_order() => Ui.RunSta(() =>
    {
        using var host = new FakeHost();
        var (conn, got) = Connect(host);
        using var _ = conn;
        host.Send(new WindowOpened { WindowId = 1, DocId = 1, Role = WindowRole.Surface });
        for (int i = 0; i < 50; i++) host.Send(F(1, 0, 0, 10, 10, (byte)i)); // each covers the previous: only the last survives
        host.Send(F(1, 20, 0, 5, 5, 200));                                    // elsewhere: must survive
        host.Send(F(2, 0, 0, 10, 10, 7));                                     // another window: untouched
        host.Send(F(1, 2, 2, 3, 3, 201));                                     // inside the full frame but later: kept (it is newer)
        host.Send(new WindowClosed { WindowId = 1 });
        host.Send(F(1, 0, 0, 10, 10, 9));                                     // after the close: nothing before it may be dropped for it
        host.Send(new Log { Level = 1, Text = "end" });
        WaitRead(conn, 1 + 50 + 1 + 1 + 1 + 1 + 1 + 1);
        Assert.Empty(got); // nothing delivered while the UI thread is busy
        Ui.WaitUntil(() => got.Count > 0 && got[got.Count - 1] is Log, 5000, "delivery");

        var frames1 = got.OfType<Frame>().Where(f => f.WindowId == 1).ToList();
        Assert.Equal(49, frames1[0].Pixels[0]); // the 50th frame
        Assert.Equal(new[] { (0, 0), (20, 0), (2, 2), (0, 0) }, frames1.Select(f => (f.X, f.Y)).ToArray());
        int closed = got.FindIndex(m => m is WindowClosed);
        Assert.Equal(0, got.FindIndex(m => m is WindowOpened));
        Assert.True(got.FindIndex(closed, m => m is Frame f && f.WindowId == 1 && f.Pixels[0] == 9) > closed);
        Assert.Equal(got.Count - 1, got.FindIndex(m => m is Log));
        Assert.Single(got.OfType<Frame>(), f => f.WindowId == 2);
    });

    [Fact]
    public void Frames_are_never_merged_across_a_window_reopen() => Ui.RunSta(() =>
    {
        using var host = new FakeHost();
        var (conn, got) = Connect(host);
        using var _ = conn;
        host.Send(F(1, 0, 0, 4, 4, 1));
        host.Send(new WindowClosed { WindowId = 1 });
        host.Send(new WindowOpened { WindowId = 1, DocId = 1, Role = WindowRole.Surface });
        host.Send(F(1, 0, 0, 4, 4, 2));
        WaitRead(conn, 4);
        Ui.WaitUntil(() => got.Count == 4, 5000, "delivery");
        Assert.Equal(1, ((Frame)got[0]).Pixels[0]);
        Assert.Equal(2, ((Frame)got[3]).Pixels[0]);
    });

    [Fact]
    public void Messages_after_dispose_are_dropped_not_run_on_the_reader_thread() => Ui.RunSta(() =>
    {
        using var host = new FakeHost();
        var (conn, got) = Connect(host);
        conn.Dispose();
        try { host.Send(new Log { Level = 1, Text = "late" }); }
        catch (IOException) { }
        Thread.Sleep(150);
        Ui.DoEvents();
        Assert.Empty(got);
    });

    // ---- Malformed frames ----

    [Fact]
    public void A_malformed_frame_disconnects_cleanly() => Ui.RunSta(() =>
    {
        using var rig = new SessionTests.Rig();
        DisconnectedEventArgs? args = null;
        int count = 0;
        rig.Session.Disconnected += (s, e) =>
        {
            args = e;
            count++;
        };
        // A Frame (0x85) whose payload is cut short: length 5 = type byte + 4 payload bytes.
        rig.Host.SendRaw(new byte[] { 5, 0, 0, 0, 0x85, 1, 0, 0, 0 });
        Ui.WaitUntil(() => args != null);
        Assert.False(rig.Session.IsConnected);
        Assert.False(string.IsNullOrEmpty(args!.Reason));
        Assert.IsAssignableFrom<InvalidDataException>(args.Exception);
        Ui.DoEvents();
        Assert.Equal(1, count);
    });

    // ---- Saves ----

    [Fact]
    public void The_document_path_changes_only_when_the_host_confirms_the_save() => Ui.RunSta(() =>
    {
        using var rig = new SessionTests.Rig();
        var original = rig.Doc.Path;
        var t1 = rig.Doc.SaveAsync(@"C:\forms\Copy.radform.json");
        rig.Host.WaitFor<SaveDocument>();
        Assert.Equal(original, rig.Doc.Path); // not yet
        rig.Host.Send(new DocumentSaved { DocId = rig.Doc.Id, Ok = false, Error = "disk full" });
        Ui.WaitUntil(() => t1.IsCompleted);
        Assert.Equal(original, rig.Doc.Path);

        var t2 = rig.Doc.SaveAsync(@"C:\forms\Copy.radform.json");
        rig.Host.Send(new DocumentSaved { DocId = rig.Doc.Id, Ok = true });
        Ui.WaitUntil(() => t2.IsCompleted);
        Assert.True(t2.Result.Ok);
        Assert.Equal(@"C:\forms\Copy.radform.json", rig.Doc.Path);
    });

    [Fact]
    public void Replies_complete_saves_in_order() => Ui.RunSta(() =>
    {
        using var rig = new SessionTests.Rig();
        var a = rig.Doc.SaveAsync(@"C:\a.json");
        var b = rig.Doc.SaveAsync(@"C:\b.json");
        rig.Host.Send(new DocumentSaved { DocId = rig.Doc.Id, Ok = true });
        Ui.WaitUntil(() => a.IsCompleted);
        Assert.False(b.IsCompleted);
        Assert.Equal(@"C:\a.json", rig.Doc.Path);
        rig.Host.Send(new DocumentSaved { DocId = rig.Doc.Id, Ok = true });
        Ui.WaitUntil(() => b.IsCompleted);
        Assert.Equal(@"C:\b.json", rig.Doc.Path);
    });

    [Fact]
    public void A_save_without_an_answer_times_out_and_does_not_wedge_the_next_one() => Ui.RunSta(() =>
    {
        var old = DesignDocument.SaveTimeout;
        DesignDocument.SaveTimeout = TimeSpan.FromMilliseconds(150);
        try
        {
            using var rig = new SessionTests.Rig();
            var original = rig.Doc.Path;
            var lost = rig.Doc.SaveAsync(@"C:\lost.json");
            Ui.WaitUntil(() => lost.IsCompleted, 5000, "save timeout");
            Assert.False(lost.Result.Ok);
            Assert.Equal(original, rig.Doc.Path);

            var next = rig.Doc.SaveAsync(@"C:\next.json");
            rig.Host.Send(new DocumentSaved { DocId = rig.Doc.Id, Ok = true });
            Ui.WaitUntil(() => next.IsCompleted);
            Assert.True(next.Result.Ok);
            Assert.Equal(@"C:\next.json", rig.Doc.Path);
        }
        finally { DesignDocument.SaveTimeout = old; }
    });

    [Fact]
    public void Closing_the_document_fails_pending_saves() => Ui.RunSta(() =>
    {
        using var rig = new SessionTests.Rig();
        var t = rig.Doc.SaveAsync();
        rig.Doc.Close();
        Assert.True(t.IsCompleted);
        Assert.False(t.Result.Ok);
    });

    // ---- Session robustness ----

    private sealed class ThrowingPresenter : RemoteWindowPresenter
    {
        public override void Show(DesignSession session, RemoteWindowInfo info, RemoteWindowView? owner) =>
            throw new NullReferenceException("no owner");
    }

    [Fact]
    public void A_dialog_that_cannot_be_shown_is_logged_and_closed_in_the_host() => Ui.RunSta(() =>
    {
        using var rig = new SessionTests.Rig();
        rig.Session.Presenter = new ThrowingPresenter();
        var log = new List<string>();
        rig.Session.Log += (s, e) => log.Add(e.Text);
        rig.Host.Send(new WindowOpened { WindowId = 30, DocId = rig.Doc.Id, Role = WindowRole.Dialog, OwnerWindowId = SurfaceId, Width = 100, Height = 80 });
        rig.Host.WaitFor<CloseWindow>(c => c.WindowId == 30);
        Assert.Contains(log, l => l.Contains("no owner"));
        Assert.True(rig.Session.IsConnected);
    });

    [Fact]
    public void A_throwing_handler_is_logged_and_later_messages_still_arrive() => Ui.RunSta(() =>
    {
        using var rig = new SessionTests.Rig();
        var log = new List<string>();
        rig.Session.Log += (s, e) => log.Add(e.Text);
        rig.Session.WindowMoved += (s, e) => throw new InvalidCastException("boom");
        rig.Host.Send(new WindowMoved { WindowId = SurfaceId, X = 1, Y = 1, Width = 5, Height = 5 });
        rig.Host.Send(new Frame { WindowId = SurfaceId, X = 0, Y = 0, Width = 1, Height = 1, Pixels = new byte[4] });
        Ui.WaitUntil(() => rig.View.FrameCount == 1);
        Assert.Contains(log, l => l.Contains("boom"));
    });
}
