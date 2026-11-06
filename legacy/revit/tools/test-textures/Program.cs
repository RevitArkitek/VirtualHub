// Tests the texture pipeline: image preparation, texture-path resolution, UV projection, and the
// GLB that comes out. Runs without Revit — the encoder and resolver are compiled from the add-in's
// own source files, so this is the shipped code, not a copy of it.
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RevitVr.Addin.Export;
using RevitVr.Core.Gltf;

var pass = 0;
var fail = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  ok   {name}"); }
    else { fail++; Console.WriteLine($"  FAIL {name}  {detail}"); }
}

var dir = Path.Combine(Path.GetTempPath(), "revitvr-texture-test-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);

// ---------------------------------------------------------------- images
byte[] Image(int w, int h, BitmapEncoder encoder, bool transparent = false)
{
    var stride = w * 4;
    var pixels = new byte[stride * h];
    for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * stride + x * 4;
            pixels[i] = (byte)(x * 255 / Math.Max(w - 1, 1));
            pixels[i + 1] = (byte)(y * 255 / Math.Max(h - 1, 1));
            pixels[i + 2] = (byte)((x + y) % 256);
            // A leaf-shaped cut-out: the left edge is see-through.
            pixels[i + 3] = transparent && x < w / 4 ? (byte)0 : (byte)255;
        }
    var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
    using var ms = new MemoryStream();
    encoder.Frames.Add(BitmapFrame.Create(src));
    encoder.Save(ms);
    return ms.ToArray();
}

Console.WriteLine("image preparation");

var bigPng = Image(3000, 1200, new PngBitmapEncoder());
var bigJpeg = Image(4000, 2000, new JpegBitmapEncoder { QualityLevel = 95 });
var tiff = Image(300, 200, new TiffBitmapEncoder());
var smallPng = Image(64, 64, new PngBitmapEncoder());

File.WriteAllBytes(Path.Combine(dir, "big.png"), bigPng);
File.WriteAllBytes(Path.Combine(dir, "big.jpg"), bigJpeg);
File.WriteAllBytes(Path.Combine(dir, "scan.tif"), tiff);

Check("png sniffs as image/png", TextureEncoder.SniffMime(bigPng) == "image/png");
Check("jpeg sniffs as image/jpeg", TextureEncoder.SniffMime(bigJpeg) == "image/jpeg");
Check("tiff sniffs as image/tiff", TextureEncoder.SniffMime(tiff) == "image/tiff");
Check("junk is not an image", TextureEncoder.SniffMime("not an image"u8.ToArray()) is null);

var small = TextureEncoder.Prepare(smallPng, "small.png");
Check("a small png is passed through untouched",
    small is not null && small.Data.AsSpan().SequenceEqual(smallPng) && !small.Resized);

var preparedPng = TextureEncoder.Prepare(bigPng, "big.png");
Check("3000 px is scaled to the 2K limit",
    preparedPng is not null && preparedPng.Width == 2048 && preparedPng.Height is >= 818 and <= 820,
    preparedPng is null ? "null" : $"{preparedPng.Width}x{preparedPng.Height}");
Check("an opaque png photo is re-encoded as jpeg", preparedPng?.Mime == "image/jpeg",
    preparedPng?.Mime);

var transparentPng = Image(3000, 1200, new PngBitmapEncoder(), transparent: true);
var preparedCutout = TextureEncoder.Prepare(transparentPng, "cutout.png");
Check("a png with real transparency stays png",
    preparedCutout is not null && preparedCutout.Mime == "image/png" && preparedCutout.Width == 2048,
    preparedCutout is null ? "null" : $"{preparedCutout.Mime} {preparedCutout.Width}");

var preparedJpeg = TextureEncoder.Prepare(bigJpeg, "big.jpg");
Check("4000 px jpeg is scaled to the 2K limit",
    preparedJpeg is not null && preparedJpeg.Width == 2048 && preparedJpeg.Height == 1024,
    preparedJpeg is null ? "null" : $"{preparedJpeg.Width}x{preparedJpeg.Height}");
Check("a scaled photo is re-encoded as jpeg", preparedJpeg?.Mime == "image/jpeg", preparedJpeg?.Mime);

var preparedTiff = TextureEncoder.Prepare(Path.Combine(dir, "scan.tif"));
Check("tiff is transcoded to something glTF allows",
    preparedTiff is not null && (preparedTiff.Mime is "image/png" or "image/jpeg"),
    preparedTiff?.Mime ?? "null");
