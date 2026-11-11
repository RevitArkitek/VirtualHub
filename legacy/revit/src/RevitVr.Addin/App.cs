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
            "Model\nLibrary",
            asmPath,
            typeof(OpenViewerCommand).FullName)
        {
            ToolTip = "Open the model library on the headset — every exported model, from Revit or IngeTrazo.",
            LongDescription =
                "Lists every model either plugin has exported, newest first. Pick one and press\n" +
                "Enter VR to walk it. Nothing is re-exported, so this is the way back in after the\n" +
                "headset went to sleep or the browser tab closed.",
        };
        panel.AddItem(reopen);

        // A third: the same scene in this PC's browser, so the model can be checked before
        // putting the headset on. Same viewer and same renderer — no second render path to trust.
        var preview = new PushButtonData(
            "RevitVrPreview",
            "Preview\nDesktop",
            asmPath,
            typeof(PreviewCommand).FullName)
        {
            ToolTip = "Open the last exported scene in this PC's browser — no headset needed.",
            LongDescription =
                "Shows the exported scene with the viewer the headset uses, at desktop scale.\n" +
                "WASD to walk, drag to look, F to fly, Q/E down and up, R to respawn, " +
                "C to draw the colliders.\n" +
                "Nothing is re-exported: press Enter VR first if the model has changed.",
        };
        panel.AddItem(preview);
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
        // This command does not export: it opens the model library, which serves every model
        // either plugin has published. With nothing published the homepage says so, which is a
        // better answer than refusing to open a page at all.
        var adb = Launch.Adb.Find();
        var deviceOk = adb is not null && Launch.Adb.Devices().Any(d => d.State == "device");
        var port = Export.ExportJob.EnsureServer(lan: !deviceOk, out var err);
        if (port == 0)
        {
            TaskDialog.Show("RevitVR", err ?? "Server did not start.");
            return Result.Failed;
        }

        var models = Directory.EnumerateDirectories(Export.ModelLibrary.Root).Count();
        var scene = Export.SceneFiles.Describe(data.Application.ActiveUIDocument?.Document?.Title);

        if (!deviceOk)
        {
            var ip = Export.ExportJob.LanAddress();
            TaskDialog.Show("RevitVR",
                scene +
                $"Open the model library on the headset:\n\nhttps://{ip}:{port + 1}/\n\n" +
                $"It lists {models} model(s), from Revit and IngeTrazo.\n" +
                "Accept the certificate warning once (Advanced → Proceed).\n" +
                "No adb, so this is the Wi-Fi path: Windows Firewall must allow the server.");
            return Result.Succeeded;
        }

        var stage = Launcher.OpenOnHeadset(port, out var launchMessage, out var forwarded);
        if (stage == LaunchStage.Opened)
        {
            TaskDialog.Show("RevitVR",
                scene +
                $"Opening the model library on the headset — {models} model(s).\n" +
                "Pick a model, then press Enter VR.");
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
}

[Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.ReadOnly)]
public sealed class PreviewCommand : IExternalCommand
{
    /// <summary>
    /// The same scene in this PC's browser, so the model can be looked at without the headset.
    /// The viewer is the one the headset runs and the server is already http on loopback, so
    /// this costs no second renderer — but it does mean the preview shows the *exported* scene,
    /// not the live Revit model: press Enter VR first if the model has changed.
    /// </summary>
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        // The same library in this PC's browser, so models can be browsed and walked without the
        // headset. The viewer is the one the headset runs and the server is http on loopback, so
        // this costs no second renderer — but it does mean the preview shows the *exported* scene,
        // not the live Revit model: press Enter VR first if the model has changed.
        // lan:false — the browser is on this machine, so loopback over http needs no certificate
        // and none of the adb work the headset path needs.
        var port = Export.ExportJob.EnsureServer(lan: false, out var err);
        if (port == 0)
        {
            TaskDialog.Show("RevitVR", err ?? "Server did not start.");
            return Result.Failed;
        }

        var url = $"http://localhost:{port}/";
        var scene = Export.SceneFiles.Describe(data.Application.ActiveUIDocument?.Document?.Title);

        if (!Launch.Desktop.Open(url, out var openMessage))
        {
            TaskDialog.Show("RevitVR — preview did not open",
                scene + "The server is up, but no browser could be launched:\n\n" +
                openMessage + "\n\nOpen this yourself:\n" + url);
            return Result.Failed;
        }

        // No dialog on success: the browser window is the confirmation, and the page itself lists
        // the desktop controls. A modal box to dismiss would defeat the point of a quick look.
        App.Log($"preview: {url}");
        return Result.Succeeded;
    }
}
