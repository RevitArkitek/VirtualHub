namespace RevitVr.Core.Gltf;

/// <summary>
/// Generates UVs for geometry that has none — which is all of it, since a Revit tessellation
/// carries positions and normals and nothing else.
///
/// This is a box projection, decided per vertex by which axis its normal points along, in world
/// space. Architectural surfaces are what it has to look right on: a wall is one plane, so a
/// projection along its own axis is exact; a wall seen from the other side is projected from that
/// side and is not mirrored; a floor is projected from above. A curved or rotated surface gets a
/// seam where the dominant axis flips, which is the standard trade and the reason texture
/// libraries are authored for box mapping.
///
/// Scale is in metres per repeat of the image, taken from the material's real-world texture size,
/// so a brick pattern that is 2 m wide in Revit is 2 m wide in the headset instead of stretching
/// to whatever the wall happens to be.
/// </summary>
public static class UvProjection
{
    public static void BoxProject(IReadOnlyList<float> positions, IReadOnlyList<float> normals,
                                  float tileMetresX, float tileMetresY,
                                  float offsetU, float offsetV, List<float> uvOut)
    {
        var sx = tileMetresX > 1e-4f ? tileMetresX : 1f;
        var sy = tileMetresY > 1e-4f ? tileMetresY : 1f;
        var haveNormals = normals.Count >= positions.Count;

        for (int i = 0; i + 2 < positions.Count; i += 3)
        {
            float x = positions[i], y = positions[i + 1], z = positions[i + 2];

            float nx = 0f, ny = 1f, nz = 0f;
            if (haveNormals)
            {
                nx = normals[i]; ny = normals[i + 1]; nz = normals[i + 2];
                if (Math.Abs(nx) + Math.Abs(ny) + Math.Abs(nz) < 1e-6f) { nx = 0f; ny = 1f; nz = 0f; }
            }

            float ax = Math.Abs(nx), ay = Math.Abs(ny), az = Math.Abs(nz);
            float u, v;

            if (ax >= ay && ax >= az)
            {
                // Facing along X: the image's right is -Z when the face looks +X.
                u = (-Math.Sign(nx) * z) / sx;
                v = (-y) / sy;
            }
            else if (az >= ax && az >= ay)
            {
                u = (Math.Sign(nz) * x) / sx;
                v = (-y) / sy;
            }
            else
            {
                // Floors and ceilings: right is +X, and image-down is +Z.
                u = x / sx;
                v = z / sy;
            }

            uvOut.Add(u + offsetU);
            uvOut.Add(v + offsetV);
        }
    }
}
