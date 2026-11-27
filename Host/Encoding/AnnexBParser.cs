using System.IO;

namespace PhoneDisplay.Host.Encoding;

/// <summary>
/// Stateful H.264 Annex-B byte-stream parser that splits incoming chunks into NAL units,
/// caches SPS (7) and PPS (8), and groups [SPS + PPS + IDR] into a single keyframe access unit
/// so WebCodecs VideoDecoder can decode immediately without extra buffering.
/// </summary>
public sealed class AnnexBParser
{
    private byte[] _buffer = new byte[512 * 1024];
    private int _length;
    private byte[]? _sps;
    private byte[]? _pps;
    private readonly MemoryStream _auStream = new(256 * 1024);

    public void Feed(ReadOnlySpan<byte> data, Action<byte[], bool> onAccessUnit)
    {
        EnsureCapacity(_length + data.Length);
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;

        int searchFrom = 0;
        while (true)
        {
            int firstStart = FindStartCode(_buffer, searchFrom, _length, out int firstPrefixLen);
            if (firstStart < 0)
            {
                if (_length > 4)
                {
                    Buffer.BlockCopy(_buffer, _length - 4, _buffer, 0, 4);
                    _length = 4;
                }
                return;
            }

            int nextStart = FindStartCode(_buffer, firstStart + firstPrefixLen, _length, out _);
            if (nextStart < 0)
            {
                // Shift incomplete NAL unit to front
                if (firstStart > 0)
                {
                    int remaining = _length - firstStart;
                    Buffer.BlockCopy(_buffer, firstStart, _buffer, 0, remaining);
                    _length = remaining;
                }
                return;
            }

            int nalBodyStart = firstStart + firstPrefixLen;
            int nalBodyLen = nextStart - nalBodyStart;
            if (nalBodyLen > 0)
            {
                ProcessNalUnit(_buffer.AsSpan(firstStart, nextStart - firstStart), _buffer[nalBodyStart], onAccessUnit);
            }

            searchFrom = nextStart;
            if (searchFrom >= _length - 3)
            {
                int remaining = _length - searchFrom;
                if (remaining > 0)
                    Buffer.BlockCopy(_buffer, searchFrom, _buffer, 0, remaining);
                _length = remaining;
                return;
            }
        }
    }

    private void ProcessNalUnit(ReadOnlySpan<byte> fullNalWithStartCode, byte headerByte, Action<byte[], bool> onAccessUnit)
    {
        int nalType = headerByte & 0x1F;

        switch (nalType)
        {
            case 7: // SPS
                _sps = fullNalWithStartCode.ToArray();
                break;
            case 8: // PPS
                _pps = fullNalWithStartCode.ToArray();
                break;
            case 5: // IDR Keyframe
                _auStream.SetLength(0);
                if (_sps is not null) _auStream.Write(_sps);
                if (_pps is not null) _auStream.Write(_pps);
                _auStream.Write(fullNalWithStartCode);
                onAccessUnit(_auStream.ToArray(), true);
                break;
            case 1: // Non-IDR Slice
                onAccessUnit(fullNalWithStartCode.ToArray(), false);
                break;
            // Ignore AUD (9), SEI (6), filler (12) to minimize overhead
        }
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buffer.Length) return;
        int newCap = Math.Max(_buffer.Length * 2, needed);
        Array.Resize(ref _buffer, newCap);
    }

    private static int FindStartCode(byte[] buf, int start, int end, out int prefixLength)
    {
        for (int i = start; i <= end - 3; i++)
        {
            if (buf[i] == 0 && buf[i + 1] == 0)
            {
                if (buf[i + 2] == 1)
                {
                    prefixLength = 3;
                    return i;
                }
                if (i <= end - 4 && buf[i + 2] == 0 && buf[i + 3] == 1)
                {
                    prefixLength = 4;
                    return i;
                }
            }
        }
        prefixLength = 0;
        return -1;
    }

    public void Reset()
    {
        _length = 0;
        _sps = null;
        _pps = null;
        _auStream.SetLength(0);
    }
}
