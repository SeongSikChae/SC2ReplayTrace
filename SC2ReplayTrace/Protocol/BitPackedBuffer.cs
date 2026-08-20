namespace Biz.Bizadm.SC2ReplayTrace.Protocol;

/// <summary>비트 단위로 데이터를 읽는 버퍼입니다.</summary>
public sealed class BitPackedBuffer
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _offset;
    private int _next;
    private int _nextBits;

    /// <summary>지정한 데이터를 사용해 버퍼를 만듭니다.</summary>
    /// <param name="data">읽을 데이터입니다.</param>
    public BitPackedBuffer(ReadOnlyMemory<byte> data) => _data = data;

    /// <summary>현재까지 사용한 비트 수입니다.</summary>
    public int UsedBits => _offset * 8 - _nextBits;

    /// <summary>현재 읽기 위치의 비트 오프셋입니다.</summary>
    public int BitPosition => UsedBits;

    /// <summary>아직 읽지 않은 비트 수입니다.</summary>
    public int RemainingBits => checked(_data.Length * 8 - UsedBits);

    /// <summary>읽을 데이터가 모두 소비되었는지 나타냅니다.</summary>
    public bool Done => _nextBits == 0 && _offset >= _data.Length;

    /// <summary>다음 바이트 경계로 이동합니다.</summary>
    public void Align() => _nextBits = 0;

    /// <summary>지정한 비트 수를 읽습니다.</summary>
    /// <param name="count">읽을 비트 수입니다.</param>
    /// <returns>읽은 값입니다.</returns>
    public int ReadBits(int count)
    {
        if (count is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(count));
        uint result = 0;
        var read = 0;
        while (read < count)
        {
            if (_nextBits == 0)
            {
                if (_offset >= _data.Length) throw new InvalidDataException("비트 스트림이 잘렸습니다.");
                _next = _data.Span[_offset++];
                _nextBits = 8;
            }

            var take = Math.Min(count - read, _nextBits);
            var mask = take == 32 ? uint.MaxValue : (uint)((1 << take) - 1);
            result |= (uint)(_next & mask) << (count - read - take);
            _next >>= take;
            _nextBits -= take;
            read += take;
        }

        return unchecked((int)result);
    }

    /// <summary>바이트 경계에서 지정한 바이트 수를 읽습니다.</summary>
    /// <param name="count">읽을 바이트 수입니다.</param>
    /// <returns>읽은 바이트입니다.</returns>
    public byte[] ReadAlignedBytes(int count)
    {
        Align();
        if (count < 0 || _offset + count > _data.Length)
            throw new InvalidDataException("바이트 스트림이 잘렸습니다.");
        var bytes = _data.Slice(_offset, count).ToArray();
        _offset += count;
        return bytes;
    }
}