Check("the transcoded tiff carries real image bytes",
    preparedTiff is not null && TextureEncoder.SniffMime(preparedTiff.Data) is "image/png" or "image/jpeg");

Check("a missing file is null, not an exception",
    TextureEncoder.Prepare(Path.Combine(dir, "nope.png")) is null);
Check("non-image bytes are rejected", TextureEncoder.Prepare("hello"u8.ToArray(), "x.png") is null);

// ---------------------------------------------------------------- path resolution
Console.WriteLine("texture path resolution");

var lib = Path.Combine(dir, "MaterialLibrary");
var project = Path.Combine(dir, "Project");
var textures = Path.Combine(lib, "Textures");
Directory.CreateDirectory(textures);
Directory.CreateDirectory(project);
var textureFile = Path.Combine(textures, "Finishes.Flooring.Carpet.4.png");
File.WriteAllBytes(textureFile, smallPng);
var roots = new[] { lib };

var docFile = Path.Combine(project, "Beside.png");
File.WriteAllBytes(docFile, smallPng);

Check("absolute path resolves to itself",
    TexturePathResolver.Resolve(textureFile, project, roots) == textureFile);

Check("path relative to the document folder resolves",
    TexturePathResolver.Resolve("Beside.png", project, roots) == docFile);

Check("a bare library filename is found inside the library root",
    TexturePathResolver.Resolve("Finishes.Flooring.Carpet.4.png", project, roots) == textureFile,
    TexturePathResolver.Resolve("Finishes.Flooring.Carpet.4.png", project, roots) ?? "null");

Check("a library path with .. segments resolves against the library root",
    TexturePathResolver.Resolve(@"..\..\..\Textures\Finishes.Flooring.Carpet.4.png", project, roots)
        .EndsWith("Carpet.4.png", StringComparison.OrdinalIgnoreCase),
    "expected the carpet texture");

Check("forward slashes are normalised",
    TexturePathResolver.Resolve(textures.Replace('\\', '/') + "/Finishes.Flooring.Carpet.4.png", project, roots)
        == textureFile);

Check("a quoted path is unwrapped",
    TexturePathResolver.Resolve("\"" + textureFile + "\"", project, roots) == textureFile);

Check("a missing texture is null",
    TexturePathResolver.Resolve("No.Such.File.png", project, roots) is null);

Check("empty input is null", TexturePathResolver.Resolve("", project, roots) is null);

// ---------------------------------------------------------------- uv projection
Console.WriteLine("UV projection");

// A 4 m x 2.5 m wall facing +Z, tiles 2 m: u runs along +X, v runs down (-Y).
var positions = new List<float> { 0, 0, 0, 4, 0, 0, 4, 2.5f, 0 };
var normals = new List<float> { 0, 0, 1, 0, 0, 1, 0, 0, 1 };
var uv = new List<float>();
UvProjection.BoxProject(positions, normals, 2f, 2f, 0f, 0f, uv);

bool Near(float a, float b, float tol = 1e-4f) => Math.Abs(a - b) <= tol;

Check("wall: u is world X over the tile size", Near(uv[2], 2f), $"{uv[2]}");
Check("wall: v is world -Y over the tile size", Near(uv[5], -1.25f), $"{uv[5]}");
Check("wall: one UV pair per vertex", uv.Count / 2 == 3, $"{uv.Count}");

var backNormals = new List<float> { 0, 0, -1, 0, 0, -1, 0, 0, -1 };
var backUv = new List<float>();
UvProjection.BoxProject(positions, backNormals, 2f, 2f, 0f, 0f, backUv);
Check("the far side of a wall is not mirrored", Near(backUv[0], 0f) && Near(backUv[2], -2f),
    $"{backUv[0]} .. {backUv[2]}");

// A floor: u along X, v along +Z.
var floorPositions = new List<float> { 0, 0, 0, 3, 0, 0, 3, 0, 5 };
var floorNormals = new List<float> { 0, 1, 0, 0, 1, 0, 0, 1, 0 };
var floorUv = new List<float>();
UvProjection.BoxProject(floorPositions, floorNormals, 1.5f, 1.5f, 0f, 0f, floorUv);
Check("floor: u is X / tile", Near(floorUv[2], 2f), $"{floorUv[2]}");
Check("floor: v is Z / tile", Near(floorUv[5], 5f / 1.5f), $"{floorUv[5]}");

// A wall facing +X: u runs along -Z.
var sideUv = new List<float>();
UvProjection.BoxProject(new List<float> { 0, 0, 3 }, new List<float> { 1, 0, 0 }, 1f, 1f, 0f, 0f, sideUv);
Check("side wall: u is -Z / tile", Near(sideUv[0], -3f), $"{sideUv[0]}");

