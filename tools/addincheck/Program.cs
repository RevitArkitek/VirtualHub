// Checks the *deployed* add-in outside Revit.
//
// Part A reads the assembly as metadata (RevitAPI itself cannot be loaded outside a Revit
// process, so nothing that touches it is executed) to confirm the manifest's class names and
// interfaces actually line up.
// Part B loads the assembly for real and executes only the Revit-free path discovery, which is
// where deployment bugs actually hide: the .addin points at a dll whose sibling folders
// (server\, web\) it then has to find; "vr-web" is accepted for older installs.
using System.Reflection;
using System.Text.Json;

var revitDir = args.Length > 1 ? args[1] : @"C:\Program Files\Autodesk\Revit 2025";
var asmPath = args.Length > 0
    ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Autodesk", "Revit", "Addins", "2025", "RevitVR", "RevitVr.Addin.dll");

Console.WriteLine($"assembly: {asmPath}");
if (!File.Exists(asmPath)) { Console.Error.WriteLine("missing"); return 1; }

var fail = 0;
void Check(string label, bool ok, string detail = "")
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {label}{(detail.Length > 0 ? "  " + detail : "")}");
    if (!ok) fail++;
}

// ---------------------------------------------------------------- A: metadata
Console.WriteLine("\nmetadata:");
var paths = Directory.GetFiles(revitDir, "*.dll").ToList();
paths.AddRange(Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll"));
paths.Add(asmPath);
var corePath = Path.Combine(Path.GetDirectoryName(asmPath)!, "RevitVr.Core.dll");
if (File.Exists(corePath)) paths.Add(corePath);

using (var mlc = new MetadataLoadContext(
           new PathAssemblyResolver(paths.GroupBy(Path.GetFileName).Select(g => g.First()).ToList())))
{
    var meta = mlc.LoadFromAssemblyPath(asmPath);

    var app = meta.GetType("RevitVr.Addin.App");
    Check("App type matches manifest FullClassName", app != null);
    if (app != null)
    {
        Check("App implements IExternalApplication",
            app.GetInterfaces().Any(i => i.Name == "IExternalApplication"));
        Check("App has OnStartup/OnShutdown",
            app.GetMethod("OnStartup") != null && app.GetMethod("OnShutdown") != null);
    }

    var cmd = meta.GetType("RevitVr.Addin.VrCommand");
    Check("VrCommand present", cmd != null);
    if (cmd != null)
    {
        Check("VrCommand implements IExternalCommand",
            cmd.GetInterfaces().Any(i => i.Name == "IExternalCommand"));
        Check("VrCommand is [Transaction]",
            cmd.GetCustomAttributesData().Any(a => a.AttributeType.Name == "TransactionAttribute"));
    }

    // The second ribbon button. Resolved by typeof() when the ribbon is built, so a missing or
    // renamed class shows up as a button that does nothing when clicked — which is exactly the
    // kind of fault this tool exists to catch before Revit does.
    var reopen = meta.GetType("RevitVr.Addin.OpenViewerCommand");
    Check("OpenViewerCommand present", reopen != null);
    if (reopen != null)
    {
        Check("OpenViewerCommand implements IExternalCommand",
            reopen.GetInterfaces().Any(i => i.Name == "IExternalCommand"));
        Check("OpenViewerCommand is [Transaction]",
            reopen.GetCustomAttributesData().Any(a => a.AttributeType.Name == "TransactionAttribute"));
    }

    var stage = meta.GetType("RevitVr.Addin.Launch.LaunchStage");
    Check("LaunchStage enum present", stage is { IsEnum: true });
    if (stage is { IsEnum: true })
    {
        var names = stage.GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.Name).ToList();
        Check("LaunchStage has Opened/NoAdb/ReverseFailed/OpenFailed",
            new[] { "Opened", "NoAdb", "ReverseFailed", "OpenFailed" }.All(names.Contains),
            string.Join(",", names));
    }

    // The third button: the same scene in this PC's browser.
    var preview = meta.GetType("RevitVr.Addin.PreviewCommand");
    Check("PreviewCommand present", preview != null);
    if (preview != null)
    {
        Check("PreviewCommand implements IExternalCommand",
            preview.GetInterfaces().Any(i => i.Name == "IExternalCommand"));
        Check("PreviewCommand is [Transaction]",
            preview.GetCustomAttributesData().Any(a => a.AttributeType.Name == "TransactionAttribute"));
    }

    Check("Desktop URL opener present", meta.GetType("RevitVr.Addin.Launch.Desktop") != null);
    Check("SceneFiles present", meta.GetType("RevitVr.Addin.Export.SceneFiles") != null);

    var harvest = meta.GetType("RevitVr.Addin.Export.GeometryExporter");
    Check("GeometryExporter present", harvest != null);
    Check("GeometryExporter has an Export(View) method",
        harvest != null && harvest.GetMethods().Any(m => m.Name == "Export"));

    Check("ColliderExtractor present", meta.GetType("RevitVr.Addin.Export.ColliderExtractor") != null);
}

// ---------------------------------------------------------------- B: execution
Console.WriteLine("\nexecution (Revit-free parts):");
var asm = Assembly.LoadFrom(asmPath);
var pathsType = asm.GetType("RevitVr.Addin.Export.Paths");
Check("Paths type loads", pathsType != null);

if (pathsType != null)
{
    var web = pathsType.GetMethod("FindVrWeb")!.Invoke(null, null) as string;
    var server = pathsType.GetMethod("FindServer")!.Invoke(null, null) as string;
    var export = pathsType.GetProperty("ExportDir")!.GetValue(null) as string;

    Check("viewer assets resolved", web != null, web ?? "null");
    Check("web app index.html reachable", web != null && File.Exists(Path.Combine(web, "index.html")));
    Check("server exe resolved", server != null, server ?? "null");
    Check("server exe on disk", server != null && File.Exists(server));
    Check("export dir creatable", export != null && Directory.Exists(export), export ?? "null");
}

