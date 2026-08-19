using System.Buffers.Binary;

namespace Biz.Bizadm.SC2ReplayTrace.Mpq;

internal static class BZip2Decoder
{
    public static byte[] Decode(ReadOnlySpan<byte> input)
    {
        var reader = new BitReader(input);
        if (reader.ReadByte() != (byte)'B' || reader.ReadByte() != (byte)'Z' || reader.ReadByte() != (byte)'h')
            throw new InvalidDataException("BZip2 헤더가 올바르지 않습니다.");
        var level = reader.ReadByte();
        if (level is < (byte)'1' or > (byte)'9')
            throw new InvalidDataException("BZip2 블록 크기가 올바르지 않습니다.");

        using var output = new MemoryStream();
        while (true)
        {
            var marker = reader.ReadBits(48);
            if (marker == 0x177245385090)
            {
                _ = reader.ReadBits(32);
                break;
            }
            if (marker != 0x314159265359)
                throw new InvalidDataException("BZip2 블록 마커가 올바르지 않습니다.");

            _ = reader.ReadBits(32);
            var randomized = reader.ReadBits(1) != 0;
            if (randomized) throw new NotSupportedException("BZip2 randomized 블록은 지원되지 않습니다.");
            var originalPointer = (int)reader.ReadBits(24);
            var block = DecodeBlock(reader, originalPointer, 100_000 * (level - '0'));
            output.Write(block.Data);
            if (block.Truncated) break;
        }
        return output.ToArray();
    }

    private static (byte[] Data, bool Truncated) DecodeBlock(BitReader reader, int originalPointer, int blockCapacity)
    {
        Span<byte> inUse16 = stackalloc byte[16];
        for (var i = 0; i < 16; i++) inUse16[i] = (byte)reader.ReadBits(1);
        var inUse = new bool[256];
        for (var group = 0; group < 16; group++)
            if (inUse16[group] != 0)
                for (var bit = 0; bit < 16; bit++)
                    inUse[group * 16 + bit] = reader.ReadBits(1) != 0;

        var symbols = inUse.Count(value => value);
        var alphaSize = symbols + 2;
        var groupCount = (int)reader.ReadBits(3);
        var selectorCount = (int)reader.ReadBits(15);
        if (groupCount is < 2 or > 6 || selectorCount <= 0)
            throw new InvalidDataException("BZip2 Huffman 메타데이터가 올바르지 않습니다.");

        var selectors = new byte[selectorCount];
        for (var i = 0; i < selectorCount; i++)
        {
            var value = 0;
            while (reader.ReadBits(1) != 0) value++;
            selectors[i] = checked((byte)value);
        }

        var mtfSelectors = new byte[groupCount];
        for (byte i = 0; i < groupCount; i++) mtfSelectors[i] = i;
        for (var i = 0; i < selectorCount; i++)
        {
            var index = selectors[i];
            if (index >= groupCount) throw new InvalidDataException("BZip2 selector가 올바르지 않습니다.");
            var value = mtfSelectors[index];
            for (var j = index; j > 0; j--) mtfSelectors[j] = mtfSelectors[j - 1];
            mtfSelectors[0] = value;
            selectors[i] = value;
        }

        var lengths = new int[groupCount][];
        for (var group = 0; group < groupCount; group++)
        {
            var length = (int)reader.ReadBits(5);
            lengths[group] = new int[alphaSize];
            for (var symbol = 0; symbol < alphaSize; symbol++)
            {
                while (reader.ReadBits(1) != 0)
                    length += reader.ReadBits(1) == 0 ? 1 : -1;
                lengths[group][symbol] = length;
            }
        }

        var tables = lengths.Select(BuildTable).ToArray();
        var ordered = new byte[symbols];
        var order = 0;
        for (var value = 0; value < 256; value++)
            if (inUse[value]) ordered[order++] = (byte)value;

        var mtf = Enumerable.Range(0, symbols).Select(value => (byte)value).ToArray();
        var transformed = new List<byte>(Math.Min(blockCapacity, 1_000_000));
        var groupPosition = 0;
        var groupUsed = 0;
        var table = tables[selectors[0]];
        var endOfBlock = false;
        while (true)
        {
            if (groupUsed == 0)
            {
                if (groupPosition >= selectors.Length) { endOfBlock = true; break; }
                table = tables[selectors[groupPosition++]];
                groupUsed = 50;
            }
            groupUsed--;
            var symbol = table.Read(reader);
            if (symbol == 0 || symbol == 1)
            {
                var run = -1;
                var power = 1;
                do
                {
                    run += (symbol == 0 ? 1 : 2) * power;
                    power <<= 1;
                    if (groupUsed == 0)
                    {
                        if (groupPosition >= selectors.Length) { endOfBlock = true; break; }
                        table = tables[selectors[groupPosition++]];
                        groupUsed = 50;
                    }
                    groupUsed--;
                    symbol = table.Read(reader);
                } while (!endOfBlock && symbol is 0 or 1);
                var runValue = MtfValue(mtf, 0);
                for (var i = 0; i < run + 1; i++) transformed.Add(ordered[runValue]);
            }
            if (endOfBlock) break;
            if (symbol == alphaSize - 1) break;
            var index = symbol - 1;
            if ((uint)index >= (uint)symbols) throw new InvalidDataException("BZip2 MTF 심볼이 올바르지 않습니다.");
            var value = MtfValue(mtf, index);
            transformed.Add(ordered[value]);
            if (transformed.Count > blockCapacity * 2) throw new InvalidDataException("BZip2 블록 크기가 올바르지 않습니다.");
        }

        var bwt = InverseBwt(transformed.ToArray(), originalPointer);
        return (UndoRle(bwt), endOfBlock);
    }

