using RadSharp.Design;
using P = RadSharp.DesignHost.Protocol;

namespace RadSharp.DesignHost.Tests;

/// <summary>A stream whose writes block while the gate is closed: a client that reads slowly.</summary>
sealed class GatedStream(Stream inner) : Stream
{
    readonly ManualResetEventSlim _open = new(true);

    public void CloseGate() => _open.Reset();
    public void OpenGate() => _open.Set();

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count)
    {
        _open.Wait();
        inner.Write(buffer, offset, count);
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _open.Set(); inner.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>A message whose serialisation fails, like a frame over the size limit.</summary>
sealed class ExplodingMessage(Exception error) : P.Message
{
    public override byte MessageType => 0x8F;
    public override void Write(BinaryWriter w) => throw error;
    public override void Read(BinaryReader r) { }
}

/// <summary>Fixes from the design-host review: bad input, bad sizes, bad writes, backpressure, input routing.</summary>
[Collection("DesignHost")]
public class HostRobustnessTests
{
    const int Doc = 1;

    static void ApplyFrame(byte[] canvas, int canvasWidth, P.Frame f)
    {
        for (int row = 0; row < f.Height; row++)
            Buffer.BlockCopy(f.Pixels, row * f.Width * 4, canvas, ((f.Y + row) * canvasWidth + f.X) * 4, f.Width * 4);
    }

    // ---- 1. sizes ----

    [Fact]
    public void Absurd_or_negative_sizes_are_ignored_or_hide_the_window()
    {
        var (client, panes) = HostTests.OpenHello();
        using var _ = client;
        int Width() => client.Ui(() => client.Server!.Window(panes.Surface)!.Width);

        client.Send(new P.ResizeWindow { WindowId = panes.Surface, Width = 20000, Height = 100, Scale = 1 });
        client.Send(new P.ResizeWindow { WindowId = panes.Surface, Width = 16384, Height = 16384, Scale = 1 });   // 268 M pixels
        client.Settle();
        Assert.Equal(800, Width());

        client.Send(new P.ResizeWindow { WindowId = panes.Surface, Width = -5, Height = 10, Scale = 1 });
        client.Settle();
        Assert.Equal(0, Width());   // hidden: no frames
        client.Send(new P.ResizeWindow { WindowId = panes.Surface, Width = 300, Height = 200, Scale = 1 });
        Assert.Equal(300 * 200 * 4, client.Expect<P.Frame>(f => f.WindowId == panes.Surface && f.Width == 300).Pixels.Length);
    }

    [Fact]
    public void A_failed_resize_keeps_the_old_back_buffer()
    {
        using var window = new HostWindow(1, P.WindowRole.Surface, 1, new Form());
        Assert.True(window.Resize(100, 50));
        Assert.False(window.Resize(100000, 5));
        Assert.False(window.Resize(-1, 5));
        Assert.Equal((100, 50), (window.Width, window.Height));
        Assert.NotNull(window.SnapshotPixels());
        Assert.True(window.Resize(0, 0));
        Assert.Null(window.SnapshotPixels());
    }

    // ---- 2. big frames and failing writes ----

    [Fact]
    public void A_large_dirty_rectangle_is_sent_as_bands()
    {
        var (client, panes) = HostTests.OpenHello();
        using var _ = client;
        client.Send(new P.ResizeWindow { WindowId = panes.Surface, Width = 4000, Height = 2200, Scale = 1 });
        int rows = 0, count = 0;
        while (rows < 2200)
        {
            var f = client.Expect<P.Frame>(f => f.WindowId == panes.Surface && f.Width == 4000);
            Assert.InRange(f.Pixels.Length, 1, HostWindow.MaxFrameBytes);
            Assert.Equal(rows, f.Y);
            rows += f.Height;
            count++;
        }
        Assert.Equal(2200, rows);
        Assert.True(count > 1, "the 35 MB rectangle should be split");
    }

    [Theory]
    [InlineData(typeof(InvalidDataException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NotSupportedException))]
    public void A_message_that_cannot_be_written_is_dropped_and_the_host_carries_on(Type errorType)
    {
        var (client, _) = HostTests.OpenHello();
        using var _ = client;
        client.Server!.Send(new ExplodingMessage((Exception)Activator.CreateInstance(errorType, "too big")!));
        client.Send(new P.Hello { ProtocolVersion = P.ProtocolInfo.Version });
        client.Expect<P.HelloAck>();
        Assert.True(client.HostThread!.IsAlive);
    }

    // ---- 3. windows that size themselves ----

    [Fact]
    public void A_dialog_that_resizes_or_moves_itself_is_followed()
    {
        var (client, panes) = HostTests.OpenHello();
        using var _ = client;
        var dialog = client.Ui(() =>
        {
            var d = new Form { Caption = "Self-sizing" };
            d.SetBounds(0, 0, 200, 100);
            d.ApplyScale(1);
            d.Show();
            return d;
        });
        var opened = client.Expect<P.WindowOpened>(w => w.Role == P.WindowRole.Dialog);
        client.Show(opened.WindowId, 200, 100);

        client.Ui(() => dialog.Resize(320, 180));
        var moved = client.Expect<P.WindowMoved>(m => m.WindowId == opened.WindowId);
        Assert.Equal((320, 180), (moved.Width, moved.Height));
        var frame = client.Expect<P.Frame>(f => f.WindowId == opened.WindowId && f.Width == 320 && f.Height == 180);
        Assert.Equal(320 * 180 * 4, frame.Pixels.Length);

        client.Ui(() => dialog.SetBounds(40, 50, 320, 180));
        var placed = client.Expect<P.WindowMoved>(m => m.WindowId == opened.WindowId && m.X == 40);
        Assert.Equal((40, 50), (placed.X, placed.Y));
    }

    [Fact]
    public void A_pane_whose_form_outgrows_its_buffer_is_clipped_not_followed()
    {
        var (client, panes) = HostTests.OpenHello();
        using var _ = client;
        client.Settle();
        client.Ui(() =>
        {
            var w = client.Server!.Window(panes.Surface)!;
            w.Form.Resize(1000, 700);
            w.Form.InvalidateAll();
        });
        var frame = client.Expect<P.Frame>(f => f.WindowId == panes.Surface);
        Assert.True(frame.X + frame.Width <= 800 && frame.Y + frame.Height <= 600, $"{frame.X},{frame.Y} {frame.Width}x{frame.Height}");
        Assert.Equal(frame.Width * frame.Height * 4, frame.Pixels.Length);
        Assert.Throws<TimeoutException>(() => client.Expect<P.WindowMoved>(timeoutMs: 300));
        Assert.Equal(800, client.Ui(() => client.Server!.Window(panes.Surface)!.Width));
    }

    // ---- 4. malformed input ----

    static byte[] Raw(byte type, Action<BinaryWriter> payload)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, new System.Text.UTF8Encoding(false), leaveOpen: true)) { w.Write(type); payload(w); }
        var body = ms.ToArray();
        return [.. BitConverter.GetBytes(body.Length), .. body];
    }

    static void AssertHostEndsCleanly(byte[] input)
    {
        var (clientEnd, hostEnd) = DuplexStream.CreatePair();
        var server = new DesignHostServer(hostEnd) { LogSink = (_, _) => { } };
        var thread = server.Start();
        clientEnd.Write(input, 0, input.Length);
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "the host should shut down, not hang");
        clientEnd.Dispose();
    }

    [Fact]
    public void Bad_utf8_in_a_message_shuts_the_host_down_cleanly() =>
        AssertHostEndsCleanly(Raw(P.Hello.Type, w => { w.Write(1); w.Write7BitEncodedInt(2); w.Write(new byte[] { 0xC3, 0x28 }); }));

    [Fact]
    public void A_short_payload_shuts_the_host_down_cleanly() =>
        AssertHostEndsCleanly(Raw(P.ResizeWindow.Type, w => w.Write(1)));

    [Fact]
    public void A_frame_with_a_wrong_pixel_count_shuts_the_host_down_cleanly() =>
        AssertHostEndsCleanly(Raw(P.Frame.Type, w => { w.Write(1); w.Write(0); w.Write(0); w.Write(2); w.Write(2); w.Write(3); w.Write(new byte[3]); }));

    // ---- 5. saving ----

    [Fact]
    public void A_failing_save_always_answers_and_leaves_the_document_alone()
    {
        var path = TestFiles.HelloForm();
        var dir = Path.GetDirectoryName(path)!;
        var (client, _) = HostTests.OpenHello(path);
        using var _ = client;
        var original = File.ReadAllText(path);

        foreach (var bad in new[] { Path.Combine(dir, "missing", "x.radform.json"), dir, "bad\0name" })
        {
            client.Send(new P.SaveDocument { DocId = Doc, Path = bad });
            var saved = client.Expect<P.DocumentSaved>();
            Assert.False(saved.Ok);
            Assert.NotEmpty(saved.Error);
        }
        Assert.Equal(path, client.Ui(() => client.Server!.Document(Doc)!.Path));   // a failed Save As did not move the document
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

        client.Send(new P.SaveDocument { DocId = Doc, Path = "" });
        Assert.True(client.Expect<P.DocumentSaved>().Ok);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void A_successful_save_as_moves_the_document_and_leaves_no_temp_file()
    {
        var path = TestFiles.HelloForm();
        var dir = Path.GetDirectoryName(path)!;
        var (client, _) = HostTests.OpenHello(path);
        using var _ = client;
        var other = Path.Combine(dir, "Other.radform.json");
        client.Send(new P.SaveDocument { DocId = Doc, Path = other });
        Assert.True(client.Expect<P.DocumentSaved>().Ok);
        Assert.Equal(other, client.Ui(() => client.Server!.Document(Doc)!.Path));
        Assert.Equal(2, Directory.GetFiles(dir).Length);
    }

    // ---- 7. backpressure ----

    [Fact]
    public async Task A_slow_client_does_not_make_the_outbox_grow_and_still_ends_up_with_the_right_picture()
    {
        var (clientEnd, hostEnd) = DuplexStream.CreatePair();
        var gate = new GatedStream(hostEnd);
        var server = new DesignHostServer(gate) { LogSink = (_, _) => { } };
        var thread = server.Start();
        using var client = new HostClient(clientEnd);
        var panes = client.Open(Doc, TestFiles.HelloForm());
        var canvas = new byte[800 * 600 * 4];
        ApplyFrame(canvas, 800, client.Show(panes.Surface, 800, 600));

        gate.CloseGate();   // the client stops reading
        int maxQueued = 0;
        for (int i = 0; i < 80; i++)
        {
            await server.InvokeAsync(() =>
            {
                var d = server.Document(Doc)!.Doc;
                if (d.Selection.Count > 0) d.Select(null);
                else d.Select(d.Form.FindControl("btnOk"));
                return 0;
            });
            Thread.Sleep(20);
            maxQueued = Math.Max(maxQueued, server.OutboxFrameCount);
        }
        Assert.True(maxQueued <= 2, $"{maxQueued} frames queued while the client was not reading");

        gate.OpenGate();
        try
        {
            while (true) ApplyFrame(canvas, 800, client.Expect<P.Frame>(f => f.WindowId == panes.Surface, timeoutMs: 700));
        }
        catch (TimeoutException) { }
        var host = await server.InvokeAsync(() => server.Window(panes.Surface)!.SnapshotPixels());
        Assert.Equal(host, canvas);
        Assert.Equal(0, server.OutboxFrameCount);

        client.Dispose();
        gate.Dispose();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
    }

    // ---- 8. / 9. modal windows ----

    static void ShowModalDialog(HostClient client, TaskCompletionSource<ModalResult> result) =>
        client.Ui(() => client.Server!.Post(() =>
        {
            var dialog = new Form { Caption = "Question" };
            dialog.SetBounds(0, 0, 200, 100);
            dialog.ApplyScale(1);
            result.SetResult(dialog.ShowModal());
        }));

    [Fact]
    public async Task A_button_release_reaches_the_window_that_got_the_press_even_after_a_modal_opened()
    {
        var (client, panes) = HostTests.OpenHello();
        using var _ = client;
        int Left() => client.Ui(() => client.Server!.Document(Doc)!.Doc.Form.FindControl("btnOk")!.Left);
        var (x, y) = client.ControlPoint(Doc, "btnOk");
        int start = Left();

        client.Move(panes.Surface, x, y);
        client.Send(new P.MouseDown { WindowId = panes.Surface, X = x, Y = y, Clicks = 1 });
        client.Move(panes.Surface, x + 24, y);
        client.Settle();
        int dragged = Left();
        Assert.NotEqual(start, dragged);

        var result = new TaskCompletionSource<ModalResult>();
        ShowModalDialog(client, result);
        var opened = client.Expect<P.WindowOpened>(w => w.Role == P.WindowRole.Dialog);
        client.Send(new P.MouseUp { WindowId = panes.Surface, X = x + 24, Y = y });
        client.Settle();

        client.Send(new P.CloseWindow { WindowId = opened.WindowId });
        client.Expect<P.WindowClosed>();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The drag ended with the release: a later move without a button must not drag any more.
        client.Move(panes.Surface, x + 100, y);
        client.Settle();
        Assert.Equal(dragged, Left());
    }

    [Fact]
    public async Task Closing_a_modal_window_the_client_never_showed_unblocks_the_modal_loop()
    {
        var (client, _) = HostTests.OpenHello();
        using var _ = client;
        var result = new TaskCompletionSource<ModalResult>();
        ShowModalDialog(client, result);
        var opened = client.Expect<P.WindowOpened>(w => w.Role == P.WindowRole.Dialog);
        Assert.True(opened.Modal);
        // The client failed to show it, so it was never attached: it just reports the close.
        client.Send(new P.CloseWindow { WindowId = opened.WindowId });
        Assert.Equal(opened.WindowId, client.Expect<P.WindowClosed>().WindowId);
        Assert.Equal(ModalResult.Cancel, await result.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        // The host is usable afterwards.
        Assert.Equal(0, client.Ui(() => 0));
    }

    // ---- 10. dirty state ----

    [Fact]
    public void Dirty_state_is_computed_only_after_changes_and_once_per_tick()
    {
        var (client, _) = HostTests.OpenHello();
        using var _ = client;
        client.Settle();
        int Evaluations() => client.Ui(() => client.Server!.Document(Doc)!.ModifiedEvaluations);
        int before = Evaluations();

        client.Ui(() =>
        {
            var d = client.Server!.Document(Doc)!.Doc;
            for (int i = 0; i < 20; i++) d.Select(i % 2 == 0 ? d.Form.FindControl("btnOk") : null);
        });
        client.Settle();
        Assert.Equal(before, Evaluations());   // selection alone never evaluates Modified

        client.Ui(() =>
        {
            var d = client.Server!.Document(Doc)!.Doc;
            for (int i = 0; i < 50; i++) d.NotifyChanged();
        });
        client.Settle();
        Assert.Equal(before + 1, Evaluations());   // 50 changes in one tick: one evaluation
    }
}
