using System.Text.Json;

namespace RevitVr.Core.Gltf;

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
        public readonly List<float> Pos = new();
        public readonly List<float> Nrm = new();
        public readonly List<uint> Idx = new();
        public readonly List<float> Min = new() { float.MaxValue, float.MaxValue, float.MaxValue };
        public readonly List<float> Max = new() { float.MinValue, float.MinValue, float.MinValue };
    }

    private readonly List<Mat> _mats = new();
    private readonly Dictionary<string, int> _matIndex = new(StringComparer.Ordinal);
    private readonly List<Mat> _used = new();

    /// <summary>Registers (or returns the existing) material and returns its index.</summary>
    public int GetMaterial(string name, float r, float g, float b, float alpha = 1f,
                           float metallic = 0f, float roughness = 0.85f, bool doubleSided = true)
    {
        var key = string.Join('|', name, r, g, b, alpha, metallic, roughness, doubleSided);
        if (_matIndex.TryGetValue(key, out var existing)) return existing;

        var m = new Mat
        {
            Name = name, R = r, G = g, B = b, A = alpha,
            Metallic = metallic, Roughness = roughness, DoubleSided = doubleSided
        };
        _mats.Add(m);
        var idx = _mats.Count - 1;
        _matIndex[key] = idx;
        return idx;
    }

    /// <summary>
    /// Appends a triangle list. <paramref name="positions"/> and <paramref name="normals"/> are
    /// XYZ triples in glTF space (metres, Y-up); <paramref name="indices"/> index into them.
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

        foreach (var i in indices) m.Idx.Add((uint)(baseVertex + i));
    }

    /// <summary>Returns the finished GLB bytes.</summary>
    public byte[] Build()
    {
        if (_used.Count == 0) return BuildEmpty();

        using var bin = new MemoryStream();
        var views = new List<(int mat, long posOff, long nrmOff, long idxOff, int vcount, int icount)>();

        void Align()
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
        }

        Span<byte> tmp = stackalloc byte[12];
        Span<byte> itmp = stackalloc byte[4];

        foreach (var m in _used)
        {
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
            Align();
            var idxOff = bin.Length;
            foreach (var i in m.Idx)
            {
                BitConverter.TryWriteBytes(itmp, i);
                bin.Write(itmp);
            }
            views.Add((_mats.IndexOf(m), posOff, nrmOff, idxOff, m.Pos.Count / 3, m.Idx.Count));
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
            for (int i = 0; i < views.Count; i++) w.WriteNumberValue(i);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteStartArray("nodes");
            for (int i = 0; i < views.Count; i++)
            {
                w.WriteStartObject();
                w.WriteString("name", _mats[views[i].mat].Name);
                w.WriteNumber("mesh", i);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("materials");
            foreach (var v in views)
            {
                var m = _mats[v.mat];
                w.WriteStartObject();
                w.WriteString("name", m.Name);
                w.WriteStartObject("pbrMetallicRoughness");
                w.WriteStartArray("baseColorFactor");
                w.WriteNumberValue(m.R); w.WriteNumberValue(m.G); w.WriteNumberValue(m.B); w.WriteNumberValue(m.A);
                w.WriteEndArray();
                w.WriteNumber("metallicFactor", m.Metallic);
                w.WriteNumber("roughnessFactor", m.Roughness);
                w.WriteEndObject();
                if (m.A < 1f) w.WriteString("alphaMode", "BLEND");
                w.WriteBoolean("doubleSided", m.DoubleSided);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("meshes");
            for (int i = 0; i < views.Count; i++)
            {
                w.WriteStartObject();
                w.WriteString("name", "mesh_" + i);
                w.WriteStartArray("primitives");
                w.WriteStartObject();
                w.WriteStartObject("attributes");
                w.WriteNumber("POSITION", i * 3);
                w.WriteNumber("NORMAL", i * 3 + 1);
                w.WriteEndObject();
                w.WriteNumber("indices", i * 3 + 2);
                w.WriteNumber("material", i);
                w.WriteNumber("mode", 4);
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("accessors");
            for (int i = 0; i < views.Count; i++)
            {
                var v = views[i];
                var m = _mats[v.mat];
                // POSITION
                w.WriteStartObject();
                w.WriteNumber("bufferView", i * 3);
                w.WriteNumber("componentType", 5126);
                w.WriteNumber("count", v.vcount);
                w.WriteString("type", "VEC3");
                w.WriteStartArray("min");
                foreach (var t in m.Min) w.WriteNumberValue(t);
                w.WriteEndArray();
                w.WriteStartArray("max");
                foreach (var t in m.Max) w.WriteNumberValue(t);
                w.WriteEndArray();
                w.WriteEndObject();
                // NORMAL
                w.WriteStartObject();
                w.WriteNumber("bufferView", i * 3 + 1);
                w.WriteNumber("componentType", 5126);
                w.WriteNumber("count", v.vcount);
                w.WriteString("type", "VEC3");
                w.WriteEndObject();
                // INDEX
                w.WriteStartObject();
                w.WriteNumber("bufferView", i * 3 + 2);
                w.WriteNumber("componentType", 5125);
                w.WriteNumber("count", v.icount);
                w.WriteString("type", "SCALAR");
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("bufferViews");
            for (int i = 0; i < views.Count; i++)
            {
                var v = views[i];
                w.WriteStartObject();
                w.WriteNumber("buffer", 0);
                w.WriteNumber("byteOffset", v.posOff);
                w.WriteNumber("byteLength", (long)v.vcount * 12);
                w.WriteNumber("target", 34962);
                w.WriteEndObject();
                w.WriteStartObject();
                w.WriteNumber("buffer", 0);
                w.WriteNumber("byteOffset", v.nrmOff);
                w.WriteNumber("byteLength", (long)v.vcount * 12);
                w.WriteNumber("target", 34962);
                w.WriteEndObject();
                w.WriteStartObject();
                w.WriteNumber("buffer", 0);
                w.WriteNumber("byteOffset", v.idxOff);
                w.WriteNumber("byteLength", (long)v.icount * 4);
                w.WriteNumber("target", 34963);
                w.WriteEndObject();
            }
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

    private static byte[] BuildEmpty()
    {
        using var ms = new MemoryStream();
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
}
