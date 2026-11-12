using System.Reflection;

namespace RadSharp.DesignHost.Tests;

// Inside the namespace, so the protocol's MouseButton and Cursor win over RadSharp's (the host reference brings those in).
using RadSharp.DesignHost.Protocol;

public class ProtocolTests
{
    private static MouseMessage Mouse(MouseMessage m)
    {
        m.WindowId = 3; m.X = -12; m.Y = 345; m.Button = MouseButton.Middle; m.Mods = Modifiers.Ctrl | Modifiers.Shift;
        m.Clicks = 2; m.Dx = 1.5f; m.Dy = -120f;
        return m;
    }

    private static KeyMessage KeyMsg(KeyMessage m)
    {
        m.WindowId = 4; m.Key = "F9"; m.Mods = Modifiers.Alt | Modifiers.Meta; m.Repeat = true;
        return m;
    }

    /// <summary>One non-default instance of every message type.</summary>
    public static IEnumerable<Message> AllMessages() => new Message[]
    {
        new Hello { ProtocolVersion = 7, ClientName = "VS 2022 \u00e9\u4e2d" },
        new OpenDocument { DocId = 5, Path = @"C:\proj\Main.radform.json" },
        new CloseDocument { DocId = 6 },
        new ActivateDocument { DocId = 7 },
        new ReloadDocument { DocId = 8 },
        new SaveDocument { DocId = 9, Path = "/tmp/a.radform.json" },
        new AttachWindow { WindowId = 10 },
        new ResizeWindow { WindowId = 11, Width = 800, Height = 600, Scale = 1.25f },
        new FocusWindow { WindowId = 12, Focused = true },
        Mouse(new MouseMove()), Mouse(new MouseDown()), Mouse(new MouseUp()), Mouse(new MouseWheel()),
        KeyMsg(new KeyDown()), KeyMsg(new KeyUp()),
        new TextInput { WindowId = 13, Text = "h\u00e9llo \U0001F600" },
        new Command { DocId = 14, Name = "SelectAll" },
        new CloseWindow { WindowId = 15 },
        new CodeResponse { RequestId = 31, Found = true, Text = "class A\r\n{\r\n}\r\n// é中" },
        new HelloAck { ProtocolVersion = 1, HostVersion = "0.1.0-dev" },
        new WindowOpened { WindowId = 16, DocId = 5, Role = WindowRole.Dialog, OwnerWindowId = 2, X = -5, Y = 6, Width = 300, Height = 200, Title = "Open", Modal = true },
        new WindowClosed { WindowId = 17 },
        new WindowMoved { WindowId = 18, X = 1, Y = 2, Width = 3, Height = 4 },
        new Frame { WindowId = 19, X = 2, Y = 3, Width = 2, Height = 2, Pixels = new byte[] { 1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255 } },
        new Cursor { WindowId = 20, CursorName = "SizeNWSE" },
        new DocumentState { DocId = 21, Dirty = true, CanUndo = true, CanRedo = true, HasSelection = true },
        new DocumentSaved { DocId = 22, Ok = false, Error = "disk full" },
        new NavigateToCode { DocId = 23, File = @"C:\proj\Main.cs", Line = 42 },
        new Error { DocId = 24, Text = "boom" },
        new Log { Level = 2, Text = "careful" },
        new CodeRequest { RequestId = 32, DocId = 5, File = @"C:\proj\Main.cs" },
        new ApplyCodeEdit { DocId = 5, File = @"C:\proj\Main.cs", Text = "class A { void H() { } }", Line = 17, BaseHash = "0123abcd" },
    };

    public static IEnumerable<object[]> Cases() => AllMessages().Select(m => new object[] { m });

    private static Message RoundTrip(Message m)
    {
        var ms = new MemoryStream();
        new MessageWriter(ms).Write(m);
        ms.Position = 0;
        var back = new MessageReader(ms).Read();
        Assert.NotNull(back);
        return back!;
    }

    private static void AssertEqual(Message expected, Message actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        foreach (var p in expected.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var a = p.GetValue(expected);
            var b = p.GetValue(actual);
            if (a is byte[] ba) Assert.Equal(ba, (byte[])b!);
            else Assert.True(Equals(a, b), $"{expected.GetType().Name}.{p.Name}: expected {a}, got {b}");
        }
    }

    [Theory, MemberData(nameof(Cases))]
    public void EveryMessageRoundTrips(Message m) => AssertEqual(m, RoundTrip(m));

