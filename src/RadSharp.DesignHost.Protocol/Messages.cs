using System;
using System.IO;

namespace RadSharp.DesignHost.Protocol;

/// <summary>Protocol constants.</summary>
public static class ProtocolInfo
{
    /// <summary>Wire protocol version, exchanged in Hello / HelloAck.</summary>
    public const int Version = 3;

    /// <summary>Largest accepted frame (type byte + payload), in bytes.</summary>
    public const int MaxFrameLength = 256 * 1024 * 1024;
}

/// <summary>Role of a host window as seen by the client.</summary>
public enum WindowRole : byte { Surface, Inspector, Palette, Structure, Popup, Dialog }

/// <summary>Mouse button.</summary>
public enum MouseButton : byte { Left, Right, Middle }

/// <summary>Keyboard modifier flags.</summary>
[Flags]
public enum Modifiers : byte { None = 0, Shift = 1, Ctrl = 2, Alt = 4, Meta = 8 }

/// <summary>Base of every protocol message. The payload is written and read with BinaryWriter / BinaryReader.</summary>
public abstract class Message
{
    /// <summary>Stable wire id. Client to host is 0x01-0x7F, host to client is 0x80-0xFF.</summary>
    public abstract byte MessageType { get; }

    /// <summary>Writes the payload (not the frame header).</summary>
    public abstract void Write(BinaryWriter w);

    /// <summary>Reads the payload (not the frame header).</summary>
    public abstract void Read(BinaryReader r);
}

// ---- Client to host ----

/// <summary>First message from the client.</summary>
public sealed class Hello : Message
{
    public const byte Type = 0x01;
    public override byte MessageType => Type;
    public int ProtocolVersion { get; set; }
    public string ClientName { get; set; } = "";
    public override void Write(BinaryWriter w) { w.Write(ProtocolVersion); w.Write(ClientName ?? ""); }
    public override void Read(BinaryReader r) { ProtocolVersion = r.ReadInt32(); ClientName = r.ReadString(); }
}

/// <summary>Opens a form file as a document.</summary>
public sealed class OpenDocument : Message
{
    public const byte Type = 0x02;
    public override byte MessageType => Type;
    public int DocId { get; set; }
    public string Path { get; set; } = "";
    public override void Write(BinaryWriter w) { w.Write(DocId); w.Write(Path ?? ""); }
    public override void Read(BinaryReader r) { DocId = r.ReadInt32(); Path = r.ReadString(); }
}

/// <summary>Closes a document.</summary>
public sealed class CloseDocument : Message
{
    public const byte Type = 0x03;
    public override byte MessageType => Type;
    public int DocId { get; set; }
    public override void Write(BinaryWriter w) => w.Write(DocId);
    public override void Read(BinaryReader r) => DocId = r.ReadInt32();
}

/// <summary>The document's tab became the active one.</summary>
public sealed class ActivateDocument : Message
{
    public const byte Type = 0x04;
    public override byte MessageType => Type;
    public int DocId { get; set; }
    public override void Write(BinaryWriter w) => w.Write(DocId);
    public override void Read(BinaryReader r) => DocId = r.ReadInt32();
}

/// <summary>The file changed on disk: reload it.</summary>
public sealed class ReloadDocument : Message
{
    public const byte Type = 0x05;
    public override byte MessageType => Type;
    public int DocId { get; set; }
    public override void Write(BinaryWriter w) => w.Write(DocId);
    public override void Read(BinaryReader r) => DocId = r.ReadInt32();
}

/// <summary>Saves a document to <see cref="Path"/>; answered by <see cref="DocumentSaved"/>.</summary>
public sealed class SaveDocument : Message
{
    public const byte Type = 0x06;
    public override byte MessageType => Type;
    public int DocId { get; set; }
    public string Path { get; set; } = "";
    public override void Write(BinaryWriter w) { w.Write(DocId); w.Write(Path ?? ""); }
    public override void Read(BinaryReader r) { DocId = r.ReadInt32(); Path = r.ReadString(); }
}

