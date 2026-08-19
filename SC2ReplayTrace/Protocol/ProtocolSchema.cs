using System.Reflection;
using System.Text.Json;

namespace Biz.Bizadm.SC2ReplayTrace.Protocol;

public sealed record ProtocolSchema(int BaseBuild, JsonDocument Document) : IDisposable
{
    public void Dispose() => Document.Dispose();
}

public static class ProtocolSchemaExtensions
{
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

public static class ProtocolSchemas
{
    private static readonly Lazy<IReadOnlyList<int>> Builds = new(LoadBuilds);

    public static IReadOnlyList<int> SupportedBuilds => Builds.Value;

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
