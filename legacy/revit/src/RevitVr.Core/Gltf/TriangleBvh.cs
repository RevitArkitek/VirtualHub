namespace RevitVr.Core.Gltf;

/// <summary>
/// A bounding volume hierarchy over a triangle soup, built once and queried millions of times by
/// <see cref="VertexOcclusion"/> — which is why it is flat arrays rather than objects.
///
/// Only one question is ever asked of it: "does anything block this ray before distance t?" —
/// so the search exits at the first hit and never tracks the nearest one.
/// </summary>
public sealed class TriangleBvh
{
    private readonly float[] _positions;
    private readonly int[] _triangles;
    private readonly float[] _centroid;      // xyz per triangle, for the median split

    // Node fields as parallel arrays, one entry per node.
    private readonly List<float> _min = new();
    private readonly List<float> _max = new();
    private readonly int[] _leftOrStart;
    private readonly int[] _right;
    private readonly int[] _count;
    private int _nodes;

    private readonly int[] _order;

    private TriangleBvh(float[] positions, int[] triangles)
    {
        _positions = positions;
        _triangles = triangles;

        var triangleCount = triangles.Length / 3;
        _order = new int[triangleCount];
        _centroid = new float[triangleCount * 3];

        for (var i = 0; i < triangleCount; i++)
        {
            _order[i] = i;
            var t = i * 3;
            var i0 = triangles[t] * 3;
            var i1 = triangles[t + 1] * 3;
            var i2 = triangles[t + 2] * 3;
            _centroid[t] = (positions[i0] + positions[i1] + positions[i2]) / 3f;
            _centroid[t + 1] = (positions[i0 + 1] + positions[i1 + 1] + positions[i2 + 1]) / 3f;
            _centroid[t + 2] = (positions[i0 + 2] + positions[i1 + 2] + positions[i2 + 2]) / 3f;
        }

        // A binary tree over n leaves has fewer than 2n nodes.
        var capacity = Math.Max(1, triangleCount * 2 + 2);
        _leftOrStart = new int[capacity];
        _right = new int[capacity];
        _count = new int[capacity];

        if (triangleCount > 0) BuildNode(0, triangleCount, 0);
    }

    public static TriangleBvh Build(float[] positions, int[] triangles)
        => new(positions, triangles);

    public int NodeCount => _nodes;

    /// <summary>Builds the node covering <c>_order[start..start+count)</c>; returns its index.</summary>
    private int BuildNode(int start, int count, int depth)
    {
        var node = _nodes++;

        _min.Add(float.MaxValue); _min.Add(float.MaxValue); _min.Add(float.MaxValue);
        _max.Add(float.MinValue); _max.Add(float.MinValue); _max.Add(float.MinValue);
        _leftOrStart[node] = start;
        _count[node] = count;

        float cxMin = float.MaxValue, cyMin = float.MaxValue, czMin = float.MaxValue;
        float cxMax = float.MinValue, cyMax = float.MinValue, czMax = float.MinValue;

        for (var i = start; i < start + count; i++)
        {
            var t = _order[i] * 3;
            var i0 = _triangles[t] * 3;
            var i1 = _triangles[t + 1] * 3;
            var i2 = _triangles[t + 2] * 3;

            float ax = _positions[i0], ay = _positions[i0 + 1], az = _positions[i0 + 2];
            float bx = _positions[i1], by = _positions[i1 + 1], bz = _positions[i1 + 2];
            float dx = _positions[i2], dy = _positions[i2 + 1], dz = _positions[i2 + 2];

            _min[node * 3] = MathF.Min(_min[node * 3], MathF.Min(ax, MathF.Min(bx, dx)));
            _min[node * 3 + 1] = MathF.Min(_min[node * 3 + 1], MathF.Min(ay, MathF.Min(by, dy)));
            _min[node * 3 + 2] = MathF.Min(_min[node * 3 + 2], MathF.Min(az, MathF.Min(bz, dz)));
            _max[node * 3] = MathF.Max(_max[node * 3], MathF.Max(ax, MathF.Max(bx, dx)));
            _max[node * 3 + 1] = MathF.Max(_max[node * 3 + 1], MathF.Max(ay, MathF.Max(by, dy)));
            _max[node * 3 + 2] = MathF.Max(_max[node * 3 + 2], MathF.Max(az, MathF.Max(bz, dz)));

            var mx = _centroid[t]; var my = _centroid[t + 1]; var mz = _centroid[t + 2];
            cxMin = MathF.Min(cxMin, mx); cxMax = MathF.Max(cxMax, mx);
            cyMin = MathF.Min(cyMin, my); cyMax = MathF.Max(cyMax, my);
            czMin = MathF.Min(czMin, mz); czMax = MathF.Max(czMax, mz);
        }

        // Leaf when small, or at the depth cap so a degenerate soup cannot recurse forever.
        if (count <= 4 || depth >= 32) return node;

        var ex = cxMax - cxMin; var ey = cyMax - cyMin; var ez = czMax - czMin;
        var axis = ex >= ey && ex >= ez ? 0 : ey >= ez ? 1 : 2;

        var mid = start + count / 2;
        Array.Sort(_order, start, count,
            Comparer<int>.Create((a, b) => _centroid[a * 3 + axis].CompareTo(_centroid[b * 3 + axis])));

        var left = BuildNode(start, mid - start, depth + 1);
        var right = BuildNode(mid, start + count - mid, depth + 1);

        _leftOrStart[node] = left;   // interior node: points at the left child
        _right[node] = right;
        _count[node] = 0;
        return node;
    }

