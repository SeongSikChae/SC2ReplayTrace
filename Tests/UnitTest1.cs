using Biz.Bizadm.SC2ReplayTrace.Protocol;
using Biz.Bizadm.SC2ReplayTrace.Protocol.Generated;
using Biz.Bizadm.SC2ReplayTrace;

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
        schema.Document.Dispose();
    }

    [Fact]
    public void BitPackedBufferReadsBigEndianBits()
    {
        var buffer = new BitPackedBuffer(new byte[] { 0b1010_0110 });
        Assert.Equal(0b110, buffer.ReadBits(3));
        Assert.Equal(0b10100, buffer.ReadBits(5));
        Assert.True(buffer.Done);
    }

    [Fact]
    public void VersionedDecoderReadsVInt()
    {
        var decoder = new VersionedBitPackedDecoder(new byte[] { 0b0000_0010 });
        Assert.Equal(1, decoder.ReadVInt());
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
        var raw = await parser.ParseRawAsync(stream);

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
}
