using System.Text;

namespace RadSharp.Design.Code;

/// <summary>
/// A text file read with its encoding remembered, so it can be written back the way it was: BOM (UTF-8, UTF-16 LE/BE,
/// UTF-32 LE/BE) or, without a BOM, strict UTF-8. A file that is neither is <see cref="IsLossy"/>: its text is a
/// best-effort Latin-1 view, good for searching, and it must not be written back from that text.
/// </summary>
public sealed class TextFileContent
{
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    TextFileContent(string text, Encoding encoding, byte[] preamble, bool lossy)
    {
        Text = text;
        Encoding = encoding;
        Preamble = preamble;
        IsLossy = lossy;
    }

    public string Text { get; }
    /// <summary>The encoding (without a BOM of its own: <see cref="Preamble"/> carries it).</summary>
    public Encoding Encoding { get; }
    /// <summary>The BOM bytes the file started with (empty if none).</summary>
    public byte[] Preamble { get; }
    public bool HasBom => Preamble.Length > 0;
    /// <summary>True if the file has no BOM and isn't valid UTF-8: <see cref="Text"/> is a lossy Latin-1 reading.</summary>
    public bool IsLossy { get; }

    /// <summary>UTF-8 without a BOM, for files that don't exist yet.</summary>
    public static TextFileContent NewUtf8(string text = "") => new(text, new UTF8Encoding(false), [], false);

    public static TextFileContent Read(string path) => Decode(File.ReadAllBytes(path));

    public static TextFileContent Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) return Bom(bytes, 4, new UTF32Encoding(false, false, true));
        if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) return Bom(bytes, 4, new UTF32Encoding(true, false, true));
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Bom(bytes, 3, new UTF8Encoding(false, true));
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return Bom(bytes, 2, new UnicodeEncoding(false, false, true));
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return Bom(bytes, 2, new UnicodeEncoding(true, false, true));
        try { return new TextFileContent(StrictUtf8.GetString(bytes), new UTF8Encoding(false), [], false); }
        catch (DecoderFallbackException) { return new TextFileContent(Encoding.Latin1.GetString(bytes), Encoding.Latin1, [], true); }
    }

    static TextFileContent Bom(byte[] bytes, int length, Encoding encoding)
    {
        try { return new TextFileContent(encoding.GetString(bytes, length, bytes.Length - length), encoding, bytes[..length], false); }
        catch (DecoderFallbackException) { return new TextFileContent(Encoding.Latin1.GetString(bytes), Encoding.Latin1, [], true); }
    }

    /// <summary>The file's bytes for <paramref name="text"/>: the same BOM and encoding the file was read with.</summary>
    public byte[] Encode(string text)
    {
        var body = Encoding.GetBytes(text);
        if (Preamble.Length == 0) return body;
        var all = new byte[Preamble.Length + body.Length];
        Preamble.CopyTo(all, 0);
        body.CopyTo(all, Preamble.Length);
        return all;
    }

    /// <summary>Writes <paramref name="text"/> to <paramref name="path"/> in this file's encoding, atomically.</summary>
    public void Write(string path, string text) => WriteAtomic(path, Encode(text));

    /// <summary>
    /// Replaces the file with <paramref name="bytes"/> without ever leaving it half-written: the bytes go to a temp file
    /// in the same folder, which then replaces the target (<see cref="File.Replace(string,string,string?)"/>, or a
    /// moving overwrite where that isn't possible).
    /// </summary>
    public static void WriteAtomic(string path, byte[] bytes)
    {
        path = Path.GetFullPath(path);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path))
            {
                try { File.Replace(temp, path, null); }
                catch (Exception e) when (e is IOException or PlatformNotSupportedException or UnauthorizedAccessException) { File.Move(temp, path, overwrite: true); }
            }
            else File.Move(temp, path);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }
}
