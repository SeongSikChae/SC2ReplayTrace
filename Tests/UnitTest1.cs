using Biz.Bizadm.SC2ReplayTrace.Protocol;
using Biz.Bizadm.SC2ReplayTrace.Protocol.Generated;
using Biz.Bizadm.SC2ReplayTrace;
using Biz.Bizadm.SC2ReplayTrace.Models;
using Biz.Bizadm.SC2ReplayTrace.Mpq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void OfficialSchemasAreEmbedded()
    {
        Assert.Contains(97563, ProtocolSchemas.SupportedBuilds);
        var schema = ProtocolSchemas.Load(97563);
        Assert.Equal(97563, schema.BaseBuild);
        Assert.True(schema.Document.RootElement.GetProperty("modules").GetArrayLength() > 0);
        var programData = Assert.IsType<ProtocolTypeInfo>(
            schema.FindTypeInfo("NNet.SProgramData"));
        Assert.Equal("StructType", programData.Kind);
        Assert.Equal(7, programData.Fields.Count);
        Assert.Equal("m_authProgramId", programData.Fields[0].Name);
        schema.Document.Dispose();
    }

    [Fact]
    public void BitPackedBufferReadsBigEndianBits()
    {
        var buffer = new BitPackedBuffer(new byte[] { 0b1010_0110 });
        Assert.Equal(0b110, buffer.ReadBits(3));
        Assert.Equal(0b10100, buffer.ReadBits(5));
        Assert.Equal(8, buffer.BitPosition);
        Assert.Equal(0, buffer.RemainingBits);
        Assert.True(buffer.Done);
    }

    [Fact]
    public void BitPackedBufferAlignsAndReadsBytesAtExactOffset()
    {
        var buffer = new BitPackedBuffer(new byte[] { 0b0000_0101, 0x11, 0x22 });

        Assert.Equal(1, buffer.ReadBits(1));
        buffer.Align();

        Assert.Equal(8, buffer.UsedBits);
        Assert.Equal(new byte[] { 0x11, 0x22 }, buffer.ReadAlignedBytes(2));
        Assert.True(buffer.Done);
    }

    [Fact]
    public void BitPackedBufferReportsTruncatedReads()
    {
        var buffer = new BitPackedBuffer(new byte[] { 0x80 });

        Assert.Throws<InvalidDataException>(() => buffer.ReadBits(9));
        Assert.Equal(8, buffer.BitPosition);
    }

    [Fact]
    public void VersionedDecoderReadsVInt()
    {
        var decoder = new VersionedBitPackedDecoder(new byte[] { 0b0000_0010 });
        Assert.Equal(1, decoder.ReadVInt());
    }

    [Theory]
    [InlineData(new byte[] { 0x03 }, -1)]
    [InlineData(new byte[] { 0x80, 0x01 }, 64)]
    [InlineData(new byte[] { 0xFE, 0x01 }, 127)]
    public void VersionedDecoderReadsCanonicalVInts(byte[] encoded, int expected)
    {
        var decoder = new VersionedBitPackedDecoder(encoded);

        Assert.Equal(expected, decoder.ReadVInt());
        Assert.Equal(encoded.Length * 8, decoder.BitPosition);
        Assert.True(decoder.Done);
    }

    [Fact]
    public void VersionedDecoderReadsOfficialPrimitiveMarkers()
    {
        var boolDecoder = new VersionedBitPackedDecoder(new byte[] { 6, 1 });
        Assert.True(boolDecoder.ReadBool());
        Assert.True(boolDecoder.Done);

        var blobDecoder = new VersionedBitPackedDecoder(new byte[] { 2, 4, 0x61, 0x62 });
        Assert.Equal(new byte[] { 0x61, 0x62 }, blobDecoder.ReadBlob());
        Assert.True(blobDecoder.Done);

        var fourCcDecoder = new VersionedBitPackedDecoder(new byte[] { 7, (byte)'S', (byte)'C', (byte)'2', 0 });
        Assert.Equal("SC2\0", fourCcDecoder.ReadFourCc());
        Assert.True(fourCcDecoder.Done);
    }

    [Fact]
    public void TrackerTypesAreGeneratedDuringBuild()
    {
        Assert.NotNull(typeof(SSUnitBornEvent));
        Assert.NotNull(typeof(GeneratedTrackerEventFactory));
        Assert.NotEmpty(GeneratedProtocolMaps.GameEventTypes);
        Assert.NotEmpty(GeneratedProtocolMaps.MessageEventTypes);
    }

    [Fact]
    public async Task SampleReplayIsParsedIntoReplayStreamsAndTrace()
    {
        var replayPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay.SC2Replay");
        Assert.True(File.Exists(replayPath), $"Fixture not found: {replayPath}");

        var parser = new Sc2ReplayParser();
        await using var stream = File.OpenRead(replayPath);
        var raw = await Sc2ReplayParser.ParseRawAsync(stream);

        Assert.Contains("replay.header", raw.Files.Keys);
        Assert.Contains("replay.details", raw.Files.Keys);
        Assert.Contains("replay.initData", raw.Files.Keys);
        Assert.Contains("replay.tracker.events", raw.Files.Keys);
        Assert.NotEmpty(raw.Files["replay.header"]);
        Assert.NotEmpty(raw.Files["replay.details"]);
        Assert.NotEmpty(raw.Files["replay.tracker.events"]);

        Assert.True(raw.Files["replay.details"].Length > 100);
        Assert.True(raw.Files["replay.initData"].Length > 100);
        Assert.True(raw.Files["replay.game.events"].Length > 100);
        Assert.True(raw.Files["replay.message.events"].Length > 0);
    }

    [Fact]
    public async Task SampleReplayParsesStartLocationForEachPlayer()
    {
        var replayPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay.SC2Replay");
        Assert.True(File.Exists(replayPath), $"Fixture not found: {replayPath}");

        var trace = await Sc2ReplayParser.ParseAsync(replayPath);

        Assert.NotEmpty(trace.Players);
        Assert.All(trace.Players, player => Assert.NotNull(player.StartLocation));

        var terran = Assert.Single(trace.Players, player => player.PlayerId == 1);
        var protoss = Assert.Single(trace.Players, player => player.PlayerId == 2);

        Assert.Equal(Race.Terran, terran.Race);
        Assert.Equal(Race.Protoss, protoss.Race);
        Assert.Equal(MatchResult.Win, terran.Result);
        Assert.Equal(MatchResult.Loss, protoss.Result);

        Assert.Equal(33f, terran.StartLocation!.X);
        Assert.Equal(138f, terran.StartLocation!.Y);
        Assert.Equal(142f, protoss.StartLocation!.X);
        Assert.Equal(33f, protoss.StartLocation!.Y);
    }

    [Fact]
    public void ReplayEventStreamsAreNotEncryptedAtMpqLevel()
    {
        var replayPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay.SC2Replay");
        using var stream = File.OpenRead(replayPath);
        using var archive = new MpqArchive(stream);
        const uint Encrypted = 0x00010000;

        var gameFlags = archive.GetBlockFlags("replay.game.events");
        var messageFlags = archive.GetBlockFlags("replay.message.events");
        var trackerFlags = archive.GetBlockFlags("replay.tracker.events");

        Assert.Equal(0u, gameFlags & Encrypted);
        Assert.Equal(0u, messageFlags & Encrypted);
        Assert.Equal(0u, trackerFlags & Encrypted);
    }

    [Fact]
    public void ReplayEventStreamCompressionMarkersAreReasonable()
    {
        var replayPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay.SC2Replay");
        using var stream = File.OpenRead(replayPath);
        using var archive = new MpqArchive(stream);

        var game = archive.GetSectorCompressionTypes("replay.game.events");
        var tracker = archive.GetSectorCompressionTypes("replay.tracker.events");

        Assert.NotEmpty(game);
        Assert.NotEmpty(tracker);
        Assert.All(game, marker => Assert.True(marker is 0 or 2 or 16 or 18, $"game marker={marker}"));
        Assert.All(tracker, marker => Assert.True(marker is 0 or 2 or 16 or 18, $"tracker marker={marker}"));
    }

    [Fact]
    public void MpqArchiveReadFileCoverage()
    {
        var replayPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay.SC2Replay");
        using var stream = File.OpenRead(replayPath);
        using var archive = new MpqArchive(stream);

        foreach (var name in new[] { "replay.details", "replay.initData", "replay.game.events", "replay.message.events", "replay.tracker.events" })
        {
            try
            {
                var bytes = archive.ReadFile(name);
                Assert.True(bytes.Length > 0, $"{name} length=0");
            }
            catch (Exception ex)
            {
                Assert.Fail($"{name} failed: {ex.GetType().Name} {ex.Message}");
            }
        }
    }

    [Fact]
    public async Task GameFirstEventBitLengthMatchesOfficial7080()
    {
        var (schema, raw, _) = await LoadEventContextAsync();
        using (schema)
        {
            var decoder = new SchemaValueDecoder(schema, raw.Files["replay.game.events"], isVersioned: false);
            _ = decoder.DecodeNext("NNet.SVarUint32");
            _ = decoder.DecodeNext("NNet.Replay.SGameUserId");
            var eventId = ExtractNumber(decoder.DecodeNext("NNet.Game.EEventId")) ?? -1;
            Assert.True(GeneratedProtocolMaps.GameEventTypes.ContainsKey(eventId), $"eventId={eventId}");
            _ = decoder.DecodeNext(GeneratedProtocolMaps.GameEventTypes[eventId].Name);
            decoder.ByteAlign();
            Assert.Equal(7080, decoder.BitPosition);
        }
    }


    [Fact]
    public async Task GameEventDecoderFirstEventMatchesGolden()
    {
        var (schema, raw, expected) = await LoadEventContextAsync();
        using (schema)
        {
            var actual = new ProtocolEventDecoder()
                .DecodeGame(schema, raw.Files["replay.game.events"])
                .Take(1)
                .ToArray();
            AssertMatches(expected["gameEvents"]!.AsArray(), actual, take: 1);
        }
    }

    [Fact]
    public async Task GameStreamFirstDeltaIsZero()
    {
        var (schema, raw, _) = await LoadEventContextAsync();
        using (schema)
        {
            var decoder = new SchemaValueDecoder(schema, raw.Files["replay.game.events"], isVersioned: false);
            var delta = ExtractNumber(decoder.DecodeNext("NNet.SVarUint32"));
            Assert.True(delta.HasValue, "SVarUint32 number missing");
            if (delta.Value != 0)
            {
                var hex = BitConverter.ToString(raw.Files["replay.game.events"].Take(8).ToArray());
                Assert.Fail($"delta={delta.Value}, first8={hex}");
            }
        }
    }

    [Fact]
    public async Task GameEventDecoderFirstTenMatchGolden()
    {
        var (schema, raw, expected) = await LoadEventContextAsync();
        using (schema)
        {
            var list = new List<ProtocolEvent>();
            try
            {
                foreach (var item in new ProtocolEventDecoder().DecodeGame(schema, raw.Files["replay.game.events"]))
                {
                    list.Add(item);
                    if (list.Count == 10) break;
                }
            }
            catch (Exception ex)
            {
                var head = string.Join(", ", list.Select((e, i) => $"#{i}:{e.GameLoop}/{e.UserId}/{e.EventName}"));
                Assert.Fail($"decoded={list.Count} head=[{head}] ex={ex.GetType().Name}:{ex.Message}");
                return;
            }
            var actual = list.ToArray();
            AssertMatches(expected["gameEvents"]!.AsArray(), actual, take: 10);
        }
    }

    [Fact]
    public async Task GameEventDecoderCardinalityMatchesGolden()
    {
        var (schema, raw, expected) = await LoadEventContextAsync();
        using (schema)
        {
            var actual = new ProtocolEventDecoder()
                .DecodeGame(schema, raw.Files["replay.game.events"])
                .ToArray();
            Assert.Equal(expected["gameEvents"]!.AsArray().Count, actual.Length);
        }
    }

    [Fact]
    public async Task TrackerEventDecoderFirstEventMatchesGolden()
    {
        var (schema, raw, expected) = await LoadEventContextAsync();
        var trackerHead = BitConverter.ToString(raw.Files["replay.tracker.events"].Take(8).ToArray());
        using (schema)
        {
            ProtocolEvent[] actual;
            try
            {
                actual = new ProtocolEventDecoder()
                    .DecodeTracker(schema, raw.Files["replay.tracker.events"])
                    .Take(1)
                    .ToArray();
            }
            catch (Exception ex)
            {
                Assert.Fail($"trackerHead={trackerHead} ex={ex.GetType().Name} {ex.Message}");
                return;
            }
            var expectedTracker = expected["trackerEvents"]!.AsArray();
            Assert.True(actual.Length > 0, $"trackerHead={trackerHead}");
            Assert.Equal(expectedTracker[0]!["gameLoop"]!.GetValue<int>(), actual[0].GameLoop);
            Assert.Equal(expectedTracker[0]!["eventName"]!.GetValue<string>(), actual[0].EventName);
        }
    }

    private static async Task<(ProtocolSchema Schema, ReplayStreams Raw, JsonObject Expected)> LoadEventContextAsync()
    {
        var replayPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay.SC2Replay");
        var expectedPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "event-expected.json");
        await using var stream = File.OpenRead(replayPath);
        var raw = await Sc2ReplayParser.ParseRawAsync(stream);
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(expectedPath))!.AsObject();
        return (ProtocolSchemas.Load(97563), raw, expected);
    }

    private static void AssertMatches(JsonArray expected, IReadOnlyList<ProtocolEvent> actual, int take)
    {
        Assert.Equal(take, actual.Count);
        for (var i = 0; i < take; i++)
        {
            var expectedItem = expected[i]!.AsObject();
            Assert.Equal(expectedItem["gameLoop"]!.GetValue<int>(), actual[i].GameLoop);
            Assert.Equal(expectedItem["userId"]?.GetValue<int?>(), actual[i].UserId);
            Assert.Equal(expectedItem["eventName"]!.GetValue<string>(), actual[i].EventName);
        }
    }

    private static int? ExtractNumber(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<int>(out var number)) return number;
        if (node is JsonObject objectNode)
            return objectNode.Select(item => ExtractNumber(item.Value)).FirstOrDefault(item => item.HasValue);
        if (node is JsonArray arrayNode)
            return arrayNode.Select(ExtractNumber).FirstOrDefault(item => item.HasValue);
        return null;
    }

}
