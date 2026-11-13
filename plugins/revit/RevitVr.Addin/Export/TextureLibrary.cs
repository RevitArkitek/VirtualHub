using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using RevitVr.Core.Gltf;

namespace RevitVr.Addin.Export;

/// <summary>
/// What a material's appearance asset contributed: an image (or not), and the values that go
/// with it — the diffuse colour for un-textured materials, and whether it is metal.
/// </summary>
public sealed record MaterialLook(
    int? BaseColor, int? Normal, float NormalScale,
    float TileX, float TileY, float OffsetU, float OffsetV,
    (float R, float G, float B)? Diffuse, bool IsMetal);

/// <summary>
/// One material's texture, as the export actually resolved it: the raw value Revit reported, the
/// unit it claimed, and the tile size in metres that went into the GLB. Written beside the scene so
/// "the textures are the wrong size" can be answered with numbers instead of a guess.
/// </summary>
public sealed record MaterialTextureRow(
    string Material, string? Image, double SizeX, double SizeY, string Unit,
    float TileX, float TileY, float RepeatU, float RepeatV, bool ScaleClamped, string? Note);

/// <summary>
/// Pulls the image maps out of Revit's appearance assets.
///
/// The data lives three levels down: Material → AppearanceAssetId → AppearanceAssetElement →
/// Asset, and from there the bitmap hangs off a *connected* asset of the diffuse property rather
/// than sitting on the asset itself. A material only sometimes has one of these — a "Generic"
/// material with a plain colour has no connected asset at all, and procedural assets (wood,
/// marble, metal) have parameters and no image whatsoever — so every step is optional and the
/// exporter falls back to the flat colour it already had.
/// </summary>
public sealed class TextureLibrary
{
    private const double FeetToMetres = GeometryExporter.FeetToMetres;

    private readonly Document _doc;
    private readonly GltfBuilder _builder;
    private readonly string? _documentDir;
    private readonly IReadOnlyList<string> _roots;

    private readonly Dictionary<long, MaterialLook> _cache = new();
    private readonly Dictionary<string, int?> _decoded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unresolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every material's texture, for the report written beside the scene.</summary>
    public List<MaterialTextureRow> Rows { get; } = new();

    public List<string> Warnings { get; } = new();
    public int MaterialsResolved { get; private set; }
    public int MaterialsMissingImage { get; private set; }

    public TextureLibrary(Document doc, GltfBuilder builder)
    {
        _doc = doc;
        _builder = builder;
        _roots = TexturePathResolver.DefaultRoots();

        try
        {
            var path = doc.PathName;
            _documentDir = string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
        }
        catch
        {
            _documentDir = null;
        }
    }

    /// <summary>
    /// What this material should look like. Never null: a material with no image still carries
    /// the diffuse colour and metal flag from its appearance asset, which is what the render
    /// appearance shows and therefore what the headset should show.
    /// </summary>
    public MaterialLook Look(Material material)
    {
        var key = material.Id.Value;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        MaterialLook look;
        try
        {
            look = Resolve(material);
        }
        catch (Exception ex)
        {
            // A texture must never be able to stop an export. A material whose asset this Revit
            // version represents differently simply comes out as flat colour.
            Warnings.Add($"Texture lookup failed for \"{material.Name}\": {ex.Message}");
            look = new MaterialLook(null, null, 1f, 1f, 1f, 0f, 0f, null, false);
        }

        _cache[key] = look;
        return look;
    }

