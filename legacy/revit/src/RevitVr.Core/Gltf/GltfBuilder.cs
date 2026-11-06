using System.Security.Cryptography;
using System.Text.Json;

namespace RevitVr.Core.Gltf;

/// <summary>An image embedded in the GLB. glTF only allows PNG and JPEG.</summary>
public sealed class GltfTexture
{
    public string Name = "texture";
    public byte[] Data = Array.Empty<byte>();
    public string Mime = "image/png";
}

/// <summary>
/// Minimal, allocation-light GLB 2.0 writer.
/// Geometry is merged per material so the Quest ends up with one draw call per material
/// instead of one per Revit element (thousands of draw calls will not hold 72 fps).
/// Deliberately has no Revit dependency so it can be unit-tested headlessly.
/// </summary>
public sealed class GltfBuilder
{
    private sealed class Mat
    {
        public string Name = "default";
        public float R = 0.72f, G = 0.72f, B = 0.72f, A = 1f;
        public float Metallic = 0f, Roughness = 0.85f;
        public bool DoubleSided = true;

        // -1 means "no map". Both indexes point into _textures.
        public int BaseTexture = -1, NormalTexture = -1;
        public float NormalScale = 1f;

        /// <summary>Real-world size of one repeat of the image, in metres.</summary>
        public float TileX = 1f, TileY = 1f;

        /// <summary>UV shift, so Revit's texture offset survives the export.</summary>
        public float OffsetU = 0f, OffsetV = 0f;

        public readonly List<float> Pos = new();
        public readonly List<float> Nrm = new();
        public readonly List<float> Uv = new();
        public readonly List<uint> Idx = new();
        public readonly List<float> Min = new() { float.MaxValue, float.MaxValue, float.MaxValue };
        public readonly List<float> Max = new() { float.MinValue, float.MinValue, float.MinValue };
    }

    private readonly List<Mat> _mats = new();
    private readonly Dictionary<string, int> _matIndex = new(StringComparer.Ordinal);
    private readonly List<Mat> _used = new();

    private readonly List<GltfTexture> _textures = new();
    private readonly Dictionary<string, int> _textureIndex = new(StringComparer.Ordinal);

    /// <summary>
    /// Embeds an image and returns its texture index, or -1 for empty input. Identical bytes are
    /// stored once: Revit gives many materials their own copy of the same library JPEG.
    /// </summary>
    public int AddTexture(string name, byte[] data, string mime)
    {
        if (data is null || data.Length == 0) return -1;
        var key = Convert.ToHexString(SHA256.HashData(data));
        if (_textureIndex.TryGetValue(key, out var existing)) return existing;

        _textures.Add(new GltfTexture
        {
            Name = string.IsNullOrWhiteSpace(name) ? "texture" : name,
            Data = data,
            Mime = string.IsNullOrWhiteSpace(mime) ? "image/png" : mime,
        });
        var idx = _textures.Count - 1;
        _textureIndex[key] = idx;
        return idx;
    }

    /// <summary>
    /// Registers (or returns the existing) material and returns its index.
    /// Passing a texture makes <see cref="AddTriangles"/> generate UVs from the geometry, so
    /// callers never have to thread a UV array through the export.
    /// </summary>
    public int GetMaterial(string name, float r, float g, float b, float alpha = 1f,
                           float metallic = 0f, float roughness = 0.85f, bool doubleSided = true,
                           int baseTexture = -1, int normalTexture = -1, float normalScale = 1f,
                           float tileMetresX = 1f, float tileMetresY = 1f,
                           float offsetU = 0f, float offsetV = 0f)
    {
        var key = string.Join('|', name, r, g, b, alpha, metallic, roughness, doubleSided,
                              baseTexture, normalTexture, normalScale,
                              tileMetresX, tileMetresY, offsetU, offsetV);
        if (_matIndex.TryGetValue(key, out var existing)) return existing;

        var m = new Mat
        {
            Name = name, R = r, G = g, B = b, A = alpha,
            Metallic = metallic, Roughness = roughness, DoubleSided = doubleSided,
            BaseTexture = baseTexture, NormalTexture = normalTexture, NormalScale = normalScale,
            TileX = tileMetresX > 1e-4f ? tileMetresX : 1f,
            TileY = tileMetresY > 1e-4f ? tileMetresY : 1f,
            OffsetU = offsetU, OffsetV = offsetV,
        };
        _mats.Add(m);
        var idx = _mats.Count - 1;
        _matIndex[key] = idx;
        return idx;
    }

