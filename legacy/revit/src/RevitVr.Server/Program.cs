// Serves the WebXR viewer, the model library, and a freshly exported scene to the headset.
//
// Two transports, both hit the same app:
//   http://127.0.0.1:<port>/           loopback. With `adb reverse tcp:<port> tcp:<port>` the
//                                      headset reaches it as http://localhost:<port>/ which
//                                      browsers treat as a secure context, so WebXR works
//                                      with no certificate to accept.
//   https://<lan-ip>:<port>/           --lan. Self-signed, accept the warning once on the Quest.
//
// Two modes:
//   --library <dir>                    the model library: "/" lists every exported model and each
//                                      one is served from /models/<slug>/scene/. Both plugins use
//                                      this, so one homepage shows models from either source.
//   (no --library)                     one scene at /scene, viewer at /viewer.html. Kept for the
//                                      demogen and test path, which has no library to show.
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

var opts = Options.Parse(args);
if (!Directory.Exists(opts.AppDir))
{
    Console.Error.WriteLine($"web directory not found: {opts.AppDir}");
    return 2;
}
if (opts.Library is not null) Directory.CreateDirectory(opts.Library);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();

// Two listeners, always:
//   http  on 127.0.0.1:<port>   the adb-reverse path, and this PC's browser
//   https on 0.0.0.0:<port+1>   the Wi-Fi path — WebXR needs a secure context, and a headset with
//                              no adb cannot use loopback at all
// --lan is still accepted (old shortcuts pass it) but there is nothing left for it to switch on.
var httpsPort = opts.Port + 1;
X509Certificate2? cert = null;
try
{
    // Reused across runs so the headset keeps trusting it; regenerate with --new-cert.
    cert = SelfSigned.LoadOrCreate("RevitVR",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitVR"),
        opts.NewCert);
}
catch (Exception ex)
{
    Console.WriteLine($"  certificate unavailable ({ex.Message}); serving http only");
}

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Listen(IPAddress.Loopback, opts.Port);
    if (cert is not null)
    {
        // A busy port+1 must not take the whole server down: downgrade to http-only and say so.
        if (PortIsFree(httpsPort))
            k.Listen(IPAddress.Any, httpsPort, listen => listen.UseHttps(cert));
        else
            Console.WriteLine($"  https port {httpsPort} is busy; serving http only");
    }
});

var app = builder.Build();

var types = new FileExtensionContentTypeProvider();
types.Mappings[".glb"] = "model/gltf-binary";
types.Mappings[".gltf"] = "model/gltf+json";
types.Mappings[".js"] = "text/javascript";
types.Mappings[".mjs"] = "text/javascript";
types.Mappings[".json"] = "application/json";

// Never cache scene data: a re-export from Revit must show up on the next refresh.
var noStore = new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(opts.SceneDir),
    RequestPath = "/scene",
    ContentTypeProvider = types,
    ServeUnknownFileTypes = true,
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.CacheControl = "no-store, must-revalidate";
    }
};

var viewerFiles = new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(opts.AppDir),
    ContentTypeProvider = types,
    ServeUnknownFileTypes = true,
    OnPrepareResponse = ctx =>
    {
        // the viewer itself may be cached briefly; the scene may not
        ctx.Context.Response.Headers.CacheControl = "no-cache";
    }
};

app.UseStaticFiles(noStore);
app.UseStaticFiles(viewerFiles);

// "/" is the library when there is one. Without a library this is the single-scene server the
// tests use, and the homepage has nothing to list — so go straight to the viewer.
app.MapGet("/", (HttpContext ctx) =>
{
    if (opts.Library is null) return Results.Redirect("/viewer.html");
    var index = Path.Combine(opts.AppDir, "index.html");
    if (!File.Exists(index)) return Results.NotFound("index.html is missing from " + opts.AppDir);
    ctx.Response.Headers.CacheControl = "no-cache";
    return Results.File(index, "text/html; charset=utf-8");
});

app.MapGet("/api/models", () =>
    Results.Json(opts.Library is null
        ? new { models = Array.Empty<ModelRow>(), library = (string?)null }
        : ModelIndex.Scan(opts.Library)));