    private MaterialLook Resolve(Material material)
    {
        var empty = new MaterialLook(null, null, 1f, 1f, 1f, 0f, 0f, null, false);

        Asset? asset = null;
        try
        {
            if (material.AppearanceAssetId == ElementId.InvalidElementId) return empty;
            if (_doc.GetElement(material.AppearanceAssetId) is not AppearanceAssetElement element) return empty;
            asset = element.GetRenderingAsset();
        }
        catch
        {
            return empty;
        }
        if (asset is null) return empty;

        // Diffuse first; if the asset has no such property, any connected bitmap will do.
        var diffuseProperty = Find(asset, Generic.GenericDiffuse);
        var diffuse = Connected(diffuseProperty);
        var baseAsset = HasBitmap(diffuse) ? diffuse : FindAnyBitmap(asset);
        var baseImage = Load(baseAsset, material.Name);

        var bump = Connected(Find(asset, Generic.GenericBumpMap));
        var bumpAsset = HasBitmap(bump) ? bump : null;
        var normalImage = Load(bumpAsset, material.Name + " (bump)");

        // The render appearance's own colour beats the shading colour: it is what Revit (and
        // Enscape) show, and the two can differ wildly on a material somebody has customised.
        var diffuseColor = diffuseProperty is AssetPropertyDoubleArray4d colour
            ? ToColor(colour)
            : null;

        var isMetal = Number(Find(asset, Generic.GenericIsMetal)) > 0.5;

        if (baseImage is null && normalImage is null)
            return empty with { Diffuse = diffuseColor, IsMetal = isMetal };

        var bumpAmount = Number(Find(asset, Generic.GenericBumpAmount));
        var normalScale = bumpAsset is null ? 1f : (float)Math.Clamp(bumpAmount > 0 ? bumpAmount : 1.0, 0.05, 1.0);

        // Revit's "Size X/Y" is the real-world size of one repeat — but the number's unit varies
        // by document, so it has to be converted by the unit the property reports, never assumed.
        // Reading a metric value as feet put textures three hundred times out of scale.
        var scaleX = ReadDistanceMetres(baseAsset, UnifiedBitmap.TextureRealWorldScaleX, out var rawX, out var unitX);
        var scaleY = ReadDistanceMetres(baseAsset, UnifiedBitmap.TextureRealWorldScaleY, out var rawY, out _);
        var offsetU = ReadDistanceMetres(baseAsset, UnifiedBitmap.TextureRealWorldOffsetX, out _, out _);
        var offsetV = ReadDistanceMetres(baseAsset, UnifiedBitmap.TextureRealWorldOffsetY, out _, out _);

        var repeatU = Number(Find(baseAsset, UnifiedBitmap.TextureURepeat));
        var repeatV = Number(Find(baseAsset, UnifiedBitmap.TextureVRepeat));
        if (repeatU > 1) scaleX /= repeatU;
        if (repeatV > 1) scaleY /= repeatV;

        // Second line of defence: a size that no texture has means the unit is still wrong.
        var tileX = TextureScale.Sanitise(scaleX, out var clampedX);
        var clampedY = clampedX;
        var tileY = scaleY > 1e-4 ? TextureScale.Sanitise(scaleY, out clampedY) : tileX;

        var note = clampedX || clampedY
            ? $"size {rawX:0.###} {unitX} is not a believable texture repeat; using {tileX:0.##} m"
            : null;

        if (note is not null && _reported.Add(material.Name ?? "material"))
            Warnings.Add($"Texture scale on \"{material.Name}\" was unusable: {note}");

        MaterialsResolved++;
        Rows.Add(new MaterialTextureRow(
            material.Name ?? "material",
            baseImage is null ? null : "embedded",
            rawX, rawY, unitX,
            tileX, (float)tileY, (float)repeatU, (float)repeatV, clampedX || clampedY, note));

        return new MaterialLook(
            baseImage, normalImage, normalScale,
            tileX, (float)tileY, (float)offsetU, (float)offsetV,
            diffuseColor, isMetal);
    }

