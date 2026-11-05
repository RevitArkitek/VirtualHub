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
        var adb = Launch.Adb.Find();
        var deviceOk = adb is not null && Launch.Adb.Devices().Any(d => d.State == "device");
        var port = Export.ExportJob.EnsureServer(lan: !deviceOk, out var err);
        if (port == 0)
        {
            TaskDialog.Show("RevitVR", err ?? "Server did not start.");
            return Result.Failed;
        }

        if (deviceOk)
        {
            Launcher.OpenOnHeadset(port);
            TaskDialog.Show("RevitVR", $"Opening http://localhost:{port}/ on the headset.");
        }
        else
        {
            var ip = Export.ExportJob.LanAddress();
            TaskDialog.Show("RevitVR", $"Open this on the headset:\n\nhttps://{ip}:{port}/");
        }
        return Result.Succeeded;
    }
}
