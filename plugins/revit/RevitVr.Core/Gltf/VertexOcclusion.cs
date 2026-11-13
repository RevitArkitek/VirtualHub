namespace RevitVr.Core.Gltf;

/// <summary>Tuning for a vertex-occlusion bake.</summary>
public sealed record AoOptions
{
    /// <summary>Hemisphere samples per vertex. 24 is smooth enough at 8-bit precision.</summary>
    public int Rays { get; init; } = 48;

    /// <summary>How far a ray looks for an occluder, in metres. This is what makes it "contact".</summary>
    public float MaxDistance { get; init; } = 0.8f;

    /// <summary>How much of the occlusion is applied. 1.0 crushes fully enclosed corners to black.</summary>
    public float Strength { get; init; } = 0.85f;

    /// <summary>Darkest a vertex may get, so a fully enclosed corner is not pure black.</summary>
    public float Floor { get; init; } = 0.15f;

    /// <summary>
    /// Longest triangle edge before the bake subdivides it, in metres. A Revit floor slab is two
    /// triangles, and AO on four corner vertices would just tint the whole room; subdividing gives
    /// the shading somewhere to land. 0 disables subdivision.
    /// </summary>
    public float MaxEdge { get; init; } = 0.6f;

    /// <summary>Hard cap on total triangles after subdivision, so a big model cannot explode.</summary>
    public long MaxTriangles { get; init; } = 3_000_000;

    /// <summary>
    /// Laplacian smoothing passes over the vertex graph. A finite ray count is noisy per vertex,
    /// and interpolating noisy values across a half-metre triangle reads as blotches and visible
    /// facets rather than occlusion. 0 disables it.
    /// </summary>
    public int SmoothingIterations { get; init; } = 8;

    /// <summary>How far each pass moves a vertex toward its neighbours' average.</summary>
    public float SmoothingStrength { get; init; } = 0.55f;
}

/// <summary>What a vertex-occlusion bake did, for the export report.</summary>
public sealed record AoReport(
    int Vertices, int Rays, long TrianglesBefore, long TrianglesAfter,
    double Seconds, bool Subdivided, bool Applied);

/// <summary>
/// Bakes per-vertex ambient occlusion and writes it to glTF <c>COLOR_0</c>.
///
/// This is the cheapest way to make a static scene stop looking like a massing model: every
/// renderer multiplies vertex colour into base colour for free, so the cost on the headset is a
/// few bytes per vertex and nothing per frame. The cost is paid once, here, on the PC.
///
/// It is occlusion, not lighting — no colour bleed, no light from windows, and it cannot brighten
/// anything. What it does is put the world's small-scale shadowing where your eye expects it:
/// corners, floor-wall junctions, under furniture.
/// </summary>
public static class VertexOcclusion
{
    /// <summary>
    /// Fills <paramref name="aoOut"/> with one value per vertex, 0 (fully enclosed) to 1 (open).
    /// </summary>
    public static void Compute(float[] positions, float[] normals, int[] indices,
                               AoOptions options, float[] aoOut)
    {
        var vertexCount = positions.Length / 3;
        if (vertexCount == 0) return;

        var bvh = TriangleBvh.Build(positions, indices);
        var rays = Math.Max(1, options.Rays);
        var maxDistance = Math.Max(0.01f, options.MaxDistance);
        const float epsilon = 0.002f;   // 2 mm along the normal: clears the surface without skipping it

        Parallel.For(0, vertexCount, i =>
        {
            var px = positions[i * 3];
            var py = positions[i * 3 + 1];
            var pz = positions[i * 3 + 2];

            var nx = 0f; var ny = 1f; var nz = 0f;
            if (normals.Length >= positions.Length)
            {
                nx = normals[i * 3]; ny = normals[i * 3 + 1]; nz = normals[i * 3 + 2];
                var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 1e-6f) { nx /= len; ny /= len; nz /= len; }
                else { nx = 0f; ny = 1f; nz = 0f; }
            }

            // Orthonormal basis around the normal (Duff et al., branchless).
            var sign = nz >= 0f ? 1f : -1f;
            var a = -1f / (sign + nz);
            var b = nx * ny * a;

            var tx = 1f + sign * nx * nx * a;
            var ty = sign * b;
            var tz = -sign * nx;

            var bx = b;
            var by = sign + ny * ny * a;
            var bz = -ny;

            var originX = px + nx * epsilon;
            var originY = py + ny * epsilon;
            var originZ = pz + nz * epsilon;

            // Per-vertex offset into the sample sequence, so neighbouring vertices do not share
            // the same 24 directions and produce banding.
            var jitter = Hash(i);

            var occluded = 0;
            for (var s = 0; s < rays; s++)
            {
                var u1 = (s + 0.5f) / rays;
                var u2 = RadicalInverse(s);

                u1 = u1 + jitter - MathF.Floor(u1 + jitter);
                u2 = u2 + jitter * 0.7548776f;
                u2 -= MathF.Floor(u2);

                // Cosine-weighted hemisphere: density matches what a diffuse surface sees.
                var r = MathF.Sqrt(u1);
                var phi = 6.2831853f * u2;
                var lx = r * MathF.Cos(phi);
                var ly = r * MathF.Sin(phi);
                var lz = MathF.Sqrt(MathF.Max(0f, 1f - u1));

                var dx = lx * tx + ly * bx + lz * nx;
                var dy = lx * ty + ly * by + lz * ny;
                var dz = lx * tz + ly * bz + lz * nz;

                var len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (len < 1e-6f) continue;
                dx /= len; dy /= len; dz /= len;

                if (bvh.Occluded(originX, originY, originZ, dx, dy, dz, maxDistance)) occluded++;
            }

            var raw = 1f - (float)occluded / rays;              // 0 = enclosed, 1 = open
            var shaded = 1f - options.Strength * (1f - raw);    // strength decides how dark it gets
            aoOut[i] = Math.Clamp(shaded, options.Floor, 1f);
        });
    }

    /// <summary>
    /// Averages each vertex toward its topological neighbours. Occlusion is a smooth quantity; the
    /// noise here is only the finite ray count, so this removes the noise and nothing else.
    /// </summary>
    public static void Smooth(float[] ao, int[] indices, int iterations, float strength)
    {
        var vertexCount = ao.Length;
        if (iterations <= 0 || vertexCount == 0) return;

        var sum = new float[vertexCount];
        var count = new int[vertexCount];

        for (var pass = 0; pass < iterations; pass++)
        {
            Array.Clear(sum);
            Array.Clear(count);

            for (var t = 0; t + 2 < indices.Length; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];

                sum[a] += ao[b] + ao[c]; count[a] += 2;
                sum[b] += ao[a] + ao[c]; count[b] += 2;
                sum[c] += ao[a] + ao[b]; count[c] += 2;
            }

            for (var i = 0; i < vertexCount; i++)
            {
                if (count[i] == 0) continue;                       // an isolated vertex keeps its value
                var average = sum[i] / count[i];
                ao[i] += (average - ao[i]) * strength;
            }
        }
    }

    /// <summary>Van der Corput radical inverse in base 2 — the second Hammersley coordinate.</summary>
    private static float RadicalInverse(int i)
    {
        var bits = (uint)i;
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
        return bits * 2.3283064365386963e-10f;   // / 2^32
    }

    private static float Hash(int i)
    {
        unchecked
        {
            var h = (uint)i * 2654435761u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }
}
