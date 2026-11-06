namespace RevitVr.Core.Gltf;

/// <summary>Tuning for a one-bounce indirect bake.</summary>
public sealed record IndirectOptions
{
    /// <summary>Primary rays per vertex. Indirect light is smooth, so this is cheap.</summary>
    public int Rays { get; init; } = 16;

    /// <summary>How far a ray travels before it counts as "escaped to sky".</summary>
    public float MaxDistance { get; init; } = 60f;

    /// <summary>Sky radiance at the zenith, in the same arbitrary units as the sun.</summary>
    public float SkyIntensity { get; init; } = 1f;

    /// <summary>
    /// Sun radiance used for *bounced* sunlight only. The viewer's real-time sun still provides
    /// direct light; this exists so a sunlit floor can light the ceiling above it.
    /// </summary>
    public float SunIntensity { get; init; } = 2.2f;

    /// <summary>Direction *towards* the sun, matching the viewer's light so the bounce lines up.</summary>
    public (float X, float Y, float Z) SunDirection { get; init; } = (0.30f, 0.66f, -0.69f);

    /// <summary>
    /// Bounces per path. One is not enough to light a room: a ceiling is lit by the floor, not by
    /// the sky, so with a single bounce a closed room bakes almost black.
    /// </summary>
    public int Bounces { get; init; } = 3;

    /// <summary>Include the direct sun on the surface itself, for a fully baked (unlit) preview.</summary>
    public bool SunOnSurface { get; init; } = true;

    /// <summary>
    /// Display gamma applied to the result. Real radiance ranges from ~0.03 in a back room to 1.0
    /// outdoors; storing that linearly in 8 bits is unviewable without tone mapping, so the preview
    /// compresses it the way a film curve would.
    /// </summary>
    public float ExposureGamma { get; init; } = 0.75f;

    public float Floor { get; init; } = 0.06f;
}

