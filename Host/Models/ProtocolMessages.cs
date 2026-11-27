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
