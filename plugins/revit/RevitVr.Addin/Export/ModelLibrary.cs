using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RevitVr.Addin.Export;

/// <summary>
/// Publishes an export into the shared model library that the homepage lists.
///
/// The library is keyed by the document's file name, so exporting the same project again replaces
/// its entry instead of adding a second card. The export folder itself is still written first:
/// it is what the viewer commands and the legacy single-scene server read, and it is the copy the
/// export dialog describes.
/// </summary>
public static class ModelLibrary
{
    /// <summary>Where both plugins publish. Deliberately outside either plugin's own data folder.</summary>
    public static string Root
    {
        get
        {
            // VRHUB_LIBRARY redirects it, which is what the add-in check uses to publish into a
            // scratch folder instead of the user's real library.
            var env = Environment.GetEnvironmentVariable("VRHUB_LIBRARY");
            var dir = string.IsNullOrWhiteSpace(env)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VRHub", "library")
                : env;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Copies the scene files into the library and writes the metadata last, so an interrupted
    /// export is a folder the homepage skips rather than one it lists with no scene in it.
    /// Returns the slug — the URL segment that opens this model.
    /// </summary>
    public static string Publish(string exportDir, string docTitle, string? docPath, string? view,
                                 int triangles, int revitVersion)
    {
        var slug = Slugify(docTitle);
        var entry = Path.Combine(Root, slug);
        var sceneDir = Path.Combine(entry, "scene");
        Directory.CreateDirectory(sceneDir);

        // railings.json and textures.json are sidecars: present for Revit exports, absent for
        // IngeTrazo's. A file the previous export wrote and this one did not must not linger,
        // or the card would claim detail the scene no longer has.
        foreach (var file in new[] { "scene.glb", "colliders.json", "railings.json", "textures.json" })
        {
            var src = Path.Combine(exportDir, file);
            var dst = Path.Combine(sceneDir, file);
            if (File.Exists(src)) CopyAtomic(src, dst);
            else if (File.Exists(dst)) File.Delete(dst);
        }

        var meta = new
        {
            slug,
            name = docTitle,
            source = "revit",
            sourceLabel = revitVersion > 0 ? $"Revit {revitVersion}" : "Revit",
            fileName = Path.GetFileName(string.IsNullOrWhiteSpace(docPath) ? docTitle : docPath),
            filePath = docPath,
            view,
            exportedUtc = DateTime.UtcNow.ToString("o"),
            triangles,
            generator = "RevitVR",
        };

        var tmp = Path.Combine(entry, "meta.json.tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(meta, JsonOptions));
        File.Move(tmp, Path.Combine(entry, "meta.json"), true);
        return slug;
    }

    /// <summary>Replace in place: a viewer may be reading the old file right now.</summary>
    private static void CopyAtomic(string src, string dst)
    {
        var tmp = dst + ".tmp";
        File.Copy(src, tmp, true);
        File.Move(tmp, dst, true);
    }

    /// <summary>
    /// The file name, reduced to something safe in a URL and on disk: "196 SW-2025" becomes
    /// "196-sw-2025". Accents are folded rather than dropped, so "Casa Árboles" and a second
    /// project called "Casa Arboles" are the same model — which they are, to a reviewer.
    /// </summary>
    public static string Slugify(string? name)
    {
        var normalised = (name ?? "").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        var pendingDash = false;

        foreach (var ch in normalised)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;

            if (ch < 128 && char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                pendingDash = false;
            }
            else if (sb.Length > 0)
            {
                pendingDash = true;
            }

            if (pendingDash && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');
        return slug.Length == 0 ? "model" : slug;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