/// <summary>Binds a client view to a host window announced by <see cref="WindowOpened"/>.</summary>
public sealed class AttachWindow : Message
{
    public const byte Type = 0x07;
    public override byte MessageType => Type;
    public int WindowId { get; set; }
    public override void Write(BinaryWriter w) => w.Write(WindowId);
    public override void Read(BinaryReader r) => WindowId = r.ReadInt32();
}

/// <summary>The client view changed size (device pixels) or DPI scale.</summary>
public sealed class ResizeWindow : Message
{
    public const byte Type = 0x08;
    public override byte MessageType => Type;
    public int WindowId { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public float Scale { get; set; } = 1f;
    public override void Write(BinaryWriter w) { w.Write(WindowId); w.Write(Width); w.Write(Height); w.Write(Scale); }
    public override void Read(BinaryReader r) { WindowId = r.ReadInt32(); Width = r.ReadInt32(); Height = r.ReadInt32(); Scale = r.ReadSingle(); }
}

/// <summary>The client view gained or lost keyboard focus.</summary>
public sealed class FocusWindow : Message
{
    public const byte Type = 0x09;
    public override byte MessageType => Type;
    public int WindowId { get; set; }
    public bool Focused { get; set; }
    public override void Write(BinaryWriter w) { w.Write(WindowId); w.Write(Focused); }
    public override void Read(BinaryReader r) { WindowId = r.ReadInt32(); Focused = r.ReadBoolean(); }
}

/// <summary>Shared payload of the mouse messages. Dx / Dy are only meaningful for wheel events.</summary>
public abstract class MouseMessage : Message
{
    public int WindowId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public MouseButton Button { get; set; }
    public Modifiers Mods { get; set; }
    public int Clicks { get; set; }
    public float Dx { get; set; }
    public float Dy { get; set; }
    public override void Write(BinaryWriter w)
    {
        w.Write(WindowId); w.Write(X); w.Write(Y); w.Write((byte)Button); w.Write((byte)Mods);
        w.Write(Clicks); w.Write(Dx); w.Write(Dy);
    }
    public override void Read(BinaryReader r)
    {
        WindowId = r.ReadInt32(); X = r.ReadInt32(); Y = r.ReadInt32(); Button = (MouseButton)r.ReadByte(); Mods = (Modifiers)r.ReadByte();
        Clicks = r.ReadInt32(); Dx = r.ReadSingle(); Dy = r.ReadSingle();
    }
}

/// <summary>Mouse moved.</summary>
public sealed class MouseMove : MouseMessage { public const byte Type = 0x10; public override byte MessageType => Type; }

/// <summary>Mouse button pressed.</summary>
public sealed class MouseDown : MouseMessage { public const byte Type = 0x11; public override byte MessageType => Type; }

/// <summary>Mouse button released.</summary>
public sealed class MouseUp : MouseMessage { public const byte Type = 0x12; public override byte MessageType => Type; }

/// <summary>Mouse wheel; Dx / Dy carry the delta.</summary>
public sealed class MouseWheel : MouseMessage { public const byte Type = 0x13; public override byte MessageType => Type; }

/// <summary>Shared payload of the key messages. <see cref="Key"/> is the RadSharp <c>Key</c> enum name.</summary>
public abstract class KeyMessage : Message
{
    public int WindowId { get; set; }
    public string Key { get; set; } = "";
    public Modifiers Mods { get; set; }
    public bool Repeat { get; set; }
    public override void Write(BinaryWriter w) { w.Write(WindowId); w.Write(Key ?? ""); w.Write((byte)Mods); w.Write(Repeat); }
    public override void Read(BinaryReader r) { WindowId = r.ReadInt32(); Key = r.ReadString(); Mods = (Modifiers)r.ReadByte(); Repeat = r.ReadBoolean(); }
}

/// <summary>Key pressed.</summary>
public sealed class KeyDown : KeyMessage { public const byte Type = 0x14; public override byte MessageType => Type; }

/// <summary>Key released.</summary>
public sealed class KeyUp : KeyMessage { public const byte Type = 0x15; public override byte MessageType => Type; }

/// <summary>Typed text (after IME / keyboard layout translation).</summary>
public sealed class TextInput : Message
{
    public const byte Type = 0x16;
    public override byte MessageType => Type;
    public int WindowId { get; set; }
    public string Text { get; set; } = "";
    public override void Write(BinaryWriter w) { w.Write(WindowId); w.Write(Text ?? ""); }
    public override void Read(BinaryReader r) { WindowId = r.ReadInt32(); Text = r.ReadString(); }
}

/// <summary>An editor command: Undo, Redo, Cut, Copy, Paste, Delete, SelectAll, and alignment commands later.</summary>
public sealed class Command : Message
{
    public const byte Type = 0x17;
    public override byte MessageType => Type;
    public int DocId { get; set; }
    public string Name { get; set; } = "";
    public override void Write(BinaryWriter w) { w.Write(DocId); w.Write(Name ?? ""); }
    public override void Read(BinaryReader r) { DocId = r.ReadInt32(); Name = r.ReadString(); }
}

/// <summary>
/// Answer to <see cref="CodeRequest"/>. <see cref="Found"/>: <see cref="Text"/> is the editor's current buffer for the
/// file; otherwise the host reads the file from disk.
/// </summary>
public sealed class CodeResponse : Message
{
    public const byte Type = 0x19;
    public override byte MessageType => Type;
    public int RequestId { get; set; }
    public bool Found { get; set; }
    public string Text { get; set; } = "";
    public override void Write(BinaryWriter w) { w.Write(RequestId); w.Write(Found); w.Write(Text ?? ""); }
    public override void Read(BinaryReader r) { RequestId = r.ReadInt32(); Found = r.ReadBoolean(); Text = r.ReadString(); }
}

/// <summary>The user closed a modal or popup on the client side.</summary>
public sealed class CloseWindow : Message
{
    public const byte Type = 0x18;
    public override byte MessageType => Type;
    public int WindowId { get; set; }
    public override void Write(BinaryWriter w) => w.Write(WindowId);
    public override void Read(BinaryReader r) => WindowId = r.ReadInt32();
}

// ---- Host to client ----

/// <summary>Answer to <see cref="Hello"/>.</summary>
public sealed class HelloAck : Message
{
    public const byte Type = 0x81;
    public override byte MessageType => Type;
    public int ProtocolVersion { get; set; }
    public string HostVersion { get; set; } = "";
    public override void Write(BinaryWriter w) { w.Write(ProtocolVersion); w.Write(HostVersion ?? ""); }
    public override void Read(BinaryReader r) { ProtocolVersion = r.ReadInt32(); HostVersion = r.ReadString(); }
}

/// <summary>The host created a window. Panes belong to a document; popups and dialogs have an owner window.</summary>
public sealed class WindowOpened : Message
{
    public const byte Type = 0x82;
    public override byte MessageType => Type;
    public int WindowId { get; set; }
    public int DocId { get; set; }
    public WindowRole Role { get; set; }
    public int OwnerWindowId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Title { get; set; } = "";
    public bool Modal { get; set; }
    public override void Write(BinaryWriter w)
    {
        w.Write(WindowId); w.Write(DocId); w.Write((byte)Role); w.Write(OwnerWindowId);
        w.Write(X); w.Write(Y); w.Write(Width); w.Write(Height); w.Write(Title ?? ""); w.Write(Modal);
    }
    public override void Read(BinaryReader r)
    {
        WindowId = r.ReadInt32(); DocId = r.ReadInt32(); Role = (WindowRole)r.ReadByte(); OwnerWindowId = r.ReadInt32();
        X = r.ReadInt32(); Y = r.ReadInt32(); Width = r.ReadInt32(); Height = r.ReadInt32(); Title = r.ReadString(); Modal = r.ReadBoolean();
    }
}

/// <summary>The host destroyed a window.</summary>
public sealed class WindowClosed : Message
{
    public const byte Type = 0x83;
    public override byte MessageType => Type;
    public int WindowId { get; set; }
    public override void Write(BinaryWriter w) => w.Write(WindowId);
    public override void Read(BinaryReader r) => WindowId = r.ReadInt32();
}

/// <summary>A host window moved or resized.</summary>
public sealed class WindowMoved : Message
{
    public const byte Type = 0x84;
    public override byte MessageType => Type;
    public int WindowId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public override void Write(BinaryWriter w) { w.Write(WindowId); w.Write(X); w.Write(Y); w.Write(Width); w.Write(Height); }
    public override void Read(BinaryReader r) { WindowId = r.ReadInt32(); X = r.ReadInt32(); Y = r.ReadInt32(); Width = r.ReadInt32(); Height = r.ReadInt32(); }
}

/// <summary>A rendered rectangle of a window: BGRA premultiplied, row-major, stride = Width * 4.</summary>
public sealed class Frame : Message
{
    public const byte Type = 0x85;
    public override byte MessageType => Type;
    public int WindowId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] Pixels { get; set; } = Array.Empty<byte>();
    public override void Write(BinaryWriter w)
    {
        w.Write(WindowId); w.Write(X); w.Write(Y); w.Write(Width); w.Write(Height);
        var p = Pixels ?? Array.Empty<byte>();
        w.Write(p.Length); w.Write(p);
    }
    public override void Read(BinaryReader r)
    {
        WindowId = r.ReadInt32(); X = r.ReadInt32(); Y = r.ReadInt32(); Width = r.ReadInt32(); Height = r.ReadInt32();
        int n = r.ReadInt32();
        if (n < 0) throw new InvalidDataException("Negative pixel buffer length.");
        if (Width < 0 || Height < 0) throw new InvalidDataException("Negative frame dimensions.");
        if (n != (long)Width * Height * 4) throw new InvalidDataException("Pixel buffer length " + n + " does not match " + Width + " x " + Height + " x 4.");
        var s = r.BaseStream;
        if (s.CanSeek && n > s.Length - s.Position) throw new InvalidDataException("Pixel buffer longer than the rest of the payload.");
        var p = r.ReadBytes(n);
        if (p.Length != n) throw new EndOfStreamException("Truncated pixel buffer.");
        Pixels = p;
    }
}

