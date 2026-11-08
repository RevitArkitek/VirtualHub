using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitVr.Addin.Launch;

namespace RevitVr.Addin;

public sealed class App : IExternalApplication
{
    public Result OnStartup(UIControlledApplication app)
    {
        try
        {
            BuildRibbon(app);
        }
        catch (Exception ex)
        {
            // Never let a ribbon problem stop Revit from opening, but the user has to know why
            // the button is missing. Log it, then say so once the UI is actually up.
            Log("OnStartup failed: " + ex);
            QueueError(app, "RevitVR could not add its ribbon button.\n\n" +
                       ex.GetType().Name + ": " + ex.Message +
                       "\n\nDetails were written to:\n" + LogPath);
        }
        return Result.Succeeded;
    }

    private static void BuildRibbon(UIControlledApplication app)
    {
        const string panelName = "RevitVR";

        // The built-in Add-Ins tab is not addressable by name: CreateRibbonPanel(tabName,
        // panelName) rejects "Add-Ins" because it only accepts tabs that add-ins created, and
        // GetRibbonPanels(tabName) returns null for the same reason. The single-argument
        // overloads are the ones that target Add-Ins.
        RibbonPanel? panel = null;
        try
        {
            panel = app.CreateRibbonPanel(panelName);
        }
        catch (Exception ex)
        {
            Log($"CreateRibbonPanel(\"{panelName}\") failed; looking for an existing panel. {ex.Message}");
            panel = app.GetRibbonPanels()?.FirstOrDefault(p => p is not null && p.Name == panelName);
            if (panel is null)
                panel = app.CreateRibbonPanel(Tab.AddIns, panelName);
        }

        if (panel is null)
            throw new InvalidOperationException("Could not obtain a ribbon panel on the Add-Ins tab.");

        var asmPath = Assembly.GetExecutingAssembly().Location;
        var button = new PushButtonData(
            "RevitVrEnterVr",
            "Enter VR\nQuest 3",
            asmPath,
            typeof(VrCommand).FullName)
        {
            ToolTip = "Export the active 3D view and open it on the Quest headset.",
            LongDescription =
                "Converts the current 3D view into a walkable WebXR scene and sends it to the headset.\n" +
                "Walls are solid; doorways are passable. Walk with the left stick, turn with the right.",
        };

        var dir = Path.GetDirectoryName(asmPath)!;
        var large = Path.Combine(dir, "Resources", "vr32.png");
        var small = Path.Combine(dir, "Resources", "vr16.png");
        try
        {
            if (File.Exists(large)) button.LargeImage = new BitmapImage(new Uri(large));
            if (File.Exists(small)) button.Image = new BitmapImage(new Uri(small));
        }
        catch { /* icons are cosmetic; never block startup over them */ }

        panel.AddItem(button);

        // A second button that just re-opens the viewer, without re-exporting.
        var reopen = new PushButtonData(
            "RevitVrReopen",
            "Open\nViewer",
            asmPath,
            typeof(OpenViewerCommand).FullName)
        {
            ToolTip = "Re-open the last exported scene on the headset without re-exporting.",
        };
        panel.AddItem(reopen);
    }

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitVR", "addin.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }

    /// <summary>Shows a dialog once Revit's UI is actually up; OnStartup is too early for one.</summary>
    private static void QueueError(UIControlledApplication app, string message)
    {
        void Handler(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            app.Idling -= Handler;
            try { TaskDialog.Show("RevitVR", message); } catch { }
        }
        try { app.Idling += Handler; } catch { /* the log stands either way */ }
    }

    public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;
}

[Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.ReadOnly)]
public sealed class OpenViewerCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        // This command does not export: it re-opens whatever the last Enter VR left in the export
        // folder. Starting a server on an empty folder gives a page that dies on a 404 with no
        // hint about what to do, so check for a scene first and say what to press.
        var exportDir = Export.Paths.ExportDir;
        var scenePath = Path.Combine(exportDir, "scene.glb");
        var colliderPath = Path.Combine(exportDir, "colliders.json");
        if (!File.Exists(scenePath) || !File.Exists(colliderPath))
        {
            TaskDialog.Show("RevitVR",
                "There is no exported scene to open yet.\n\n" +
                "Open a 3D view and press Enter VR — that is what exports the model.\n" +
                "Open Viewer only re-opens the scene the last export left behind:\n\n" +
                exportDir);
            return Result.Cancelled;
        }

        var adb = Launch.Adb.Find();
        var deviceOk = adb is not null && Launch.Adb.Devices().Any(d => d.State == "device");
        var port = Export.ExportJob.EnsureServer(lan: !deviceOk, out var err);
        if (port == 0)
        {
            TaskDialog.Show("RevitVR", err ?? "Server did not start.");
            return Result.Failed;
        }

        var title = data.Application.ActiveUIDocument?.Document?.Title;
        var scene = DescribeScene(colliderPath, title);

        if (!deviceOk)
        {
            var ip = Export.ExportJob.LanAddress();
            TaskDialog.Show("RevitVR",
                scene +
                $"Open this on the headset:\n\nhttps://{ip}:{port}/\n\n" +
                "Accept the certificate warning once (Advanced → Proceed).\n" +
                "No adb, so this is the Wi-Fi path: Windows Firewall must allow the server.");
            return Result.Succeeded;
        }

        var stage = Launcher.OpenOnHeadset(port, out var launchMessage, out var forwarded);
        if (stage == LaunchStage.Opened)
        {
            TaskDialog.Show("RevitVR",
                scene +
                $"Opening http://localhost:{port}/ on the headset.\n" +
                "Put it on and press Enter VR.");
            return Result.Succeeded;
        }

        // Saying "opened" when the launch failed is worse than saying nothing: the user then
        // hunts for a fault in the headset that is really on this end.
        var next = stage switch
        {
            LaunchStage.ReverseFailed =>
                "The PC's server is running, but the headset cannot reach it — adb reverse did not\n" +
                "take, so http://localhost on the headset goes nowhere. Unplug and reconnect the\n" +
                "headset, accept 'Allow USB debugging', then press Open Viewer again.",
            LaunchStage.OpenFailed =>
                $"The address was forwarded, but the browser did not open. Type this into the\nheadset's browser yourself: http://localhost:{port}/",
            LaunchStage.NoAdb =>
                "adb.exe was not found, so there is no USB path to the headset.",
            _ => "The viewer did not open.",
        };

        TaskDialog.Show("RevitVR — viewer did not open",
            scene +
            launchMessage + "\n\n" + next);
        return Result.Failed;
    }

    /// <summary>
    /// Names the scene the server is about to serve, from the manifest beside it. Worth saying
    /// because this command ignores which document is open: from project B it opens project A's
    /// last export, which is otherwise indistinguishable from a stale view.
    /// </summary>
    private static string DescribeScene(string colliderPath, string? openDocument)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(colliderPath));
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
            // The manifest is a nicety; never block the launch on it.
            return "";
        }
    }
}
