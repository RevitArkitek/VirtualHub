// Draws the ribbon icons (a VR headset glyph) so the repo carries no opaque binaries.
using System.Drawing;
using System.Drawing.Drawing2D;

var outDir = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "RevitVr.Addin", "Resources");
Directory.CreateDirectory(outDir);

foreach (var size in new[] { 16, 32 })
{
    using var bmp = new Bitmap(size, size);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.Clear(Color.Transparent);

    float s = size;
    // background tile
    using (var bg = new LinearGradientBrush(
               new RectangleF(0, 0, s, s),
               Color.FromArgb(255, 37, 132, 224),
               Color.FromArgb(255, 18, 86, 160),
               LinearGradientMode.ForwardDiagonal))
    {
        using var path = RoundRect(new RectangleF(0.5f, 0.5f, s - 1, s - 1), s * 0.22f);
        g.FillPath(bg, path);
    }

    // headset body
    var body = new RectangleF(s * 0.14f, s * 0.31f, s * 0.72f, s * 0.38f);
    using (var white = new SolidBrush(Color.FromArgb(255, 245, 250, 255)))
    {
        using var path = RoundRect(body, s * 0.14f);
        g.FillPath(white, path);
    }

    // lenses
    using (var lens = new SolidBrush(Color.FromArgb(255, 22, 74, 130)))
    {
        var r = s * 0.075f;
        g.FillEllipse(lens, s * 0.30f - r, s * 0.50f - r, r * 2, r * 2);
        g.FillEllipse(lens, s * 0.70f - r, s * 0.50f - r, r * 2, r * 2);
    }

    // strap hint
    if (size >= 32)
    {
        using var pen = new Pen(Color.FromArgb(180, 245, 250, 255), Math.Max(1f, s * 0.045f));
        g.DrawLine(pen, s * 0.14f, s * 0.42f, s * 0.04f, s * 0.36f);
        g.DrawLine(pen, s * 0.86f, s * 0.42f, s * 0.96f, s * 0.36f);
    }

    var file = Path.Combine(outDir, $"vr{size}.png");
    bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
    Console.WriteLine($"wrote {file}");
}

static GraphicsPath RoundRect(RectangleF r, float radius)
{
    var path = new GraphicsPath();
    var d = radius * 2;
    path.AddArc(r.X, r.Y, d, d, 180, 90);
    path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}
