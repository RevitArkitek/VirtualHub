using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitVr.Core.Gltf;
using RevitVr.Core.Scene;

namespace RevitVr.Addin.Export;

public static class Paths
{
    public static string AddinDir => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

    public static string ExportDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitVR", "export");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Walks up from the add-in looking for the viewer assets.</summary>
    public static string? FindVrWeb()
    {
        var env = Environment.GetEnvironmentVariable("REVITVR_WEB");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(Path.Combine(env, "index.html"))) return env;

        var dir = new DirectoryInfo(AddinDir);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var direct = Path.Combine(dir.FullName, "vr-web");
            if (File.Exists(Path.Combine(direct, "index.html"))) return direct;

            var nested = Path.Combine(dir.FullName, "src", "RevitVr.Addin", "vr-web");
            if (File.Exists(Path.Combine(nested, "index.html"))) return nested;
        }
        return null;
    }

    /// <summary>Finds the server executable, preferring the packaged copy.</summary>
    public static string? FindServer()
    {
        var env = Environment.GetEnvironmentVariable("REVITVR_SERVER");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        var packaged = Path.Combine(AddinDir, "server", "RevitVr.Server.exe");
        if (File.Exists(packaged)) return packaged;

        var dir = new DirectoryInfo(AddinDir);
        for (var i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
        {
            foreach (var cfg in new[] { "Release", "Debug" })
            {
                var p = Path.Combine(dir.FullName, "src", "RevitVr.Server", "bin", cfg, "net9.0", "RevitVr.Server.exe");
                if (File.Exists(p)) return p;
                var p80 = Path.Combine(dir.FullName, "src", "RevitVr.Server", "bin", cfg, "net8.0", "RevitVr.Server.exe");
                if (File.Exists(p80)) return p80;
            }
        }
        return null;
    }

    public static int FreePort(int start = 8765, int end = 8795)
    {
        for (var p = start; p <= end; p++)
            if (!Launch.Adb.PortOpen(p, 120)) return p;
        return start;
    }
}

public sealed record ExportOutcome(
    bool Ok,
    string ScenePath,
    string Message,
    int Triangles,
    int Materials,
    int Segments,
    int Openings,
    long GlbBytes);

