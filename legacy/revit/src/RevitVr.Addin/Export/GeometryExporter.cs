using Autodesk.Revit.DB;
using RevitVr.Core.Gltf;

namespace RevitVr.Addin.Export;

/// <summary>
/// Harvests render geometry by walking the model directly rather than through CustomExporter.
///
/// This is deliberate. CustomExporter hands out family-instance geometry in *symbol* space and
/// exposes no transform on InstanceNode, LinkNode or any other render node — so every door,
/// window, mullion and chair arrives at the origin, stacked on top of itself. Walking the model
/// gives us GeometryInstance.Transform, which is the only way to place instances correctly.
///
/// It also lets us force DetailLevel.Fine, so curtain wall panels and mullions are generated
/// regardless of how the source view happens to be set.
/// </summary>
public sealed class GeometryExporter
{
    /// <summary>Revit internal units are feet; the viewer works in metres.</summary>
    public const double FeetToMetres = 0.3048;

    /// <summary>Tessellation density for curved faces, 0..1. Flat faces always give 2 triangles.</summary>
    private const double TessellationLod = 0.5;

    private const int MaxDepth = 10;

    private readonly Document _doc;
    private readonly GltfBuilder _builder;
    private readonly Dictionary<long, int> _materialCache = new();
    private readonly int _fallbackMaterial;

    public int Elements { get; private set; }
    public int Instances { get; private set; }
    public int Solids { get; private set; }
    public int Meshes { get; private set; }
    public int LightSourcesSkipped { get; private set; }
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Revit light fixtures carry a photometric web — the mesh that shows how light spreads.
    /// It is a modelling aid, not architecture, and has no place in a walkthrough. There is no
    /// flag that marks it, so it is identified by the subcategory or material it is placed on.
    /// </summary>
    private static readonly string[] LightSourceHints =
    {
        "light source", "lightsource", "photometric", "light distribution",
        "lighting web", "photometric web", "light web",
    };

    public GeometryExporter(Document doc, GltfBuilder builder)
    {
        _doc = doc;
        _builder = builder;
        _fallbackMaterial = builder.GetMaterial("Default", 0.72f, 0.72f, 0.70f, roughness: 0.9f);
    }

    public void Export(View view)
    {
        var options = new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = false,
            DetailLevel = ViewDetailLevel.Fine,
        };

        var collector = new FilteredElementCollector(_doc, view.Id).WhereElementIsNotElementType();

        foreach (var element in collector)
        {
            GeometryElement? geometry;
            try
            {
                geometry = element.get_Geometry(options);
            }
            catch
            {
                // a handful of elements (imports, some MEP) throw depending on the view
                continue;
            }
            if (geometry is null) continue;

            Elements++;
            Walk(geometry, Transform.Identity, element, 0);
        }

