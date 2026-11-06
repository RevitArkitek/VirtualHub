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

        // Revit's "Size X/Y" is the real-world size of one repeat, in feet.
        var scaleX = Number(Find(baseAsset, UnifiedBitmap.TextureRealWorldScaleX)) * FeetToMetres;
        var scaleY = Number(Find(baseAsset, UnifiedBitmap.TextureRealWorldScaleY)) * FeetToMetres;
        var offsetU = Number(Find(baseAsset, UnifiedBitmap.TextureRealWorldOffsetX)) * FeetToMetres;
        var offsetV = Number(Find(baseAsset, UnifiedBitmap.TextureRealWorldOffsetY)) * FeetToMetres;

        var repeatU = Number(Find(baseAsset, UnifiedBitmap.TextureURepeat));
        var repeatV = Number(Find(baseAsset, UnifiedBitmap.TextureVRepeat));
        if (repeatU > 1) scaleX /= repeatU;
        if (repeatV > 1) scaleY /= repeatV;

        // No usable scale in the asset: 1 m per repeat keeps the pattern architectural rather
        // than smeared to a single image across a whole wall.
        if (scaleX <= 1e-4) scaleX = 1.0;
        if (scaleY <= 1e-4) scaleY = scaleX;

        MaterialsResolved++;
        return new MaterialLook(
            baseImage, normalImage, normalScale,
            (float)scaleX, (float)scaleY, (float)offsetU, (float)offsetV,
            diffuseColor, isMetal);
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
