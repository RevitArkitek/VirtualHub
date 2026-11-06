// Generates a demo scene in the exact format the Revit exporter writes: two rooms joined by a
// doorway, plus an exterior door. Used to test the headset and the viewer without opening Revit.
//
// Wall geometry is centred on its collision line so what you see is exactly what you bump into.
using RevitVr.Core.Gltf;
using RevitVr.Core.Scene;

var outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "export");
Directory.CreateDirectory(outDir);

var g = new GltfBuilder();

// Textures the way the Revit exporter supplies them: raw image bytes plus the real-world size of
// one repeat. Procedural here so the whole path — UVs, embedded images, viewer — is testable
// without a Revit session.
var plasterTex = g.AddTexture("plaster", Textures.Plaster(), "image/png");
var oakTex     = g.AddTexture("oak", Textures.OakPlanks(), "image/png");
var oakNormal  = g.AddTexture("oak-normal", Textures.OakPlankNormal(), "image/png");
var steelTex   = g.AddTexture("brushed-steel", Textures.BrushedSteel(), "image/png");
var rugTex     = g.AddTexture("rug", Textures.Rug(), "image/png");

var wallMat  = g.GetMaterial("Plaster", 1f, 1f, 1f, roughness: 0.92f,
                             baseTexture: plasterTex, tileMetresX: 1.2f, tileMetresY: 1.2f);
var floorMat = g.GetMaterial("Oak Floor", 1f, 1f, 1f, roughness: 0.55f,
                             baseTexture: oakTex, normalTexture: oakNormal, normalScale: 0.55f,
                             tileMetresX: 0.6f, tileMetresY: 2.4f);   // one texture = 5 boards by 4
// The same board image as the floor: proves identical images are stored once, not twice.
var doorMat  = g.GetMaterial("Door", 1f, 1f, 1f, roughness: 0.45f,
                             baseTexture: oakTex, tileMetresX: 1.0f, tileMetresY: 1.0f);
var steelMat = g.GetMaterial("Steel", 1f, 1f, 1f, metallic: 0.55f, roughness: 0.35f,
                             baseTexture: steelTex, tileMetresX: 0.8f, tileMetresY: 0.8f);
var rugMat   = g.GetMaterial("Rug", 1f, 1f, 1f, roughness: 1f,
                             baseTexture: rugTex, tileMetresX: 2.0f, tileMetresY: 2.0f);

const float W = 6f;        // partition line
const float L = 10f;       // building length in x
const float D = 4f;        // building depth in z
const float H = 2.8f;      // wall height
const float T = 0.2f;      // wall thickness
const float DoorY = 2.1f;  // head of the door openings

// exterior door in the south wall
const float Sx0 = 2.0f, Sx1 = 2.9f;
// interior door in the partition
const float Pz0 = 1.4f, Pz1 = 2.3f;

// --- floor slab -------------------------------------------------------------
AxisBox(g, floorMat, -0.1f, -0.1f, -0.1f, L + 0.1f, 0f, D + 0.1f);
AxisBox(g, rugMat, 0.6f, 0.001f, 0.7f, 3.2f, 0.02f, 3.0f);

// --- walls ------------------------------------------------------------------
Wall(g, wallMat, -0.1f, 0f, L + 0.1f, 0f);                     // north
Wall(g, wallMat, -0.1f, D, Sx0, D);                            // south, west of the door
Wall(g, wallMat, Sx1, D, L + 0.1f, D);                         // south, east of the door
AxisBox(g, wallMat, Sx0, DoorY, D - T / 2, Sx1, H, D + T / 2); // lintel over the exterior door

Wall(g, wallMat, 0f, -0.1f, 0f, D + 0.1f);                     // west
Wall(g, wallMat, L, -0.1f, L, D + 0.1f);                       // east

Wall(g, wallMat, W, -0.1f, W, Pz0);                            // partition, north of the door
Wall(g, wallMat, W, Pz1, W, D + 0.1f);                         // partition, south of the door
AxisBox(g, wallMat, W - T / 2, DoorY, Pz0, W + T / 2, H, Pz1); // lintel over the interior door

