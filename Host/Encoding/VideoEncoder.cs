using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using PhoneDisplay.Host.Models;

namespace PhoneDisplay.Host.Encoding;

public sealed class VideoEncoder : IDisposable
{
    private readonly string _ffmpegPath;
    private readonly PreferredEncoder _preferredEncoder;
    private readonly AnnexBParser _parser = new();
    private readonly object _lifecycleLock = new();
    private readonly object _stdinLock = new();

    private Process? _process;
    private Stream? _stdin;
    private CancellationTokenSource? _cts;
    private Task? _stdoutTask;
    private Task? _stderrTask;

    private readonly ConcurrentQueue<long> _submitTimestampsUs = new();
    private int _isWritingFrame;
    private bool _disposed;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Fps { get; private set; }
    public int BitrateKbps { get; private set; }
    public string ActiveEncoderName { get; private set; } = "initializing";
    public double LastEncodeLatencyMs { get; private set; }
    public double CurrentBitrateMbps { get; private set; }
    public string? LastError { get; private set; }

    private long _bytesSinceLastBitrateCalc;
    private long _lastBitrateCalcTimestamp = Stopwatch.GetTimestamp();

    public event Action<byte[], bool, long, uint>? OnEncodedFrame;

    public VideoEncoder(string ffmpegPath, PreferredEncoder preferredEncoder, int width, int height, int fps, int bitrateKbps)
    {
        _ffmpegPath = ResolveFfmpegPath(ffmpegPath);
        _preferredEncoder = preferredEncoder;
        Start(width, height, fps, bitrateKbps);
    }

    private static string ResolveFfmpegPath(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && configured != "ffmpeg" && File.Exists(configured))
            return configured;

        string[] localCandidates =
        [
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"),
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe"
        ];

        foreach (string c in localCandidates)
        {
            if (File.Exists(c)) return c;
        }

