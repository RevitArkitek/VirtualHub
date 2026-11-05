// Serves the WebXR viewer and a freshly exported scene to the headset.
//
// Two transports, both hit the same app:
//   http://127.0.0.1:<port>/           loopback. With `adb reverse tcp:<port> tcp:<port>` the
//                                      headset reaches it as http://localhost:<port>/ which
//                                      browsers treat as a secure context, so WebXR works
//                                      with no certificate to accept.
//   https://<lan-ip>:<port>/           --lan. Self-signed, accept the warning once on the Quest.
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

var opts = Options.Parse(args);
if (!Directory.Exists(opts.SceneDir))
{
    Console.Error.WriteLine($"scene directory not found: {opts.SceneDir}");
    return 2;
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();

X509Certificate2? cert = null;
if (opts.Lan)
{
    // Reused across runs so the headset keeps trusting it; regenerate with --new-cert.
    cert = SelfSigned.LoadOrCreate("RevitVR",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitVR"),
        opts.NewCert);
}

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    var ip = opts.Lan ? IPAddress.Any : IPAddress.Loopback;
    k.Listen(ip, opts.Port, listen => { if (cert != null) listen.UseHttps(cert); });
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

app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new PhysicalFileProvider(opts.AppDir) });
app.UseStaticFiles(noStore);
app.UseStaticFiles(viewerFiles);

app.MapGet("/api/status", () =>
{
    var manifest = Path.Combine(opts.SceneDir, "colliders.json");
    var glb = Path.Combine(opts.SceneDir, "scene.glb");
    return Results.Json(new
    {
        ok = true,
        scene = opts.SceneDir,
        hasScene = File.Exists(glb) && File.Exists(manifest),
        glbBytes = File.Exists(glb) ? new FileInfo(glb).Length : 0,
        exportedAt = File.Exists(manifest) ? File.GetLastWriteTimeUtc(manifest).ToString("o") : null,
        port = opts.Port,
        transport = cert != null ? "https" : "http",
    });
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    var scheme = cert != null ? "https" : "http";
    Console.WriteLine($"RevitVR server up   scene: {opts.SceneDir}");
    Console.WriteLine($"  loopback  {scheme}://127.0.0.1:{opts.Port}/");
    if (opts.Lan)
    {
        foreach (var ip in Lan.Addresses())
            Console.WriteLine($"  headset   {scheme}://{ip}:{opts.Port}/");
    }
    else
    {
        Console.WriteLine($"  headset   adb reverse tcp:{opts.Port} tcp:{opts.Port}  ->  http://localhost:{opts.Port}/");
    }
    Console.WriteLine("READY");
});

app.Run();
return 0;

internal sealed record Options(string AppDir, string SceneDir, int Port, bool Lan, bool NewCert)
{
    public static Options Parse(string[] args)
    {
        string? app = null, scene = null;
        var port = 8765;
        var lan = false;
        var newCert = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--app": app = args[++i]; break;
                case "--scene": scene = args[++i]; break;
                case "--port": port = int.Parse(args[++i]); break;
                case "--lan": lan = true; break;
                case "--new-cert": newCert = true; break;
            }
        }
        app ??= Path.Combine(AppContext.BaseDirectory, "vr-web");
        scene ??= Path.Combine(AppContext.BaseDirectory, "export");
        return new Options(Path.GetFullPath(app), Path.GetFullPath(scene), port, lan, newCert);
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