    /// <summary>
    /// Nearest hit along a ray, for baking bounce light: unlike <see cref="Occluded"/> this has to
    /// know *what* was hit and how far away, not merely that something was.
    /// </summary>
    public bool Raycast(float ox, float oy, float oz, float dx, float dy, float dz, float tMax,
                        out float distance, out int triangle)
    {
        distance = tMax;
        triangle = -1;
        if (_nodes == 0) return false;

        var invX = 1f / dx; var invY = 1f / dy; var invZ = 1f / dz;
        return Raycast(0, ox, oy, oz, dx, dy, dz, invX, invY, invZ, ref distance, ref triangle, 0);
    }

    private bool Raycast(int node, float ox, float oy, float oz, float dx, float dy, float dz,
                         float invX, float invY, float invZ, ref float best, ref int triangle, int depth)
    {
        if (depth > 40) return false;
        if (!SlabTest(node, ox, oy, oz, invX, invY, invZ, best)) return false;

        var found = false;
        var count = _count[node];
        if (count > 0)
        {
            var start = _leftOrStart[node];
            for (var i = start; i < start + count; i++)
            {
                var t = _order[i];
                if (HitDistance(t, ox, oy, oz, dx, dy, dz, best, out var d))
                {
                    best = d; triangle = t; found = true;
                }
            }
            return found;
        }

        var left = _leftOrStart[node];
        var right = _right[node];
        var hitLeft = Raycast(left, ox, oy, oz, dx, dy, dz, invX, invY, invZ, ref best, ref triangle, depth + 1);
        var hitRight = Raycast(right, ox, oy, oz, dx, dy, dz, invX, invY, invZ, ref best, ref triangle, depth + 1);
        return hitLeft || hitRight;
    }

    /// <summary>Möller–Trumbore returning the hit distance instead of a yes/no.</summary>
    private bool HitDistance(int triangle, float ox, float oy, float oz,
                             float dx, float dy, float dz, float tMax, out float distance)
    {
        distance = 0;

        var t = triangle * 3;
        var i0 = _triangles[t] * 3;
        var i1 = _triangles[t + 1] * 3;
        var i2 = _triangles[t + 2] * 3;

        float ax = _positions[i0], ay = _positions[i0 + 1], az = _positions[i0 + 2];
        float e1x = _positions[i1] - ax, e1y = _positions[i1 + 1] - ay, e1z = _positions[i1 + 2] - az;
        float e2x = _positions[i2] - ax, e2y = _positions[i2 + 1] - ay, e2z = _positions[i2 + 2] - az;

        var px = dy * e2z - dz * e2y;
        var py = dz * e2x - dx * e2z;
        var pz = dx * e2y - dy * e2x;

        var det = e1x * px + e1y * py + e1z * pz;
        if (MathF.Abs(det) < 1e-9f) return false;
        var invDet = 1f / det;

        var tx = ox - ax;
        var u = (tx * px + (oy - ay) * py + (oz - az) * pz) * invDet;
        if (u < 0f || u > 1f) return false;

        var qx = (oy - ay) * e1z - (oz - az) * e1y;
        var qy = (oz - az) * e1x - (ox - ax) * e1z;
        var qz = (ox - ax) * e1y - (oy - ay) * e1x;

        var v = (dx * qx + dy * qy + dz * qz) * invDet;
        if (v < 0f || u + v > 1f) return false;

        var d = (e2x * qx + e2y * qy + e2z * qz) * invDet;
        if (d <= 1e-4f || d >= tMax) return false;

        distance = d;
        return true;
    }