/// <summary>The mouse cursor to show over a window (RadSharp cursor name).</summary>
public sealed class Cursor : Message
{
    public const byte Type = 0x86;
    public override byte MessageType => Type;
    public int WindowId { get; set; }
    public string CursorName { get; set; } = "";
    public override void Write(BinaryWriter w) { w.Write(WindowId); w.Write(CursorName ?? ""); }
    public override void Read(BinaryReader r) { WindowId = r.ReadInt32(); CursorName = r.ReadString(); }
}

/// <summary>Document state for the client's commands and dirty marker.</summary>
public sealed class DocumentState : Message
{
    public const byte Type = 0x87;
    public override byte MessageType => Type;
    public int DocId { get; set; }
    public bool Dirty { get; set; }
    public bool CanUndo { get; set; }
    public bool CanRedo { get; set; }
    public bool HasSelection { get; set; }
    public override void Write(BinaryWriter w) { w.Write(DocId); w.Write(Dirty); w.Write(CanUndo); w.Write(CanRedo); w.Write(HasSelection); }
    public override void Read(BinaryReader r) { DocId = r.ReadInt32(); Dirty = r.ReadBoolean(); CanUndo = r.ReadBoolean(); CanRedo = r.ReadBoolean(); HasSelection = r.ReadBoolean(); }
}

