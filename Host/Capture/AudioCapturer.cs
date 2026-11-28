using System.Buffers.Binary;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PhoneDisplay.Host.Capture;

public sealed record AudioDeviceInfo(string Id, string FriendlyName, bool IsDefault);

/// <summary>
/// Captures Windows system loopback audio via WASAPI and emits 16-bit Little-Endian PCM buffers.
/// </summary>
public sealed class AudioCapturer : IDisposable
{
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly string? _deviceId;
    private MMDevice? _device;
    private WasapiLoopbackCapture? _capture;
    private int _consecutiveSilentBuffers;
    private bool _disposed;

    public event Action<byte[], int, int, long>? OnAudioCaptured;

    public int SampleRate { get; private set; } = 48000;
    public int Channels { get; private set; } = 2;
    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }

    public AudioCapturer(string? deviceId = null)
    {
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
    }

    public static List<AudioDeviceInfo> EnumerateOutputDevices()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = def.ID;
            }
            catch { }

            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (dev)
                {
                    list.Add(new AudioDeviceInfo(
                        dev.ID,
                        dev.FriendlyName,
                        string.Equals(dev.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
            }
        }
        catch
        {
            // Ignore audio enumeration failure if no audio service/device exists
        }

        return list
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Start()
    {
        if (_disposed || IsRunning) return;

        try
        {
            WasapiLoopbackCapture capture;
            if (!string.IsNullOrWhiteSpace(_deviceId))
            {
                using var enumerator = new MMDeviceEnumerator();
                _device = enumerator.GetDevice(_deviceId);
                capture = new WasapiLoopbackCapture(_device);
            }
            else
            {
                capture = new WasapiLoopbackCapture();
            }

            WaveFormat wf = capture.WaveFormat;
            SampleRate = wf.SampleRate;
            Channels = wf.Channels >= 2 ? 2 : 1;

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();

            _capture = capture;
            IsRunning = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            IsRunning = false;
            LastError = ex.Message;
            try { _capture?.Dispose(); } catch { }
            _capture = null;
            try { _device?.Dispose(); } catch { }
            _device = null;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        IsRunning = false;
        if (e.Exception is not null)
            LastError = e.Exception.Message;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_disposed || e.BytesRecorded <= 0 || _capture is null)
            return;

        WaveFormat wf = _capture.WaveFormat;
        int srcChannels = Math.Max(1, wf.Channels);
        int dstChannels = Channels;
        int bytesPerSample = wf.BitsPerSample / 8;
        if (bytesPerSample <= 0) return;

        int blockAlign = wf.BlockAlign > 0 ? wf.BlockAlign : srcChannels * bytesPerSample;
        int frameCount = e.BytesRecorded / blockAlign;
        if (frameCount <= 0) return;

        bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (wf is WaveFormatExtensible wfe && wfe.SubFormat == SubtypeIeeeFloat) ||
                       (wf.Encoding == WaveFormatEncoding.Extensible && wf.BitsPerSample == 32);

        byte[] pcm16 = new byte[frameCount * dstChannels * 2];
        bool hasSound = false;
        ReadOnlySpan<byte> src = e.Buffer.AsSpan(0, e.BytesRecorded);
        Span<byte> dst = pcm16.AsSpan();

        if (isFloat && wf.BitsPerSample == 32)
        {
            for (int f = 0; f < frameCount; f++)
            {
                int frameOff = f * blockAlign;
                if (dstChannels == 1)
                {
                    float s0 = BitConverter.ToSingle(src.Slice(frameOff, 4));
                    short v = FloatToInt16(s0);
                    if (v != 0) hasSound = true;
                    BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(f * 2, 2), v);
                }
                else
                {
                    float fl = BitConverter.ToSingle(src.Slice(frameOff, 4));
                    float fr = srcChannels >= 2 ? BitConverter.ToSingle(src.Slice(frameOff + 4, 4)) : fl;
                    if (srcChannels >= 6)
                    {
                        // Mix center channel (ch2) into both L & R for 5.1/7.1 layouts
                        float fc = BitConverter.ToSingle(src.Slice(frameOff + 8, 4)) * 0.707f;
                        fl += fc;
                        fr += fc;
                    }
                    short vl = FloatToInt16(fl);
                    short vr = FloatToInt16(fr);
                    if (vl != 0 || vr != 0) hasSound = true;
                    BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(f * 4, 2), vl);
                    BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(f * 4 + 2, 2), vr);
                }
            }
        }
        else if (wf.BitsPerSample == 16)
        {
            for (int f = 0; f < frameCount; f++)
            {
                int frameOff = f * blockAlign;
                if (dstChannels == 1)
                {
                    short v = BinaryPrimitives.ReadInt16LittleEndian(src.Slice(frameOff, 2));
                    if (v != 0) hasSound = true;
                    BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(f * 2, 2), v);
                }
                else
                {
                    short vl = BinaryPrimitives.ReadInt16LittleEndian(src.Slice(frameOff, 2));
                    short vr = srcChannels >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(src.Slice(frameOff + 2, 2)) : vl;
                    if (vl != 0 || vr != 0) hasSound = true;
                    BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(f * 4, 2), vl);
                    BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(f * 4 + 2, 2), vr);
                }
            }
        }
        else if (wf.BitsPerSample == 24)
        {
            for (int f = 0; f < frameCount; f++)
            {
                int frameOff = f * blockAlign;
                if (dstChannels == 1)
                {
                    short v = ReadPcm24AsInt16(src.Slice(frameOff, 3));
                    if (v != 0) hasSound = true;
                    BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(f * 2, 2), v);
                }
                else
                {
                    short vl = ReadPcm24AsInt16(src.Slice(frameOff, 3));
                    short vr = srcChannels >= 2 ? ReadPcm24AsInt16(src.Slice(frameOff + 3, 3)) : vl;
                    if (vl != 0 || vr != 0) hasSound = true;
                    BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(f * 4, 2), vl);
                    BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(f * 4 + 2, 2), vr);
                }
            }
        }
        else if (wf.BitsPerSample == 32)
        {
            for (int f = 0; f < frameCount; f++)
            {
                int frameOff = f * blockAlign;
                if (dstChannels == 1)
                {
                    short v = (short)(BinaryPrimitives.ReadInt32LittleEndian(src.Slice(frameOff, 4)) >> 16);
                    if (v != 0) hasSound = true;
                    BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(f * 2, 2), v);
                }
                else
                {
                    short vl = (short)(BinaryPrimitives.ReadInt32LittleEndian(src.Slice(frameOff, 4)) >> 16);
                    short vr = srcChannels >= 2
                        ? (short)(BinaryPrimitives.ReadInt32LittleEndian(src.Slice(frameOff + 4, 4)) >> 16)
                        : vl;
                    if (vl != 0 || vr != 0) hasSound = true;
                    BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(f * 4, 2), vl);
                    BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(f * 4 + 2, 2), vr);
                }
            }
        }
        else
        {
            return;
        }

        if (hasSound)
        {
            _consecutiveSilentBuffers = 0;
        }
        else
        {
            _consecutiveSilentBuffers++;
            if (_consecutiveSilentBuffers > 2)
                return;
        }

        long timestampUs = Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;
        OnAudioCaptured?.Invoke(pcm16, SampleRate, Channels, timestampUs);
    }

    private static short FloatToInt16(float sample)
    {
        float clamped = Math.Clamp(sample, -1.0f, 1.0f);
        return (short)(clamped * 32767.0f);
    }

    private static short ReadPcm24AsInt16(ReadOnlySpan<byte> b)
    {
        int raw = b[0] | (b[1] << 8) | ((sbyte)b[2] << 16);
        return (short)(raw >> 8);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsRunning = false;

        if (_capture is not null)
        {
            try
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.StopRecording();
            }
            catch { }

            try { _capture.Dispose(); } catch { }
            _capture = null;
        }

        try { _device?.Dispose(); } catch { }
        _device = null;
    }
}
