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
    /// you can walk around on.
    /// </summary>
    public float[]? P { get; set; }
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