        string? wingetPkg = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetPkg))
        {
            var found = Directory.EnumerateFiles(wingetPkg, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null) return found;
        }

        return configured;
    }

    public void Reconfigure(int width, int height, int fps, int bitrateKbps)
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            if (width == Width && height == Height && fps == Fps && bitrateKbps == BitrateKbps && _process is { HasExited: false })
                return;

            StopInternal();
            StartInternal(width, height, fps, bitrateKbps);
        }
    }

    private int _framesSubmittedToCurrentProcess;

    public void ForceKeyframe()
    {
        // Restarting piped ffmpeg forces immediate SPS/PPS/IDR emission,
        // but skip if the current ffmpeg instance hasn't encoded any frame yet.
        lock (_lifecycleLock)
        {
            if (_disposed || _framesSubmittedToCurrentProcess == 0) return;
            int w = Width, h = Height, fps = Fps, kbps = BitrateKbps;
            StopInternal();
            StartInternal(w, h, fps, kbps);
        }
    }

    private void Start(int width, int height, int fps, int bitrateKbps)
    {
        lock (_lifecycleLock)
        {
            StartInternal(width, height, fps, bitrateKbps);
        }
    }

    private void StartInternal(int width, int height, int fps, int bitrateKbps)
    {
        Width = width;
        Height = height;
        Fps = fps;
        BitrateKbps = bitrateKbps;
        _parser.Reset();
        _submitTimestampsUs.Clear();
        _framesSubmittedToCurrentProcess = 0;
        LastError = null;

        bool tryNvenc = _preferredEncoder != PreferredEncoder.X264;
        if (tryNvenc && TryStartProcess("h264_nvenc", BuildNvencArgs(width, height, fps, bitrateKbps)))
        {
            ActiveEncoderName = "h264_nvenc (RTX Hardware)";
            return;
        }

        if (_preferredEncoder == PreferredEncoder.Nvenc)
        {
            throw new InvalidOperationException($"NVENC failed to start: {LastError ?? "unknown ffmpeg error"}");
        }

        if (TryStartProcess("libx264", BuildX264Args(width, height, fps, bitrateKbps)))
        {
            ActiveEncoderName = "libx264 (CPU Fallback)";
            return;
        }

        throw new InvalidOperationException(
            $"Failed to start ffmpeg ('{_ffmpegPath}'). Ensure FFmpeg is installed and in PATH. Details: {LastError}");
    }

    private bool TryStartProcess(string encoderLabel, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!proc.Start())
                return false;

            // Quick probe: wait 120ms to verify FFmpeg doesn't immediately fail (e.g., missing NVENC DLL)
            if (proc.WaitForExit(120))
            {
                LastError = proc.StandardError.ReadToEnd().Trim();
                proc.Dispose();
                return false;
            }

            _process = proc;
            _stdin = proc.StandardInput.BaseStream;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            _stdoutTask = Task.Run(() => ReadStdoutLoop(proc.StandardOutput.BaseStream, ct), ct);
            _stderrTask = Task.Run(() => ReadStderrLoop(proc.StandardError, encoderLabel, ct), ct);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    private static string BuildNvencArgs(int w, int h, int fps, int kbps)
    {
        int bufSize = Math.Max(kbps / 4, 500);
        int gop = fps * 2;
        return $"-hide_banner -loglevel warning -fflags nobuffer -flags low_delay " +
               $"-f rawvideo -pix_fmt bgra -s {w}x{h} -r {fps} -i pipe:0 " +
               $"-c:v h264_nvenc -preset p1 -tune ull -zerolatency 1 -profile:v baseline " +
               $"-pix_fmt yuv420p -rc cbr -b:v {kbps}k -maxrate {kbps}k -bufsize {bufSize}k " +
               $"-g {gop} -bf 0 -forced-idr 1 -f h264 pipe:1";
    }

    private static string BuildX264Args(int w, int h, int fps, int kbps)
    {
        int bufSize = Math.Max(kbps / 4, 500);
        int gop = fps * 2;
        return $"-hide_banner -loglevel warning -fflags nobuffer -flags low_delay " +
               $"-f rawvideo -pix_fmt bgra -s {w}x{h} -r {fps} -i pipe:0 " +
               $"-c:v libx264 -preset ultrafast -tune zerolatency -profile:v baseline " +
               $"-pix_fmt yuv420p -b:v {kbps}k -maxrate {kbps}k -bufsize {bufSize}k " +
               $"-x264-params \"keyint={gop}:min-keyint={fps}:scenecut=0:bframes=0:repeat-headers=1\" " +
               $"-f h264 pipe:1";
    }

    /// <summary>
    /// Submits a raw BGRA frame to FFmpeg stdin. Drops the frame if stdin is currently busy
    /// to prevent queue buildup and keep glass-to-glass latency minimal.
    /// </summary>
    public bool TrySubmitFrame(ReadOnlySpan<byte> bgraFrame)
    {
        if (_disposed) return false;

        if (_process is null || _process.HasExited)
        {
            if (_preferredEncoder == PreferredEncoder.Auto && ActiveEncoderName.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            {
                lock (_lifecycleLock)
                {
                    if (!_disposed && (_process is null || _process.HasExited))
                    {
                        StopInternal();
                        if (TryStartProcess("libx264", BuildX264Args(Width, Height, Fps, BitrateKbps)))
                        {
                            ActiveEncoderName = "libx264 (CPU Fallback)";
                        }
                    }
                }
            }

            if (_process is null || _process.HasExited || _stdin is null)
                return false;
        }

        if (_stdin is null) return false;
        if (bgraFrame.Length != Width * Height * 4) return false;

        if (Interlocked.CompareExchange(ref _isWritingFrame, 1, 0) != 0)
            return false;

        try
        {
            long nowUs = GetTimestampUs();
            lock (_stdinLock)
            {
                if (_disposed || _stdin is null || _process is null || _process.HasExited)
                    return false;

                _submitTimestampsUs.Enqueue(nowUs);
                _stdin.Write(bgraFrame);
                _stdin.Flush();
                Interlocked.Increment(ref _framesSubmittedToCurrentProcess);
            }
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _isWritingFrame, 0);
        }
    }

    private void ReadStdoutLoop(Stream stdout, CancellationToken ct)
    {
        byte[] readBuf = new byte[128 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = stdout.Read(readBuf, 0, readBuf.Length);
                if (read <= 0) break;

                _parser.Feed(readBuf.AsSpan(0, read), (nalUnit, isKeyframe) =>
                {
                    long nowUs = GetTimestampUs();
                    uint encodeUs = 0;
                    if (_submitTimestampsUs.TryDequeue(out long submitUs))
                    {
                        long diff = Math.Max(0, nowUs - submitUs);
                        encodeUs = (uint)Math.Min(diff, uint.MaxValue);
                        LastEncodeLatencyMs = diff / 1000.0;
                    }

                    UpdateBitrateStats(nalUnit.Length);
                    OnEncodedFrame?.Invoke(nalUnit, isKeyframe, nowUs, encodeUs);
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private void ReadStderrLoop(StreamReader stderr, string encoderLabel, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = stderr.ReadLine();
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line) &&
                    !line.Contains("No filtered frames for output stream", StringComparison.OrdinalIgnoreCase))
                {
                    LastError = $"[{encoderLabel}] {line}";
                }
            }
        }
        catch { }
    }

    private void UpdateBitrateStats(int bytesAdded)
    {
        _bytesSinceLastBitrateCalc += bytesAdded;
        long now = Stopwatch.GetTimestamp();
        double elapsedSec = (now - _lastBitrateCalcTimestamp) / (double)Stopwatch.Frequency;
        if (elapsedSec >= 1.0)
        {
            CurrentBitrateMbps = (_bytesSinceLastBitrateCalc * 8.0) / (elapsedSec * 1_000_000.0);
            _bytesSinceLastBitrateCalc = 0;
            _lastBitrateCalcTimestamp = now;
        }
    }

    private static long GetTimestampUs()
    {
        return (long)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));
    }

    private void StopInternal()
    {
        try { _cts?.Cancel(); } catch { }
        lock (_stdinLock)
        {
            try { _stdin?.Close(); } catch { }
            _stdin = null;
        }
        try
        {
            if (_process is { HasExited: false })
            {
                if (!_process.WaitForExit(400))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch { }

        _process?.Dispose();
        _process = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            StopInternal();
        }
    }
}