    [Fact]
    public void TableCoversEveryMessageClassWithUniqueTypeBytes()
    {
        var concrete = typeof(Message).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Message)) && !t.IsAbstract).ToList();
        var tested = AllMessages().Select(m => m.GetType()).ToHashSet();
        Assert.All(concrete, t => Assert.Contains(t, tested));
        var bytes = AllMessages().Select(m => m.MessageType).ToList();
        Assert.Equal(bytes.Count, bytes.Distinct().Count());
        Assert.Equal(3, ProtocolInfo.Version);
    }

    [Fact]
    public void FrameCarriesRealPixelBuffer()
    {
        const int w = 64, h = 48;
        var px = new byte[w * h * 4];
        new Random(1).NextBytes(px);
        var f = (Frame)RoundTrip(new Frame { WindowId = 1, X = 0, Y = 0, Width = w, Height = h, Pixels = px });
        Assert.Equal(px, f.Pixels);
        Assert.Equal(w, f.Width);
    }

    [Fact]
    public void MessagesRoundTripBackToBack()
    {
        var ms = new MemoryStream();
        var w = new MessageWriter(ms);
        var all = AllMessages().ToList();
        foreach (var m in all) w.Write(m);
        ms.Position = 0;
        var r = new MessageReader(ms);
        foreach (var m in all) AssertEqual(m, r.Read()!);
        Assert.Null(r.Read());
    }

    [Fact]
    public void EofReturnsNull() => Assert.Null(new MessageReader(new MemoryStream()).Read());

    [Fact]
    public void TruncatedFrameThrows()
    {
        var ms = new MemoryStream();
        new MessageWriter(ms).Write(new OpenDocument { DocId = 1, Path = "some/long/path.radform.json" });
        var bytes = ms.ToArray();
        foreach (var cut in new[] { 2, 4, 5, bytes.Length - 1 })
        {
            var r = new MessageReader(new MemoryStream(bytes, 0, cut));
            Assert.Throws<EndOfStreamException>(() => r.Read());
        }
    }

    [Fact]
    public void OversizedAndBadFramesThrow()
    {
        Assert.Throws<InvalidDataException>(() => new MessageReader(new MemoryStream(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 1 })).Read());
        Assert.Throws<InvalidDataException>(() => new MessageReader(new MemoryStream(new byte[] { 0, 0, 0, 0 })).Read());
        Assert.Throws<InvalidDataException>(() => new MessageReader(new MemoryStream(new byte[] { 1, 0, 0, 0, 0x7E })).Read());
        // Valid type, payload too short.
        Assert.Throws<InvalidDataException>(() => new MessageReader(new MemoryStream(new byte[] { 2, 0, 0, 0, OpenDocument.Type, 1 })).Read());
    }

    [Fact]
    public void ConcurrentWritersProduceIntactFrames()
    {
        const int threads = 8, perThread = 200;
        var ms = new MemoryStream();
        var w = new MessageWriter(ms);
        var pixels = new byte[200 * 4];
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            for (int i = 0; i < perThread; i++)
            {
                w.Write(new TextInput { WindowId = t, Text = new string((char)('a' + t), 50 + i) });
                w.Write(new Frame { WindowId = t, Width = i, Height = 1, Pixels = pixels[..(i * 4)] });
            }
        });
        ms.Position = 0;
        var r = new MessageReader(ms);
        var counts = new int[threads];
        Message? m;
        while ((m = r.Read()) != null)
        {
            if (m is TextInput ti)
            {
                Assert.All(ti.Text, c => Assert.Equal((char)('a' + ti.WindowId), c));
                counts[ti.WindowId]++;
            }
            else { var f = (Frame)m; Assert.Equal(f.Width * 4, f.Pixels.Length); }
        }
        Assert.All(counts, c => Assert.Equal(perThread, c));
    }

    static byte[] Raw(byte type, Action<BinaryWriter> payload)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, new System.Text.UTF8Encoding(false), leaveOpen: true)) { w.Write((byte)type); payload(w); }
        var body = ms.ToArray();
        return [.. BitConverter.GetBytes(body.Length), .. body];
    }

    [Fact]
    public void BadUtf8AndOtherDecodeErrorsBecomeInvalidData()
    {
        // A string whose bytes are not valid UTF-8.
        var bad = Raw(Hello.Type, w => { w.Write(1); w.Write7BitEncodedInt(2); w.Write(new byte[] { 0xC3, 0x28 }); });
        Assert.Throws<InvalidDataException>(() => new MessageReader(new MemoryStream(bad)).Read());
        // A string length prefix that is malformed (more than 5 bytes of 7-bit encoding).
        var format = Raw(Hello.Type, w => { w.Write(1); w.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F }); });
        Assert.Throws<InvalidDataException>(() => new MessageReader(new MemoryStream(format)).Read());
    }

    [Fact]
    public void FrameValidatesItsPixelBuffer()
    {
        Assert.Throws<InvalidDataException>(() => new MessageReader(new MemoryStream(
            Raw(Frame.Type, w => { w.Write(1); w.Write(0); w.Write(0); w.Write(2); w.Write(2); w.Write(8); w.Write(new byte[8]); }))).Read());   // 2x2 needs 16
        Assert.Throws<InvalidDataException>(() => new MessageReader(new MemoryStream(
            Raw(Frame.Type, w => { w.Write(1); w.Write(0); w.Write(0); w.Write(-2); w.Write(-2); w.Write(16); w.Write(new byte[16]); }))).Read());
        Assert.Throws<InvalidDataException>(() => new MessageReader(new MemoryStream(
            Raw(Frame.Type, w => { w.Write(1); w.Write(0); w.Write(0); w.Write(1); w.Write(1); w.Write(int.MaxValue); }))).Read());
        Assert.Throws<InvalidDataException>(() => new MessageReader(new MemoryStream(
            Raw(Frame.Type, w => { w.Write(1); w.Write(0); w.Write(0); w.Write(40000); w.Write(40000); w.Write(int.MinValue + 1); }))).Read());
        // Width * Height * 4 overflowing int must not slip through.
        Assert.Throws<InvalidDataException>(() => new MessageReader(new MemoryStream(
            Raw(Frame.Type, w => { w.Write(1); w.Write(0); w.Write(0); w.Write(1 << 30); w.Write(1 << 30); w.Write(0); }))).Read());
    }
}
