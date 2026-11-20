using System.Text;

namespace RadSharp.Design.Code;

/// <summary>
/// File access that cooperates with the designer saving on the UI thread. The framework's
/// <c>File.ReadAll*</c>/<c>OpenRead</c> open with <c>FileShare.Read</c>, which on Windows denies writers, so a
/// background compile reading a form at the moment of a save made the save fail with a sharing violation.
/// Reads here share read/write/delete; writes retry a sharing violation briefly.
/// </summary>
public static class SharedFile
{
    const FileShare Share = FileShare.ReadWrite | FileShare.Delete;

    public static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, Share, 4096, FileOptions.SequentialScan);

    public static byte[] ReadAllBytes(string path)
    {
        using var s = OpenRead(path);
        var buffer = new MemoryStream();
        s.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Like <see cref="File.ReadAllText(string)"/>: UTF-8, honouring a BOM.</summary>
    public static string ReadAllText(string path)
    {
        using var s = OpenRead(path);
        using var r = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return r.ReadToEnd();
    }

    /// <summary>True for the Windows sharing (0x20) and lock (0x21) violations.</summary>
    public static bool IsSharingViolation(IOException e) =>
        OperatingSystem.IsWindows() && ((e.HResult & 0xFFFF) is 32 or 33);

    /// <summary>Total delay budget of <see cref="WriteAllText"/>'s retries.</summary>
    public static int RetryAttempts { get; set; } = 20;
    public static int RetryDelayMs { get; set; } = 50;

    /// <summary>
    /// <see cref="File.WriteAllText(string, string)"/>, retrying a sharing violation (about one second in
    /// total); other failures, and a violation that outlasts the retries, are thrown.
    /// </summary>
    public static void WriteAllText(string path, string text)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { File.WriteAllText(path, text); return; }
            catch (IOException e) when (IsSharingViolation(e) && attempt < RetryAttempts)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }
    }
}