public static class ExportJob
{
    public static ExportOutcome Run(Document doc, View view)
    {
        var sw = Stopwatch.StartNew();
        var dir = Paths.ExportDir;
        var glbPath = Path.Combine(dir, "scene.glb");
        var jsonPath = Path.Combine(dir, "colliders.json");

        // ---- render geometry -------------------------------------------------
        // A direct model walk rather than CustomExporter: only this way do family instances keep
        // their placement, which is what doors, windows, mullions and furniture all are.
        var builder = new GltfBuilder();
        var harvest = new GeometryExporter(doc, builder);
        harvest.Export(view);

        // Baked occlusion subdivides large triangles and shades per vertex, so it changes the mesh.
        // It is off by default: the honest baseline is the model as Revit exports it, and it can be
        // switched on in settings.ini once the geometry itself looks right.
        var settings = ExportSettings.Load();

        AoReport? ao = null;
        if (settings.BakeOcclusion)
        {
            var tris = builder.TriangleCount;
            var aoOptions = new AoOptions
            {
                Rays = tris > 750_000 ? 16 : tris > 300_000 ? 24 : settings.Rays,
                MaxEdge = tris > 1_000_000 ? 0f : tris > 300_000 ? 1.0f : settings.SubdivisionMetres,
            };
            ao = builder.BakeVertexOcclusion(aoOptions);
        }

        var glb = builder.Build();
        File.WriteAllBytes(glbPath, glb);

        // ---- manifest + collision -------------------------------------------
        var manifest = new SceneManifest
        {
            Name = doc.Title,
            RevitVersion = int.TryParse(doc.Application.VersionNumber, out var v) ? v : 0,
            TriangleCount = builder.TriangleCount,
            EyeHeight = 1.6f,
        };

        SetSpawnFromView(view, manifest);
        var stats = ColliderExtractor.Build(doc, view, manifest);
        ApplySpawnSanity(manifest);
        File.WriteAllText(jsonPath, manifest.ToJson());

        // Per-railing detail beside the scene, so a railing that misbehaves in the headset can be
        // traced to an ElementId instead of guessed at from coordinates.
        File.WriteAllText(
            Path.Combine(dir, "railings.json"),
            System.Text.Json.JsonSerializer.Serialize(stats.Railings, SceneManifest.JsonOptions));

        // Per-material texture scale beside the scene: the raw value Revit gave, the unit it
        // claimed, and the tile size that went into the GLB. A texture that is out of scale is
        // almost always a unit that was read wrong, and this is what shows it.
        File.WriteAllText(
            Path.Combine(dir, "textures.json"),
            System.Text.Json.JsonSerializer.Serialize(harvest.Textures.Rows, SceneManifest.JsonOptions));

        sw.Stop();

        var message =
            $"Exported in {sw.Elapsed.TotalSeconds:0.0}s — {builder.TriangleCount:N0} tris, " +
            $"{builder.MaterialCount} materials, {stats.WallRuns} wall runs → {manifest.Segments.Count} colliders, " +
            $"{stats.Openings} door openings cut, {stats.RailingBoxes} railing colliders.\n" +
            $"textures: {builder.TextureCount} embedded ({builder.TextureBytes / 1048576.0:0.0} MB), " +
            $"{builder.TexturedMaterials} of {builder.MaterialCount} materials textured.\n" +
            (ao is null
                ? "ambient occlusion: off (settings.ini) — geometry exported as Revit produced it.\n"
                : $"ambient occlusion: {ao.Vertices:N0} vertices, {ao.Rays} rays, " +
                  $"{ao.TrianglesBefore:N0} → {ao.TrianglesAfter:N0} tris, {ao.Seconds:0.0}s.\n") +
            $"{harvest.Elements:N0} elements, {harvest.Instances:N0} instances, {harvest.Solids:N0} solids." +
            (harvest.LightSourcesSkipped > 0
                ? $"\n{harvest.LightSourcesSkipped} light-source meshes skipped."
                : "");

        // A material whose image could not be found silently renders as flat colour, which looks
        // like the texture feature did nothing at all — so name the files.
        if (harvest.Textures.MaterialsMissingImage > 0)
        {
            var shown = harvest.Textures.Warnings.Take(4).ToList();
            message += $"\n\nWARNING: {harvest.Textures.MaterialsMissingImage} texture files could not be found " +
                       "or decoded, so those materials export as flat colour:\n  " +
                       string.Join("\n  ", shown) +
                       (harvest.Textures.Warnings.Count > shown.Count
                           ? $"\n  ...and {harvest.Textures.Warnings.Count - shown.Count} more (see log)"
                           : "");
        }

        // An out-of-scale texture is almost always a unit read the wrong way, so say what came out
        // rather than leaving it to be noticed in the headset.
        var scales = harvest.Textures.Rows.Where(r => !r.ScaleClamped).Select(r => r.TileX)
                          .OrderBy(v => v).ToList();
        var clampedCount = harvest.Textures.Rows.Count(r => r.ScaleClamped);

        if (scales.Count > 0)
        {
            message += $"\ntexture scale: median {scales[scales.Count / 2]:0.##} m per repeat " +
                       $"(range {scales[0]:0.##}–{scales[^1]:0.##} m), full detail in textures.json.";
        }

        if (clampedCount > 0)
        {
            message += $"\n\nWARNING: {clampedCount} texture sizes were outside anything a texture could be " +
                       $"({TextureScale.MinMetres:0.##}–{TextureScale.MaxMetres:0} m per repeat) and were " +
                       "replaced with 1 m. This is a unit problem, not a big pattern — textures.json " +
                       "has the raw value and the unit Revit reported for each material.";
        }

        // A zero here is the difference between "walkthrough" and "sealed box", so say it loudly.
        if (stats.DoorsFound > 0 && stats.Openings == 0)
            message += $"\n\nWARNING: {stats.DoorsFound} doors found, none matched a wall. " +
                       "Doorways will be solid — send the log at " + App.LogPath;

        if (stats.RailingsUnmeasured > 0)
            message += $"\n\nWARNING: {stats.RailingsUnmeasured} railings could not be measured from their " +
                       "geometry, so their barrier sits at the level's elevation. Railings on upper " +
                       "floors will not stop you — send the log at " + App.LogPath;

        if (builder.TriangleCount > 750_000)
            message += "\n\nNote: this is a heavy scene for a standalone headset. Cropping the 3D view " +
                       "to one floor gives a much smoother framerate, and makes the baked " +
                       "occlusion sharper too.";

        var outcome = new ExportOutcome(
            true, dir, message,
            builder.TriangleCount, builder.MaterialCount, manifest.Segments.Count, stats.Openings, glb.Length);

        if (builder.TriangleCount == 0)
            return outcome with { Ok = false, Message = "Nothing exported. Is the view empty or cropped out?" };

        return outcome;
    }

