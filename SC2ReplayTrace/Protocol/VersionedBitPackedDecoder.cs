namespace Biz.Bizadm.SC2ReplayTrace.Protocol;

/// <summary>Blizzard s2protocol의 버전 스트림 primitive를 구현합니다.</summary>
public sealed class VersionedBitPackedDecoder
{
    private readonly BitPackedBuffer _buffer;

    /// <summary>지정한 버전 스트림을 디코더에 연결합니다.</summary>
    /// <param name="contents">디코드할 내용입니다.</param>
    public VersionedBitPackedDecoder(ReadOnlyMemory<byte> contents) =>
        _buffer = new BitPackedBuffer(contents);

    /// <summary>스트림이 끝났는지 나타냅니다.</summary>
    public bool Done => _buffer.Done;
    /// <summary>현재까지 사용한 비트 수입니다.</summary>
    public int UsedBits => _buffer.UsedBits;
    /// <summary>현재 읽기 위치의 비트 오프셋입니다.</summary>
    public int BitPosition => _buffer.BitPosition;

    /// <summary>다음 바이트 경계로 정렬합니다.</summary>
    public void Align() => _buffer.Align();

    /// <summary>가변 길이 정수를 읽습니다.</summary>
    public int ReadVInt()
    {
        long result = 0;
        var bits = 0;
        while (true)
        {
            var part = _buffer.ReadBits(8);
            result |= (long)(part & 0x7F) << bits;
            bits += 7;
            if ((part & 0x80) == 0) break;
            if (bits >= 64) throw new InvalidDataException("vint가 너무 큽니다.");
        }
        result = (result & 1) != 0 ? -(result >> 1) : (result >> 1);
        return unchecked((int)result);
    }

    /// <summary>versioned int 인스턴스를 읽습니다(marker 9).</summary>
    public int ReadInt() { Expect(9); return ReadVInt(); }

    /// <summary>versioned array 길이를 읽습니다(marker 0).</summary>
    public int ReadArrayLength() { Expect(0); return ReadVInt(); }

    /// <summary>versioned struct 필드 수를 읽습니다(marker 5).</summary>
    public int ReadStructFieldCount() { Expect(5); return ReadVInt(); }

    /// <summary>versioned struct 필드 태그를 읽습니다.</summary>
    public int ReadStructFieldTag() => ReadVInt();

    /// <summary>versioned choice 태그를 읽습니다(marker 3).</summary>
    public int ReadChoiceTag() { Expect(3); return ReadVInt(); }

    /// <summary>versioned optional 존재 플래그를 읽습니다(marker 4).</summary>
    public bool ReadOptionalExists() { Expect(4); return _buffer.ReadBits(8) != 0; }

    /// <summary>versioned u32 인스턴스를 읽습니다(marker 7).</summary>
    public uint ReadUInt32() { Expect(7); return BitConverter.ToUInt32(_buffer.ReadAlignedBytes(4)); }

    /// <summary>versioned u64 인스턴스를 읽습니다(marker 8).</summary>
    public ulong ReadUInt64() { Expect(8); return BitConverter.ToUInt64(_buffer.ReadAlignedBytes(8)); }

    /// <summary>길이 접두사가 있는 바이트 배열을 읽습니다.</summary>
    public byte[] ReadBlob()
    {
        Expect(2);
        return _buffer.ReadAlignedBytes(ReadVInt());
    }

    /// <summary>비트 배열을 읽습니다.</summary>
    public byte[] ReadBitArray()
    {
        Expect(1);
        var bits = ReadVInt();
        return _buffer.ReadAlignedBytes((bits + 7) / 8);
    }

    /// <summary>FourCC 문자열을 읽습니다.</summary>
    public string ReadFourCc()
    {
        Expect(7);
        return System.Text.Encoding.ASCII.GetString(_buffer.ReadAlignedBytes(4));
    }

    /// <summary>불리언 값을 읽습니다.</summary>
    public bool ReadBool()
    {
        Expect(6);
        return _buffer.ReadBits(8) != 0;
    }

    /// <summary>현재 인스턴스를 읽지 않고 건너뜁니다.</summary>
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
        var actual = _buffer.ReadBits(8);
        if (actual != marker)
            throw new InvalidDataException($"s2protocol 버전 스트림 marker가 일치하지 않습니다. expected={marker} actual={actual} bitPos={_buffer.BitPosition}");
    }
}