    /// <summary>
    /// Reads a distance property as metres. Revit documents <c>AssetPropertyDistance</c> as
    /// "holds a double value; the unit varies", with <c>GetUnitTypeId()</c> naming the unit — so
    /// that is what is used, and a value with no unit falls back to Revit's internal feet.
    /// </summary>
    private static double ReadDistanceMetres(Asset? asset, string propertyName,
                                             out double raw, out string unitName)
    {
        raw = 0;
        unitName = "";

        var property = Find(asset, propertyName);
        if (property is null) return 0;

        try
        {
            switch (property)
            {
                case AssetPropertyDistance distance:
                {
                    raw = distance.Value;
                    var unit = distance.GetUnitTypeId();
                    if (unit is not null && !unit.Empty())
                    {
                        try { unitName = LabelUtils.GetLabelForUnit(unit); } catch { unitName = "?"; }
                        return UnitUtils.ConvertToInternalUnits(raw, unit) * FeetToMetres;
                    }
                    unitName = "ft";
                    return raw * FeetToMetres;
                }
                case AssetPropertyDouble d:
                    raw = d.Value; unitName = "ft";
                    return raw * FeetToMetres;
                case AssetPropertyFloat f:
                    raw = f.Value; unitName = "ft";
                    return raw * FeetToMetres;
                default:
                    return 0;
            }
        }
        catch
        {
            // A property that will not read is a property we do not have.
            return 0;
        }
    }

    /// <summary>An asset colour is 0..1 RGBA in Revit's asset space.</summary>
    private static (float R, float G, float B)? ToColor(AssetPropertyDoubleArray4d property)
    {
        try
        {
            var values = property.GetValueAsDoubles();
            if (values is null || values.Count < 3) return null;

            return ((float)Math.Clamp(values[0], 0, 1),
                    (float)Math.Clamp(values[1], 0, 1),
                    (float)Math.Clamp(values[2], 0, 1));
        }
        catch
        {
            return null;
        }
    }

    private int? Load(Asset? bitmapAsset, string materialName)
    {
        if (bitmapAsset is null) return null;

        var path = Find(bitmapAsset, UnifiedBitmap.UnifiedbitmapBitmap) as AssetPropertyString;
        var stored = path?.Value;
        if (string.IsNullOrWhiteSpace(stored)) return null;

        var resolved = TexturePathResolver.Resolve(stored, _documentDir, _roots);
        if (resolved is null)
        {
            // Counted by distinct file, not by material: one moved JPEG used by ten materials is
            // one problem, and the dialog says so.
            if (_unresolved.Add(stored))
            {
                MaterialsMissingImage++;
                Warnings.Add($"Texture not found for \"{materialName}\": {stored}");
            }
            return null;
        }

        // Many materials point at the same library image; decode each file once.
        if (_decoded.TryGetValue(resolved, out var known)) return known;

        var encoded = TextureEncoder.Prepare(resolved);
        if (encoded is null)
        {
            if (_unresolved.Add(stored))
            {
                MaterialsMissingImage++;
                Warnings.Add($"Texture could not be decoded for \"{materialName}\": {resolved}");
            }
            _decoded[resolved] = null;
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(resolved);
        var index = _builder.AddTexture(name, encoded.Data, encoded.Mime);
        _decoded[resolved] = index;
        return index;
    }

    private static AssetProperty? Find(Asset? asset, string propertyName)
    {
        if (asset is null) return null;
        try { return asset.FindByName(propertyName); } catch { return null; }
    }

    private static Asset? Connected(AssetProperty? property)
    {
        if (property is null) return null;
        try { return property.GetSingleConnectedAsset(); } catch { return null; }
    }

    private static bool HasBitmap(Asset? asset)
        => asset is not null && Find(asset, UnifiedBitmap.UnifiedbitmapBitmap) is AssetPropertyString;

    /// <summary>Breadth-first hunt for any connected asset that carries a bitmap.</summary>
    private static Asset? FindAnyBitmap(Asset asset)
    {
        try
        {
            for (var i = 0; i < asset.Size; i++)
            {
                var child = Connected(asset.Get(i));
                if (HasBitmap(child)) return child;
           }
        }
        catch
        {
            // an asset property that refuses to be read is simply not a bitmap we can use
        }
        return null;
    }

    private static double Number(AssetProperty? property) => property switch
    {
        null => 0,
        AssetPropertyDistance d => d.Value,
        AssetPropertyDouble d => d.Value,
        AssetPropertyFloat f => f.Value,
        AssetPropertyInteger i => i.Value,
        AssetPropertyBoolean b => b.Value ? 1 : 0,
        _ => 0,
    };
}
