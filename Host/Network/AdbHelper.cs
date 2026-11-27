using System.Diagnostics;
using System.IO;

namespace PhoneDisplay.Host.Network;

public sealed class AdbHelper : IDisposable
{
    private readonly int _httpPort;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _watcherTask;
    private readonly HashSet<string> _reversedSerials = new(StringComparer.Ordinal);

    public bool AdbAvailable { get; private set; } = true;
    public int ConnectedDevicesCount { get; private set; }
    public string StatusSummary { get; private set; } = "Checking ADB...";

    public AdbHelper(int httpPort)
    {
        _httpPort = httpPort;
        _watcherTask = Task.Run(WatchLoopAsync);
    }

    private async Task WatchLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await PollDevicesAsync();
            }
            catch
            {
                // Keep watcher alive
            }

            try
            {
                await Task.Delay(3000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollDevicesAsync()
    {
        var (ok, output) = await RunAdbAsync("devices");
        if (!ok)
        {
            AdbAvailable = false;
            ConnectedDevicesCount = 0;
            StatusSummary = "adb not in PATH (USB optional)";
            return;
        }

        AdbAvailable = true;
        var activeSerials = new List<string>();
        bool hasUnauthorized = false;
        foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                continue;

            string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                if (parts[1] == "device")
                    activeSerials.Add(parts[0]);
                else if (parts[1] == "unauthorized")
                    hasUnauthorized = true;
            }
        }

        ConnectedDevicesCount = activeSerials.Count;
        _reversedSerials.RemoveWhere(s => !activeSerials.Contains(s));

        if (activeSerials.Count == 0)
        {
            StatusSummary = hasUnauthorized
                ? "Chưa bấm Cho phép (Allow USB Debugging) trên ĐT!"
                : "Waiting for USB device";
            return;
        }

        int reversedCount = 0;
        foreach (string serial in activeSerials)
        {
            if (!_reversedSerials.Contains(serial))
            {
                var (revOk, _) = await RunAdbAsync($"-s \"{serial}\" reverse tcp:{_httpPort} tcp:{_httpPort}");
                if (revOk)
                {
                    _reversedSerials.Add(serial);
                }
            }
            if (_reversedSerials.Contains(serial))
                reversedCount++;
        }

        StatusSummary = reversedCount > 0
            ? $"USB Ready ({reversedCount} device, localhost:{_httpPort})"
            : $"Device detected ({activeSerials.Count}), reverse pending";
    }

    private static async Task<(bool Ok, string Output)> RunAdbAsync(string args)
    {
        try
        {
            string adbExe = ResolveAdbPath();
            var psi = new ProcessStartInfo
            {
                FileName = adbExe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return (false, string.Empty);

            string stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return (proc.ExitCode == 0, stdout);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    private static string ResolveAdbPath()
    {
        string[] localCandidates =
        [
            Path.Combine(AppContext.BaseDirectory, "adb.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "adb.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"),
            @"C:\platform-tools\adb.exe"
        ];

        foreach (string c in localCandidates)
        {
            if (File.Exists(c)) return c;
        }

        string wingetPkg = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetPkg))
        {
            var found = Directory.EnumerateFiles(wingetPkg, "adb.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null) return found;
        }

        return "adb";
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _watcherTask.Wait(500); } catch { }
        _cts.Dispose();
    }
}