// --- door leaves, propped open ----------------------------------------------
Leaf(g, doorMat, steelMat, Sx0 + 0.02f, D - T / 2, Sx1 - Sx0 - 0.06f, 75f, hingeAlongX: true);
Leaf(g, doorMat, steelMat, W + T / 2, Pz0 + 0.02f, Pz1 - Pz0 - 0.06f, -80f, hingeAlongX: false);

// --- some furniture so material merging is visible in both rooms -------------
AxisBox(g, steelMat, 7.2f, 0f, 1.0f, 8.6f, 0.75f, 2.4f);   // table in room 2
AxisBox(g, doorMat, 7.4f, 0.75f, 1.2f, 7.6f, 1.55f, 2.2f);
AxisBox(g, steelMat, 0.6f, 0f, 3.4f, 1.4f, 0.45f, 3.9f);    // bench in room 1

// --- a straight flight of stairs, so climbing can be tested ------------------
const int Steps = 10;
const float Riser = 0.17f, Going = 0.25f, StairZ0 = 2.8f, StairZ1 = 3.9f, StairX0 = 6.6f;
const float LandingX = StairX0 + Going * Steps;

for (var i = 0; i < Steps; i++)
{
    var x0 = StairX0 + Going * i;
    AxisBox(g, wallMat, x0, 0f, StairZ0, x0 + Going, Riser * (i + 1), StairZ1);
}
AxisBox(g, steelMat, LandingX, 0f, StairZ0, 9.9f, Riser * Steps, StairZ1);

// --- a guard rail along the open edge of the landing -------------------------
// 1 m of barrier at landing height. In a Revit export this is a railing collider from
// Railing.GetPath(); here it stands in for one so the viewer and the collision test can prove
// that a mezzanine edge stops you.
const float RailTop = Riser * Steps + 1.0f;
AxisBox(g, steelMat, LandingX, RailTop - 0.08f, StairZ0 - 0.03f, 9.9f, RailTop, StairZ0 + 0.03f);
AxisBox(g, steelMat, LandingX, Riser * Steps + 0.5f, StairZ0 - 0.02f, 9.9f, Riser * Steps + 0.55f, StairZ0 + 0.02f);
for (var i = 0; i <= 2; i++)
{
    var px = LandingX + (9.9f - LandingX) * i / 2f;
    AxisBox(g, steelMat, px - 0.03f, Riser * Steps, StairZ0 - 0.03f, px + 0.03f, RailTop, StairZ0 + 0.03f);
}

// --- a sloped lawn outside the south door, so terrain walking can be tested --
var grassTex = g.AddTexture("grass", Textures.Grass(), "image/png");
var grassMat = g.GetMaterial("Grass", 1f, 1f, 1f, roughness: 1f,
                             baseTexture: grassTex, tileMetresX: 2.5f, tileMetresY: 2.5f);
const float LawnX0 = -2f, LawnX1 = 12f, LawnZ0 = 4.1f, LawnZ1 = 12f, LawnRise = 2.0f;
var lawnTris = new List<float[]>
{
    new[] { LawnX0, 0f, LawnZ0, LawnX1, LawnRise, LawnZ1, LawnX1, 0f, LawnZ0 },
    new[] { LawnX0, 0f, LawnZ0, LawnX0, LawnRise, LawnZ1, LawnX1, LawnRise, LawnZ1 },
};
foreach (var t in lawnTris) AddTriangle(g, grassMat, t);

var ao = args.Contains("--no-ao")
    ? new AoReport(0, 0, g.TriangleCount, g.TriangleCount, 0, false, false)
    : g.BakeVertexOcclusion();
var glb = g.Build();
File.WriteAllBytes(Path.Combine(outDir, "scene.glb"), glb);

// --- collision proxy --------------------------------------------------------
var m = new SceneManifest
{
    Name = "demo-two-rooms",
    RevitVersion = 2025,
    Spawn = new[] { 4.4f, 1.6f, 1.85f },
    SpawnForward = new[] { 1f, 0f, 0f },   // looking east, at the doorway into room 2
    EyeHeight = 1.6f,
    ModelMinY = 0f,
    ModelMaxY = H,
    TriangleCount = g.TriangleCount
};

void Seg(float x1, float z1, float x2, float z2, float y0 = 0f, float y1 = H)
    => m.Segments.Add(new WallSegment { X1 = x1, Z1 = z1, X2 = x2, Z2 = z2, Thickness = T, Y0 = y0, Y1 = y1 });