// SceneFiles.Describe is Revit-free, so the text both viewer commands show can be checked for
// real rather than by reading it: it is the only thing standing between "this is the right
// scene" and "this is last week's project".
var sceneFiles = asm.GetType("RevitVr.Addin.Export.SceneFiles");
Check("SceneFiles type loads", sceneFiles != null);
if (sceneFiles != null)
{
    var ready = (bool)sceneFiles.GetProperty("Ready")!.GetValue(null)!;
    Console.WriteLine($"        (export folder {(ready ? "has" : "has no")} scene)");

    var describe = sceneFiles.GetMethod("Describe", new[] { typeof(string), typeof(string) })!;
    var temp = Path.Combine(Path.GetTempPath(), "revitvr-describe-check.json");
    File.WriteAllText(temp,
        "{\"name\":\"Tower A\",\"generatedUtc\":\"2026-01-02T03:04:05.0000000Z\"}");

    var mismatched = (string)describe.Invoke(null, new object?[] { temp, "Tower B" })!;
    Check("Describe names the scene", mismatched.Contains("Scene: Tower A"), mismatched.Trim());
    Check("Describe flags a scene from another document",
        mismatched.Contains("not the open document 'Tower B'"));
    Check("Describe dates a stale export", mismatched.Contains("not today"));

    var matched = (string)describe.Invoke(null, new object?[] { temp, "Tower A" })!;
    Check("Describe stays quiet for the open document",
        !matched.Contains("not the open document"));

    var unreadable = (string)describe.Invoke(null, new object?[] { temp + ".missing", null })!;
    Check("Describe returns empty for an unreadable manifest", unreadable.Length == 0);

    File.Delete(temp);
}

// ---------------------------------------------------------------- C: library publish
// ModelLibrary is Revit-free, so the last step Enter VR runs — publishing into the shared library
// — can be executed here for real: the slug is the entry's identity, and a second publish has to
// replace that entry rather than add a card.
Console.WriteLine("\nlibrary publish (Revit-free):");
var libType = asm.GetType("RevitVr.Addin.Export.ModelLibrary");
Check("ModelLibrary type loads", libType != null);
if (libType != null)
{
    var slugify = libType.GetMethod("Slugify")!;
    string Slug(string s) => (string)slugify.Invoke(null, new object?[] { s })!;

    // One list of cases, shared with the Python plugin's tests: the two languages must agree, or the
    // same document exported from each plugin lands on two different cards.
    var contracts = FindContracts();
    if (contracts is not null)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(contracts));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input").GetString() ?? "";
            var expected = c.GetProperty("expected").GetString() ?? "";
            var got = Slug(input);
            Check($"slug {Trim(input, 34)}", got == expected, got);
        }
    }
    else
    {
        Console.WriteLine("        (contracts/slug-cases.json not found; shared cases skipped)");
    }

    var scratch = Path.Combine(Path.GetTempPath(), "vrhub-check-" + Guid.NewGuid().ToString("N"));
    var export = Path.Combine(scratch, "export");
    Directory.CreateDirectory(export);
    File.WriteAllBytes(Path.Combine(export, "scene.glb"), new byte[] { 1, 2, 3 });
    File.WriteAllText(Path.Combine(export, "colliders.json"), "{\"name\":\"Check Model\"}");
    Environment.SetEnvironmentVariable("VRHUB_LIBRARY", Path.Combine(scratch, "library"));

    var publish = libType.GetMethod("Publish")!;
    var slug = (string)publish.Invoke(null,
        new object?[] { export, "Check Model", @"C:\models\Check Model.rvt", "Level 1", 1234, 2025 })!;
    var entry = Path.Combine(scratch, "library", slug);
    Check("publish copies the scene", File.Exists(Path.Combine(entry, "scene", "scene.glb")));
    Check("publish writes metadata", File.Exists(Path.Combine(entry, "meta.json")));
    var meta = File.ReadAllText(Path.Combine(entry, "meta.json"));
    Check("metadata names the source", meta.Contains("\"source\"") && meta.Contains("revit"));
    Check("metadata carries the triangle count", meta.Contains("1234"));

    publish.Invoke(null, new object?[] { export, "Check Model", null, "Level 2", 4321, 2025 });
    var entries = Directory.GetDirectories(Path.Combine(scratch, "library"));
    Check("re-publish replaces the entry", entries.Length == 1, entries.Length + " entr(ies)");
    Check("re-publish refreshes the metadata",
        File.ReadAllText(Path.Combine(entry, "meta.json")).Contains("4321"));

    Environment.SetEnvironmentVariable("VRHUB_LIBRARY", null);
    try { Directory.Delete(scratch, true); } catch { }
}

var dir = Path.GetDirectoryName(asmPath)!;
Check("RevitVr.Core.dll deployed", File.Exists(Path.Combine(dir, "RevitVr.Core.dll")));
Check("ribbon icons deployed",
    File.Exists(Path.Combine(dir, "Resources", "vr32.png")) &&
    File.Exists(Path.Combine(dir, "Resources", "vr16.png")));

static string? FindContracts()
{
    var env = Environment.GetEnvironmentVariable("VRHUB_CONTRACTS");
    if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "contracts", "slug-cases.json");
        if (File.Exists(candidate)) return candidate;
    }
    return null;
}

static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 3)] + "...";

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} check(s) failed");
return fail == 0 ? 0 : 1;
