using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Biz.Bizadm.SC2ReplayTrace.Mpq;

public sealed class MpqArchive : IDisposable
{
    private const uint MpqSignature = 0x1A51504D;
    private const uint Exists = 0x80000000;
    private const uint SingleUnit = 0x01000000;
    private const uint Compressed = 0x00000200;
    private const uint Imploded = 0x00000100;
    private const uint SectorCrc = 0x04000000;
    private readonly Stream _stream;
    private readonly MpqHash[] _hashes;
    private readonly MpqBlock[] _blocks;
    private readonly uint _sectorSize;
    private readonly long _archiveOffset;
    private bool _disposed;

    public byte[]? UserData { get; }

    public MpqArchive(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("MPQ 스트림은 읽기 및 검색이 가능해야 합니다.", nameof(stream));
        _stream = stream;
        UserData = ReadUserData(stream);
        var headerOffset = FindHeader(stream);
        _archiveOffset = headerOffset;
        stream.Position = headerOffset + 4;
        var headerSize = ReadUInt32();
        var archiveSize = ReadUInt32();
        var formatVersion = ReadUInt16();
        var blockSizeShift = ReadUInt16();
        var hashOffsetLow = ReadUInt32();
        var blockOffsetLow = ReadUInt32();
        var hashCount = ReadUInt32();
        var blockCount = ReadUInt32();
        if (formatVersion > 3) throw new NotSupportedException($"지원하지 않는 MPQ 포맷 버전입니다: {formatVersion}");
        var hashOffset = (ulong)hashOffsetLow;
        var blockOffset = (ulong)blockOffsetLow;
        if (formatVersion == 1)
        {
            _stream.Position = headerOffset + 40;
            var hashOffsetHigh = ReadUInt16();
            var blockOffsetHigh = ReadUInt16();
            hashOffset |= (ulong)hashOffsetHigh << 32;
            blockOffset |= (ulong)blockOffsetHigh << 32;
        }
        _sectorSize = 512u << blockSizeShift;
        _hashes = ReadHashes(headerOffset + (long)hashOffset, hashCount);
        _blocks = ReadBlocks(headerOffset + (long)blockOffset, blockCount);
        _ = headerSize;
        _ = archiveSize;
    }

