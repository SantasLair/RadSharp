// Generates the neutral default app icon that new RadSharp projects ship (app.png 1024x1024 and a multi-size app.ico).
// Deterministic: same bytes every run. Usage: dotnet run tools/make-app-icon/make-app-icon.cs -- <output-folder>
// Copy the results to src/RadSharp.Design.Code/Packaging/ and templates/radsharp-app/ (a test checks they match).
using System.IO.Compression;

var outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);
File.WriteAllBytes(Path.Combine(outDir, "app.png"), Png(Render(1024), 1024));
int[] sizes = [16, 24, 32, 48, 64, 128, 256];
var images = sizes.Select(s => (Size: s, Data: s <= 48 ? Dib(Render(s), s) : Png(Render(s), s))).ToArray();
using (var ms = new MemoryStream())
using (var w = new BinaryWriter(ms))
{
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Length);
    int offset = 6 + 16 * images.Length;
    foreach (var (size, data) in images)
    {
        w.Write((byte)(size == 256 ? 0 : size)); w.Write((byte)(size == 256 ? 0 : size));
        w.Write((byte)0); w.Write((byte)0); w.Write((ushort)1); w.Write((ushort)32);
        w.Write(data.Length); w.Write(offset);
        offset += data.Length;
    }
    foreach (var (_, data) in images) w.Write(data);
    File.WriteAllBytes(Path.Combine(outDir, "app.ico"), ms.ToArray());
}

// A rounded square with a blue gradient and a white "window" glyph; 3x3 supersampled, straight RGBA.
static byte[] Render(int n)
{
    var px = new byte[n * n * 4];
    const int ss = 3;
    for (int y = 0; y < n; y++)
    for (int x = 0; x < n; x++)
    {
        double r = 0, g = 0, b = 0, a = 0;
        for (int sy = 0; sy < ss; sy++)
        for (int sx = 0; sx < ss; sx++)
        {
            double u = (x + (sx + 0.5) / ss) / n, v = (y + (sy + 0.5) / ss) / n;
            if (!InRoundRect(u, v, 0.06, 0.06, 0.94, 0.94, 0.2)) continue;
            double cr = 0.20 + 0.10 * v, cg = 0.42 + 0.12 * v, cb = 0.85 - 0.10 * v;   // gradient
            if (InRoundRect(u, v, 0.22, 0.26, 0.78, 0.74, 0.06))
            {
                bool bar = v < 0.38;
                cr = cg = cb = bar ? 0.92 : 1.0;
                if (!bar && InRoundRect(u, v, 0.30, 0.46, 0.50, 0.58, 0.02)) { cr = 0.20; cg = 0.42; cb = 0.85; }
                if (!bar && InRoundRect(u, v, 0.30, 0.64, 0.70, 0.68, 0.02)) { cr = 0.75; cg = 0.80; cb = 0.90; }
            }
            r += cr; g += cg; b += cb; a += 1;
        }
        int i = (y * n + x) * 4;
        if (a > 0) { px[i] = (byte)Math.Round(r / a * 255); px[i + 1] = (byte)Math.Round(g / a * 255); px[i + 2] = (byte)Math.Round(b / a * 255); }
        px[i + 3] = (byte)Math.Round(a / (ss * ss) * 255);
    }
    return px;
}

static bool InRoundRect(double u, double v, double x0, double y0, double x1, double y1, double rad)
{
    if (u < x0 || u > x1 || v < y0 || v > y1) return false;
    double cx = Math.Clamp(u, x0 + rad, x1 - rad), cy = Math.Clamp(v, y0 + rad, y1 - rad);
    return (u - cx) * (u - cx) + (v - cy) * (v - cy) <= rad * rad;
}

static byte[] Png(byte[] rgba, int n)
{
    using var ms = new MemoryStream();
    ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    Chunk(ms, "IHDR", [.. BE(n), .. BE(n), 8, 6, 0, 0, 0]);
    var raw = new byte[n * (n * 4 + 1)];
    for (int y = 0; y < n; y++) Array.Copy(rgba, y * n * 4, raw, y * (n * 4 + 1) + 1, n * 4);
    using var z = new MemoryStream();
    using (var d = new ZLibStream(z, CompressionLevel.SmallestSize, true)) d.Write(raw);
    Chunk(ms, "IDAT", z.ToArray());
    Chunk(ms, "IEND", []);
    return ms.ToArray();
}

static byte[] BE(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

static void Chunk(Stream s, string type, byte[] data)
{
    s.Write(BE(data.Length));
    var body = new byte[4 + data.Length];
    System.Text.Encoding.ASCII.GetBytes(type).CopyTo(body, 0);
    data.CopyTo(body, 4);
    s.Write(body);
    s.Write(BE((int)Crc(body)));
}

// ICO image entry as a BMP DIB (BGRA, bottom-up, with a zero AND mask).
static byte[] Dib(byte[] rgba, int n)
{
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write(40); w.Write(n); w.Write(n * 2); w.Write((ushort)1); w.Write((ushort)32);
    w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
    for (int y = n - 1; y >= 0; y--)
        for (int x = 0; x < n; x++)
        {
            int i = (y * n + x) * 4;
            w.Write(rgba[i + 2]); w.Write(rgba[i + 1]); w.Write(rgba[i]); w.Write(rgba[i + 3]);
        }
    w.Write(new byte[((n + 31) / 32 * 4) * n]);
    return ms.ToArray();
}

static uint Crc(byte[] data)
{
    uint c = 0xFFFFFFFF;
    foreach (var b in data)
    {
        c ^= b;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
    }
    return ~c;
}