    private static int MtfValue(byte[] values, int index)
    {
        var value = values[index];
        for (var i = index; i > 0; i--) values[i] = values[i - 1];
        values[0] = value;
        return value;
    }

    private static byte[] InverseBwt(byte[] data, int originalPointer)
    {
        var counts = new int[256];
        foreach (var value in data) counts[value]++;
        var starts = new int[256];
        var total = 0;
        for (var i = 0; i < 256; i++) { starts[i] = total; total += counts[i]; }
        var next = new int[data.Length];
        var seen = new int[256];
        for (var i = 0; i < data.Length; i++) next[starts[data[i]] + seen[data[i]]++] = i;
        var result = new byte[data.Length];
        var row = originalPointer;
        for (var i = data.Length - 1; i >= 0; i--) { row = next[row]; result[i] = data[row]; }
        return result;
    }

    private static byte[] UndoRle(byte[] data)
    {
        using var output = new MemoryStream(data.Length);
        for (var i = 0; i < data.Length;)
        {
            var value = data[i++];
            output.WriteByte(value);
            var count = 1;
            while (i < data.Length && data[i] == value && count < 4) { output.WriteByte(data[i++]); count++; }
            if (count == 4 && i < data.Length) { var extra = data[i++]; for (var j = 0; j < extra; j++) output.WriteByte(value); }
        }
        return output.ToArray();
    }

    private static HuffmanTable BuildTable(int[] lengths)
    {
        var min = lengths.Min();
        var max = lengths.Max();
        var permutation = new int[lengths.Length];
        var position = 0;
        for (var bits = min; bits <= max; bits++)
            for (var symbol = 0; symbol < lengths.Length; symbol++)
                if (lengths[symbol] == bits) permutation[position++] = symbol;
        var baseValues = new int[23];
        foreach (var length in lengths) baseValues[length + 1]++;
        for (var i = 1; i < baseValues.Length; i++) baseValues[i] += baseValues[i - 1];
        var limits = new int[23];
        var vector = 0;
        for (var bits = min; bits <= max; bits++)
        {
            vector += baseValues[bits + 1] - baseValues[bits];
            limits[bits] = vector - 1;
            vector <<= 1;
        }
        for (var bits = min + 1; bits <= max; bits++)
            baseValues[bits] = ((limits[bits - 1] + 1) << 1) - baseValues[bits];
        return new HuffmanTable(permutation, baseValues, limits, min, max);
    }

    private sealed class HuffmanTable(int[] permutation, int[] baseValues, int[] limits, int minimum, int maximum)
    {
        public int Read(BitReader reader)
        {
            var code = (int)reader.ReadBits(minimum);
            var bits = minimum;
            while (bits <= maximum && code > limits[bits])
            {
                code = (code << 1) | (int)reader.ReadBits(1);
                bits++;
            }
            if (bits > maximum) throw new InvalidDataException("BZip2 Huffman 코드가 올바르지 않습니다.");
            var index = code - baseValues[bits];
            if ((uint)index >= (uint)permutation.Length) throw new InvalidDataException("BZip2 Huffman 심볼이 올바르지 않습니다.");
            return permutation[index];
        }
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;
        public ulong ReadBits(int count)
        {
            ulong value = 0;
            for (var i = 0; i < count; i++)
            {
                if (_position >= _data.Length * 8) throw new EndOfStreamException();
                value = (value << 1) | (uint)((_data[_position / 8] >> (7 - _position % 8)) & 1);
                _position++;
            }
            return value;
        }
        public byte ReadByte() => (byte)ReadBits(8);
    }
}
