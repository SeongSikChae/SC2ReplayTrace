using System.IO.Compression;
using System.Net.Http;

if (args.Length != 2)
{
    Console.Error.WriteLine("사용법: SchemaFetcher <commit> <destination>");
    return 2;
}

var commit = args[0];
var destination = Path.GetFullPath(args[1]);
if (commit.Length != 40 || !commit.All(Uri.IsHexDigit))
    throw new ArgumentException("커밋 SHA는 40자리 hexadecimal이어야 합니다.", nameof(args));

var marker = Path.Combine(destination, $".{commit}.complete-v2");
if (File.Exists(marker))
    return 0;

Directory.CreateDirectory(destination);
foreach (var oldFile in Directory.EnumerateFiles(destination))
    File.Delete(oldFile);
using var client = new HttpClient();
client.DefaultRequestHeaders.UserAgent.ParseAdd("SC2ReplayTrace-SchemaFetcher/1.0");
var archiveUrl = $"https://codeload.github.com/Blizzard/s2protocol/zip/{commit}";
await using var archiveStream = await client.GetStreamAsync(archiveUrl);
using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);

foreach (var entry in archive.Entries.Where(item =>
    (item.FullName.Contains("/json/protocol", StringComparison.OrdinalIgnoreCase) ||
     item.FullName.Contains("/s2protocol/versions/protocol", StringComparison.OrdinalIgnoreCase)) &&
    (item.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
     item.Name.EndsWith(".py", StringComparison.OrdinalIgnoreCase))))
{
    await using var input = entry.Open();
    await using var output = File.Create(Path.Combine(destination, entry.Name));
    await input.CopyToAsync(output);
}

File.WriteAllText(marker, commit);
return 0;
