// Procedural textures for the demo scene, plus the minimum PNG writer needed to embed them.
//
// This exists so the texture path can be exercised end to end without opening Revit: the exporter
// only ever hands GltfBuilder (name, bytes, mime), so a scene built here goes through exactly the
// same code the add-in uses. Real projects get their images from Revit appearance assets instead.
using System.IO.Compression;

static class Textures
{
    public const int Size = 512;

    /// <summary>Oak boards, 4 planks across and 6 boards down, with grain and shadow gaps.</summary>
    public static byte[] OakPlanks()
    {
        var rgb = new byte[Size * Size * 3];
        const int Planks = 5;
        const int Boards = 4;
        var plankH = Size / Boards;
        var plankW = Size / Planks;
        var rnd = new Rng(7);

        for (var y = 0; y < Size; y++)
        {
            var board = y / plankH;
            var rowOffset = (int)(rnd.Value(board) * plankW);
            for (var x = 0; x < Size; x++)
            {
                var px = (x + rowOffset) % Size;
                var plank = px / plankW;
                var seed = board * 31 + plank;

                // grain: fine rings along the plank, plus slow tone drift between boards
                var grain = MathF.Sin((y * 0.55f + Noise(px * 0.02f, seed * 7.3f) * 9f)) * 0.5f + 0.5f;
                var tone = 0.72f + 0.18f * Noise(px * 0.004f, y * 0.03f + seed);
                var v = tone + (grain - 0.5f) * 0.10f;

                var edgeX = Math.Min(px % plankW, plankW - px % plankW);
                var edgeY = Math.Min(y % plankH, plankH - y % plankH);
                var edge = Math.Min(edgeX, edgeY);
                if (edge < 1.5f) v *= 0.55f;              // shadow gap between boards
                else if (edge < 3f) v *= 0.9f;

                Put(rgb, x, y, Mul(158, 108, 62, v));
            }
        }
        return Png(rgb);
    }

    /// <summary>Height field of the boards above, turned into a tangent-space normal map.</summary>
    public static byte[] OakPlankNormal()
    {
        const int Planks = 5, Boards = 4;
        var plankH = Size / Boards;
        var plankW = Size / Planks;
        var rnd = new Rng(7);
        var height = new float[Size * Size];

        for (var y = 0; y < Size; y++)
        {
            var board = y / plankH;
            var rowOffset = (int)(rnd.Value(board) * plankW);
            for (var x = 0; x < Size; x++)
            {
                var px = (x + rowOffset) % Size;
                var seed = board * 31 + px / plankW;
                var grain = Noise(px * 0.05f, y * 0.35f + seed * 5f);
                var edgeX = Math.Min(px % plankW, plankW - px % plankW);
                var edgeY = Math.Min(y % plankH, plankH - y % plankH);
                var edge = Math.Min(edgeX, edgeY);
                var groove = edge < 2.5f ? 0f : 1f;
                height[y * Size + x] = 0.55f + grain * 0.15f - (1f - groove) * 0.5f;
            }
        }
        return NormalFromHeight(height, Size, Size, strength: 2.4f);
    }

