using System.Buffers.Binary;
using RadSharp.Drawing;

namespace RadSharp.CodeEditing;

/// <summary>
/// The bundled monospace font, JetBrains Mono (Regular and Bold, SIL Open Font License 1.1; the licence ships as
/// <c>licenses/JetBrainsMono-LICENSE.txt</c>). It is registered with the font registry under two family names:
/// <see cref="Family"/> without ligatures (the default for code) and <see cref="LigaturesFamily"/> with them.
/// </summary>
public static class CodeEditorFonts
{
    /// <summary>JetBrains Mono with its programming ligatures turned off.</summary>
    public const string Family = "JetBrains Mono";
    /// <summary>JetBrains Mono as shipped, with ligatures (<c>=&gt;</c>, <c>!=</c>...).</summary>
    public const string LigaturesFamily = "JetBrains Mono Ligatures";

    static readonly object Gate = new();
    static bool _registered;

    /// <summary>Registers both families with <see cref="FontFace.Register"/> (once; later calls do nothing).</summary>
    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered) return;
            var regular = Load("JetBrainsMono-Regular");
            var bold = Load("JetBrainsMono-Bold");
            FontFace.Register(LigaturesFamily, regular, bold);
            FontFace.Register(Family, WithoutSubstitutions(regular), WithoutSubstitutions(bold));
            _registered = true;
        }
    }

    /// <summary>The regular or bold face of <see cref="Family"/> (or <see cref="LigaturesFamily"/>).</summary>
    public static FontFace Face(bool bold = false, bool ligatures = false)
    {
        EnsureRegistered();
        return FontFace.Resolve(ligatures ? LigaturesFamily : Family, bold, false);
    }

    static byte[] Load(string name)
    {
        using var s = typeof(CodeEditorFonts).Assembly.GetManifestResourceStream($"RadSharp.CodeEditor.Fonts.{name}.ttf")
            ?? throw new InvalidOperationException($"Bundled font {name} is missing.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// A copy of a font file whose glyph substitution table (GSUB) is hidden by renaming its tag, so HarfBuzz applies
    /// no contextual alternates: JetBrains Mono's ligatures are all <c>calt</c> substitutions. Positioning (GPOS) and
    /// every glyph stay as they are.
    /// </summary>
    static byte[] WithoutSubstitutions(byte[] font)
    {
        var copy = (byte[])font.Clone();
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(copy.AsSpan(4));
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            if (copy[rec] == 'G' && copy[rec + 1] == 'S' && copy[rec + 2] == 'U' && copy[rec + 3] == 'B')
            {
                copy[rec + 3] = (byte)'A';   // "GSUA": unknown to HarfBuzz, and the directory stays sorted
            }
        }
        return copy;
    }
}