/// <summary>
/// Bakes indirect light — sky visibility plus one diffuse bounce — and writes it into the same
/// per-vertex colour the occlusion bake uses.
///
/// This is deliberately *not* a lightmap. Indirect light is low-frequency: it carries no crisp
/// shadow edges, only the way a room fills with light and the way a sunlit floor brightens the
/// ceiling. Storing it per vertex costs four bytes and no texture memory, and answers the question
/// a lightmap pipeline would be built to answer — "does bounce light change how this reads?" —
/// before paying for an unwrapper, an atlas and a second texture set.
/// </summary>
public static class IndirectLight
{
    /// <summary>
    /// Fills <paramref name="shadeOut"/> with a multiplier per vertex: 1 = open sky, lower in a
    /// room. <paramref name="triangleAlbedo"/> is 3 floats per triangle.
    /// </summary>
    public static void Compute(float[] positions, float[] normals, int[] indices,
                               float[] triangleAlbedo, IndirectOptions options, float[] shadeOut)
    {
        var vertexCount = positions.Length / 3;
        if (vertexCount == 0) return;

        var bvh = TriangleBvh.Build(positions, indices);
        var rays = Math.Max(1, options.Rays);
        var maxDistance = Math.Max(1f, options.MaxDistance);

        var (sx, sy, sz) = options.SunDirection;
        var sunLength = MathF.Sqrt(sx * sx + sy * sy + sz * sz);
        if (sunLength > 1e-6f) { sx /= sunLength; sy /= sunLength; sz /= sunLength; }

        // Normalising by the radiance of the open sky straight up: an unobstructed floor comes out
        // at 1.0 and keeps exactly the shading it had before, so the bake only ever explains
        // *missing* light rather than re-lighting the whole scene to some new exposure.
        var reference = SkyRadiance(0f, 1f, 0f, options.SkyIntensity).R;
        if (reference < 1e-4f) reference = 1f;

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

            var ox = px + nx * 0.002f;
            var oy = py + ny * 0.002f;
            var oz = pz + nz * 0.002f;

            var jitter = Hash(i);
            var total = 0f;

            for (var s = 0; s < rays; s++)
            {
                var u1 = (s + 0.5f) / rays;
                var u2 = RadicalInverse(s);
                u1 = u1 + jitter - MathF.Floor(u1 + jitter);
                u2 = u2 + jitter * 0.7548776f;
                u2 -= MathF.Floor(u2);

                // Primary direction: cosine-weighted around the surface normal.
                CosineHemisphere(tx, ty, tz, bx, by, bz, nx, ny, nz, u1, u2,
                                 out var dx, out var dy, out var dz);

                ox = px + nx * 0.002f;
                oy = py + ny * 0.002f;
                oz = pz + nz * 0.002f;

                var throughput = 1f;
                var radiance = 0f;

                for (var bounce = 0; bounce < Math.Max(1, options.Bounces); bounce++)
                {
                    if (!bvh.Raycast(ox, oy, oz, dx, dy, dz, maxDistance, out var distance, out var tri))
                    {
                        // The path escaped: it sees sky.
                        radiance += throughput * SkyRadiance(dx, dy, dz, options.SkyIntensity).R;
                        break;
                    }

                    var hx = ox + dx * distance;
                    var hy = oy + dy * distance;
                    var hz = oz + dz * distance;

                    bvh.TriangleNormal(tri, out var hnx, out var hny, out var hnz);
                    if (hnx * dx + hny * dy + hnz * dz > 0f) { hnx = -hnx; hny = -hny; hnz = -hnz; }

                    var albedo = tri >= 0 && tri * 3 + 2 < triangleAlbedo.Length
                        ? triangleAlbedo[tri * 3]
                        : 0.5f;

                    // Direct sun reaching this hit, shadowed. This is the whole reason a room does
                    // not bake flat black: a sunlit floor is what lights the ceiling above it.
                    var cosSun = hnx * sx + hny * sy + hnz * sz;
                    if (cosSun > 0f)
                    {
                        var blocked = bvh.Occluded(hx + hnx * 0.002f, hy + hny * 0.002f, hz + hnz * 0.002f,
                                                   sx, sy, sz, maxDistance);
                        if (!blocked) radiance += throughput * albedo * options.SunIntensity * cosSun;
                    }

                    throughput *= albedo;
                    if (throughput < 0.02f) break;

                    // Next segment: cosine-weighted around the hit normal.
                    var r1 = Hash(i * 73856093 ^ s * 19349663 ^ bounce * 83492791);
                    var r2 = Hash(i * 83492791 ^ s * 73856093 ^ bounce * 19349663);

                    // Rebuild an orthonormal basis around the hit normal.
                    var hsign = hnz >= 0f ? 1f : -1f;
                    var ha = -1f / (hsign + hnz);
                    var hb = hnx * hny * ha;

                    CosineHemisphere(
                        1f + hsign * hnx * hnx * ha, hsign * hb, -hsign * hnx,
                        hb, hsign + hny * hny * ha, -hny,
                        hnx, hny, hnz, r1, r2,
                        out dx, out dy, out dz);

                    ox = hx + hnx * 0.002f;
                    oy = hy + hny * 0.002f;
                    oz = hz + hnz * 0.002f;
                }

                total += radiance;
            }

            var radianceOut = total / rays;

            // Direct sun on this very vertex, shadowed. For a fully baked preview this is what
            // puts the patch of sunlight on the floor; the hemisphere samples carry everything
            // the surface *sees*.
            var sunTerm = 0f;
            if (options.SunOnSurface)
            {
                var cosSunSelf = nx * sx + ny * sy + nz * sz;
                if (cosSunSelf > 0f &&
                    !bvh.Occluded(px + nx * 0.003f, py + ny * 0.003f, pz + nz * 0.003f, sx, sy, sz, maxDistance))
                {
                    sunTerm = options.SunIntensity * cosSunSelf;
                }
            }

            var normalised = Math.Clamp((radianceOut + sunTerm) / reference, 0f, 1f);
            var display = MathF.Pow(normalised, Math.Max(0.05f, options.ExposureGamma));
            shadeOut[i] = Math.Clamp(display, options.Floor, 1f);
        });
    }

    /// <summary>Cosine-weighted direction in the hemisphere of (nx,ny,nz), from two uniforms.</summary>
    private static void CosineHemisphere(float tx, float ty, float tz,
                                         float bx, float by, float bz,
                                         float nx, float ny, float nz,
                                         float u1, float u2,
                                         out float dx, out float dy, out float dz)
    {
        var r = MathF.Sqrt(Math.Clamp(u1, 0f, 1f));
        var phi = 6.2831853f * u2;
        var lx = r * MathF.Cos(phi);
        var ly = r * MathF.Sin(phi);
        var lz = MathF.Sqrt(MathF.Max(0f, 1f - u1));

        dx = lx * tx + ly * bx + lz * nx;
        dy = lx * ty + ly * by + lz * ny;
        dz = lx * tz + ly * bz + lz * nz;

        var len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 1e-6f) { dx = nx; dy = ny; dz = nz; return; }
        dx /= len; dy /= len; dz /= len;
    }

    /// <summary>Simple two-colour sky: bright overhead, dim toward the horizon and below.</summary>
    private static (float R, float G, float B) SkyRadiance(float dx, float dy, float dz, float intensity)
    {
        var t = Math.Clamp(dy * 0.5f + 0.5f, 0f, 1f);          // 0 straight down, 1 straight up
        var v = (0.18f + 0.82f * t * t) * intensity;
        // A slight blue bias upward, warmer at the horizon, matching the viewer's probe.
        return (v * 0.94f, v * 0.97f, v * 1.06f);
    }

    private static float RadicalInverse(int i)
    {
        var bits = (uint)i;
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
        return bits * 2.3283064365386963e-10f;
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
