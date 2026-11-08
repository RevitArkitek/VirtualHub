// Checks the *deployed* add-in outside Revit.
//
// Part A reads the assembly as metadata (RevitAPI itself cannot be loaded outside a Revit
// process, so nothing that touches it is executed) to confirm the manifest's class names and
// interfaces actually line up.
// Part B loads the assembly for real and executes only the Revit-free path discovery, which is
// where deployment bugs actually hide: the .addin points at a dll whose sibling folders
// (server\, vr-web\) it then has to find.
using System.Reflection;

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
    Check("viewer index.html reachable", web != null && File.Exists(Path.Combine(web, "index.html")));
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

var dir = Path.GetDirectoryName(asmPath)!;
Check("RevitVr.Core.dll deployed", File.Exists(Path.Combine(dir, "RevitVr.Core.dll")));
Check("ribbon icons deployed",
    File.Exists(Path.Combine(dir, "Resources", "vr32.png")) &&
    File.Exists(Path.Combine(dir, "Resources", "vr16.png")));

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} check(s) failed");
return fail == 0 ? 0 : 1;