        if (Instances == 0)
            Warnings.Add("No family instances were found — doors, windows and furniture will be missing.");
    }

    private void Walk(GeometryElement geometry, Transform transform, Element element, int depth)
    {
        if (depth > MaxDepth) return;

        foreach (var obj in geometry)
        {
            switch (obj)
            {
                case GeometryInstance instance:
                {
                    Instances++;
                    GeometryElement? symbol;
                    try { symbol = instance.GetSymbolGeometry(); }
                    catch { continue; }
                    if (symbol is null) continue;

                    // symbol space -> the space the instance sits in
                    Walk(symbol, transform.Multiply(instance.Transform), element, depth + 1);
                    break;
                }

                case Solid solid when solid.Faces.Size > 0:
                {
                    if (IsLightSource(solid)) { LightSourcesSkipped++; break; }
                    Solids++;
                    foreach (Face face in solid.Faces)
                        EmitFace(face, transform, element);
                    break;
                }

                case Mesh mesh when mesh.NumTriangles > 0:
                {
                    if (IsLightSource(mesh) || IsLightSourceMaterial(mesh.MaterialElementId))
                    {
                        LightSourcesSkipped++;
                        break;
                    }
                    Meshes++;
                    EmitMesh(mesh, transform, MaterialFor(mesh.MaterialElementId, element));
                    break;
                }
            }
        }
    }

    private void EmitFace(Face face, Transform transform, Element element)
    {
        Mesh mesh;
        try { mesh = face.Triangulate(TessellationLod); }
        catch { return; }
        if (mesh is null || mesh.NumTriangles == 0) return;

        EmitMesh(mesh, transform, MaterialFor(face.MaterialElementId, element));
    }

    private void EmitMesh(Mesh mesh, Transform transform, int material)
    {
        var vertices = mesh.Vertices;
        var count = vertices.Count;
        if (count == 0 || mesh.NumTriangles == 0) return;

        var positions = new float[count * 3];
        for (var i = 0; i < count; i++)
        {
            var p = transform.OfPoint(vertices[i]);
            positions[i * 3] = (float)(p.X * FeetToMetres);
            positions[i * 3 + 1] = (float)(p.Z * FeetToMetres);
            positions[i * 3 + 2] = (float)(-p.Y * FeetToMetres);
        }

        var hasNormals = mesh.NumberOfNormals == count;
        var triangles = mesh.NumTriangles;

        if (hasNormals)
        {
            var normals = new float[count * 3];
            for (var i = 0; i < count; i++)
            {
                var n = transform.OfVector(mesh.GetNormal(i));
                var len = n.GetLength();
                if (len > 1e-9) { n = n / len; } else { n = XYZ.BasisZ; }
                normals[i * 3] = (float)n.X;
                normals[i * 3 + 1] = (float)n.Z;
                normals[i * 3 + 2] = (float)(-n.Y);
            }

            var indices = new int[triangles * 3];
            for (var t = 0; t < triangles; t++)
            {
                var tri = mesh.get_Triangle(t);
                indices[t * 3] = (int)tri.get_Index(0);
                indices[t * 3 + 1] = (int)tri.get_Index(1);
                indices[t * 3 + 2] = (int)tri.get_Index(2);
            }

            _builder.AddTriangles(material, positions, normals, indices);
            return;
        }

        // No usable normals: emit unwelded triangles with a computed flat normal each.
        var flatPositions = new List<float>(triangles * 9);
        var flatNormals = new List<float>(triangles * 9);
        var flatIndices = new List<int>(triangles * 3);

        for (var t = 0; t < triangles; t++)
        {
            var tri = mesh.get_Triangle(t);
            var a = transform.OfPoint(tri.get_Vertex(0));
            var b = transform.OfPoint(tri.get_Vertex(1));
            var c = transform.OfPoint(tri.get_Vertex(2));

            var n = (b - a).CrossProduct(c - a);
            if (n.GetLength() > 1e-9) n = n.Normalize(); else n = XYZ.BasisZ;

            foreach (var p in new[] { a, b, c })
            {
                flatPositions.Add((float)(p.X * FeetToMetres));
                flatPositions.Add((float)(p.Z * FeetToMetres));
                flatPositions.Add((float)(-p.Y * FeetToMetres));
                flatNormals.Add((float)n.X);
                flatNormals.Add((float)n.Z);
                flatNormals.Add((float)(-n.Y));
                flatIndices.Add(flatIndices.Count);
            }
        }

        _builder.AddTriangles(material, flatPositions, flatNormals, flatIndices);
    }

    /// <summary>
    /// True when this geometry is a light fixture's photometric web rather than building fabric.
    /// </summary>
    private bool IsLightSource(GeometryObject geometry)
    {
        var styleId = geometry.GraphicsStyleId;
        if (styleId == ElementId.InvalidElementId) return false;
        if (_doc.GetElement(styleId) is not GraphicsStyle style) return false;

        return Matches(style.Name) || Matches(style.GraphicsStyleCategory?.Name);
    }

    private bool IsLightSourceMaterial(ElementId materialId)
    {
        if (materialId == ElementId.InvalidElementId) return false;
        return _doc.GetElement(materialId) is Material material && Matches(material.Name);
    }

    private static bool Matches(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && LightSourceHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase));

    /// <summary>Face material, then the element's, then its category's, then a neutral grey.</summary>
    private int MaterialFor(ElementId materialId, Element element)
    {
        if (materialId != ElementId.InvalidElementId && _doc.GetElement(materialId) is Material direct)
            return Register(direct);

        var categoryMaterial = element.Category?.Material;
        if (categoryMaterial is not null) return Register(categoryMaterial);

        return _fallbackMaterial;
    }

    private int Register(Material material)
    {
        var key = material.Id.Value;
        if (_materialCache.TryGetValue(key, out var cached)) return cached;

        float r = 0.72f, g = 0.72f, b = 0.70f;
        var color = material.Color;
        if (color is { IsValid: true })
        {
            r = color.Red / 255f;
            g = color.Green / 255f;
            b = color.Blue / 255f;
        }

        var transparency = Math.Clamp(material.Transparency, 0, 100) / 100.0;
        var alpha = (float)(1.0 - transparency);
        var smoothness = Math.Clamp(material.Smoothness, 0, 100) / 100f;
        var roughness = Math.Clamp(1f - smoothness, 0.06f, 1f);

        var name = string.IsNullOrWhiteSpace(material.Name) ? "Material" : material.Name;
        var index = _builder.GetMaterial(name, r, g, b, alpha, 0f, roughness);
        _materialCache[key] = index;
        return index;
    }
}