/// <summary>Answer to <see cref="SaveDocument"/>.</summary>
public sealed class DocumentSaved : Message
{
    public const byte Type = 0x88;
    public override byte MessageType => Type;
    public int DocId { get; set; }
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public override void Write(BinaryWriter w) { w.Write(DocId); w.Write(Ok); w.Write(Error ?? ""); }
    public override void Read(BinaryReader r) { DocId = r.ReadInt32(); Ok = r.ReadBoolean(); Error = r.ReadString(); }
}

/// <summary>Asks the client to show a source location in the IDE's editor.</summary>
public sealed class NavigateToCode : Message
{
    public const byte Type = 0x89;
    public override byte MessageType => Type;
    public int DocId { get; set; }
    public string File { get; set; } = "";
    public int Line { get; set; }
    public override void Write(BinaryWriter w) { w.Write(DocId); w.Write(File ?? ""); w.Write(Line); }
    public override void Read(BinaryReader r) { DocId = r.ReadInt32(); File = r.ReadString(); Line = r.ReadInt32(); }
}

/// <summary>An error to show the user. <see cref="DocId"/> is 0 when not tied to a document.</summary>
public sealed class Error : Message
{
    public const byte Type = 0x8A;
    public override byte MessageType => Type;
    public int DocId { get; set; }
    public string Text { get; set; } = "";
    public override void Write(BinaryWriter w) { w.Write(DocId); w.Write(Text ?? ""); }
    public override void Read(BinaryReader r) { DocId = r.ReadInt32(); Text = r.ReadString(); }
}