// One folder per model, one URL per folder. Only a file directly under <slug>/scene is served:
// the library is a folder on the user's disk, so a traversal-shaped slug must go nowhere.
app.MapGet("/models/{slug}/scene/{file}", (string slug, string file, HttpContext ctx) =>
{
    if (opts.Library is null) return Results.NotFound();
    var path = ModelIndex.Resolve(opts.Library, slug, file);
    if (path is null) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "no-store, must-revalidate";
    var contentType = types.TryGetContentType(Path.GetExtension(path), out var ct)
        ? ct
        : "application/octet-stream";
    return Results.File(path, contentType);
});

app.MapGet("/api/status", () =>
{
    var manifest = Path.Combine(opts.SceneDir, "colliders.json");
    var glb = Path.Combine(opts.SceneDir, "scene.glb");
    return Results.Json(new
    {
        ok = true,
        app = "revitvr",
        scene = opts.SceneDir,
        hasScene = File.Exists(glb) && File.Exists(manifest),
        glbBytes = File.Exists(glb) ? new FileInfo(glb).Length : 0,
        exportedAt = File.Exists(manifest) ? File.GetLastWriteTimeUtc(manifest).ToString("o") : null,
        library = opts.Library,
        models = opts.Library is null ? 0 : ModelIndex.Count(opts.Library),
        port = opts.Port,
        httpsPort = cert is not null ? httpsPort : 0,
        https = cert is not null,
        transport = "http",
    });
});

static bool PortIsFree(int port)
{
    try
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Any, port);
        probe.Start();
        probe.Stop();
        return true;
    }
    catch { return false; }
}

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"RevitVR server up   scene: {opts.SceneDir}");
    if (opts.Library is not null)
        Console.WriteLine($"  library   {opts.Library}  ({ModelIndex.Count(opts.Library)} models)");
    Console.WriteLine($"  local     http://127.0.0.1:{opts.Port}/");
    if (cert is not null)
    {
        foreach (var ip in Lan.Addresses())
            Console.WriteLine($"  headset   https://{ip}:{httpsPort}/   (accept the certificate once)");
    }
    Console.WriteLine($"  adb       adb reverse tcp:{opts.Port} tcp:{opts.Port} -> http://localhost:{opts.Port}/");
    Console.WriteLine("READY");
});

app.Run();
return 0;

/// <summary>One exported model, as the homepage sees it.</summary>
internal sealed record ModelRow(
    string Slug, string Name, string Source, string? SourceLabel, string? FileName,
    string? FilePath, string? View, string ExportedUtc, int? Triangles, long GlbBytes, long ColliderBytes);

/// <summary>The model library: one folder per exported file, each holding <c>scene/</c>.</summary>
internal static class ModelIndex
{
    /// <summary>
    /// Builds the list the homepage renders. The folder is the only source of truth, so a model
    /// folder copied in by hand shows up and one deleted disappears — nothing is cached here,
    /// which is what makes a re-export replace its card instead of adding a second one.
    /// </summary>
    public static object Scan(string library)
    {
        var rows = new List<ModelRow>();
        if (!Directory.Exists(library)) return new { models = rows, library };

        foreach (var dir in Directory.EnumerateDirectories(library))
        {
            var metaPath = Path.Combine(dir, "meta.json");
            var glb = Path.Combine(dir, "scene", "scene.glb");
            var colliders = Path.Combine(dir, "scene", "colliders.json");
            if (!File.Exists(metaPath) || !File.Exists(glb) || !File.Exists(colliders)) continue;

            string? name, source, sourceLabel, fileName, filePath, view, exportedUtc;
            int? triangles = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                var root = doc.RootElement;
                name = Str(root, "name");
                source = Str(root, "source");
                sourceLabel = Str(root, "sourceLabel");
                fileName = Str(root, "fileName");
                filePath = Str(root, "filePath");
                view = Str(root, "view");
                exportedUtc = Str(root, "exportedUtc");
                if (root.TryGetProperty("triangles", out var t) && t.TryGetInt32(out var ti)) triangles = ti;
            }
            catch
            {
                // A half-written meta.json is a model that was interrupted mid-publish. It is not
                // listed; the next export rewrites it.
                continue;
            }