// Offset and anisotropic tiles.
var offsetUv = new List<float>();
UvProjection.BoxProject(new List<float> { 2, 0, 0 }, new List<float> { 0, 0, 1 }, 1f, 4f, 0.5f, 0.25f, offsetUv);
Check("tile offset is applied", Near(offsetUv[0], 2.5f) && Near(offsetUv[1], 0.25f),
    $"{offsetUv[0]}, {offsetUv[1]}");

Check("a zero tile size falls back to 1 m instead of dividing by zero",
    !float.IsInfinity(RunZeroTile()), $"{RunZeroTile()}");
float RunZeroTile()
{
    var u = new List<float>();
    UvProjection.BoxProject(new List<float> { 3, 0, 0 }, new List<float> { 0, 0, 1 }, 0f, 0f, 0f, 0f, u);
    return u[0];
}

// ---------------------------------------------------------------- texture scale guard
Console.WriteLine("texture scale");

// A 300 mm tile on a 3 m wall is ten repeats — the case a document authored in millimetres produces.
var mmUv = new List<float>();
UvProjection.BoxProject(new List<float> { 3, 0, 0 }, new List<float> { 0, 0, 1 }, 0.3f, 0.3f, 0f, 0f, mmUv);
Check("a 300 mm tile repeats ten times across 3 m", Near(mmUv[0], 10f), $"{mmUv[0]}");

// The failure this guards: that same value read as feet becomes 91 m and the texture disappears.
var badUv = new List<float>();
UvProjection.BoxProject(new List<float> { 3, 0, 0 }, new List<float> { 0, 0, 1 }, 91.44f, 91.44f, 0f, 0f, badUv);
Check("a 91 m tile would smear one image over the wall", Near(badUv[0], 0.0328f), $"{badUv[0]}");

var plausible = TextureScale.Sanitise(0.3, out var clampedPlausible);
Check("a believable size is kept", Near(plausible, 0.3f) && !clampedPlausible, $"{plausible}");

var tooBig = TextureScale.Sanitise(91.44, out var clampedBig);
Check("a millimetre value read as feet is rejected",
    Near(tooBig, TextureScale.FallbackMetres) && clampedBig, $"{tooBig}");

var tooSmall = TextureScale.Sanitise(0.0008, out var clampedSmall);
Check("a sub-millimetre size is rejected", Near(tooSmall, TextureScale.FallbackMetres) && clampedSmall,
    $"{tooSmall}");

Check("a zero size is rejected", TextureScale.Sanitise(0, out var clampedZero) > 0 && clampedZero);
Check("a negative size is rejected", TextureScale.Sanitise(-3, out var clampedNeg) > 0 && clampedNeg);
Check("NaN is rejected", TextureScale.Sanitise(double.NaN, out var clampedNan) > 0 && clampedNan);

var minEdge = TextureScale.Sanitise(TextureScale.MinMetres, out var clampedMin);
var maxEdge = TextureScale.Sanitise(TextureScale.MaxMetres, out var clampedMax);
Check("the edges of the believable range are kept",
    !clampedMin && !clampedMax && Near(minEdge, TextureScale.MinMetres),
    $"{minEdge} / {maxEdge}");

// ---------------------------------------------------------------- GLB structure
Console.WriteLine("GLB output");

var png = Image(8, 8, new PngBitmapEncoder());
var builder = new GltfBuilder();
var texA = builder.AddTexture("planks", png, "image/png");
var texADup = builder.AddTexture("planks-again", png, "image/png");
var texNormal = builder.AddTexture("planks-normal", Image(16, 16, new PngBitmapEncoder()), "image/png");

Check("identical images are stored once", texA == texADup && builder.TextureCount == 2,
    $"{texA}/{texADup} count={builder.TextureCount}");

var textured = builder.GetMaterial("Textured", 1, 1, 1, baseTexture: texA);
var mapped = builder.GetMaterial("Mapped", 1, 1, 1, baseTexture: texA, normalTexture: texNormal);
var plain = builder.GetMaterial("Plain", 0.5f, 0.5f, 0.5f);

var quadPos = new List<float> { 0, 0, 0, 2, 0, 0, 2, 2, 0, 0, 2, 0 };
var quadNrm = new List<float> { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 };
var quadIdx = new List<int> { 0, 1, 2, 0, 2, 3 };
builder.AddTriangles(textured, quadPos, quadNrm, quadIdx);
builder.AddTriangles(mapped, quadPos, quadNrm, quadIdx);
builder.AddTriangles(plain, quadPos, quadNrm, quadIdx);