    /// <summary>Painted plaster: barely-there blotches, so a flat wall stops reading as plastic.</summary>
    public static byte[] Plaster()
    {
        var rgb = new byte[Size * Size * 3];
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var n = Noise(x * 0.05f, y * 0.05f) * 0.5f + Noise(x * 0.21f, y * 0.21f) * 0.5f;
                var v = 0.94f + (n - 0.5f) * 0.09f;
                Put(rgb, x, y, Mul(232, 229, 221, v));
            }
        return Png(rgb);
    }

    /// <summary>Brushed steel: long horizontal streaks.</summary>
    public static byte[] BrushedSteel()
    {
        var rgb = new byte[Size * Size * 3];
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var streak = Noise(x * 0.008f, y * 0.9f) * 0.6f + Noise(x * 0.11f, y * 0.02f) * 0.4f;
                var v = 0.86f + (streak - 0.5f) * 0.25f;
                Put(rgb, x, y, Mul(150, 154, 158, v));
            }
        return Png(rgb);
    }

    /// <summary>Lawn: two scales of green noise.</summary>
    public static byte[] Grass()
    {
        var rgb = new byte[Size * Size * 3];
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var n = Noise(x * 0.35f, y * 0.35f) * 0.6f + Noise(x * 0.04f, y * 0.04f) * 0.4f;
                var v = 0.72f + (n - 0.5f) * 0.5f;
                Put(rgb, x, y, Mul(96, 124, 62, v));
            }
        return Png(rgb);
    }

    /// <summary>Woven rug: navy with a lighter border band.</summary>
    public static byte[] Rug()
    {
        var rgb = new byte[Size * Size * 3];
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var weave = ((x / 3 + y / 3) % 2 == 0 ? 1.04f : 0.96f) * (0.97f + Noise(x * 0.4f, y * 0.4f) * 0.06f);
                var border = x < 26 || y < 26 || x > Size - 26 || y > Size - 26;
                Put(rgb, x, y, border ? Mul(120, 96, 64, weave) : Mul(46, 66, 96, weave));
            }
        return Png(rgb);
    }

    // ------------------------------------------------------------------ helpers

    private static (byte r, byte g, byte b) Mul(int r, int g, int b, float k)
        => (Clamp(r * k), Clamp(g * k), Clamp(b * k));

    private static byte Clamp(float v) => (byte)Math.Clamp(v, 0f, 255f);

    private static void Put(byte[] rgb, int x, int y, (byte r, byte g, byte b) c)
    {
        var i = (y * Size + x) * 3;
        rgb[i] = c.r; rgb[i + 1] = c.g; rgb[i + 2] = c.b;
    }

    /// <summary>Smooth value noise in 0..1, deterministic for a given position.</summary>
    private static float Noise(float x, float y)
    {
        var x0 = (int)MathF.Floor(x); var y0 = (int)MathF.Floor(y);
        var fx = x - x0; var fy = y - y0;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);

        float H(int ix, int iy)
        {
            unchecked
            {
                var h = (uint)(ix * 374761393 + iy * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
            }
        }

        var a = H(x0, y0); var b = H(x0 + 1, y0);
        var c = H(x0, y0 + 1); var d = H(x0 + 1, y0 + 1);
        return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy;
    }

    private static byte[] NormalFromHeight(float[] height, int w, int h, float strength)
    {
        var rgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var xl = height[y * w + Math.Max(x - 1, 0)];
                var xr = height[y * w + Math.Min(x + 1, w - 1)];
                var yu = height[Math.Max(y - 1, 0) * w + x];
                var yd = height[Math.Min(y + 1, h - 1) * w + x];

                var nx = (xl - xr) * strength;
                var ny = (yu - yd) * strength;
                var nz = 1f;
                var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                nx /= len; ny /= len; nz /= len;

                var i = (y * w + x) * 3;
                rgb[i] = Clamp((nx * 0.5f + 0.5f) * 255f);
                rgb[i + 1] = Clamp((ny * 0.5f + 0.5f) * 255f);
                rgb[i + 2] = Clamp((nz * 0.5f + 0.5f) * 255f);
            }
        return Png(rgb);
    }

    /// <summary>Minimal 8-bit RGB PNG writer (deflate via ZLibStream).</summary>
    public static byte[] Png(byte[] rgb)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var ihdr = new byte[13];
        WriteInt(ihdr, 0, Size);
        WriteInt(ihdr, 4, Size);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 2;   // truecolour
        Chunk(ms, "IHDR", ihdr);

        var stride = Size * 3;
        var raw = new byte[Size * (stride + 1)];
        for (var y = 0; y < Size; y++)
        {
            raw[y * (stride + 1)] = 0;                                   // filter: none
            Array.Copy(rgb, y * stride, raw, y * (stride + 1) + 1, stride);
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw);
        Chunk(ms, "IDAT", compressed.ToArray());
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteInt(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteInt(len, 0, data.Length);
        s.Write(len);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);

        var crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        WriteInt(crcBytes, 0, unchecked((int)crc));
        s.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var v in a) c = CrcTable[(c ^ v) & 0xFF] ^ (c >> 8);
        foreach (var v in b) c = CrcTable[(c ^ v) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    /// <summary>Tiny deterministic RNG, so the demo scene is byte-identical on every run.</summary>
    private sealed class Rng(uint seed)
    {
        public float Value(int n)
        {
            unchecked
            {
                var h = seed * 2654435761u + (uint)n * 2246822519u;
                h ^= h >> 15;
                return (h & 0xFFFF) / 65535f;
            }
        }
    }
}