            var slug = Path.GetFileName(dir);
            rows.Add(new ModelRow(
                slug,
                name ?? slug,
                source ?? "other",
                sourceLabel,
                fileName,
                filePath,
                view,
                exportedUtc ?? File.GetLastWriteTimeUtc(colliders).ToString("o"),
                triangles,
                new FileInfo(glb).Length,
                new FileInfo(colliders).Length));
        }

        rows.Sort((a, b) => string.CompareOrdinal(b.ExportedUtc, a.ExportedUtc));
        return new { models = rows, library };
    }

    public static int Count(string library)
    {
        if (!Directory.Exists(library)) return 0;
        return Directory.EnumerateDirectories(library)
            .Count(d => File.Exists(Path.Combine(d, "meta.json")) &&
                        File.Exists(Path.Combine(d, "scene", "scene.glb")));
    }

    private static string? Str(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Maps /models/&lt;slug&gt;/scene/&lt;file&gt; to a real path, or null if it is outside the library.</summary>
    public static string? Resolve(string library, string slug, string file)
    {
        if (!IsName(slug) || !IsName(file)) return null;
        var path = Path.GetFullPath(Path.Combine(library, slug, "scene", file));
        var root = Path.GetFullPath(library);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(path) ? path : null;
    }

    private static bool IsName(string s)
        => s.Length is > 0 and <= 160
           && s != "." && s != ".."
           && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && !s.Contains("..", StringComparison.Ordinal);
}

internal sealed record Options(string AppDir, string SceneDir, string? Library, int Port, bool Lan, bool NewCert)
{
    public static Options Parse(string[] args)
    {
        string? app = null, scene = null, library = null;
        var port = 8765;
        var lan = false;
        var newCert = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--app": app = args[++i]; break;
                case "--scene": scene = args[++i]; break;
                case "--library": library = args[++i]; break;
                case "--port": port = int.Parse(args[++i]); break;
                case "--lan": lan = true; break;
                case "--new-cert": newCert = true; break;
            }
        }
        app ??= Path.Combine(AppContext.BaseDirectory, "vr-web");
        scene ??= Path.Combine(AppContext.BaseDirectory, "export");
        return new Options(
            Path.GetFullPath(app),
            Path.GetFullPath(scene),
            string.IsNullOrWhiteSpace(library) ? null : Path.GetFullPath(library),
            port, lan, newCert);
    }
}

internal static class SelfSigned
{
    /// <summary>Reuses a previously generated certificate so the headset only warns once.</summary>
    public static X509Certificate2 LoadOrCreate(string cn, string dir, bool force)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "cert.pfx");

        if (!force && File.Exists(path))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(path), null);
                if (existing.NotAfter > DateTime.Now.AddDays(7))
                {
                    Console.WriteLine($"  certificate reused: {path} (expires {existing.NotAfter:yyyy-MM-dd})");
                    return existing;
                }
            }
            catch { /* fall through and regenerate */ }
        }

        var pfx = CreatePfx(cn);
        try
        {
            File.WriteAllBytes(path, pfx);
            Console.WriteLine($"  certificate written: {path}");
        }
        catch (Exception ex)
        {
            // not fatal — the server still runs, the headset just warns again next time
            Console.WriteLine($"  certificate could not be saved ({ex.Message}); using a temporary one");
        }

        return X509CertificateLoader.LoadPkcs12(pfx, null);
    }

    public static X509Certificate2 Create(string cn) => X509CertificateLoader.LoadPkcs12(CreatePfx(cn), null);

    private static byte[] CreatePfx(string cn)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(cn);
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in Lan.Addresses()) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());

        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        return cert.Export(X509ContentType.Pfx);
    }
}

internal static class Lan
{
    public static IEnumerable<IPAddress> Addresses() => RevitVr.Core.NetAddress.All();
}
