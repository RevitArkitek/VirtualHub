using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;

namespace RevitVr.Addin.Launch;

/// <summary>
/// "Finds the Quest" via adb. This is also what makes the one-click flow work without a
/// certificate: `adb reverse` maps a port on the headset to this PC, so the headset loads the
/// viewer from http://localhost, which browsers treat as a secure context and therefore allow
/// WebXR on, with no self-signed certificate to click through.
/// </summary>
public static class Adb
{
    public static string? Located { get; private set; }
    public static string LastError { get; private set; } = "";

    public static string? Find()
    {
        if (Located is not null) return Located;

        foreach (var candidate in Candidates())
        {
            if (candidate is null || !File.Exists(candidate)) continue;
            Located = candidate;
            return Located;
        }
        return null;
    }

    private static IEnumerable<string?> Candidates()
    {
        var env = Environment.GetEnvironmentVariable("REVITVR_ADB");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;

        var addinDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        yield return Path.Combine(addinDir, "adb", "adb.exe");
        yield return Path.Combine(addinDir, "platform-tools", "adb.exe");

        // Android SDK default locations
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe");

        foreach (var root in new[] { @"C:\platform-tools", @"C:\Android\platform-tools", @"C:\Program Files\Android\platform-tools" })
            yield return Path.Combine(root, "adb.exe");

        // whatever is on PATH
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string full;
            try { full = Path.Combine(dir.Trim(), "adb.exe"); }
            catch { continue; }
            yield return full;
        }
    }

    public sealed record RunResult(int ExitCode, string Out, string Err)
    {
        public bool Ok => ExitCode == 0;
    }

    public static RunResult Run(params string[] args)
    {
        var exe = Find();
        if (exe is null) { LastError = "adb.exe not found"; return new RunResult(-1, "", LastError); }

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(20000))
            {
                try { p.Kill(true); } catch { }
                return new RunResult(-1, stdout, "adb timed out");
            }
            return new RunResult(p.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return new RunResult(-1, "", ex.Message);
        }
    }

    public sealed record Device(string Serial, string State);

    public static List<Device> Devices()
    {
        var list = new List<Device>();
        var r = Run("devices");
        if (!r.Ok) return list;
        foreach (var raw in r.Out.Split('\n').Skip(1))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('*')) continue;
            var parts = line.Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            list.Add(new Device(parts[0], parts[1]));
        }
        return list;
    }

    /// <summary>Maps a host port onto the headset so it can reach us over localhost.</summary>
    public static bool Reverse(int port, out string message)
    {
        var r = Run("-s", PickSerial(), "reverse", $"tcp:{port}", $"tcp:{port}");
        message = r.Ok ? $"adb reverse tcp:{port} -> host:{port}" : (r.Err.Trim() + r.Out.Trim()).Trim();
        return r.Ok;
    }

    public static string PickSerial()
    {
        var devices = Devices().Where(d => d.State == "device").ToList();
        return devices.Count > 0 ? devices[0].Serial : "";
    }

    /// <summary>Opens the viewer in the headset browser.</summary>
    public static bool Open(string url, out string message)
    {
        var serial = PickSerial();
        var r = Run("-s", serial, "shell", "am", "start", "-a", "android.intent.action.VIEW", "-d", url);
        if (r.Ok && !r.Out.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            message = "Opened " + url;
            return true;
        }

        // fall back to targeting the stock Quest browser explicitly
        var r2 = Run("-s", serial, "shell", "am", "start", "-a", "android.intent.action.VIEW",
                     "-d", url, "com.oculus.browser");
        message = r2.Ok ? "Opened " + url : (r2.Err + r2.Out).Trim();
        return r2.Ok;
    }

    public static bool PortOpen(int port, int timeoutMs = 400)
    {
        try
        {
            using var c = new TcpClient();
            var task = c.ConnectAsync("127.0.0.1", port);
            return task.Wait(timeoutMs) && c.Connected;
        }
        catch { return false; }
    }
}