var glb = builder.Build();
var (root, binChunk) = ReadGlb(glb);

Check("glb header length matches the file", (uint)glb.Length == BitConverter.ToUInt32(glb, 8));

var meshes = root.GetProperty("meshes");
var prims = new[] { 0, 1, 2 }.Select(i => meshes[i].GetProperty("primitives")[0]).ToList();
Check("textured primitive has TEXCOORD_0", prims[0].GetProperty("attributes").TryGetProperty("TEXCOORD_0", out _));
Check("plain primitive has no TEXCOORD_0", !prims[2].GetProperty("attributes").TryGetProperty("TEXCOORD_0", out _));
Check("normal-mapped primitive has TANGENT", prims[1].GetProperty("attributes").TryGetProperty("TANGENT", out _));
Check("plain primitive has no TANGENT", !prims[2].GetProperty("attributes").TryGetProperty("TANGENT", out _));

var materials = root.GetProperty("materials");
Check("baseColorTexture is wired to the material",
    materials[0].GetProperty("pbrMetallicRoughness").TryGetProperty("baseColorTexture", out var bct)
    && bct.GetProperty("index").GetInt32() == texA);
Check("normalTexture is wired to the material",
    materials[1].TryGetProperty("normalTexture", out var nt) && nt.GetProperty("index").GetInt32() == texNormal);
Check("plain material has no baseColorTexture",
    !materials[2].GetProperty("pbrMetallicRoughness").TryGetProperty("baseColorTexture", out _));

var images = root.GetProperty("images");
Check("image mimeType is written", images[0].GetProperty("mimeType").GetString() == "image/png");

// The bytes must survive the round trip: a texture that decodes to nothing renders black.
var view = root.GetProperty("bufferViews")[images[0].GetProperty("bufferView").GetInt32()];
var offset = view.GetProperty("byteOffset").GetInt32();
var length = view.GetProperty("byteLength").GetInt32();
var embedded = binChunk.AsSpan(offset, length).ToArray();
Check("embedded image bytes round-trip exactly", embedded.AsSpan().SequenceEqual(png),
    $"{embedded.Length} vs {png.Length}");

var texCoordAccessor = root.GetProperty("accessors")[prims[0].GetProperty("attributes").GetProperty("TEXCOORD_0").GetInt32()];
Check("TEXCOORD_0 accessor is VEC2 float", texCoordAccessor.GetProperty("type").GetString() == "VEC2"
    && texCoordAccessor.GetProperty("componentType").GetInt32() == 5126);
Check("TEXCOORD_0 count matches the vertex count", texCoordAccessor.GetProperty("count").GetInt32() == 4);

var tangentAccessor = root.GetProperty("accessors")[prims[1].GetProperty("attributes").GetProperty("TANGENT").GetInt32()];
Check("TANGENT accessor is VEC4 float", tangentAccessor.GetProperty("type").GetString() == "VEC4");

// UVs actually land in the buffer, not just in the JSON.
var uvView = root.GetProperty("bufferViews")[texCoordAccessor.GetProperty("bufferView").GetInt32()];
var uvOffset = uvView.GetProperty("byteOffset").GetInt32();
var u0 = BitConverter.ToSingle(binChunk, uvOffset);
var v0 = BitConverter.ToSingle(binChunk, uvOffset + 4);
Check("uv values are written to the buffer", Near(u0, 0f) && Near(v0, 0f), $"{u0},{v0}");

Directory.Delete(dir, recursive: true);

Console.WriteLine();
Console.WriteLine(fail == 0 ? $"all {pass} checks passed" : $"{pass} passed, {fail} FAILED");
return fail == 0 ? 0 : 1;

// ---------------------------------------------------------------------------
static (JsonElement root, byte[] bin) ReadGlb(byte[] glb)
{
    var jsonLength = BitConverter.ToInt32(glb, 12);
    var json = glb.AsSpan(20, jsonLength).ToArray();
    var doc = JsonDocument.Parse(TrimPadding(json));
    var binHeader = 20 + jsonLength;
    var binLength = BitConverter.ToInt32(glb, binHeader);
    var bin = glb.AsSpan(binHeader + 8, binLength).ToArray();
    return (doc.RootElement.Clone(), bin);
}

static byte[] TrimPadding(byte[] json)
{
    var end = json.Length;
    while (end > 0 && json[end - 1] is 0x20 or 0) end--;
    return json[..end];
}