    /// <summary>True when this material carries an image, so the caller knows UVs are generated.</summary>
    public bool IsTextured(int material)
        => material >= 0 && material < _mats.Count && _mats[material].BaseTexture >= 0;

    /// <summary>
    /// Appends a triangle list. <paramref name="positions"/> and <paramref name="normals"/> are
    /// XYZ triples in glTF space (metres, Y-up); <paramref name="indices"/> index into them.
    /// UVs are generated here, by box projection, when the material carries a texture.
    /// </summary>
    public void AddTriangles(int material, IReadOnlyList<float> positions, IReadOnlyList<float> normals,
                             IReadOnlyList<int> indices)
    {
        if (indices.Count == 0) return;
        var m = _mats[material];
        if (!_used.Contains(m)) _used.Add(m);

        var baseVertex = m.Pos.Count / 3;
        for (int i = 0; i < positions.Count; i += 3)
        {
            float x = positions[i], y = positions[i + 1], z = positions[i + 2];
            m.Pos.Add(x); m.Pos.Add(y); m.Pos.Add(z);
            if (x < m.Min[0]) m.Min[0] = x;
            if (y < m.Min[1]) m.Min[1] = y;
            if (z < m.Min[2]) m.Min[2] = z;
            if (x > m.Max[0]) m.Max[0] = x;
            if (y > m.Max[1]) m.Max[1] = y;
            if (z > m.Max[2]) m.Max[2] = z;
        }
        for (int i = 0; i < normals.Count; i++) m.Nrm.Add(normals[i]);
        // If the source had no normals, pad with +Y so the accessor stays well formed.
        while (m.Nrm.Count < m.Pos.Count) m.Nrm.Add(0f);

        if (m.BaseTexture >= 0 || m.NormalTexture >= 0)
            UvProjection.BoxProject(positions, normals, m.TileX, m.TileY, m.OffsetU, m.OffsetV, m.Uv);

        foreach (var i in indices) m.Idx.Add((uint)(baseVertex + i));
    }

    /// <summary>Returns the finished GLB bytes.</summary>
    public byte[] Build()
    {
        if (_used.Count == 0) return BuildEmpty();

        using var bin = new MemoryStream();
        var accessors = new List<Action<Utf8JsonWriter>>();
        var bufferViews = new List<Action<Utf8JsonWriter>>();
        var meshAccessors = new List<(int mat, int aPos, int aNrm, int aUv, int aIdx, int aTan)>();

        void Align()
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
        }

        Span<byte> tmp = stackalloc byte[12];
        Span<byte> ftmp = stackalloc byte[4];

