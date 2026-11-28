using System.Buffers.Binary;
using System.Text.Json.Serialization;

namespace PhoneDisplay.Host.Models;

public sealed class ClientMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("dpr")]
    public double Dpr { get; set; } = 1.0;

    [JsonPropertyName("refreshRate")]
    public int RefreshRate { get; set; } = 60;

    [JsonPropertyName("fps")]
    public int? Fps { get; set; }

    [JsonPropertyName("bitrateKbps")]
    public int? BitrateKbps { get; set; }

    [JsonPropertyName("audioEnabled")]
    public bool? AudioEnabled { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("ts")]
    public long TimestampMs { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("dx")]
    public double Dx { get; set; }

    [JsonPropertyName("dy")]
    public double Dy { get; set; }

    [JsonPropertyName("wheelX")]
    public double WheelX { get; set; }

    [JsonPropertyName("wheelY")]
    public double WheelY { get; set; }

    [JsonPropertyName("button")]
    public string? Button { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

public sealed class ServerInfoMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "info";

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("fps")]
    public int Fps { get; set; }

    [JsonPropertyName("bitrateKbps")]
    public int BitrateKbps { get; set; }

    [JsonPropertyName("encoder")]
    public string Encoder { get; set; } = string.Empty;

    [JsonPropertyName("codec")]
    public string Codec { get; set; } = "avc1.42E033";

    [JsonPropertyName("audioEnabled")]
    public bool AudioEnabled { get; set; } = true;

    [JsonPropertyName("audioSampleRate")]
    public int AudioSampleRate { get; set; } = 48000;

    [JsonPropertyName("audioChannels")]
    public int AudioChannels { get; set; } = 2;

    [JsonPropertyName("videoEnabled")]
    public bool VideoEnabled { get; set; } = true;

    [JsonPropertyName("allowInput")]
    public bool AllowInput { get; set; } = true;

    [JsonPropertyName("sourceLabel")]
    public string SourceLabel { get; set; } = string.Empty;
}

public sealed class ServerPongMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "pong";

    [JsonPropertyName("ts")]
    public long TimestampMs { get; set; }
}

public static class VideoPacketBuilder
{
    /// <summary>
    /// Binary packet layout (13-byte header + Annex-B H.264 payload):
    ///   [0]     byte   frameType (1 = Keyframe IDR+SPS+PPS, 0 = Delta P-frame)
    ///   [1..8]  int64  timestampUs (big-endian)
    ///   [9..12] uint32 encodeLatencyUs (big-endian)
    ///   [13..]  bytes  H.264 Annex-B NAL unit(s)
    /// </summary>
    public static byte[] Build(bool isKeyframe, long timestampUs, uint encodeLatencyUs, ReadOnlySpan<byte> nalData)
    {
        byte[] packet = new byte[13 + nalData.Length];
        packet[0] = isKeyframe ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(1, 8), timestampUs);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(9, 4), encodeLatencyUs);
        nalData.CopyTo(packet.AsSpan(13));
        return packet;
    }
}

public static class AudioPacketBuilder
{
    /// <summary>
    /// Binary packet layout (14-byte header + interleaved Int16 LE PCM payload):
    ///   [0]     byte   packetType (2 = PCM16 audio)
    ///   [1..4]  uint32 sampleRate (big-endian)
    ///   [5]     byte   channels (1 or 2)
    ///   [6..13] int64  timestampUs (big-endian)
    ///   [14..]  bytes  Interleaved Int16 LE PCM frames
    /// </summary>
    public static byte[] Build(int sampleRate, int channels, long timestampUs, ReadOnlySpan<byte> pcm16Data)
    {
        byte[] packet = new byte[14 + pcm16Data.Length];
        packet[0] = 2;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1, 4), (uint)sampleRate);
        packet[5] = (byte)Math.Clamp(channels, 1, 2);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(6, 8), timestampUs);
        pcm16Data.CopyTo(packet.AsSpan(14));
        return packet;
    }
}