    /// <summary>Per-triangle geometric normal (not the shading normal), for bounce shading.</summary>
    public void TriangleNormal(int triangle, out float nx, out float ny, out float nz)
    {
        var t = triangle * 3;
        var i0 = _triangles[t] * 3;
        var i1 = _triangles[t + 1] * 3;
        var i2 = _triangles[t + 2] * 3;

        float ax = _positions[i0], ay = _positions[i0 + 1], az = _positions[i0 + 2];
        float e1x = _positions[i1] - ax, e1y = _positions[i1 + 1] - ay, e1z = _positions[i1 + 2] - az;
        float e2x = _positions[i2] - ax, e2y = _positions[i2 + 1] - ay, e2z = _positions[i2 + 2] - az;

        nx = e1y * e2z - e1z * e2y;
        ny = e1z * e2x - e1x * e2z;
        nz = e1x * e2y - e1y * e2x;

        var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-12f) { nx = 0; ny = 1; nz = 0; return; }
        nx /= len; ny /= len; nz /= len;
    }

    /// <summary>Returns any triangle blocking the ray before <paramref name="tMax"/>.</summary>
    public bool Occluded(float ox, float oy, float oz, float dx, float dy, float dz, float tMax)
    {
        if (_nodes == 0) return false;
        var invX = 1f / dx; var invY = 1f / dy; var invZ = 1f / dz;
        return Occluded(0, ox, oy, oz, dx, dy, dz, invX, invY, invZ, tMax, 0);
    }

    private bool Occluded(int node, float ox, float oy, float oz, float dx, float dy, float dz,
                          float invX, float invY, float invZ, float tMax, int depth)
    {
        if (depth > 40) return false;
        if (!SlabTest(node, ox, oy, oz, invX, invY, invZ, tMax)) return false;

        var count = _count[node];
        if (count > 0)
        {
            var start = _leftOrStart[node];
            for (var i = start; i < start + count; i++)
                if (HitsTriangle(_order[i], ox, oy, oz, dx, dy, dz, tMax)) return true;
            return false;
        }

        var left = _leftOrStart[node];
        var right = _right[node];
        return Occluded(left, ox, oy, oz, dx, dy, dz, invX, invY, invZ, tMax, depth + 1)
            || Occluded(right, ox, oy, oz, dx, dy, dz, invX, invY, invZ, tMax, depth + 1);
    }

    /// <summary>Ray-box intersection, slab method, with the inverse direction precomputed.</summary>
    private bool SlabTest(int node, float ox, float oy, float oz,
                          float invX, float invY, float invZ, float tMax)
    {
        var minX = _min[node * 3]; var minY = _min[node * 3 + 1]; var minZ = _min[node * 3 + 2];
        var maxX = _max[node * 3]; var maxY = _max[node * 3 + 1]; var maxZ = _max[node * 3 + 2];

        var t1 = (minX - ox) * invX; var t2 = (maxX - ox) * invX;
        var tNear = MathF.Min(t1, t2); var tFar = MathF.Max(t1, t2);

        t1 = (minY - oy) * invY; t2 = (maxY - oy) * invY;
        tNear = MathF.Max(tNear, MathF.Min(t1, t2)); tFar = MathF.Min(tFar, MathF.Max(t1, t2));

        t1 = (minZ - oz) * invZ; t2 = (maxZ - oz) * invZ;
        tNear = MathF.Max(tNear, MathF.Min(t1, t2)); tFar = MathF.Min(tFar, MathF.Max(t1, t2));

        return tFar >= MathF.Max(tNear, 0f) && tNear <= tMax;
    }

    /// <summary>Möller–Trumbore, double-sided: any triangle can block a ray.</summary>
    private bool HitsTriangle(int triangle, float ox, float oy, float oz,
                              float dx, float dy, float dz, float tMax)
    {
        var t = triangle * 3;
        var i0 = _triangles[t] * 3;
        var i1 = _triangles[t + 1] * 3;
        var i2 = _triangles[t + 2] * 3;

        float ax = _positions[i0], ay = _positions[i0 + 1], az = _positions[i0 + 2];
        float e1x = _positions[i1] - ax, e1y = _positions[i1 + 1] - ay, e1z = _positions[i1 + 2] - az;
        float e2x = _positions[i2] - ax, e2y = _positions[i2 + 1] - ay, e2z = _positions[i2 + 2] - az;

        var px = dy * e2z - dz * e2y;
        var py = dz * e2x - dx * e2z;
        var pz = dx * e2y - dy * e2x;

        var det = e1x * px + e1y * py + e1z * pz;
        if (MathF.Abs(det) < 1e-9f) return false;   // parallel: cannot block
        var invDet = 1f / det;

        var tx = ox - ax;
        var u = (tx * px + (oy - ay) * py + (oz - az) * pz) * invDet;
        if (u < 0f || u > 1f) return false;

        var qx = (oy - ay) * e1z - (oz - az) * e1y;
        var qy = (oz - az) * e1x - (ox - ax) * e1z;
        var qz = (ox - ax) * e1y - (oy - ay) * e1x;

        var v = (dx * qx + dy * qy + dz * qz) * invDet;
        if (v < 0f || u + v > 1f) return false;

        var distance = (e2x * qx + e2y * qy + e2z * qz) * invDet;
        return distance > 1e-4f && distance < tMax;
    }
}
