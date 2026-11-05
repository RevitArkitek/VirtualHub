using System.Diagnostics;

namespace RevitVr.Addin.Launch;

/// <summary>Pushes the viewer at the headset: adb reverse first, then open the URL.</summary>
public static class Launcher
{
    public static bool OpenOnHeadset(int port, out string message)
    {
        var url = $"http://localhost:{port}/";

        if (Adb.Find() is null)
        {
            message = "adb.exe not found. Install Android platform-tools, or use the HTTPS address.";
            return false;
        }

        if (!Adb.Reverse(port, out var revMsg))
        {
            message = "adb reverse failed: " + revMsg;
            return false;
        }

        return Adb.Open(url, out message);
    }

    public static void OpenOnHeadset(int port) => OpenOnHeadset(port, out _);
}
