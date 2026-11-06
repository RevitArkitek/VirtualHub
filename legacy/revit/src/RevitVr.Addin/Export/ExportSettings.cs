using System.Globalization;
using System.IO;

namespace RevitVr.Addin.Export;

/// <summary>
/// Export settings, in a file you can open and edit in Notepad.
///
/// Deliberately an INI rather than JSON: the values are few, they want comments next to them, and
/// the point is that somebody can find it, read it and change one line without a tool.
/// </summary>
public sealed record ExportSettings
{
    /// <summary>Defaults, also what is written when the file does not exist yet.</summary>
    public bool BakeOcclusion { get; init; }

    public float SubdivisionMetres { get; init; } = 0.6f;

    public int Rays { get; init; } = 48;

    public static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitVR");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.ini");
        }
    }

    /// <summary>Reads the settings file, writing a commented default if there is none.</summary>
    public static ExportSettings Load()
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            try { File.WriteAllText(path, DefaultText); } catch { /* read-only profile: use defaults */ }
            return new ExportSettings();
        }

        var settings = new ExportSettings();
        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;

                var split = line.IndexOf('=');
                if (split <= 0) continue;

                var key = line[..split].Trim();
                var value = line[(split + 1)..].Trim();

                switch (key.ToLowerInvariant())
                {
                    case "bakeocclusion":
                        settings = settings with { BakeOcclusion = ParseBool(value, false) };
                        break;
                    case "subdivisionmetres":
                        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var m))
                            settings = settings with { SubdivisionMetres = Math.Clamp(m, 0f, 5f) };
                        break;
                    case "rays":
                        if (int.TryParse(value, out var r))
                            settings = settings with { Rays = Math.Clamp(r, 4, 256) };
                        break;
                }
            }
        }
        catch
        {
            // A broken settings file must not stop an export; the defaults are safe.
            return new ExportSettings();
        }

        return settings;
    }

    private static bool ParseBool(string value, bool fallback) => value.ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        "false" or "0" or "no" or "off" => false,
        _ => fallback,
    };

    /// <summary>The file written on first run: every option, with what it does and its cost.</summary>
    public static string DefaultText =>
        "# RevitVR export settings. Edit, save, then export again — no restart needed.\n" +
        "# Delete this file to get these defaults back.\n" +
        "\n" +
        "# Baked ambient occlusion. Darkens corners, floor-wall junctions and under furniture.\n" +
        "# It works by subdividing large triangles and shading per vertex, so it changes the mesh:\n" +
        "# turn it off to see the model exactly as Revit exported it.\n" +
        "bakeOcclusion = false\n" +
        "\n" +
        "# Longest triangle edge the occlusion bake will subdivide to, in metres.\n" +
        "# Larger = fewer triangles and softer shading. Ignored when bakeOcclusion is false.\n" +
        "subdivisionMetres = 0.6\n" +
        "\n" +
        "# Hemisphere rays per vertex. More rays = less noise, slower export.\n" +
        "rays = 48\n";
}
