using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Numerics;

namespace Biz.Bizadm.SC2ReplayTrace.Protocol;

/// <summary>공식 JSON 스키마의 타입 정보를 해석하는 범용 값 디코더입니다.</summary>
public sealed class SchemaValueDecoder
{
    private readonly ProtocolSchema _schema;
    private readonly VersionedBitPackedDecoder _versioned;
    private readonly BitPackedBuffer _packed;
    private readonly bool _isVersioned;

    /// <summary>스키마와 프로토콜 스트림을 사용해 디코더를 만듭니다.</summary>
    /// <param name="schema">프로토콜 스키마입니다.</param>
    /// <param name="contents">디코드할 내용입니다.</param>
    /// <param name="isVersioned">버전 스트림인지 나타냅니다.</param>
    public SchemaValueDecoder(ProtocolSchema schema, ReadOnlyMemory<byte> contents, bool isVersioned)
    {
        _schema = schema;
        _isVersioned = isVersioned;
        _versioned = new VersionedBitPackedDecoder(contents);
        _packed = new BitPackedBuffer(contents);
    }

    /// <summary>스트림이 끝났는지 나타냅니다.</summary>
    public bool IsDone => _isVersioned ? _versioned.Done : _packed.Done;

    /// <summary>지정한 스키마 타입의 값을 디코드합니다.</summary>
    /// <param name="typeName">스키마 타입 이름입니다.</param>
    /// <returns>디코드된 JSON 값입니다.</returns>
    public JsonNode? Decode(string typeName) =>
        DecodeType(_schema.FindType(typeName)
            ?? throw new InvalidOperationException($"스키마 타입을 찾을 수 없습니다: {typeName}"));

    /// <summary>같은 스트림에서 다음 값을 디코드합니다.</summary>
    public JsonNode? DecodeNext(string typeName) => Decode(typeName);

    private JsonNode? DecodeType(JsonElement type)
    {
        if (type.ValueKind != JsonValueKind.Object) throw new InvalidDataException("잘못된 타입 정보입니다.");
        var kind = type.GetProperty("type").GetString();
        return kind switch
        {
            "UserType" => DecodeUser(type),
            "BoolType" => JsonValue.Create(ReadBool()),
            "IntType" or "UintType" => JsonValue.Create(ReadInt(type)),
            "Real32Type" => JsonValue.Create(BitConverter.Int32BitsToSingle(unchecked((int)ReadUInt32()))),
            "Real64Type" => JsonValue.Create(BitConverter.Int64BitsToDouble((long)ReadUInt64())),
            "FourCCType" => JsonValue.Create(ReadFourCc()),
            "StringType" or "AsciiStringType" => JsonValue.Create(ReadString()),
            "BlobType" => JsonValue.Create(Convert.ToBase64String(ReadBlob())),
            "BitArrayType" => JsonValue.Create(Convert.ToBase64String(ReadBitArray())),
            "ArrayType" => DecodeArray(type),
            "StructType" => DecodeStruct(type),
            "ChoiceType" => DecodeChoice(type),
            "OptionalType" => ReadBool() ? DecodeType(type.GetProperty("value_type")) : null,
            "EnumType" => JsonValue.Create(ReadEnum(type)),
            _ => throw new NotSupportedException($"지원하지 않는 공식 타입: {kind}")
        };
    }

    private JsonNode? DecodeUser(JsonElement type) =>
        DecodeType(_schema.FindType(type.GetProperty("fullname").GetString()!)
            ?? throw new InvalidDataException($"사용자 타입을 찾을 수 없습니다: {type.GetProperty("fullname")}"));

    private JsonArray DecodeArray(JsonElement type)
    {
        var result = new JsonArray();
        var length = _isVersioned ? _versioned.ReadVInt() : ReadInt(type.GetProperty("bounds"));
        for (var i = 0; i < length; i++) result.Add(DecodeType(type.GetProperty("element_type")));
        return result;
    }

    private JsonObject DecodeStruct(JsonElement type)
    {
        var result = new JsonObject();
        var fields = type.GetProperty("fields").EnumerateArray().ToArray();
        var count = _isVersioned ? _versioned.ReadVInt() : fields.Length;
        for (var i = 0; i < count; i++)
        {
            var index = _isVersioned ? _versioned.ReadVInt() : i;
            if ((uint)index >= (uint)fields.Length) { _versioned.SkipInstance(); continue; }
            var field = fields[index];
            result[field.GetProperty("name").GetString()!] = DecodeType(field.GetProperty("type_info"));
        }
        return result;
    }

    private JsonNode DecodeChoice(JsonElement type)
    {
        var tag = _isVersioned ? _versioned.ReadVInt() : ReadInt(type.GetProperty("bounds"));
        var fields = type.GetProperty("fields").EnumerateArray().ToArray();
        var field = fields.FirstOrDefault(item => item.GetProperty("tag").GetInt32() == tag);
        if (field.ValueKind == JsonValueKind.Undefined) { _versioned.SkipInstance(); return new JsonObject(); }
        return new JsonObject { [field.GetProperty("name").GetString()!] = DecodeType(field.GetProperty("type_info")) };
    }

    private int ReadInt(JsonElement type)
    {
        if (_isVersioned) return _versioned.ReadVInt();
        var bounds = type.ValueKind == JsonValueKind.Object && type.TryGetProperty("bounds", out var value) ? value : default;
        var bits = BoundsBits(bounds);
        return _packed.ReadBits(bits);
    }

    private int ReadEnum(JsonElement type)
    {
        if (_isVersioned) return _versioned.ReadVInt();
        var max = type.TryGetProperty("fields", out var fields)
            ? fields.EnumerateArray()
                .Select(item => item.GetProperty("value").GetProperty("value").GetInt32())
                .DefaultIfEmpty(0).Max()
            : 0;
        return _packed.ReadBits(Math.Max(1, 32 - BitOperations.LeadingZeroCount((uint)Math.Max(1, max))));
    }

    private bool ReadBool() => _isVersioned ? _versioned.ReadBool() : _packed.ReadBits(1) != 0;
    private byte[] ReadBlob() => _isVersioned ? _versioned.ReadBlob() : _packed.ReadAlignedBytes(ReadInt(default));
    private byte[] ReadBitArray() => _isVersioned ? _versioned.ReadBitArray() : _packed.ReadAlignedBytes((ReadInt(default) + 7) / 8);
    private string ReadFourCc() => _isVersioned ? _versioned.ReadFourCc() : Encoding.ASCII.GetString(_packed.ReadAlignedBytes(4));
    private string ReadString() => Encoding.UTF8.GetString(ReadBlob());
    private uint ReadUInt32() => BitConverter.ToUInt32(_isVersioned ? _versioned.ReadBlob() : _packed.ReadAlignedBytes(4));
    private ulong ReadUInt64() => BitConverter.ToUInt64(_isVersioned ? _versioned.ReadBlob() : _packed.ReadAlignedBytes(8));

    private static int BoundsBits(JsonElement bounds)
    {
        if (bounds.ValueKind != JsonValueKind.Object || !bounds.TryGetProperty("max", out var max))
            return 32;
        var value = max.GetProperty("value").GetProperty("value").GetInt64();
        return Math.Max(1, 64 - BitOperations.LeadingZeroCount((ulong)Math.Max(1, value)));
    }
}