Seg(0, 0, L, 0);                       // north
Seg(0, D, Sx0, D);                     // south, west of the door
Seg(Sx1, D, L, D);                     // south, east of the door
Seg(Sx0, D, Sx1, D, DoorY, H);         // exterior lintel
Seg(0, 0, 0, D);                       // west
Seg(L, 0, L, D);                       // east
Seg(W, 0, W, Pz0);                     // partition, north of the door
Seg(W, Pz1, W, D);                     // partition, south of the door
Seg(W, Pz0, W, Pz1, DoorY, H);         // interior lintel

m.Surfaces.Add(new WalkSurface { MinX = -0.1f, MinZ = -0.1f, MaxX = L + 0.1f, MaxZ = D + 0.1f, Y = 0f });

// stair treads, as polygons at their own height — exactly what the Revit exporter emits
for (var i = 0; i < Steps; i++)
{
    var x0 = StairX0 + Going * i;
    var x1 = x0 + Going;
    m.Surfaces.Add(new WalkSurface
    {
        MinX = x0, MaxX = x1, MinZ = StairZ0, MaxZ = StairZ1, Y = Riser * (i + 1),
        P = new[] { x0, StairZ0, x1, StairZ0, x1, StairZ1, x0, StairZ1 },
    });
}
m.Surfaces.Add(new WalkSurface
{
    MinX = LandingX, MaxX = 9.9f, MinZ = StairZ0, MaxZ = StairZ1, Y = Riser * Steps,
    P = new[] { LandingX, StairZ0, 9.9f, StairZ0, 9.9f, StairZ1, LandingX, StairZ1 },
});

m.Terrain = lawnTris;

// the table as an obstacle, standing in for the casework/furniture colliders
m.Segments.Add(new WallSegment
{
    X1 = 7.2f, Z1 = 1.7f, X2 = 8.6f, Z2 = 1.7f, Thickness = 1.4f, Y0 = 0f, Y1 = 0.75f,
});

// the landing guard rail, emitted the way a Revit railing is: a thin box whose height range is
// the railing itself, so it stops you at landing level and not on the floor beneath it
m.Segments.Add(new WallSegment
{
    X1 = LandingX, Z1 = StairZ0, X2 = 9.9f, Z2 = StairZ0,
    Thickness = 0.10f, Y0 = Riser * Steps, Y1 = RailTop,
});

File.WriteAllText(Path.Combine(outDir, "colliders.json"), m.ToJson());

Console.WriteLine($"wrote {outDir}");
Console.WriteLine($"{glb.Length,18:N0} bytes  {g.TriangleCount,7:N0} tris  {g.MaterialCount} materials  " +
                  $"{g.TextureCount} textures ({g.TextureBytes / 1024.0:0} KB)");
Console.WriteLine($"{ao.Vertices,18:N0} verts   ao {ao.Rays} rays  {ao.TrianglesBefore:N0} → {ao.TrianglesAfter:N0} tris  " +
                  $"{ao.Seconds:0.00}s");
Console.WriteLine($"  colliders.json {m.Segments.Count,10} segments, {m.Surfaces.Count} surfaces");

// ---------------------------------------------------------------------------
// A straight wall run centred on the segment line, thickened across it.
static void Wall(GltfBuilder g, int mat, float x0, float z0, float x1, float z1)
{
    if (MathF.Abs(x1 - x0) < 1e-4)
        AxisBox(g, mat, x0 - T / 2, 0f, MathF.Min(z0, z1), x0 + T / 2, H, MathF.Max(z0, z1));
    else
        AxisBox(g, mat, MathF.Min(x0, x1), 0f, z0 - T / 2, MathF.Max(x0, x1), H, z0 + T / 2);
}

