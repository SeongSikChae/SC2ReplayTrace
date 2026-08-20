using System.Reflection;
using System.Text.Json;

namespace Biz.Bizadm.SC2ReplayTrace.Protocol;

/// <summary>하나의 s2protocol JSON 스키마입니다.</summary>
/// <param name="BaseBuild">스키마의 기본 빌드 번호입니다.</param>
/// <param name="Document">스키마 JSON 문서입니다.</param>
public sealed record ProtocolSchema(int BaseBuild, JsonDocument Document) : IDisposable
{
    private readonly Lazy<IReadOnlyDictionary<string, ProtocolTypeInfo>> _types =
        new Lazy<IReadOnlyDictionary<string, ProtocolTypeInfo>>(
            () => CreateTypeIndex(Document));
    private readonly Lazy<IReadOnlyDictionary<string, long>> _constants =
        new Lazy<IReadOnlyDictionary<string, long>>(
            () => CreateConstantIndex(Document));

    /// <summary>스키마 문서가 보유한 리소스를 해제합니다.</summary>
    public void Dispose() => Document.Dispose();

    /// <summary>공식 typeinfos를 선언 순서와 함께 노출합니다.</summary>
    public IReadOnlyDictionary<string, ProtocolTypeInfo> Types => _types.Value;
    /// <summary>스키마 상수(정수) 테이블입니다.</summary>
    public IReadOnlyDictionary<string, long> Constants => _constants.Value;

    /// <summary>정규화된 이름의 runtime typeinfo를 찾습니다.</summary>
    public ProtocolTypeInfo? FindTypeInfo(string fullname) =>
        Types.TryGetValue(fullname, out var type) ? type : FindTypeInfoDirect(fullname);

    /// <summary>스키마 상수 값을 찾습니다.</summary>
    public bool TryResolveConstant(string nameOrFullname, out long value)
    {
        if (Constants.TryGetValue(nameOrFullname, out value))
            return true;
        var shortName = nameOrFullname.Split('.').Last();
        return Constants.TryGetValue(shortName, out value);
    }

    private ProtocolTypeInfo? FindTypeInfoDirect(string fullname)
    {
        foreach (var module in Document.RootElement.GetProperty("modules").EnumerateArray())
        {
            var found = FindDeclaration(module, fullname);
            if (found is not null)
                return found;
        }
        return null;
    }

    private static ProtocolTypeInfo? FindDeclaration(JsonElement element, string fullname)
    {
        if (element.TryGetProperty("decls", out var declarations))
        foreach (var declaration in declarations.EnumerateArray())
        {
            if (declaration.TryGetProperty("fullname", out var name) &&
                string.Equals(name.GetString(), fullname, StringComparison.Ordinal) &&
                declaration.TryGetProperty("type_info", out var definition))
                return new ProtocolTypeInfo(-1, fullname, definition.GetProperty("type").GetString()!,
                    definition, Array.Empty<ProtocolFieldInfo>(), Array.Empty<string>(), null, null);

            var nestedDeclaration = FindDeclaration(declaration, fullname);
            if (nestedDeclaration is not null) return nestedDeclaration;
        }
        if (element.TryGetProperty("modules", out var modules))
        foreach (var module in modules.EnumerateArray())
        {
            var found = FindDeclaration(module, fullname);
            if (found is not null) return found;
        }
        return null;
    }