        foreach (var m in _used)
        {
            var textured = m.BaseTexture >= 0 || m.NormalTexture >= 0;

            Align();
            var posOff = bin.Length;
            for (int i = 0; i < m.Pos.Count; i += 3)
            {
                BitConverter.TryWriteBytes(tmp[..4], m.Pos[i]);
                BitConverter.TryWriteBytes(tmp[4..8], m.Pos[i + 1]);
                BitConverter.TryWriteBytes(tmp[8..12], m.Pos[i + 2]);
                bin.Write(tmp);
            }
            Align();
            var nrmOff = bin.Length;
            for (int i = 0; i + 2 < m.Nrm.Count; i += 3)
            {
                BitConverter.TryWriteBytes(tmp[..4], m.Nrm[i]);
                BitConverter.TryWriteBytes(tmp[4..8], m.Nrm[i + 1]);
                BitConverter.TryWriteBytes(tmp[8..12], m.Nrm[i + 2]);
                bin.Write(tmp);
            }
            var uvOff = -1L;
            if (textured)
            {
                Align();
                uvOff = bin.Length;
                for (int i = 0; i + 1 < m.Uv.Count; i += 2)
                {
                    BitConverter.TryWriteBytes(ftmp, m.Uv[i]);
                    bin.Write(ftmp);
                    BitConverter.TryWriteBytes(ftmp, m.Uv[i + 1]);
                    bin.Write(ftmp);
                }
            }
            // A normal map needs a tangent basis. Letting the runtime derive it from screen-space
            // derivatives breaks down at the seams a box projection necessarily has, so it is
            // written out explicitly: per-vertex from the UV gradients, then Gram-Schmidt.
            var tangents = m.NormalTexture >= 0 ? ComputeTangents(m) : null;
            var tanOff = -1L;
            if (tangents is not null)
            {
                Align();
                tanOff = bin.Length;
                for (int i = 0; i + 3 < tangents.Length; i += 4)
                {
                    BitConverter.TryWriteBytes(tmp[..4], tangents[i]);
                    BitConverter.TryWriteBytes(tmp[4..8], tangents[i + 1]);
                    BitConverter.TryWriteBytes(tmp[8..12], tangents[i + 2]);
                    bin.Write(tmp);
                    BitConverter.TryWriteBytes(ftmp, tangents[i + 3]);
                    bin.Write(ftmp);
                }
            }
            Align();
            var idxOff = bin.Length;
            foreach (var i in m.Idx)
            {
                BitConverter.TryWriteBytes(ftmp, i);
                bin.Write(ftmp);
            }

            var vcount = m.Pos.Count / 3;
            var icount = m.Idx.Count;

            var bPos = bufferViews.Count;
            { var off = posOff; var len = (long)vcount * 12;
              bufferViews.Add(w => WriteView(w, off, len, 34962)); }
            var bNrm = bufferViews.Count;
            { var off = nrmOff; var len = (long)vcount * 12;
              bufferViews.Add(w => WriteView(w, off, len, 34962)); }
            var bUv = -1;
            if (textured)
            {
                bUv = bufferViews.Count;
                var off = uvOff; var len = (long)vcount * 8;
                bufferViews.Add(w => WriteView(w, off, len, 34962));
            }
            var bIdx = bufferViews.Count;
            { var off = idxOff; var len = (long)icount * 4;
              bufferViews.Add(w => WriteView(w, off, len, 34963)); }
            var bTan = -1;
            if (tangents is not null)
            {
                bTan = bufferViews.Count;
                var off = tanOff; var len = (long)vcount * 16;
                bufferViews.Add(w => WriteView(w, off, len, 34962));
            }

            var aPos = accessors.Count;
            {
                var bv = bPos; var n = vcount;
                var min = m.Min.ToArray(); var max = m.Max.ToArray();
                accessors.Add(w =>
                {
                    w.WriteStartObject();
                    w.WriteNumber("bufferView", bv);
                    w.WriteNumber("componentType", 5126);
                    w.WriteNumber("count", n);
                    w.WriteString("type", "VEC3");
                    w.WriteStartArray("min"); foreach (var t in min) w.WriteNumberValue(t); w.WriteEndArray();
                    w.WriteStartArray("max"); foreach (var t in max) w.WriteNumberValue(t); w.WriteEndArray();
                    w.WriteEndObject();
                });
            }
            var aNrm = accessors.Count;
            {
                var bv = bNrm; var n = vcount;
                accessors.Add(w =>
                {
                    w.WriteStartObject();
                    w.WriteNumber("bufferView", bv);
                    w.WriteNumber("componentType", 5126);
                    w.WriteNumber("count", n);
                    w.WriteString("type", "VEC3");
                    w.WriteEndObject();
                });
            }
            var aUv = -1;
            if (textured)
            {
                aUv = accessors.Count;
                var bv = bUv; var n = vcount;
                accessors.Add(w =>
                {
                    w.WriteStartObject();
                    w.WriteNumber("bufferView", bv);
                    w.WriteNumber("componentType", 5126);
                    w.WriteNumber("count", n);
                    w.WriteString("type", "VEC2");
                    w.WriteEndObject();
                });
            }
            var aIdx = accessors.Count;
            {
                var bv = bIdx; var n = icount;
                accessors.Add(w =>
                {
                    w.WriteStartObject();
                    w.WriteNumber("bufferView", bv);
                    w.WriteNumber("componentType", 5125);
                    w.WriteNumber("count", n);
                    w.WriteString("type", "SCALAR");
                    w.WriteEndObject();
                });
            }
            var aTan = -1;
            if (tangents is not null)
            {
                aTan = accessors.Count;
                var bv = bTan; var n = vcount;
                accessors.Add(w =>
                {
                    w.WriteStartObject();
                    w.WriteNumber("bufferView", bv);
                    w.WriteNumber("componentType", 5126);
                    w.WriteNumber("count", n);
                    w.WriteString("type", "VEC4");
                    w.WriteEndObject();
                });
            }

            meshAccessors.Add((_mats.IndexOf(m), aPos, aNrm, aUv, aIdx, aTan));
        }

