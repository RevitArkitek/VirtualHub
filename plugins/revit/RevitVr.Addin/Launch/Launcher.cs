using System.Diagnostics;

namespace RevitVr.Addin.Launch;

/// <summary>How far <see cref="Launcher.OpenOnHeadset(int, out string, out bool)"/> got.</summary>
public enum LaunchStage
{
    /// <summary>The viewer is in front of the user on the headset.</summary>
    Opened,
    /// <summary>adb.exe was not found, so there is no USB path to the headset.</summary>
    NoAdb,
    /// <summary>`adb reverse` failed, so a localhost URL on the headset reaches nothing.</summary>
    ReverseFailed,
    /// <summary>The address was forwarded, but the browser could not be launched.</summary>
    OpenFailed,
}

/// <summary>Pushes the viewer at the headset: adb reverse first, then open the URL.</summary>
public static class Launcher
{
    /// <summary>
    /// The stage is reported rather than collapsed into a bool because the two failures need
    /// different advice: a failed reverse means the headset cannot reach this PC at all, while a
    /// failed open only means the link did not launch — the user can type the URL themselves.
    /// </summary>
    public static LaunchStage OpenOnHeadset(int port, out string message, out bool forwarded)
    {
        forwarded = false;
        var url = $"http://localhost:{port}/";

        if (Adb.Find() is null)
        {
            message = "adb.exe not found. Install Android platform-tools, or use the HTTPS address.";
            return LaunchStage.NoAdb;
        }

        if (!Adb.Reverse(port, out var revMsg))
        {
            message = "adb reverse failed: " + revMsg;
            return LaunchStage.ReverseFailed;
        }
        forwarded = true;

        if (!Adb.Open(url, out message)) return LaunchStage.OpenFailed;
        return LaunchStage.Opened;
    }

    public static bool OpenOnHeadset(int port, out string message)
        => OpenOnHeadset(port, out message, out _) == LaunchStage.Opened;
}
