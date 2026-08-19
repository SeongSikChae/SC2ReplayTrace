namespace Biz.Bizadm.SC2ReplayTrace.Protocol;

/// <summary>Blizzard s2protocol의 버전 스트림 primitive를 구현합니다.</summary>
public sealed class VersionedBitPackedDecoder
{
    private readonly BitPackedBuffer _buffer;

    public VersionedBitPackedDecoder(ReadOnlyMemory<byte> contents) =>
        _buffer = new BitPackedBuffer(contents);

    public bool Done => _buffer.Done;
    public int UsedBits => _buffer.UsedBits;

    public int ReadVInt()
    {
        var first = _buffer.ReadBits(8);
        var negative = (first & 1) != 0;
        var result = (long)((first >> 1) & 0x3F);
        var bits = 6;
        while ((first & 0x80) != 0)
        {
            first = _buffer.ReadBits(8);
            result |= (long)(first & 0x7F) << bits;
            bits += 7;
            if (bits > 63) throw new InvalidDataException("vint가 너무 큽니다.");
        }
        return checked((int)(negative ? -result : result));
    }

    public byte[] ReadBlob()
    {
        Expect(2);
        return _buffer.ReadAlignedBytes(ReadVInt());
    }

    public byte[] ReadBitArray()
    {
        Expect(1);
        var bits = ReadVInt();
        return _buffer.ReadAlignedBytes((bits + 7) / 8);
    }

    public string ReadFourCc()
    {
        Expect(7);
        return System.Text.Encoding.ASCII.GetString(_buffer.ReadAlignedBytes(4));
    }

    public bool ReadBool()
    {
        Expect(6);
        return _buffer.ReadBits(8) != 0;
    }

    public void SkipInstance()
    {
        switch (_buffer.ReadBits(8))
        {
            case 0:
                for (var i = ReadVInt(); i > 0; i--) SkipInstance();
                break;
            case 1: _ = _buffer.ReadAlignedBytes((ReadVInt() + 7) / 8); break;
            case 2: _ = _buffer.ReadAlignedBytes(ReadVInt()); break;
            case 3: _ = ReadVInt(); SkipInstance(); break;
            case 4: if (_buffer.ReadBits(8) != 0) SkipInstance(); break;
            case 5:
                for (var i = ReadVInt(); i > 0; i--) { _ = ReadVInt(); SkipInstance(); }
                break;
            case 6: _ = _buffer.ReadAlignedBytes(1); break;
            case 7: _ = _buffer.ReadAlignedBytes(4); break;
            case 8: _ = _buffer.ReadAlignedBytes(8); break;
            case 9: _ = ReadVInt(); break;
            default: throw new InvalidDataException("알 수 없는 s2protocol 인스턴스 형식입니다.");
        }
    }

    private void Expect(int marker)
    {
        if (_buffer.ReadBits(8) != marker)
            throw new InvalidDataException("s2protocol 버전 스트림 marker가 일치하지 않습니다.");
    }
}
