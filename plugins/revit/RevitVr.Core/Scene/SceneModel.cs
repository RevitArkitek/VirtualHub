using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitVr.Core.Scene;

/// <summary>
/// Collision proxy geometry, derived analytically from Revit elements rather than from the
/// render mesh. A wall becomes a handful of 2D boxes; a door cuts a gap in one of them.
/// This is what keeps collision cheap enough to run at 72 Hz on a standalone headset.
/// All values are metres in glTF space (Y up, X right, -Z forward).
/// </summary>
public sealed class WallSegment
{
    public float X1 { get; set; }
    public float Z1 { get; set; }
    public float X2 { get; set; }
    public float Z2 { get; set; }
    public float Thickness { get; set; }
    public float Y0 { get; set; }
    public float Y1 { get; set; }
    /// <summary>False for openings (door reveals) kept only for debug rendering.</summary>
    public bool Solid { get; set; } = true;

    /// <summary>
    /// Revit ElementId this barrier came from, when the exporter knows it. Carried so a collider
    /// that misbehaves in the headset can be traced back to the element to fix, instead of
    /// guessing from coordinates.
    /// </summary>
    public long? SourceId { get; set; }
}

/// <summary>A walkable horizontal plane used for ground snapping (floor slabs, roofs, stair treads).</summary>
public sealed class WalkSurface
{
    public float MinX { get; set; }
    public float MinZ { get; set; }
    public float MaxX { get; set; }
    public float MaxZ { get; set; }
    public float Y { get; set; }

    /// <summary>
    /// Optional plan outline as a flat [x,z,x,z,...] polygon. When present it is the shape that
    /// counts and the rectangle above is only a cheap pre-filter. Stair treads need this: they
    /// are often not axis-aligned, and a bounding box would turn a diagonal flight into a slab
    /// you can walk around on. Floor slabs need it for the opposite reason — see <see cref="H"/>.
    /// </summary>
    public float[]? P { get; set; }

    /// <summary>
    /// Holes cut out of <see cref="P"/>, each a flat [x,z,x,z,...] polygon.
    ///
    /// A floor slab drawn around a stairwell is a donut, and its bounding box is not the slab: the
    /// box spans the opening, so the walkable world holds a phantom floor across the top of the
    /// stair and the ground snap never lets you down it. Carrying the face's inner loops as holes
    /// is what makes the stairwell a hole you can walk into rather than a floor you stand on.
    /// </summary>
    public float[][]? H { get; set; }

    /// <summary>
    /// True for a surface that came from a floor or roof element rather than from a stair tread.
    ///
    /// Railing barriers are placed from "the surface under the path", and that lookup deliberately
    /// keeps treating a slab the way it did before slabs had outlines: as its bounding rectangle,
    /// and never as an outline a climbing railing may trust. Those are the rules the barrier
    /// heights were tuned against — a slab is the wrong answer for a climbing railing whichever
    /// shape it has, because the slab that matches is the one a storey up. The walkable world
    /// itself uses the real shape (see <see cref="Contains"/>); this flag is not serialised.
    /// </summary>
    [JsonIgnore]
    public bool Slab { get; set; }

    /// <summary>
    /// True when (x,z) is on this surface: inside its rectangle, inside its outline if it has one,
    /// and outside every hole.
    /// </summary>
    public bool Contains(double x, double z)
    {
        if (x < MinX || x > MaxX || z < MinZ || z > MaxZ) return false;

        if (P is { Length: >= 6 } && !PlanGeometry.IsInside(P.AsSpan(), x, z)) return false;

        if (H is { Length: > 0 })
            foreach (var hole in H)
                if (hole is { Length: >= 6 } && PlanGeometry.IsInside(hole.AsSpan(), x, z)) return false;

        return true;
    }

    /// <summary>Walkable area in m², less any holes; used to pick the largest ground surface.</summary>
    public double Area
    {
        get
        {
            var area = (double)(MaxX - MinX) * (MaxZ - MinZ);
            if (P is { Length: >= 6 })
            {
                area = Math.Abs(PlanGeometry.SignedArea(ToPoints(P)));
                if (H is { Length: > 0 })
                    foreach (var hole in H)
                        if (hole is { Length: >= 6 }) area -= Math.Abs(PlanGeometry.SignedArea(ToPoints(hole)));
            }
            return area;
        }
    }

    private static List<PlanPoint> ToPoints(float[] flat)
    {
        var points = new List<PlanPoint>(flat.Length / 2);
        for (var i = 0; i + 1 < flat.Length; i += 2) points.Add(new PlanPoint(flat[i], flat[i + 1]));
        return points;
    }
}

public sealed class SceneManifest
{
    public string Name { get; set; } = "untitled";
    public string GeneratedUtc { get; set; } = DateTime.UtcNow.ToString("o");
    public int RevitVersion { get; set; }
    /// <summary>Player spawn, metres, glTF space.</summary>
    public float[] Spawn { get; set; } = { 0f, 0f, 0f };
    public float[] SpawnForward { get; set; } = { 0f, 0f, -1f };
    public float EyeHeight { get; set; } = 1.6f;
    public float ModelMinY { get; set; }
    public float ModelMaxY { get; set; }
    public int TriangleCount { get; set; }

    /// <summary>
    /// True when the geometry's vertex colours carry baked light rather than just occlusion. The
    /// viewer dims its ambient and hemisphere fill in that case, because the baked term now
    /// provides them and counting both would double the room's brightness.
    /// </summary>
    public bool BakedLight { get; set; }

    public List<WallSegment> Segments { get; set; } = new();
    public List<WalkSurface> Surfaces { get; set; } = new();

    /// <summary>
    /// Sloped ground (topography, pads) as flat triangles: nine floats per triangle, x,y,z per
    /// vertex. Surfaces above carry one height each, which cannot describe a hillside — here the
    /// height is interpolated across the triangle instead.
    /// </summary>
    public List<float[]>? Terrain { get; set; }
    public List<string> Warnings { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
