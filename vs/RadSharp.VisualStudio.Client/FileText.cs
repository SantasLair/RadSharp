using System.IO;
using System.Text;

namespace RadSharp.VisualStudio.Client;

/// <summary>File reads and writes for code edits to files that aren't open in an editor.</summary>
public static class FileText
{
    /// <summary>
    /// Writes <paramref name="text"/> in the file's current encoding, detected from its byte order mark (UTF-8 with or
    /// without BOM, UTF-16 LE/BE, UTF-32). Files without a BOM, and new files, are written as UTF-8 without a BOM.
    /// </summary>
    public static void WritePreservingEncoding(string file, string text)
    {
        Encoding encoding = new UTF8Encoding(false);
        if (File.Exists(file))
        {
            using var reader = new StreamReader(file, encoding, detectEncodingFromByteOrderMarks: true);
            reader.Peek();
            encoding = reader.CurrentEncoding;
        }

        File.WriteAllText(file, text, encoding);
    }
}