        // Embedded images sit in the same BIN chunk, after the geometry.
        var imageViews = new int[_textures.Count];
        for (int i = 0; i < _textures.Count; i++)
        {
            Align();
            var off = bin.Length;
            bin.Write(_textures[i].Data);
            var len = (long)_textures[i].Data.Length;
            imageViews[i] = bufferViews.Count;
            bufferViews.Add(w => WriteView(w, off, len, -1));
        }
        Align();

        var binBytes = bin.ToArray();

        using var json = new MemoryStream();
        using (var w = new Utf8JsonWriter(json, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteStartObject("asset");
            w.WriteString("version", "2.0");
            w.WriteString("generator", "RevitVR");
            w.WriteEndObject();

            w.WriteNumber("scene", 0);

            w.WriteStartArray("scenes");
            w.WriteStartObject();
            w.WriteStartArray("nodes");
            for (int i = 0; i < meshAccessors.Count; i++) w.WriteNumberValue(i);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteStartArray("nodes");
            for (int i = 0; i < meshAccessors.Count; i++)
            {
                w.WriteStartObject();
                w.WriteString("name", _mats[meshAccessors[i].mat].Name);
                w.WriteNumber("mesh", i);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("materials");
            foreach (var v in meshAccessors)
            {
                var m = _mats[v.mat];
                w.WriteStartObject();
                w.WriteString("name", m.Name);
                w.WriteStartObject("pbrMetallicRoughness");
                w.WriteStartArray("baseColorFactor");
                w.WriteNumberValue(m.R); w.WriteNumberValue(m.G); w.WriteNumberValue(m.B); w.WriteNumberValue(m.A);
                w.WriteEndArray();
                if (m.BaseTexture >= 0)
                {
                    w.WriteStartObject("baseColorTexture");
                    w.WriteNumber("index", m.BaseTexture);
                    w.WriteNumber("texCoord", 0);
                    w.WriteEndObject();
                }
                w.WriteNumber("metallicFactor", m.Metallic);
                w.WriteNumber("roughnessFactor", m.Roughness);
                w.WriteEndObject();
                if (m.NormalTexture >= 0)
                {
                    w.WriteStartObject("normalTexture");
                    w.WriteNumber("index", m.NormalTexture);
                    w.WriteNumber("texCoord", 0);
                    w.WriteNumber("scale", m.NormalScale);
                    w.WriteEndObject();
                }
                if (m.A < 1f) w.WriteString("alphaMode", "BLEND");
                w.WriteBoolean("doubleSided", m.DoubleSided);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            if (_textures.Count > 0)
            {
                w.WriteStartArray("samplers");
                w.WriteStartObject();
                w.WriteNumber("magFilter", 9729);   // LINEAR
                w.WriteNumber("minFilter", 9987);   // LINEAR_MIPMAP_LINEAR
                w.WriteNumber("wrapS", 10497);      // REPEAT
                w.WriteNumber("wrapT", 10497);
                w.WriteEndObject();
                w.WriteEndArray();

                w.WriteStartArray("images");
                for (int i = 0; i < _textures.Count; i++)
                {
                    w.WriteStartObject();
                    w.WriteString("name", _textures[i].Name);
                    w.WriteString("mimeType", _textures[i].Mime);
                    w.WriteNumber("bufferView", imageViews[i]);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("textures");
                for (int i = 0; i < _textures.Count; i++)
                {
                    w.WriteStartObject();
                    w.WriteString("name", _textures[i].Name);
                    w.WriteNumber("sampler", 0);
                    w.WriteNumber("source", i);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            w.WriteStartArray("meshes");
            for (int i = 0; i < meshAccessors.Count; i++)
            {
                var v = meshAccessors[i];
                w.WriteStartObject();
                w.WriteString("name", "mesh_" + i);
                w.WriteStartArray("primitives");
                w.WriteStartObject();
                w.WriteStartObject("attributes");
                w.WriteNumber("POSITION", v.aPos);
                w.WriteNumber("NORMAL", v.aNrm);
                if (v.aUv >= 0) w.WriteNumber("TEXCOORD_0", v.aUv);
                if (v.aTan >= 0) w.WriteNumber("TANGENT", v.aTan);
                w.WriteEndObject();
                w.WriteNumber("indices", v.aIdx);
                w.WriteNumber("material", i);
                w.WriteNumber("mode", 4);
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("accessors");
            foreach (var a in accessors) a(w);
            w.WriteEndArray();

            w.WriteStartArray("bufferViews");
            foreach (var b in bufferViews) b(w);
            w.WriteEndArray();

            w.WriteStartArray("buffers");
            w.WriteStartObject();
            w.WriteNumber("byteLength", binBytes.Length);
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteEndObject();
        }

        var jsonBytes = json.ToArray();
        var jsonPadded = (jsonBytes.Length + 3) & ~3;
        var binPadded = (binBytes.Length + 3) & ~3;
        var total = 12 + 8 + jsonPadded + 8 + binPadded;

        var outBytes = new byte[total];
        var span = outBytes.AsSpan();
        BitConverter.TryWriteBytes(span[..4], 0x46546C67u);      // "glTF"
        BitConverter.TryWriteBytes(span[4..8], 2u);
        BitConverter.TryWriteBytes(span[8..12], (uint)total);
        BitConverter.TryWriteBytes(span[12..16], (uint)jsonPadded);
        BitConverter.TryWriteBytes(span[16..20], 0x4E4F534Au);   // "JSON"
        jsonBytes.CopyTo(span[20..]);
        for (int i = 20 + jsonBytes.Length; i < 20 + jsonPadded; i++) span[i] = 0x20; // pad with spaces
        var binHeader = 20 + jsonPadded;
        BitConverter.TryWriteBytes(span[binHeader..(binHeader + 4)], (uint)binPadded);
        BitConverter.TryWriteBytes(span[(binHeader + 4)..(binHeader + 8)], 0x004E4942u); // "BIN"
        binBytes.CopyTo(span[(binHeader + 8)..]);
        return outBytes;
    }

    /// <summary>
    /// Per-vertex tangent basis (xyz + handedness) from the UV gradients, so a normal map is
    /// evaluated against the surface it was authored for rather than against screen derivatives.
    /// </summary>
    private static float[] ComputeTangents(Mat m)
    {
        var vcount = m.Pos.Count / 3;
        var tan = new float[vcount * 3];
        var bit = new float[vcount * 3];

        for (int t = 0; t + 2 < m.Idx.Count; t += 3)
        {
            int i0 = (int)m.Idx[t], i1 = (int)m.Idx[t + 1], i2 = (int)m.Idx[t + 2];

            float x0 = m.Pos[i0 * 3], y0 = m.Pos[i0 * 3 + 1], z0 = m.Pos[i0 * 3 + 2];
            float x1 = m.Pos[i1 * 3], y1 = m.Pos[i1 * 3 + 1], z1 = m.Pos[i1 * 3 + 2];
            float x2 = m.Pos[i2 * 3], y2 = m.Pos[i2 * 3 + 1], z2 = m.Pos[i2 * 3 + 2];

            float u0 = m.Uv[i0 * 2], v0 = m.Uv[i0 * 2 + 1];
            float u1 = m.Uv[i1 * 2], v1 = m.Uv[i1 * 2 + 1];
            float u2 = m.Uv[i2 * 2], v2 = m.Uv[i2 * 2 + 1];

            float e1x = x1 - x0, e1y = y1 - y0, e1z = z1 - z0;
            float e2x = x2 - x0, e2y = y2 - y0, e2z = z2 - z0;

            float du1 = u1 - u0, dv1 = v1 - v0;
            float du2 = u2 - u0, dv2 = v2 - v0;

            var det = du1 * dv2 - du2 * dv1;
            if (Math.Abs(det) < 1e-12f) continue;   // degenerate UVs: leave the basis at zero
            var r = 1f / det;

            var tx = (e1x * dv2 - e2x * dv1) * r;
            var ty = (e1y * dv2 - e2y * dv1) * r;
            var tz = (e1z * dv2 - e2z * dv1) * r;

            var bx = (e2x * du1 - e1x * du2) * r;
            var by = (e2y * du1 - e1y * du2) * r;
            var bz = (e2z * du1 - e1z * du2) * r;

            foreach (var i in new[] { i0, i1, i2 })
            {
                tan[i * 3] += tx; tan[i * 3 + 1] += ty; tan[i * 3 + 2] += tz;
                bit[i * 3] += bx; bit[i * 3 + 1] += by; bit[i * 3 + 2] += bz;
            }
        }

        var result = new float[vcount * 4];
        for (var i = 0; i < vcount; i++)
        {
            float nx = m.Nrm[i * 3], ny = m.Nrm[i * 3 + 1], nz = m.Nrm[i * 3 + 2];
            var nl = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl < 1e-9f) { nx = 0; ny = 1; nz = 0; } else { nx /= nl; ny /= nl; nz /= nl; }

            var tx = tan[i * 3]; var ty = tan[i * 3 + 1]; var tz = tan[i * 3 + 2];

            // Gram-Schmidt against the normal.
            var d = nx * tx + ny * ty + nz * tz;
            tx -= nx * d; ty -= ny * d; tz -= nz * d;

            var tl = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
            if (tl < 1e-6f)
            {
                // No usable gradient here: pick any perpendicular so the attribute stays valid.
                tx = Math.Abs(nx) > 0.9f ? 0f : 1f;
                ty = Math.Abs(nx) > 0.9f ? 1f : 0f;
                tz = 0f;
                d = nx * tx + ny * ty + nz * tz;
                tx -= nx * d; ty -= ny * d; tz -= nz * d;
                tl = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
                if (tl < 1e-6f) { tx = 1f; ty = 0f; tz = 0f; tl = 1f; }
            }
            tx /= tl; ty /= tl; tz /= tl;

            // Handedness: the sign of the bitangent relative to cross(normal, tangent).
            var cx = ny * tz - nz * ty;
            var cy = nz * tx - nx * tz;
            var cz = nx * ty - ny * tx;
            var w = (cx * bit[i * 3] + cy * bit[i * 3 + 1] + cz * bit[i * 3 + 2]) < 0f ? -1f : 1f;

            result[i * 4] = tx; result[i * 4 + 1] = ty; result[i * 4 + 2] = tz; result[i * 4 + 3] = w;
        }

        return result;
    }

    private static void WriteView(Utf8JsonWriter w, long offset, long length, int target)
    {
        w.WriteStartObject();
        w.WriteNumber("buffer", 0);
        w.WriteNumber("byteOffset", offset);
        w.WriteNumber("byteLength", length);
        if (target > 0) w.WriteNumber("target", target);
        w.WriteEndObject();
    }

    private static byte[] BuildEmpty()
    {
        var json = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[]}]}"u8.ToArray();
        var padded = (json.Length + 3) & ~3;
        var total = 12 + 8 + padded;
        var buf = new byte[total];
        var s = buf.AsSpan();
        BitConverter.TryWriteBytes(s[..4], 0x46546C67u);
        BitConverter.TryWriteBytes(s[4..8], 2u);
        BitConverter.TryWriteBytes(s[8..12], (uint)total);
        BitConverter.TryWriteBytes(s[12..16], (uint)padded);
        BitConverter.TryWriteBytes(s[16..20], 0x4E4F534Au);
        json.CopyTo(s[20..]);
        for (int i = 20 + json.Length; i < 20 + padded; i++) s[i] = 0x20;
        return buf;
    }

    public int TriangleCount => _used.Sum(m => m.Idx.Count / 3);
    public int MaterialCount => _used.Count;
    public int VertexCount => _used.Sum(m => m.Pos.Count / 3);
    public int TextureCount => _textures.Count;
    public long TextureBytes => _textures.Sum(t => (long)t.Data.Length);
    public int TexturedMaterials => _used.Count(m => m.BaseTexture >= 0 || m.NormalTexture >= 0);
}
