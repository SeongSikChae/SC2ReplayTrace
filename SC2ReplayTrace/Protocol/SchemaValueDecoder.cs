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
    /// <summary>현재 비트 위치입니다.</summary>
    public int BitPosition => _isVersioned ? _versioned.BitPosition : _packed.BitPosition;

    /// <summary>지정한 스키마 타입의 값을 디코드합니다.</summary>
    /// <param name="typeName">스키마 타입 이름입니다.</param>
    /// <returns>디코드된 JSON 값입니다.</returns>
    public JsonNode? Decode(string typeName) =>
        DecodeType(_schema.FindType(typeName)
            ?? throw new InvalidOperationException($"스키마 타입을 찾을 수 없습니다: {typeName}"));

    /// <summary>같은 스트림에서 다음 값을 디코드합니다.</summary>
    public JsonNode? DecodeNext(string typeName) => Decode(typeName);

    /// <summary>다음 바이트 경계로 정렬합니다.</summary>
    public void ByteAlign()
    {
        if (_isVersioned) _versioned.Align();
        else _packed.Align();
    }

    /// <summary>packed stream에서 지정한 비트 수의 원시 정수를 읽습니다.</summary>
    public int ReadPackedInt(int bits)
    {
        if (_isVersioned) throw new InvalidOperationException("versioned stream에는 packed 정수를 사용할 수 없습니다.");
        return _packed.ReadBits(bits);
    }

    private JsonNode? DecodeType(JsonElement type)
    {
        if (type.ValueKind != JsonValueKind.Object) throw new InvalidDataException("잘못된 타입 정보입니다.");
        var kind = type.GetProperty("type").GetString();
        return kind switch
        {
            "UserType" => DecodeUser(type),
            "BoolType" => JsonValue.Create(ReadBool()),
            "IntType" or "UintType" or "InumType" => JsonValue.Create(ReadInt(type)),
            "Real32Type" => JsonValue.Create(BitConverter.Int32BitsToSingle(unchecked((int)ReadUInt32()))),
            "Real64Type" => JsonValue.Create(BitConverter.Int64BitsToDouble((long)ReadUInt64())),
            "FourCCType" => JsonValue.Create(ReadFourCc()),
            "StringType" or "AsciiStringType" => JsonValue.Create(ReadString(type)),
            "BlobType" => JsonValue.Create(Convert.ToBase64String(ReadBlob(type))),
            "BitArrayType" => JsonValue.Create(Convert.ToBase64String(ReadBitArray(type))),
            "NullType" => null,
            "ArrayType" => DecodeArray(type),
            "StructType" => DecodeStruct(type),
            "ChoiceType" => DecodeChoice(type),
            "OptionalType" => DecodeOptional(type),
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
        var length = _isVersioned ? _versioned.ReadArrayLength() : ReadInt(type);
        for (var i = 0; i < length; i++) result.Add(DecodeType(type.GetProperty("element_type")));
        return result;
    }

    private JsonObject DecodeStruct(JsonElement type)
    {
        var result = new JsonObject();
        if (type.TryGetProperty("parents", out var parents))
        {
            foreach (var parent in parents.EnumerateArray())
            {
                if (parent.ValueKind != JsonValueKind.String) continue;
                var parentType = _schema.FindType(parent.GetString()!);
                if (parentType is null) continue;
                var parentValue = DecodeType(parentType.Value) as JsonObject;
                if (parentValue is null) continue;
                foreach (var kv in parentValue)
                    result[kv.Key] = kv.Value;
            }
        }
        var fields = type.GetProperty("fields")
            .EnumerateArray()
            .Where(field => field.TryGetProperty("type", out var fieldType) &&
                            fieldType.GetString() == "MemberStructField")
            .ToArray();
        if (_isVersioned)
        {
            var count = _versioned.ReadStructFieldCount();
            for (var i = 0; i < count; i++)
            {
                var tag = _versioned.ReadStructFieldTag();
                var field = fields.FirstOrDefault(item => ReadTag(item) == tag);
                if (field.ValueKind == JsonValueKind.Undefined) { _versioned.SkipInstance(); continue; }
                result[field.GetProperty("name").GetString()!] = DecodeType(field.GetProperty("type_info"));
            }
            return result;
        }
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            result[field.GetProperty("name").GetString()!] = DecodeType(field.GetProperty("type_info"));
        }
        return result;
    }

    private JsonNode DecodeChoice(JsonElement type)
    {
        var fields = type.GetProperty("fields").EnumerateArray().ToArray();
        var tag = _isVersioned
            ? _versioned.ReadChoiceTag()
            : _packed.ReadBits(BoundsBits(type.GetProperty("bounds"), fields.Select(ReadTag).DefaultIfEmpty(0).Max()));
        var field = fields.FirstOrDefault(item => ReadTag(item) == tag);
        if (field.ValueKind == JsonValueKind.Undefined) { _versioned.SkipInstance(); return new JsonObject(); }
        return new JsonObject { [field.GetProperty("name").GetString()!] = DecodeType(field.GetProperty("type_info")) };
    }

    private JsonNode? DecodeOptional(JsonElement type)
    {
        if (_isVersioned)
        {
            if (!_versioned.ReadOptionalExists()) return null;
        }
        else if (!ReadBool()) return null;
        var valueType = type.TryGetProperty("type_info", out var typeInfo)
            ? typeInfo
            : type.GetProperty("value_type");
        return DecodeType(valueType);
    }

    private static int ReadTag(JsonElement field)
    {
        if (!field.TryGetProperty("tag", out var tag)) return 0;
        if (tag.ValueKind == JsonValueKind.Number) return tag.GetInt32();
        if (tag.TryGetProperty("value", out var value) && int.TryParse(value.GetString(), out var literal))
            return literal;
        return 0;
    }

    private int ReadInt(JsonElement type)
    {
        if (_isVersioned) return _versioned.ReadInt();
        var bounds = type.ValueKind == JsonValueKind.Object && type.TryGetProperty("bounds", out var value)
            ? value
            : type;
        var bits = BoundsBits(bounds, 0);
        var raw = unchecked((uint)_packed.ReadBits(bits));
        if (bounds.ValueKind == JsonValueKind.Object && TryReadBound(bounds, "min", out var minValue, out _))
            return unchecked((int)(minValue + raw));
        return unchecked((int)raw);
    }

    private int ReadEnum(JsonElement type)
    {
        if (_isVersioned) return _versioned.ReadInt();
        var max = type.TryGetProperty("fields", out var fields)
            ? fields.EnumerateArray()
                .Select(item =>
                {
                    var raw = item.GetProperty("value").GetProperty("value");
                    return raw.ValueKind == JsonValueKind.Number
                        ? raw.GetInt32()
                        : int.TryParse(raw.GetString(), out var parsed) ? parsed : 0;
                })
                .DefaultIfEmpty(0).Max()
            : 0;
        return _packed.ReadBits(Math.Max(1, 32 - BitOperations.LeadingZeroCount((uint)Math.Max(1, max))));
    }

    private bool ReadBool() => _isVersioned ? _versioned.ReadBool() : _packed.ReadBits(1) != 0;
    private byte[] ReadBlob(JsonElement type) =>
        _isVersioned
            ? _versioned.ReadBlob()
            : _packed.ReadAlignedBytes(ReadInt(type));
    private byte[] ReadBitArray(JsonElement type) =>
        _isVersioned
            ? _versioned.ReadBitArray()
            : ReadPackedBitArray(ReadInt(type));
    private string ReadFourCc() => _isVersioned ? _versioned.ReadFourCc() : Encoding.ASCII.GetString(_packed.ReadAlignedBytes(4));
    private string ReadString(JsonElement type) => Encoding.UTF8.GetString(ReadBlob(type));
    private uint ReadUInt32() => _isVersioned ? _versioned.ReadUInt32() : BitConverter.ToUInt32(_packed.ReadAlignedBytes(4));
    private ulong ReadUInt64() => _isVersioned ? _versioned.ReadUInt64() : BitConverter.ToUInt64(_packed.ReadAlignedBytes(8));

    private int BoundsBits(JsonElement bounds, int fallbackMax)
    {
        if (bounds.ValueKind != JsonValueKind.Object || !bounds.TryGetProperty("max", out var max))
            return Math.Max(1, 32 - BitOperations.LeadingZeroCount((uint)Math.Max(1, fallbackMax)));

        if (!TryReadBound(bounds, "min", out var minValue, out _) ||
            !TryReadBound(bounds, "max", out var maxValue, out var maxInclusive))
            return Math.Max(1, 32 - BitOperations.LeadingZeroCount((uint)Math.Max(1, fallbackMax)));

        var span = maxInclusive
            ? maxValue - minValue + 1
            : maxValue - minValue;
        span = Math.Max(1, span);
        return Math.Max(1, 64 - BitOperations.LeadingZeroCount((ulong)span - 1));
    }

    private bool TryReadBound(JsonElement bounds, string name, out long value, out bool inclusive)
    {
        value = 0;
        inclusive = true;
        if (!bounds.TryGetProperty(name, out var bound)) return false;
        inclusive = !bound.TryGetProperty("inclusive", out var inc) || inc.GetBoolean();
        if (bound.TryGetProperty("evalue", out var evalue) &&
            long.TryParse(evalue.GetString(), out var parsedEvalue))
        {
            value = parsedEvalue;
            return true;
        }
        if (!bound.TryGetProperty("value", out var expr)) return false;
        return TryEvalExpr(expr, out value);
    }

    private bool TryEvalExpr(JsonElement expr, out long value)
    {
        value = 0;
        if (!expr.TryGetProperty("type", out var typeElement)) return false;
        var type = typeElement.GetString();
        if (type == "IntLiteral")
            return long.TryParse(expr.GetProperty("value").GetString(), out value);
        if (type == "IdentifierExpr")
        {
            var name = expr.TryGetProperty("fullname", out var fullname)
                ? fullname.GetString()!
                : expr.GetProperty("value").GetString()!;
            return _schema.TryResolveConstant(name, out value);
        }
        if (type == "PowExpr" &&
            TryEvalExpr(expr.GetProperty("lhs"), out var lhs) &&
            TryEvalExpr(expr.GetProperty("rhs"), out var rhs))
        {
            value = (long)Math.Pow(lhs, rhs);
            return true;
        }
        return false;
    }

    private byte[] ReadPackedBitArray(int bitLength)
    {
        if (bitLength <= 0) return Array.Empty<byte>();
        var bytes = new byte[(bitLength + 7) / 8];
        for (var i = 0; i < bitLength; i++)
        {
            var bit = _packed.ReadBits(1);
            if (bit != 0)
                bytes[i / 8] |= (byte)(1 << (i % 8));
        }
        return bytes;
    }
}
