namespace Biz.Bizadm.SC2ReplayTrace.Protocol;

public sealed class BitPackedBuffer
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _offset;
    private int _next;
    private int _nextBits;

    public BitPackedBuffer(ReadOnlyMemory<byte> data) => _data = data;

    public int UsedBits => _offset * 8 - _nextBits;

    public bool Done => _nextBits == 0 && _offset >= _data.Length;

    public void Align() => _nextBits = 0;

    public int ReadBits(int count)
    {
        if (count is < 0 or > 31) throw new ArgumentOutOfRangeException(nameof(count));
        var result = 0;
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
            result |= (_next & ((1 << take) - 1)) << (count - read - take);
            _next >>= take;
            _nextBits -= take;
            read += take;
        }

        return result;
    }

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
