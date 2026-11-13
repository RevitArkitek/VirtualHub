using System.Diagnostics;

namespace RevitVr.Addin.Launch;

/// <summary>Opens a URL on this PC, in whatever the machine uses for a browser.</summary>
public static class Desktop
{
    public static bool Open(string url, out string message)
    {
        try
        {
            // Shell execute hands the URL to the default browser; no browser path is hard-coded,
            // which matters because nobody's default browser is the one anyone would guess.
            using var p = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            message = "Opened " + url;
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }
}
