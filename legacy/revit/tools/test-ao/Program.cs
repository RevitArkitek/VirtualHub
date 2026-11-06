// Tests the ambient-occlusion bake in RevitVr.Core: the BVH, the ray setup, the falloff with
// distance, and the COLOR_0 the builder writes.
using RevitVr.Core.Gltf;

var pass = 0;
var fail = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  ok   {name}"); }
    else { fail++; Console.WriteLine($"  FAIL {name}  {detail}"); }
}

// ---------------------------------------------------------------- BVH
Console.WriteLine("bvh");

// A 4 x 4 m floor at y = 0, made of two triangles.
var floorPositions = new float[]
{
    0, 0, 0,  4, 0, 0,  4, 0, 4,
    0, 0, 0,  4, 0, 4,  0, 0, 4,
};
var floorTriangles = new int[] { 0, 1, 2, 3, 4, 5 };
var floorBvh = TriangleBvh.Build(floorPositions, floorTriangles);

Check("a ray straight down hits the floor", floorBvh.Occluded(2f, 2f, 2f, 0f, -1f, 0f, 10f));
Check("a ray straight up misses it", !floorBvh.Occluded(2f, 2f, 2f, 0f, 1f, 0f, 10f));
Check("a hit beyond the maximum distance is not an occlusion",
    !floorBvh.Occluded(2f, 2f, 2f, 0f, -1f, 0f, 1.0f));

// A box has more than four triangles, so its tree has interior nodes — the case one leaf never
// exercises. A wrong child index here silently returns "no occlusion" for everything.
var boxProbe = new SoupBuilder();
boxProbe.Box((-1, -1, -1), (1, 1, 1));
var boxBvh = TriangleBvh.Build(boxProbe.Positions.ToArray(), boxProbe.Indices.ToArray());
Check("a downward ray inside a box hits", boxBvh.Occluded(0f, 0f, 0f, 0f, -1f, 0f, 5f));
Check("a wall-facing ray inside a box hits", boxBvh.Occluded(0f, 0f, 0f, 1f, 0f, 0f, 5f));

// ---------------------------------------------------------------- occlusion
Console.WriteLine("occlusion");

// A 6 x 6 m floor with a 2 m wall standing on its edge: the case that matters in a room. The floor
// is a grid so it has vertices near the wall, as a subdivided export would.
SoupBuilder Room()
{
    var s = new SoupBuilder();
    const float size = 6f;
    const float step = 0.25f;

    for (var z = 0f; z < size - 1e-4f; z += step)
        for (var x = 0f; x < size - 1e-4f; x += step)
            s.Quad(new[] { (x, 0f, z), (x + step, 0f, z), (x + step, 0f, z + step), (x, 0f, z + step) },
                   normal: (0, 1, 0));

    // Wall on the x = 0 edge, 0.2 m thick, facing into the room.
    const float x1 = 0f, x0 = -0.2f;
    s.Quad(new[] { (x1, 0f, 0f), (x1, 0f, size), (x1, 2f, size), (x1, 2f, 0f) }, normal: (1, 0, 0));
    s.Quad(new[] { (x0, 0f, size), (x0, 0f, 0f), (x0, 2f, 0f), (x0, 2f, size) }, normal: (-1, 0, 0));
    s.Quad(new[] { (x0, 2f, 0f), (x1, 2f, 0f), (x1, 2f, size), (x0, 2f, size) }, normal: (0, 1, 0));
    return s;
}

float DarkestFloorNear(SoupBuilder s, (float[] Ao, int _) bake, float distance)
{
    var best = 1f;
    var n = s.Positions.Count / 3;
    for (var i = 0; i < n; i++)
    {
        if (Math.Abs(s.Positions[i * 3 + 1]) > 1e-4f) continue;   // floor vertices only
        if (s.Positions[i * 3] > distance) continue;
        if (bake.Ao[i] < best) best = bake.Ao[i];
    }
    return best;
}

var room = Room();
var roomBake = room.Bake(new AoOptions { Rays = 64, MaxDistance = 0.8f, MaxEdge = 0 });

var nearWall = DarkestFloorNear(room, roomBake, 0.3f);

// The vertex closest to the middle of the room, not an index guess.
int middle = 0; var bestDist = float.MaxValue;
for (var i = 0; i < room.Positions.Count / 3; i++)
{
    if (Math.Abs(room.Positions[i * 3 + 1]) > 1e-4f) continue;
    var dx = room.Positions[i * 3] - 3f; var dz = room.Positions[i * 3 + 2] - 3f;
    var d = dx * dx + dz * dz;
    if (d < bestDist) { bestDist = d; middle = i; }
}

