using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitVr.Addin.Export;
using RevitVr.Addin.Launch;

namespace RevitVr.Addin;

[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class VrCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var uidoc = data.Application.ActiveUIDocument;
        if (uidoc is null)
        {
            TaskDialog.Show("RevitVR", "Open a project first.");
            return Result.Cancelled;
        }

        var view = uidoc.ActiveView;
        if (view is not View3D || view.IsTemplate)
        {
            TaskDialog.Show("RevitVR", "Open a 3D view and run Enter VR from there.\n\n" +
                                        "The view's crop box and visibility settings decide what gets exported.");
            return Result.Cancelled;
        }

        ExportOutcome outcome;
        try
        {
            outcome = ExportJob.Run(uidoc.Document, view);
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("RevitVR — export failed", ex.Message + "\n\n" + ex.StackTrace);
            return Result.Failed;
        }

        if (!outcome.Ok)
        {
            TaskDialog.Show("RevitVR", outcome.Message);
            return Result.Cancelled;
        }

        // ---- find the headset ------------------------------------------------
        var adb = Adb.Find();
        var deviceOk = false;
        var deviceName = "";
        if (adb is not null)
        {
            var devices = Adb.Devices().Where(d => d.State == "device").ToList();
            deviceOk = devices.Count > 0;
            if (deviceOk) deviceName = devices[0].Serial;
        }

        var port = ExportJob.EnsureServer(lan: !deviceOk, out var serverError);
        if (port == 0)
        {
            TaskDialog.Show("RevitVR — server", serverError ?? "Unknown server error.");
            return Result.Failed;
        }

        var lines = new List<string>
        {
            outcome.Message,
            "",
        };

        if (deviceOk)
        {
            var reversed = Adb.Reverse(port, out var revMsg);
            var openMsg = "";
            var opened = reversed && Adb.Open($"http://localhost:{port}/", out openMsg);
            lines.Add(reversed ? $"Headset: {deviceName}" : "Headset: " + revMsg);
            if (opened)
            {
                lines.Add("Viewer opened on the headset. Put it on and press Enter VR.");
            }
            else
            {
                lines.Add($"Could not launch the browser automatically. Open this on the headset:");
                lines.Add($"    http://localhost:{port}/");
                lines.Add($"({openMsg})");
            }
        }
        else
        {
            var ips = ExportJob.LanAddresses();
            var serverExe = Paths.FindServer() ?? "RevitVr.Server.exe";

            lines.Add("Put the headset on, exit Link, and open this in the Quest browser:");
            lines.Add("");
            foreach (var ip in ips.Take(2))
                lines.Add($"    https://{ip}:{port}/");
            if (ips.Count > 1)
                lines.Add("(use the 192.168.x one — the other is a VPN adapter)");
            lines.Add("");
            lines.Add("Accept the certificate warning once: Advanced → Proceed.");
            lines.Add("");
            lines.Add("If the page will not load, Windows Firewall is blocking the port.");
            lines.Add("Run this once in an Administrator PowerShell, then reload:");
            lines.Add($"    New-NetFirewallRule -DisplayName 'RevitVR' -Direction Inbound -Program '{serverExe}' -Action Allow");
        }

        var dialog = new TaskDialog("RevitVR")
        {
            MainContent = string.Join("\n", lines),
            TitleAutoPrefix = false,
            MainIcon = TaskDialogIcon.TaskDialogIconInformation,
        };
        dialog.ExpandedContent =
            $"Scene:   {outcome.ScenePath}\n" +
            $"GLB:     {outcome.GlbBytes / 1024.0 / 1024.0:0.0} MB, {outcome.Triangles:N0} triangles\n" +
            $"Walls:   {outcome.Segments} segments, {outcome.Openings} door openings cut\n" +
            $"Transport: {(deviceOk ? "adb reverse over USB/Wi-Fi debugging" : "HTTPS over LAN")}" +
            (deviceOk
                ? ""
                : "\n\nOne-click setup (optional): enable Developer Mode in the Meta Horizon phone app,\n" +
                  "connect the headset by USB, then accept 'Allow USB debugging' in the headset.\n" +
                  "Link and casting are not adb — they never appear as a device.");
        dialog.Show();

        return Result.Succeeded;
    }
}