/// <summary>A diagnostic log line. Level: 0 debug, 1 info, 2 warning, 3 error.</summary>
public sealed class Log : Message
{
    public const byte Type = 0x8B;
    public override byte MessageType => Type;
    public int Level { get; set; }
    public string Text { get; set; } = "";
    public override void Write(BinaryWriter w) { w.Write(Level); w.Write(Text ?? ""); }
    public override void Read(BinaryReader r) { Level = r.ReadInt32(); Text = r.ReadString(); }
}

/// <summary>Asks the client for the current text of a code file (its editor buffer); answered by <see cref="CodeResponse"/>.</summary>
public sealed class CodeRequest : Message
{
    public const byte Type = 0x8C;
    public override byte MessageType => Type;
    public int RequestId { get; set; }
    public int DocId { get; set; }
    public string File { get; set; } = "";
    public override void Write(BinaryWriter w) { w.Write(RequestId); w.Write(DocId); w.Write(File ?? ""); }
    public override void Read(BinaryReader r) { RequestId = r.ReadInt32(); DocId = r.ReadInt32(); File = r.ReadString(); }
}

/// <summary>
/// Asks the client to replace the file's text with <see cref="Text"/> (the host never writes code files itself)
/// and to put the caret on the 1-based <see cref="Line"/>. <see cref="BaseHash"/> is the lowercase hex SHA-256 of the
/// UTF-8 text the edit was computed from: the client must not apply the edit if its buffer no longer hashes to it.
/// </summary>
public sealed class ApplyCodeEdit : Message
{
    public const byte Type = 0x8D;
    public override byte MessageType => Type;
    public int DocId { get; set; }
    public string File { get; set; } = "";
    public string Text { get; set; } = "";
    public int Line { get; set; }
    public string BaseHash { get; set; } = "";
    public override void Write(BinaryWriter w) { w.Write(DocId); w.Write(File ?? ""); w.Write(Text ?? ""); w.Write(Line); w.Write(BaseHash ?? ""); }
    public override void Read(BinaryReader r) { DocId = r.ReadInt32(); File = r.ReadString(); Text = r.ReadString(); Line = r.ReadInt32(); BaseHash = r.ReadString(); }
}