Console.WriteLine($"       floor ao: near wall {nearWall:0.00}, middle {roomBake.Ao[middle]:0.00}");
Check("the floor is dark against the wall", nearWall < 0.85f, $"ao={nearWall:0.00}");
Check("the floor is open in the middle", roomBake.Ao[middle] > 0.95f, $"ao={roomBake.Ao[middle]:0.00}");

// A short reach must leave more of the floor open than a long one.
var shortReach = room.Bake(new AoOptions { Rays = 64, MaxDistance = 0.25f, MaxEdge = 0 });
Check("a short reach leaves the floor lighter",
    DarkestFloorNear(room, shortReach, 0.3f) > nearWall,
    $"{DarkestFloorNear(room, shortReach, 0.3f):0.00} vs {nearWall:0.00}");

// Two facing planes close together: the ceiling is wider than the floor, so it covers nearly the
// whole sky above every floor vertex.
var closed = new SoupBuilder();
closed.Quad(new[] { (0f, 0f, 0f), (4f, 0f, 0f), (4f, 0f, 4f), (0f, 0f, 4f) }, normal: (0, 1, 0));
closed.Quad(new[] { (-4f, 0.4f, 8f), (8f, 0.4f, 8f), (8f, 0.4f, -4f), (-4f, 0.4f, -4f) }, normal: (0, -1, 0));
var closedBake = closed.Bake(new AoOptions { Rays = 64, MaxDistance = 5.0f, MaxEdge = 0 });
// Only the floor's own vertices: the ceiling's corners stick out past the floor, so they correctly
// see open sky.
var floorOnly = closedBake.Ao.Take(4).ToArray();
Check("a low ceiling darkens the floor", floorOnly.All(v => v < 0.3f), $"max={floorOnly.Max():0.00}");
Check("nothing is fully black", floorOnly.All(v => v >= 0.15f - 1e-4f), $"min={floorOnly.Min():0.00}");

// An isolated quad occludes nothing.
var lone = new SoupBuilder();
lone.Quad(new[] { (0f, 0f, 0f), (4f, 0f, 0f), (4f, 0f, 4f), (0f, 0f, 4f) }, normal: (0, 1, 0));
var loneBake = lone.Bake(new AoOptions { Rays = 32, MaxEdge = 0 });
Check("a lone quad stays open", loneBake.Ao.All(v => v > 0.99f), $"min={loneBake.Ao.Min():0.00}");

// Determinism: the same input twice must give the same numbers.
var again = room.Bake(new AoOptions { Rays = 64, MaxDistance = 0.8f, MaxEdge = 0 });
Check("the bake is deterministic", again.Ao.SequenceEqual(roomBake.Ao));

// ---------------------------------------------------------------- builder integration
Console.WriteLine("glb");

var builder = new GltfBuilder();
var mat = builder.GetMaterial("Floor", 0.8f, 0.8f, 0.8f);
builder.AddTriangles(mat, new List<float>(floorPositions),
                     new List<float> { 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0 },
                     new List<int>(floorTriangles));

var unbaked = builder.Build();
Check("an unbaked scene has no COLOR_0", !System.Text.Encoding.ASCII.GetString(unbaked).Contains("COLOR_0"));

var report = builder.BakeVertexOcclusion();
Check("the bake reports vertices", report.Vertices > 0, $"{report.Vertices}");

var baked = builder.Build();
var json = System.Text.Encoding.ASCII.GetString(baked);
Check("a baked scene carries COLOR_0", json.Contains("COLOR_0"));
Check("COLOR_0 is normalised bytes",
    json.Contains("\"normalized\":true") && json.Contains("\"componentType\":5121"));

// A big flat floor must be subdivided, or its four corner values get smeared across the room.
var subdiv = new GltfBuilder();
var smat = subdiv.GetMaterial("Slab", 0.8f, 0.8f, 0.8f);
subdiv.AddTriangles(smat, new List<float> { 0, 0, 0, 8, 0, 0, 8, 0, 8, 0, 0, 8 },
                    new List<float> { 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0 },
                    new List<int> { 0, 1, 2, 0, 2, 3 });
var subdivReport = subdiv.BakeVertexOcclusion(new AoOptions { MaxEdge = 1.0f });
Check("a large slab is subdivided before the bake",
    subdivReport.Subdivided && subdivReport.TrianglesAfter > subdivReport.TrianglesBefore,
    $"{subdivReport.TrianglesBefore} → {subdivReport.TrianglesAfter}");

// The same room through the builder — soup assembly, subdivision, colours and encoding all in
// one path. This is what the exporter actually runs.
var built = new GltfBuilder();
var floorMat = built.GetMaterial("Floor", 0.8f, 0.8f, 0.8f);
var wallMat = built.GetMaterial("Wall", 0.8f, 0.8f, 0.8f);
built.AddTriangles(floorMat, room.Positions, room.Normals, room.Indices);
var wallSoup = new SoupBuilder();
{
    const float size = 6f, x1 = 0f, x0 = -0.2f;
    wallSoup.Quad(new[] { (x1, 0f, 0f), (x1, 0f, size), (x1, 2f, size), (x1, 2f, 0f) }, normal: (1, 0, 0));
}
built.AddTriangles(wallMat, wallSoup.Positions, wallSoup.Normals, wallSoup.Indices);

