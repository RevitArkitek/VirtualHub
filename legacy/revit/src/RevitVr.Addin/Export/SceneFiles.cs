using System.Text.Json;

namespace RevitVr.Addin.Export;

/// <summary>
/// The files a viewer needs, and what to say about them. Deliberately Revit-free: three ribbon
/// commands share it, and addincheck calls <see cref="Describe(string, string?)"/> for real
/// against a manifest on disk.
/// </summary>
public static class SceneFiles
{
    public static string Dir => Paths.ExportDir;
    public static string ScenePath => Path.Combine(Dir, "scene.glb");
    public static string ColliderPath => Path.Combine(Dir, "colliders.json");

    /// <summary>
    /// Both files, because the viewer fetches colliders.json first and dies on its 404 — a
    /// scene.glb without it is not an openable scene.
    /// </summary>
    public static bool Ready => File.Exists(ScenePath) && File.Exists(ColliderPath);

    /// <summary>What to press when there is nothing to open yet.</summary>
    public const string NothingExported =
        "There is no exported scene to open yet.\n\n" +
        "Open a 3D view and press Enter VR — that is what exports the model.\n" +
        "This button only opens the scene the last export left behind:";

    public static string Describe(string? openDocument) => Describe(ColliderPath, openDocument);

    /// <summary>
    /// Names the scene the viewer is about to serve, and when it was exported. Worth saying
    /// because every viewer command serves the last export regardless of which document is open,
    /// so from project B they open project A — which is otherwise indistinguishable from a stale
    /// view of the right model.
    /// </summary>
    public static string Describe(string colliderPath, string? openDocument)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(colliderPath));
            var root = json.RootElement;

            var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            var lines = new List<string>
            {
                "Scene: " + (string.IsNullOrWhiteSpace(name) ? "unnamed" : name),
            };

            if (root.TryGetProperty("generatedUtc", out var g) &&
                DateTime.TryParse(g.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var generated))
            {
                var local = generated.ToLocalTime();
                lines.Add($"Exported: {local:yyyy-MM-dd HH:mm}" +
                          (local.Date == DateTime.Today
                              ? ""
                              : " (not today — press Enter VR if the model has changed)"));
            }

            if (!string.IsNullOrWhiteSpace(name) &&
                !string.IsNullOrWhiteSpace(openDocument) &&
                !string.Equals(name, openDocument, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add("");
                lines.Add($"Note: this scene is from '{name}', not the open document '{openDocument}'.");
            }

            return string.Join("\n", lines) + "\n\n";
        }
        catch
        {
            // The manifest is a nicety; never block a launch on it.
            return "";
        }
    }
}
