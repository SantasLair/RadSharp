using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RadSharp.DesignHost.Protocol;

/// <summary>Writes length-prefixed messages to a stream. Thread-safe: each frame is written atomically.</summary>
public sealed class MessageWriter
{
    private readonly Stream _stream;
    private readonly object _lock = new object();

    /// <summary>Creates a writer over <paramref name="stream"/>.</summary>
    public MessageWriter(Stream stream) => _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    /// <summary>Sends one message and flushes.</summary>
    public void Write(Message message)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        var ms = new MemoryStream();
        ms.Write(new byte[4], 0, 4); // length placeholder
        ms.WriteByte(message.MessageType);
        using (var w = new BinaryWriter(ms, new UTF8Encoding(false), leaveOpen: true))
            message.Write(w);
        long len = ms.Length - 4;
        if (len > ProtocolInfo.MaxFrameLength) throw new InvalidDataException("Message too large: " + len + " bytes.");
        var buf = ms.GetBuffer();
        buf[0] = (byte)len; buf[1] = (byte)(len >> 8); buf[2] = (byte)(len >> 16); buf[3] = (byte)(len >> 24);
        lock (_lock)
        {
            _stream.Write(buf, 0, (int)ms.Length);
            _stream.Flush();
        }
    }
}

/// <summary>Reads length-prefixed messages from a stream. Single consumer.</summary>
public sealed class MessageReader
{
    private static readonly Dictionary<byte, Func<Message>> Factories = Build();
    private readonly Stream _stream;

    /// <summary>Creates a reader over <paramref name="stream"/>.</summary>
    public MessageReader(Stream stream) => _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    /// <summary>Reads the next message, or null at a clean end of stream (EOF on a frame boundary).</summary>
    /// <exception cref="EndOfStreamException">The stream ended inside a frame.</exception>
    /// <exception cref="InvalidDataException">Bad length, unknown type or malformed payload.</exception>
    public Message? Read()
    {
        var head = new byte[4];
        int got = ReadFully(head, 4);
        if (got == 0) return null;
        if (got < 4) throw new EndOfStreamException("Stream ended inside a frame header.");
        uint len = (uint)(head[0] | head[1] << 8 | head[2] << 16 | head[3] << 24);
        if (len < 1) throw new InvalidDataException("Empty frame.");
        if (len > ProtocolInfo.MaxFrameLength) throw new InvalidDataException("Frame too large: " + len + " bytes.");
        var body = new byte[len];
        if (ReadFully(body, (int)len) < len) throw new EndOfStreamException("Stream ended inside a frame (expected " + len + " bytes).");

        if (!Factories.TryGetValue(body[0], out var make))
            throw new InvalidDataException("Unknown message type 0x" + body[0].ToString("X2") + ".");
        var msg = make();
        try
        {
            using var r = new BinaryReader(new MemoryStream(body, 1, body.Length - 1, false), new UTF8Encoding(false, true));
            msg.Read(r);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // EndOfStream, FormatException, ArgumentException / DecoderFallbackException (bad UTF-8), overflow, ...
            throw new InvalidDataException("Malformed " + msg.GetType().Name + " payload: " + e.Message, e);
        }
        return msg;
    }

    private int ReadFully(byte[] buf, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = _stream.Read(buf, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private static Dictionary<byte, Func<Message>> Build()
    {
        var d = new Dictionary<byte, Func<Message>>();
        void Add<T>() where T : Message, new() => d[new T().MessageType] = () => new T();
        Add<Hello>(); Add<OpenDocument>(); Add<CloseDocument>(); Add<ActivateDocument>(); Add<ReloadDocument>(); Add<SaveDocument>();
        Add<AttachWindow>(); Add<ResizeWindow>(); Add<FocusWindow>();
        Add<MouseMove>(); Add<MouseDown>(); Add<MouseUp>(); Add<MouseWheel>(); Add<KeyDown>(); Add<KeyUp>(); Add<TextInput>();
        Add<Command>(); Add<CloseWindow>(); Add<CodeResponse>();
        Add<HelloAck>(); Add<WindowOpened>(); Add<WindowClosed>(); Add<WindowMoved>(); Add<Frame>(); Add<Cursor>();
        Add<DocumentState>(); Add<DocumentSaved>(); Add<NavigateToCode>(); Add<Error>(); Add<Log>(); Add<CodeRequest>(); Add<ApplyCodeEdit>();
        return d;
    }
}