// A door leaf hinged at (hx,hz), rotated `deg` about Y from its closed position.
static void Leaf(GltfBuilder g, int doorMat, int ironMat, float hx, float hz, float width, float deg, bool hingeAlongX)
{
    var ang = deg * MathF.PI / 180f;
    var p = new List<float>();
    var n = new List<float>();
    var idx = new List<int>();

    if (hingeAlongX)
        AppendBox(p, n, idx, 0f, 0f, -0.025f, width, 2.05f, 0.025f);
    else
        AppendBox(p, n, idx, -0.025f, 0f, 0f, 0.025f, 2.05f, width);

    for (var i = 0; i < p.Count; i += 3)
    {
        var (x, z) = (p[i], p[i + 2]);
        p[i] = hx + x * MathF.Cos(ang) - z * MathF.Sin(ang);
        p[i + 2] = hz + x * MathF.Sin(ang) + z * MathF.Cos(ang);
    }
    for (var i = 0; i < n.Count; i += 3)
    {
        var (x, z) = (n[i], n[i + 2]);
        n[i] = x * MathF.Cos(ang) - z * MathF.Sin(ang);
        n[i + 2] = x * MathF.Sin(ang) + z * MathF.Cos(ang);
    }
    g.AddTriangles(doorMat, p, n, idx);

    // handle, on the free edge
    var hxp = hingeAlongX ? width * 0.8f : 0.06f;
    var hzp = hingeAlongX ? 0.06f : width * 0.8f;
    var wx = hx + hxp * MathF.Cos(ang) - hzp * MathF.Sin(ang);
    var wz = hz + hxp * MathF.Sin(ang) + hzp * MathF.Cos(ang);
    AxisBox(g, ironMat, wx - 0.04f, 0.98f, wz - 0.04f, wx + 0.04f, 1.06f, wz + 0.04f);
}

// A single triangle in glTF space, with a normal derived from its winding.
static void AddTriangle(GltfBuilder g, int mat, float[] t)
{
    var (ax, ay, az) = (t[0], t[1], t[2]);
    var (bx, by, bz) = (t[3], t[4], t[5]);
    var (cx, cy, cz) = (t[6], t[7], t[8]);

    var ux = bx - ax; var uy = by - ay; var uz = bz - az;
    var vx = cx - ax; var vy = cy - ay; var vz = cz - az;
    var nx = uy * vz - uz * vy;
    var ny = uz * vx - ux * vz;
    var nz = ux * vy - uy * vx;
    var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
    if (len > 1e-9) { nx /= len; ny /= len; nz /= len; } else { nx = 0; ny = 1; nz = 0; }

    var p = new List<float> { ax, ay, az, bx, by, bz, cx, cy, cz };
    var n = new List<float>();
    for (var k = 0; k < 3; k++) n.AddRange(new[] { nx, ny, nz });
    g.AddTriangles(mat, p, n, new List<int> { 0, 1, 2 });
}

static void AxisBox(GltfBuilder g, int mat, float x0, float y0, float z0, float x1, float y1, float z1)
{
    var p = new List<float>();
    var n = new List<float>();
    var i = new List<int>();
    AppendBox(p, n, i, x0, y0, z0, x1, y1, z1);
    g.AddTriangles(mat, p, n, i);
}

static void AppendBox(List<float> p, List<float> n, List<int> idx,
                      float x0, float y0, float z0, float x1, float y1, float z1)
{
    // 6 quads, each with its own 4 verts so normals stay flat
    void Face(float ax, float ay, float az, float bx, float by, float bz,
              float cx, float cy, float cz, float dx, float dy, float dz,
              float nx, float ny, float nz)
    {
        var b = p.Count / 3;
        p.AddRange(new[] { ax, ay, az, bx, by, bz, cx, cy, cz, dx, dy, dz });
        for (var k = 0; k < 4; k++) n.AddRange(new[] { nx, ny, nz });
        idx.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
    }
    Face(x1, y0, z1, x1, y0, z0, x1, y1, z0, x1, y1, z1, 1, 0, 0);
    Face(x0, y0, z0, x0, y0, z1, x0, y1, z1, x0, y1, z0, -1, 0, 0);
    Face(x0, y1, z1, x1, y1, z1, x1, y1, z0, x0, y1, z0, 0, 1, 0);
    Face(x0, y0, z0, x1, y0, z0, x1, y0, z1, x0, y0, z1, 0, -1, 0);
    Face(x0, y0, z1, x1, y0, z1, x1, y1, z1, x0, y1, z1, 0, 0, 1);
    Face(x1, y0, z0, x0, y0, z0, x0, y1, z0, x1, y1, z0, 0, 0, -1);
}