    private static void SetSpawnFromView(View view, SceneManifest manifest)
    {
        const double ft = GeometryExporter.FeetToMetres;

        if (view is View3D v3d)
        {
            try
            {
                var o = v3d.GetOrientation();
                var eye = o.EyePosition;
                var fwd = o.ForwardDirection;
                manifest.Spawn = new[] { (float)(eye.X * ft), (float)(eye.Z * ft), (float)(-eye.Y * ft) };
                manifest.SpawnForward = new[] { (float)fwd.X, (float)fwd.Z, (float)(-fwd.Y) };
                return;
            }
            catch
            {
                // section boxes and some iso views have no orientation; fall through to the crop box
            }

            var box = v3d.GetSectionBox();
            if (box is not null)
            {
                var c = box.Transform.OfPoint((box.Min + box.Max) * 0.5);
                manifest.Spawn = new[] { (float)(c.X * ft), (float)(c.Z * ft), (float)(-c.Y * ft) };
                manifest.SpawnForward = new[] { 0f, 0f, -1f };
                return;
            }
        }

        var bb = view.get_BoundingBox(null);
        var mid = bb is null ? XYZ.Zero : (bb.Min + bb.Max) * 0.5;
        manifest.Spawn = new[] { (float)(mid.X * ft), (float)(mid.Z * ft), (float)(-mid.Y * ft) };
        manifest.SpawnForward = new[] { 0f, 0f, -1f };
    }

    /// <summary>
    /// A 3D view's camera is often nowhere near the building — axonometric views report a
    /// synthetic eye position well outside the model, which would drop the player in the void.
    /// If the spawn isn't standing on a floor slab, move it to the middle of the largest one.
    /// </summary>
    private static void ApplySpawnSanity(SceneManifest manifest)
    {
        var surfaces = manifest.Surfaces;
        if (surfaces.Count == 0) return;

        var x = manifest.Spawn[0];
        var y = manifest.Spawn[1];
        var z = manifest.Spawn[2];

        var standing = surfaces.Any(s =>
            x >= s.MinX && x <= s.MaxX &&
            z >= s.MinZ && z <= s.MaxZ &&
            y >= s.Y - 0.5f && y <= s.Y + 3.5f);
        if (standing) return;

        var lowest = surfaces.Min(s => s.Y);
        var ground = surfaces
            .Where(s => Math.Abs(s.Y - lowest) < 0.3f)
            .OrderByDescending(s => (s.MaxX - s.MinX) * (s.MaxZ - s.MinZ))
            .First();

        manifest.Spawn = new[]
        {
            (ground.MinX + ground.MaxX) / 2f,
            ground.Y + manifest.EyeHeight,
            (ground.MinZ + ground.MaxZ) / 2f,
        };
        manifest.SpawnForward = new[] { 0f, 0f, -1f };
        manifest.Warnings.Add("View camera was outside the model; spawn moved to the largest ground-floor slab.");
    }

    /// <summary>Starts the static server if it isn't already listening. Returns the port.</summary>
    public static int EnsureServer(bool lan, out string? error)
    {
        error = null;

        // reuse a server that's already up and speaking the transport we need
        var status = ProbeStatus(lan, out var runningPort);
        if (status is not null) return runningPort;

        var serverExe = Paths.FindServer();
        var web = Paths.FindVrWeb();
        if (serverExe is null) { error = "RevitVr.Server.exe not found (build it, or set REVITVR_SERVER)."; return 0; }
        if (web is null) { error = "vr-web folder not found (set REVITVR_WEB)."; return 0; }

        var port = Paths.FreePort();
        try
        {
            var psi = new ProcessStartInfo(serverExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--app"); psi.ArgumentList.Add(web);
            psi.ArgumentList.Add("--scene"); psi.ArgumentList.Add(Paths.ExportDir);
            psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(port.ToString());
            if (lan) psi.ArgumentList.Add("--lan");
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            error = "Could not start server: " + ex.Message;
            return 0;
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (Launch.Adb.PortOpen(port, 200)) return port;
            Thread.Sleep(200);
        }

        error = "Server did not come up on port " + port;
        return 0;
    }

    /// <summary>Addresses the headset might reach us on, best candidate first.</summary>
    public static IReadOnlyList<string> LanAddresses()
        => RevitVr.Core.NetAddress.Ranked().Select(c => c.Address.ToString()).ToList();

    public static string LanAddress() => LanAddresses().FirstOrDefault() ?? "127.0.0.1";

    private static object? ProbeStatus(bool lan, out int port)
    {
        port = 0;
        var want = lan ? "https" : "http";
        for (var p = 8765; p <= 8795; p++)
        {
            if (!Launch.Adb.PortOpen(p, 80)) continue;
            try
            {
                var scheme = p == 8765 && lan ? "https" : "http";
                // a lan server answers on https; probe both cheaply
                foreach (var s in new[] { "http", "https" })
                {
                    try
                    {
                        using var http = CreateClient();
                        var json = http.GetStringAsync($"{s}://127.0.0.1:{p}/api/status").GetAwaiter().GetResult();
                        using var d = JsonDocument.Parse(json);
                        if (!d.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) continue;
                        var transport = d.RootElement.TryGetProperty("transport", out var t) ? t.GetString() : "http";
                        if (transport != want) continue;
                        port = p;
                        return d.RootElement.Clone();
                    }
                    catch { }
                }
            }
            catch { /* not ours */ }
        }
        return null;
    }

    private static System.Net.Http.HttpClient CreateClient()
    {
        var handler = new System.Net.Http.HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        return new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(700) };
    }
}