    private static IReadOnlyDictionary<string, ProtocolTypeInfo> CreateTypeIndex(JsonDocument document)
    {
        var result = new Dictionary<string, ProtocolTypeInfo>(StringComparer.Ordinal);
        var typeId = 0;
        foreach (var module in document.RootElement.GetProperty("modules").EnumerateArray())
        {
            foreach (var declaration in module.GetProperty("decls").EnumerateArray())
            {
                if (!declaration.TryGetProperty("fullname", out var name) ||
                    !declaration.TryGetProperty("type_info", out var typeInfo))
                    continue;

                var fullname = name.GetString()!;
                var fields = typeInfo.TryGetProperty("fields", out var fieldsElement)
                    ? fieldsElement.EnumerateArray()
                        .Where(field => field.TryGetProperty("name", out _))
                        .Select(field => new ProtocolFieldInfo(
                            field.GetProperty("name").GetString()!,
                            field.TryGetProperty("tag", out var tag) && tag.ValueKind == JsonValueKind.Number
                                ? tag.GetInt32()
                                : null,
                            field.TryGetProperty("type_info", out var fieldType) ? fieldType : default))
                        .ToArray()
                    : Array.Empty<ProtocolFieldInfo>();

                var parents = typeInfo.TryGetProperty("parents", out var parentElement)
                    ? parentElement.EnumerateArray()
                        .Where(parent => parent.ValueKind == JsonValueKind.String)
                        .Select(parent => parent.GetString()!)
                        .ToArray()
                    : Array.Empty<string>();

                result[fullname] = new ProtocolTypeInfo(
                    typeId++,
                    fullname,
                    typeInfo.GetProperty("type").GetString()!,
                    typeInfo,
                    fields,
                    parents,
                    ReadBound(typeInfo, "min"),
                    ReadBound(typeInfo, "max"));
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, long> CreateConstantIndex(JsonDocument document)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var module in document.RootElement.GetProperty("modules").EnumerateArray())
            IndexConstants(module, result);
        return result;
    }

    private static void IndexConstants(JsonElement element, IDictionary<string, long> output)
    {
        if (element.TryGetProperty("decls", out var declarations))
        {
            foreach (var declaration in declarations.EnumerateArray())
            {
                if (declaration.TryGetProperty("type", out var declarationType) &&
                    declarationType.GetString() == "ConstDecl" &&
                    declaration.TryGetProperty("value", out var value) &&
                    value.TryGetProperty("type", out var valueType) &&
                    valueType.GetString() == "IntLiteral" &&
                    long.TryParse(value.GetProperty("value").GetString(), out var number))
                {
                    if (declaration.TryGetProperty("name", out var name))
                        output[name.GetString()!] = number;
                    if (declaration.TryGetProperty("fullname", out var fullname))
                        output[fullname.GetString()!] = number;
                }

                IndexConstants(declaration, output);
            }
        }

        if (element.TryGetProperty("modules", out var modules))
        {
            foreach (var module in modules.EnumerateArray())
                IndexConstants(module, output);
        }
    }

    private static long? ReadBound(JsonElement typeInfo, string boundName)
    {
        if (!typeInfo.TryGetProperty("bounds", out var bounds) ||
            bounds.ValueKind != JsonValueKind.Object ||
            !bounds.TryGetProperty(boundName, out var bound) ||
            !bound.TryGetProperty("value", out var value))
            return null;
        if (value.TryGetProperty("value", out var literal) &&
            long.TryParse(literal.GetString(), out var number))
            return number;
        return null;
    }
}

/// <summary>공식 type_info의 종류입니다.</summary>
public sealed record ProtocolTypeInfo(
    int TypeId,
    string FullName,
    string Kind,
    JsonElement Definition,
    IReadOnlyList<ProtocolFieldInfo> Fields,
    IReadOnlyList<string> Parents,
    long? Min,
    long? Max);

/// <summary>struct/choice field의 공식 metadata입니다.</summary>
public sealed record ProtocolFieldInfo(
    string Name,
    int? Tag,
    JsonElement Definition);

/// <summary>프로토콜 스키마 조회 확장 메서드입니다.</summary>
public static class ProtocolSchemaExtensions
{
    /// <summary>정규화된 이름으로 타입 정보를 찾습니다.</summary>
    /// <param name="schema">검색할 스키마입니다.</param>
    /// <param name="fullname">정규화된 타입 이름입니다.</param>
    /// <returns>타입 정보 또는 찾지 못한 경우 <see langword="null"/>입니다.</returns>
    public static JsonElement? FindType(this ProtocolSchema schema, string fullname)
    {
        return schema.FindTypeInfo(fullname)?.Definition;
    }
}

/// <summary>내장된 프로토콜 스키마를 관리합니다.</summary>
public static class ProtocolSchemas
{
    private static readonly Lazy<IReadOnlyList<int>> Builds = new(LoadBuilds);

    /// <summary>내장된 스키마가 지원하는 빌드 목록입니다.</summary>
    public static IReadOnlyList<int> SupportedBuilds => Builds.Value;

    /// <summary>요청한 빌드 이하에서 가장 가까운 스키마를 로드합니다.</summary>
    /// <param name="baseBuild">기준 빌드 번호입니다.</param>
    /// <returns>로드된 프로토콜 스키마입니다.</returns>
    public static ProtocolSchema Load(int baseBuild)
    {
        var build = SupportedBuilds
            .Where(candidate => candidate <= baseBuild)
            .DefaultIfEmpty(SupportedBuilds.Min())
            .Max();
        var name = $"protocol{build}.json";
        var resource = typeof(ProtocolSchemas).Assembly.GetManifestResourceNames()
            .SingleOrDefault(item => item.EndsWith($".{name}", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"프로토콜 스키마를 찾을 수 없습니다: {name}");
        using var stream = typeof(ProtocolSchemas).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"프로토콜 스키마 리소스를 열 수 없습니다: {name}");
        return new ProtocolSchema(build, JsonDocument.Parse(stream));
    }

    private static IReadOnlyList<int> LoadBuilds() =>
        typeof(ProtocolSchemas).Assembly.GetManifestResourceNames()
            .Select(item => Path.GetFileNameWithoutExtension(item))
            .Select(item => item?.Split('.').LastOrDefault())
            .Where(item => item?.StartsWith("protocol", StringComparison.OrdinalIgnoreCase) == true)
            .Select(item => int.TryParse(item![8..], out var build) ? build : 0)
            .Where(build => build > 0)
            .OrderBy(build => build)
            .ToArray();
}