    public byte[] ReadFile(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var hash = FindHash(name);
        if (hash.BlockIndex == uint.MaxValue) throw new FileNotFoundException("MPQ 내부 파일을 찾을 수 없습니다.", name);
        var block = _blocks[hash.BlockIndex];
        if ((block.Flags & Exists) == 0) throw new InvalidDataException("MPQ 블록이 존재하지 않습니다.");
        if (block.CompressedSize == 0) return [];
        _stream.Position = _archiveOffset + block.Offset;
        var data = new byte[block.CompressedSize];
        ReadExactly(data);
        if ((block.Flags & SingleUnit) != 0) return Decompress(data, block.Flags, (int)block.Size);

        var sectorCount = (int)(block.Size / _sectorSize) + 2 + ((block.Flags & SectorCrc) != 0 ? 1 : 0);
        var offsets = new uint[sectorCount];
        if (offsets.Length * 4 > data.Length)
            throw new InvalidDataException($"MPQ 섹터 오프셋 테이블이 잘렸습니다: {name}, 필요={offsets.Length * 4}, 실제={data.Length}");
        for (var i = 0; i < offsets.Length; i++)
            offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4));
        using var output = new MemoryStream((int)block.Size);
        var dataSectorCount = offsets.Length - 1 - ((block.Flags & SectorCrc) != 0 ? 1 : 0);
        for (var i = 0; i < dataSectorCount; i++)
        {
            var start = (int)offsets[i];
            var length = (int)offsets[i + 1] - start;
            if (length <= 0) continue;
            if (start < 0 || length > data.Length - start)
                throw new InvalidDataException($"MPQ 섹터 범위가 올바르지 않습니다: {name}, 시작={start}, 길이={length}, 실제={data.Length}");
            output.Write(Decompress(data.AsSpan(start, length).ToArray(), block.Flags, (int)_sectorSize));
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private MpqHash FindHash(string name)
    {
        var start = Hash(name, 0) % (uint)_hashes.Length;
        for (var i = 0; i < _hashes.Length; i++)
        {
            var hash = _hashes[(start + (uint)i) % (uint)_hashes.Length];
            if (hash.BlockIndex == uint.MaxValue) continue;
            if (hash.Name1 == Hash(name, 1) && hash.Name2 == Hash(name, 2)) return hash;
        }
        return new MpqHash(0, 0, 0, uint.MaxValue);
    }

    private byte[] Decompress(byte[] data, uint flags, int expected)
    {
        if ((flags & (Compressed | Imploded)) == 0) return data;
        if ((flags & Imploded) != 0) throw new NotSupportedException("MPQ implode 압축은 지원하지 않습니다.");
        if (data.Length == 0) return [];
        var compressionType = data[0];
        if (compressionType == 0) return data[1..];
        if ((compressionType & 0x10) != 0)
            return BZip2Decoder.Decode(data[1..]);
        if (compressionType != 2)
            throw new NotSupportedException($"MPQ 압축 방식이 지원되지 않습니다: {compressionType}");
        using var input = new MemoryStream(data, 1, data.Length - 1, writable: false);
        using var deflate = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(expected > 0 ? expected : data.Length * 2);
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private MpqHash[] ReadHashes(long offset, uint count)
    {
        _stream.Position = offset;
        var bytes = new byte[count * 16];
        ReadExactly(bytes);
        Decrypt(bytes, "(hash table)");
        var result = new MpqHash[count];
        for (var i = 0; i < count; i++)
        {
            var span = bytes.AsSpan(i * 16, 16);
            result[i] = new MpqHash(
                BinaryPrimitives.ReadUInt32LittleEndian(span),
                BinaryPrimitives.ReadUInt32LittleEndian(span[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[12..]));
        }
        return result;
    }

    private MpqBlock[] ReadBlocks(long offset, uint count)
    {
        _stream.Position = offset;
        var bytes = new byte[count * 16];
        ReadExactly(bytes);
        Decrypt(bytes, "(block table)");
        var result = new MpqBlock[count];
        for (var i = 0; i < count; i++)
        {
            var span = bytes.AsSpan(i * 16, 16);
            result[i] = new MpqBlock(
                BinaryPrimitives.ReadUInt32LittleEndian(span),
                BinaryPrimitives.ReadUInt32LittleEndian(span[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[12..]));
        }
        return result;
    }

    private void Decrypt(byte[] data, string keyText)
    {
        var key = Hash(keyText, 3);
        var seed = 0xEEEEEEEEu;
        for (var i = 0; i < data.Length / 4; i++)
        {
            seed += CryptTable[0x400 + (key & 0xFF)];
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4));
            value ^= key + seed;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), value);
            key = ((~key << 21) + 0x11111111) | (key >> 11);
            seed = value + seed + (seed << 5) + 3;
        }
    }

    private static long FindHeader(Stream stream)
    {
        stream.Position = 0;
        Span<byte> buffer = stackalloc byte[4];
        for (long position = 0; position <= stream.Length - 4; position += 512)
        {
            stream.Position = position;
            if (stream.Read(buffer) == 4 && BinaryPrimitives.ReadUInt32LittleEndian(buffer) == MpqSignature)
                return position;
        }
        throw new InvalidDataException("MPQ 헤더를 찾을 수 없습니다.");
    }

    private static byte[]? ReadUserData(Stream stream)
    {
        stream.Position = 0;
        Span<byte> header = stackalloc byte[16];
        if (stream.Read(header) != header.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x1B51504D)
            return null;

        var size = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (size == 0 || size > stream.Length - 16) return null;
        var content = new byte[size];
        stream.Position = 16;
        stream.ReadExactly(content);
        return content;
    }

    private ushort ReadUInt16() { Span<byte> b = stackalloc byte[2]; ReadExactly(b); return BinaryPrimitives.ReadUInt16LittleEndian(b); }
    private uint ReadUInt32() { Span<byte> b = stackalloc byte[4]; ReadExactly(b); return BinaryPrimitives.ReadUInt32LittleEndian(b); }
    private ushort ReadUInt16At(long position) { _stream.Position = position; return ReadUInt16(); }
    private void ReadExactly(Span<byte> buffer) { while (!buffer.IsEmpty) { var n = _stream.Read(buffer); if (n == 0) throw new EndOfStreamException(); buffer = buffer[n..]; } }
    private void ReadExactly(byte[] buffer) => ReadExactly(buffer.AsSpan());

    private static uint[] CryptTable { get; } = BuildCryptTable();
    private static uint[] BuildCryptTable()
    {
        var table = new uint[0x500];
        var seed = 0x00100001u;
        for (var index1 = 0; index1 < 0x100; index1++)
            for (var index2 = index1; index2 < 0x500; index2 += 0x100)
            {
                seed = (seed * 125 + 3) % 0x2AAAAB;
                var high = (seed & 0xFFFF) << 16;
                seed = (seed * 125 + 3) % 0x2AAAAB;
                table[index2] = high | (seed & 0xFFFF);
            }
        return table;
    }

    private static uint Hash(string text, uint type)
    {
        var seed1 = 0x7FED7FEDu;
        var seed2 = 0xEEEEEEEEu;
        foreach (var character in Encoding.ASCII.GetBytes(text.ToUpperInvariant()))
        {
            var value = (byte)character;
            seed1 = CryptTable[(type << 8) + value] ^ (seed1 + seed2);
            seed2 = value + seed1 + seed2 + (seed2 << 5) + 3;
        }
        return seed1;
    }

    private readonly record struct MpqHash(uint Name1, uint Name2, uint Locale, uint BlockIndex);
    private readonly record struct MpqBlock(uint Offset, uint CompressedSize, uint Size, uint Flags);
}
