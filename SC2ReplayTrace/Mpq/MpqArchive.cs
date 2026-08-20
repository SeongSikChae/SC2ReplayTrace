using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ICSharpCode.SharpZipLib.BZip2;

namespace Biz.Bizadm.SC2ReplayTrace.Mpq;

/// <summary>MPQ 아카이브에서 파일을 읽습니다.</summary>
public sealed class MpqArchive : IDisposable
{
    private const uint MpqSignature = 0x1A51504D;
    private const uint Exists = 0x80000000;
    private const uint SingleUnit = 0x01000000;
    private const uint Compressed = 0x00000200;
    private const uint Imploded = 0x00000100;
    private const uint SectorCrc = 0x04000000;
    private const uint Encrypted = 0x00010000;
    private readonly Stream _stream;
    private readonly MpqHash[] _hashes;
    private readonly MpqBlock[] _blocks;
    private readonly uint _sectorSize;
    private readonly long _archiveOffset;
    private bool _disposed;

    /// <summary>아카이브 앞부분의 사용자 데이터입니다.</summary>
    public byte[]? UserData { get; }

    /// <summary>지정한 스트림에서 MPQ 아카이브를 엽니다.</summary>
    /// <param name="stream">읽을 MPQ 스트림입니다.</param>
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

    /// <summary>아카이브 내부 파일을 읽습니다.</summary>
    /// <param name="name">내부 파일 이름입니다.</param>
    /// <returns>압축 해제된 파일 내용입니다.</returns>
    public byte[] ReadFile(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var hash = FindHash(name);
        if (hash.BlockIndex == uint.MaxValue) throw new FileNotFoundException("MPQ 내부 파일을 찾을 수 없습니다.", name);
        var block = _blocks[hash.BlockIndex];
        if ((block.Flags & Exists) == 0) throw new InvalidDataException("MPQ 블록이 존재하지 않습니다.");
        if (block.CompressedSize == 0) return [];
        if ((block.Flags & Encrypted) != 0)
            throw new NotSupportedException($"MPQ 암호화 블록은 아직 지원되지 않습니다: {name}");
        _stream.Position = _archiveOffset + block.Offset;
        var data = new byte[block.CompressedSize];
        ReadExactly(data);
        if ((block.Flags & SingleUnit) != 0)
        {
            var shouldDecompress = (block.Flags & Compressed) != 0 && block.Size > block.CompressedSize;
            return shouldDecompress ? Decompress(data, block.Flags, (int)block.Size) : data;
        }

        var dataSectorCount = checked((int)((block.Size + _sectorSize - 1) / _sectorSize));
        var tableEntryCount = dataSectorCount + 1 + ((block.Flags & SectorCrc) != 0 ? 1 : 0);
        var offsets = new uint[tableEntryCount];
        if (offsets.Length * 4 > data.Length)
            throw new InvalidDataException($"MPQ 섹터 오프셋 테이블이 잘렸습니다: {name}, 필요={offsets.Length * 4}, 실제={data.Length}");
        for (var i = 0; i < offsets.Length; i++)
            offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4));
        using var output = new MemoryStream((int)block.Size);
        for (var i = 0; i < dataSectorCount; i++)
        {
            var start = (int)offsets[i];
            var length = (int)offsets[i + 1] - start;
            if (length <= 0) continue;
            if (start < 0 || length > data.Length - start)
                throw new InvalidDataException($"MPQ 섹터 범위가 올바르지 않습니다: {name}, 시작={start}, 길이={length}, 실제={data.Length}");
            var sector = data.AsSpan(start, length).ToArray();
            var shouldDecompress = (block.Flags & Compressed) != 0 && ((int)block.Size - (int)output.Length) > sector.Length;
            if (shouldDecompress)
                sector = Decompress(sector, block.Flags, (int)_sectorSize);
            var remaining = (int)block.Size - (int)output.Length;
            if (remaining <= 0) break;
            if (sector.Length > remaining)
                output.Write(sector, 0, remaining);
            else
                output.Write(sector);
        }
        return output.Length == block.Size ? output.ToArray() : output.ToArray().AsSpan(0, (int)Math.Min(output.Length, block.Size)).ToArray();
    }

    /// <summary>디버깅용으로 내부 파일의 MPQ 블록 플래그를 조회합니다.</summary>
    public uint GetBlockFlags(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var hash = FindHash(name);
        if (hash.BlockIndex == uint.MaxValue) throw new FileNotFoundException("MPQ 내부 파일을 찾을 수 없습니다.", name);
        return _blocks[hash.BlockIndex].Flags;
    }

    /// <summary>디버깅용으로 섹터별 압축 타입 바이트를 조회합니다.</summary>
    public byte[] GetSectorCompressionTypes(string name, int maxSectors = 8)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var hash = FindHash(name);
        if (hash.BlockIndex == uint.MaxValue) throw new FileNotFoundException("MPQ 내부 파일을 찾을 수 없습니다.", name);
        var block = _blocks[hash.BlockIndex];
        _stream.Position = _archiveOffset + block.Offset;
        var data = new byte[block.CompressedSize];
        ReadExactly(data);
        if ((block.Flags & SingleUnit) != 0)
            return data.Length == 0 ? [] : [data[0]];

        var dataSectorCount = checked((int)((block.Size + _sectorSize - 1) / _sectorSize));
        var tableEntryCount = dataSectorCount + 1 + ((block.Flags & SectorCrc) != 0 ? 1 : 0);
        var offsets = new uint[tableEntryCount];
        for (var i = 0; i < offsets.Length; i++)
            offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4));

        var result = new List<byte>(Math.Min(dataSectorCount, maxSectors));
        for (var i = 0; i < dataSectorCount && i < maxSectors; i++)
        {
            var start = (int)offsets[i];
            var length = (int)offsets[i + 1] - start;
            if (length > 0 && start >= 0 && start < data.Length)
                result.Add(data[start]);
        }
        return result.ToArray();
    }

    /// <summary>아카이브를 폐기합니다.</summary>
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

    private static byte[] Decompress(byte[] data, uint flags, int expected)
    {
        if ((flags & (Compressed | Imploded)) == 0) return data;
        if ((flags & Imploded) != 0) throw new NotSupportedException("MPQ implode 압축은 지원하지 않습니다.");
        if (data.Length == 0) return [];
        var compressionType = data[0];
        if (compressionType == 0) return data;
        if ((compressionType & 0x10) != 0)
            return DecodeBZip2(data.AsSpan(1));
        if (compressionType != 2)
            throw new NotSupportedException($"MPQ 압축 방식이 지원되지 않습니다: {compressionType}");
        using var input = new MemoryStream(data, 1, data.Length - 1, writable: false);
        using var deflate = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(expected > 0 ? expected : data.Length * 2);
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] DecodeBZip2(ReadOnlySpan<byte> input)
    {
        using var source = new MemoryStream(input.ToArray(), writable: false);
        using var bz2 = new BZip2InputStream(source);
        using var output = new MemoryStream();
        bz2.CopyTo(output);
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

    private static void Decrypt(byte[] data, string keyText)
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
