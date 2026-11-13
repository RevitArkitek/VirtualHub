using System.IO;

namespace RevitVr.Addin.Export;

/// <summary>
/// Finds the image file behind an appearance asset's bitmap path.
///
/// Revit stores that path in whatever form the material library was authored with: absolute for a
/// library asset, relative to the .rvt for anything a user linked, and — most often — a bare
/// filename such as "Finishes.Flooring.Carpet.4.png", which is only meaningful next to the
/// material library it came from. A resolver that trusts the string as a path finds almost
/// nothing, so this one tries the literal path, the document's folder, and then each known
/// library root before giving up and searching those roots for the filename.
/// </summary>
public static class TexturePathResolver
{
    /// <summary>Directories that hold Revit's stock texture and material libraries.</summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        var roots = new List<string>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var publicDocs = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        var userDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        void Add(string? p)
        {
            if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) roots.Add(p);
        }

        Add(Path.Combine(common, "Autodesk", "Shared", "Materials", "Textures"));
        Add(Path.Combine(common, "Autodesk", "Revit", "MaterialLibrary"));
        Add(Path.Combine(publicDocs, "Autodesk", "Rendering"));
        Add(Path.Combine(publicDocs, "Autodesk", "Materials"));
        Add(Path.Combine(userDocs, "Autodesk", "Materials"));
        Add(Path.Combine(programFiles, "Autodesk", "Shared", "Materials", "Textures"));
        Add(Path.Combine(programFilesX86, "Common Files", "Autodesk Shared", "Materials", "Textures"));
        Add(Path.Combine(programFiles, "Common Files", "Autodesk Shared", "Materials", "Textures"));
        return roots;
    }

    /// <summary>
    /// Resolves a bitmap path against the document folder and a set of library roots.
    /// Returns null when the file cannot be found anywhere.
    /// </summary>
    public static string? Resolve(string? stored, string? documentDir, IReadOnlyList<string>? roots = null)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;

        var raw = Expand(stored);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (File.Exists(raw)) return raw;

        // Relative to the project: Revit writes these when a user links an image stored beside
        // the .rvt.
        if (!string.IsNullOrWhiteSpace(documentDir))
        {
            var beside = Path.Combine(documentDir, raw.TrimStart('\\', '/'));
            if (File.Exists(beside)) return beside;

            var combined = SafeCombine(documentDir, raw);
            if (combined is not null && File.Exists(combined)) return combined;
        }

        var library = roots ?? DefaultRoots();

        // A library path like "..\..\..\Program Files\Autodesk\..." resolves against the library
        // root, not the file system root it pretends to climb to.
        var stripped = raw;
        while (stripped.StartsWith("..", StringComparison.Ordinal))
        {
            var cut = stripped.IndexOfAny(new[] { '\\', '/' });
            if (cut < 0) break;
            stripped = stripped[(cut + 1)..];
        }

        foreach (var root in library)
        {
            foreach (var candidate in new[] { SafeCombine(root, raw), SafeCombine(root, stripped) })
            {
                if (candidate is not null && File.Exists(candidate)) return candidate;
            }
        }

        // Last resort: the filename alone, somewhere in the libraries.
        var name = Path.GetFileName(raw.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (LibraryIndex(library).TryGetValue(name, out var found)) return found;

        return null;
    }

    private static string Expand(string stored)
    {
        var s = stored.Trim().Trim('"');
        if (s.Length == 0) return s;

        try { s = Environment.ExpandEnvironmentVariables(s); } catch { /* literal % in a filename */ }

        // Revit occasionally stores forward slashes, and sometimes doubled separators.
        s = s.Replace('/', '\\');
        while (s.Contains("\\\\")) s = s.Replace("\\\\", "\\");
        return s;
    }

    private static string? SafeCombine(string a, string b)
    {
        try { return Path.Combine(a, b); } catch { return null; }
    }

    private static Dictionary<string, string>? _index;
    private static IReadOnlyList<string>? _indexRoots;

    /// <summary>Filename → full path, built once over the library roots (bounded).</summary>
    private static Dictionary<string, string> LibraryIndex(IReadOnlyList<string> roots)
    {
        if (_index is not null && ReferenceEquals(_indexRoots, roots)) return _index;
        _index = BuildIndex(roots);
        _indexRoots = roots;
        return _index;
    }

    private static Dictionary<string, string> BuildIndex(IReadOnlyList<string> roots)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var budget = 60_000;

        foreach (var root in roots)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                {
                    if (--budget < 0) return map;
                    var ext = Path.GetExtension(file);
                    if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
                    {
                        map.TryAdd(Path.GetFileName(file), file);
                    }
                }
            }
            catch
            {
                // unreadable/inaccessible root: skip it, the others may still have the file
            }
        }

        return map;
    }
}
