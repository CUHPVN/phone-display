namespace PhoneDisplay.Host.Models;

public enum PreferredEncoder
{
    Auto,
    Nvenc,
    X264
}

public sealed class AppConfig
{
    public int? MonitorIndex { get; set; }
    public int TargetFps { get; set; } = 60;
    public int BitrateKbps { get; set; } = 12000;
    public int HttpPort { get; set; } = 8080;
    public int HttpsPort { get; set; } = 8443;
    public PreferredEncoder Encoder { get; set; } = PreferredEncoder.Auto;
    public bool AutoMatchResolution { get; set; } = true;
    public bool AllowPrimaryAutoResize { get; set; } = false;
    public string FfmpegPath { get; set; } = "ffmpeg";

    public static AppConfig Parse(string[] args)
    {
        var config = new AppConfig();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (arg.ToLowerInvariant())
            {
                case "--monitor":
                case "-m":
                    if (int.TryParse(Next(), out int m)) config.MonitorIndex = m;
                    break;
                case "--fps":
                case "-f":
                    if (int.TryParse(Next(), out int fps))
                        config.TargetFps = Math.Clamp(fps, 15, 120);
                    break;
                case "--bitrate":
                case "-b":
                    if (int.TryParse(Next(), out int kbps))
                        config.BitrateKbps = Math.Clamp(kbps, 1000, 80000);
                    break;
                case "--port":
                case "-p":
                    if (int.TryParse(Next(), out int port))
                        config.HttpPort = Math.Clamp(port, 1024, 65535);
                    break;
                case "--https-port":
                    if (int.TryParse(Next(), out int hport))
                        config.HttpsPort = Math.Clamp(hport, 1024, 65535);
                    break;
                case "--encoder":
                case "-e":
                    string? enc = Next()?.ToLowerInvariant();
                    config.Encoder = enc switch
                    {
                        "nvenc" => PreferredEncoder.Nvenc,
                        "x264" or "libx264" => PreferredEncoder.X264,
                        _ => PreferredEncoder.Auto
                    };
                    break;
                case "--no-auto-res":
                    config.AutoMatchResolution = false;
                    break;
                case "--allow-primary-resize":
                    config.AllowPrimaryAutoResize = true;
                    break;
                case "--ffmpeg":
                    string? path = Next();
                    if (!string.IsNullOrWhiteSpace(path)) config.FfmpegPath = path;
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }

        return config;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            PhoneDisplay.Host - Ultra-Low-Latency Android Second Monitor for Windows 11

            Usage:
              PhoneDisplay.Host [options]

            Options:
              -m, --monitor <index>     Select DXGI monitor index directly (skips prompt)
              -f, --fps <15-120>        Target capture/encode FPS (default: 60)
              -b, --bitrate <kbps>      Video bitrate in kbps (default: 12000)
              -p, --port <port>         HTTP port for USB ADB localhost (default: 8080)
                  --https-port <port>   HTTPS port for Wi-Fi LAN WebCodecs (default: 8443)
              -e, --encoder <mode>      auto | nvenc | x264 (default: auto)
                  --no-auto-res         Disable auto-matching secondary monitor resolution
                  --ffmpeg <path>       Path to ffmpeg.exe (default: ffmpeg in PATH)
              -h, --help                Show this help
            """);
    }
}
