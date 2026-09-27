using System.Reflection;
using SkiaSharp;

namespace RadSharp.Drawing;

/// <summary>A decoded raster image (PNG, JPEG, WebP, GIF, BMP, ICO).</summary>
public sealed class Picture
{
    internal SKImage Image { get; }

    Picture(SKImage image) => Image = image;

    internal static Picture FromImage(SKImage image) => new(image);

    public int Width => Image.Width;
    public int Height => Image.Height;

    public static Picture? FromBytes(byte[] data)
    {
        using var bitmap = SKBitmap.Decode(data);
        return bitmap == null ? null : new Picture(SKImage.FromBitmap(bitmap));
    }

    public static Picture? FromFile(string path) => File.Exists(path) ? FromBytes(File.ReadAllBytes(path)) : null;

    /// <summary>
    /// Resolves a form file's picture value: a <c>data:image/...;base64,</c> URI, an embedded resource
    /// name in <paramref name="assembly"/>, or a file path relative to the app's folder.
    /// </summary>
    public static Picture? FromSource(string source, Assembly? assembly = null)
    {
        if (string.IsNullOrEmpty(source)) return null;
        try
        {
            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = source.IndexOf(',');
                return comma < 0 ? null : FromBytes(Convert.FromBase64String(source[(comma + 1)..]));
            }
            if (assembly?.GetManifestResourceStream(source) is { } stream)
            {
                using (stream)
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    return FromBytes(ms.ToArray());
                }
            }
            return FromFile(Path.IsPathRooted(source) ? source : Path.Combine(AppContext.BaseDirectory, source));
        }
        catch (FormatException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>Encodes image bytes as a data URI for storing in a form file.</summary>
    public static string ToDataUri(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);
}