var builtReport = built.BakeVertexOcclusion(new AoOptions { Rays = 32, MaxDistance = 0.8f, MaxEdge = 0.5f });
var builtMin = MinColorByte(built.Build());
Console.WriteLine($"       builder: {builtReport.TrianglesBefore} → {builtReport.TrianglesAfter} tris, darkest color {builtMin}");
Check("the builder bakes real occlusion into COLOR_0", builtMin < 235, $"darkest={builtMin}");

Console.WriteLine();
Console.WriteLine(fail == 0 ? $"all {pass} checks passed" : $"{pass} passed, {fail} FAILED");
return fail == 0 ? 0 : 1;

// ---------------------------------------------------------------------------
/// <summary>Smallest red channel in the first COLOR_0 accessor of a GLB, 0..255.</summary>
static int MinColorByte(byte[] glb)
{
    var jsonLength = BitConverter.ToInt32(glb, 12);
    var json = System.Text.Json.JsonDocument.Parse(
        glb.AsSpan(20, jsonLength).ToArray().AsSpan(0, TrimLength(glb.AsSpan(20, jsonLength).ToArray())).ToArray());
    var binStart = 20 + jsonLength + 8;

    foreach (var mesh in json.RootElement.GetProperty("meshes").EnumerateArray())
    {
        var primitive = mesh.GetProperty("primitives")[0];
        if (!primitive.GetProperty("attributes").TryGetProperty("COLOR_0", out var colorAccessor)) continue;

        var accessor = json.RootElement.GetProperty("accessors")[colorAccessor.GetInt32()];
        var view = json.RootElement.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
        var offset = binStart + view.GetProperty("byteOffset").GetInt32();
        var count = accessor.GetProperty("count").GetInt32();

        var min = 255;
        for (var i = 0; i < count; i++)
            min = Math.Min(min, glb[offset + i * 4]);
        return min;
    }
    return 255;
}

static int TrimLength(byte[] json)
{
    var end = json.Length;
    while (end > 0 && json[end - 1] is 0x20 or 0) end--;
    return end;
}

// ---------------------------------------------------------------------------
/// <summary>Builds a triangle soup the way the exporter does, for the occlusion tests.</summary>
internal sealed class SoupBuilder
{
    public readonly List<float> Positions = new();
    public readonly List<float> Normals = new();
    public readonly List<int> Indices = new();

    public void Quad((float X, float Y, float Z)[] q, (float X, float Y, float Z) normal)
    {
        var b = Positions.Count / 3;
        foreach (var p in q)
        {
            Positions.Add(p.X); Positions.Add(p.Y); Positions.Add(p.Z);
            Normals.Add(normal.X); Normals.Add(normal.Y); Normals.Add(normal.Z);
        }
        Indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
    }

    public void Box((float X, float Y, float Z) min, (float X, float Y, float Z) max)
    {
        void Face((float X, float Y, float Z) a, (float X, float Y, float Z) b,
                  (float X, float Y, float Z) c, (float X, float Y, float Z) d,
                  (float X, float Y, float Z) n) => Quad(new[] { a, b, c, d }, n);

        Face((max.X, min.Y, max.Z), (max.X, min.Y, min.Z), (max.X, max.Y, min.Z), (max.X, max.Y, max.Z), (1, 0, 0));
        Face((min.X, min.Y, min.Z), (min.X, min.Y, max.Z), (min.X, max.Y, max.Z), (min.X, max.Y, min.Z), (-1, 0, 0));
        Face((min.X, max.Y, max.Z), (max.X, max.Y, max.Z), (max.X, max.Y, min.Z), (min.X, max.Y, min.Z), (0, 1, 0));
        Face((min.X, min.Y, min.Z), (max.X, min.Y, min.Z), (max.X, min.Y, max.Z), (min.X, min.Y, max.Z), (0, -1, 0));
        Face((min.X, min.Y, max.Z), (max.X, min.Y, max.Z), (max.X, max.Y, max.Z), (min.X, max.Y, max.Z), (0, 0, 1));
        Face((max.X, min.Y, min.Z), (min.X, min.Y, min.Z), (min.X, max.Y, min.Z), (max.X, max.Y, min.Z), (0, 0, -1));
    }

    public (float[] Ao, int VertexCount) Bake(AoOptions options)
    {
        var ao = new float[Positions.Count / 3];
        VertexOcclusion.Compute(Positions.ToArray(), Normals.ToArray(), Indices.ToArray(), options, ao);
        return (ao, ao.Length);
    }
}
