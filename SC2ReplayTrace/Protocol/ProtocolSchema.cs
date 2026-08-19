using System.Reflection;
using System.Text.Json;

namespace Biz.Bizadm.SC2ReplayTrace.Protocol;

/// <summary>하나의 s2protocol JSON 스키마입니다.</summary>
/// <param name="BaseBuild">스키마의 기본 빌드 번호입니다.</param>
/// <param name="Document">스키마 JSON 문서입니다.</param>
public sealed record ProtocolSchema(int BaseBuild, JsonDocument Document) : IDisposable
{
    /// <summary>스키마 문서가 보유한 리소스를 해제합니다.</summary>
    public void Dispose() => Document.Dispose();
}

/// <summary>프로토콜 스키마 조회 확장 메서드입니다.</summary>
public static class ProtocolSchemaExtensions
{
    /// <summary>정규화된 이름으로 타입 정보를 찾습니다.</summary>
    /// <param name="schema">검색할 스키마입니다.</param>
    /// <param name="fullname">정규화된 타입 이름입니다.</param>
    /// <returns>타입 정보 또는 찾지 못한 경우 <see langword="null"/>입니다.</returns>
    public static JsonElement? FindType(this ProtocolSchema schema, string fullname)
    {
        foreach (var module in schema.Document.RootElement.GetProperty("modules").EnumerateArray())
        {
            foreach (var declaration in module.GetProperty("decls").EnumerateArray())
            {
                if (declaration.TryGetProperty("fullname", out var name) &&
                    string.Equals(name.GetString(), fullname, StringComparison.Ordinal))
                    return declaration.GetProperty("type_info");
            }
        }
        return null;
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
