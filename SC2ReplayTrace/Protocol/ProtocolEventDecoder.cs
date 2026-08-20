using System.Text.Json.Nodes;

namespace Biz.Bizadm.SC2ReplayTrace.Protocol;

/// <summary>디코드된 프로토콜 이벤트입니다.</summary>
/// <param name="GameLoop">게임 루프입니다.</param>
/// <param name="UserId">사용자 식별자입니다.</param>
/// <param name="EventName">이벤트 이름입니다.</param>
/// <param name="Data">이벤트 데이터입니다.</param>
public sealed record ProtocolEvent(int GameLoop, int? UserId, string EventName, JsonNode? Data);
/// <summary>강한 타입으로 디코드된 프로토콜 이벤트입니다.</summary>
/// <param name="GameLoop">게임 루프입니다.</param>
/// <param name="UserId">사용자 식별자입니다.</param>
/// <param name="EventName">이벤트 이름입니다.</param>
/// <param name="Data">강한 타입 이벤트 데이터입니다.</param>
public sealed record TypedProtocolEvent(int GameLoop, int? UserId, string EventName, Generated.IGeneratedTrackerEvent Data);

/// <summary>공식 tracker/game/message 이벤트 스트림의 공통 프리픽스를 해석합니다.</summary>
public sealed class ProtocolEventDecoder
{
    private readonly ProtocolEventDecoderOptions _options;

    private static readonly IReadOnlyDictionary<int, string> TrackerEvents =
        new Dictionary<int, string>
        {
            [0] = "NNet.Replay.Tracker.SPlayerStatsEvent",
            [1] = "NNet.Replay.Tracker.SUnitBornEvent",
            [2] = "NNet.Replay.Tracker.SUnitDiedEvent",
            [3] = "NNet.Replay.Tracker.SUnitOwnerChangeEvent",
            [4] = "NNet.Replay.Tracker.SUnitTypeChangeEvent",
            [5] = "NNet.Replay.Tracker.SUpgradeEvent",
            [6] = "NNet.Replay.Tracker.SUnitInitEvent",
            [7] = "NNet.Replay.Tracker.SUnitDoneEvent",
            [8] = "NNet.Replay.Tracker.SUnitPositionsEvent",
            [9] = "NNet.Replay.Tracker.SPlayerSetupEvent"
        };

    /// <summary>기본 옵션으로 이벤트 스트림 디코더를 만듭니다.</summary>
    public ProtocolEventDecoder() : this(new ProtocolEventDecoderOptions()) { }

    /// <summary>지정한 옵션으로 이벤트 스트림 디코더를 만듭니다.</summary>
    /// <param name="options">디코더 동작 옵션입니다.</param>
    public ProtocolEventDecoder(ProtocolEventDecoderOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.MessageTrailingToleranceBits < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MessageTrailingToleranceBits는 0 이상이어야 합니다.");
    }

    /// <summary>tracker 이벤트 스트림을 디코드합니다.</summary>
    public IEnumerable<ProtocolEvent> DecodeTracker(
        ProtocolSchema schema,
        ReadOnlyMemory<byte> contents)
    {
        var decoder = new SchemaValueDecoder(schema, contents, isVersioned: true);
        var loop = 0;
        while (!decoder.IsDone)
        {
            var delta = FindNumber(decoder.DecodeNext("NNet.SVarUint32")) ?? 0;
            loop += delta;
            var id = FindNumber(decoder.DecodeNext("NNet.Replay.Tracker.EEventId"))
                ?? throw new InvalidDataException("tracker event id를 읽을 수 없습니다.");
            if (!TrackerEvents.TryGetValue(id, out var name))
                throw new InvalidDataException($"알 수 없는 tracker event id: {id}");
            yield return new ProtocolEvent(loop, null, name, decoder.DecodeNext(name));
        }
    }

    /// <summary>tracker 이벤트 스트림을 강한 타입으로 디코드합니다.</summary>
    public IEnumerable<TypedProtocolEvent> DecodeTrackerTyped(
        ProtocolSchema schema,
        ReadOnlyMemory<byte> contents)
    {
        foreach (var item in DecodeTracker(schema, contents))
        {
            if (item.Data is null) continue;
            var typed = Generated.GeneratedTrackerEventFactory.Create(item.EventName, item.Data);
            if (typed is not null)
                yield return new TypedProtocolEvent(item.GameLoop, item.UserId, item.EventName, typed);
        }
    }

    /// <summary>게임 이벤트 스트림을 디코드합니다.</summary>
    public IEnumerable<ProtocolEvent> DecodeGame(
        ProtocolSchema schema,
        ReadOnlyMemory<byte> contents) =>
        DecodeMapped(schema, contents, Generated.GeneratedProtocolMaps.GameEventTypes,
            "NNet.Game.EEventId", isVersioned: false, decodeUserId: true);

    /// <summary>메시지 이벤트 스트림을 디코드합니다.</summary>
    public IEnumerable<ProtocolEvent> DecodeMessage(
        ProtocolSchema schema,
        ReadOnlyMemory<byte> contents) =>
        DecodeMapped(schema, contents, Generated.GeneratedProtocolMaps.MessageEventTypes,
            "NNet.Game.EMessageId", isVersioned: false, decodeUserId: true,
            trailingToleranceBits: _options.MessageTrailingToleranceBits);

    private static IEnumerable<ProtocolEvent> DecodeMapped(
        ProtocolSchema schema,
        ReadOnlyMemory<byte> contents,
        IReadOnlyDictionary<int, (int TypeId, string Name)> map,
        string eventIdType,
        bool isVersioned,
        bool decodeUserId,
        int trailingToleranceBits = 0)
    {
        var decoder = new SchemaValueDecoder(schema, contents, isVersioned);
        var loop = 0;
        while (!decoder.IsDone)
        {
            ProtocolEvent? decoded = null;
            try
            {
                loop += FindNumber(decoder.DecodeNext("NNet.SVarUint32")) ?? 0;
                var userId = decodeUserId ? FindNumber(decoder.DecodeNext("NNet.Replay.SGameUserId")) : null;
                var eventId = FindNumber(decoder.DecodeNext(eventIdType))
                    ?? throw new InvalidDataException("이벤트 ID를 읽을 수 없습니다.");
                if (!map.TryGetValue(eventId, out var eventInfo))
                    throw new InvalidDataException($"알 수 없는 이벤트 ID: {eventId}");
                var payload = decoder.DecodeNext(eventInfo.Name);
                decoder.ByteAlign();
                decoded = new ProtocolEvent(loop, userId, eventInfo.Name, payload);
            }
            catch (InvalidDataException) when (!isVersioned && IsTrailingZeroPadding(contents.Span, decoder.BitPosition))
            {
                yield break;
            }
            catch (InvalidDataException) when (!isVersioned && trailingToleranceBits > 0 &&
                                               decoder.BitPosition >= contents.Length * 8 - trailingToleranceBits)
            {
                // Some message streams include non-event trailing bytes near EOF.
                yield break;
            }
            if (decoded is not null)
                yield return decoded;
        }
    }

    private static bool IsTrailingZeroPadding(ReadOnlySpan<byte> contents, int bitPosition)
    {
        if (bitPosition < 0) return false;
        var byteIndex = bitPosition / 8;
        var bitOffset = bitPosition % 8;
        if (byteIndex >= contents.Length) return true;
        if (((contents[byteIndex] >> bitOffset) & 0xFF) != 0) return false;
        for (var i = byteIndex + 1; i < contents.Length; i++)
            if (contents[i] != 0) return false;
        return true;
    }

    private static int? FindNumber(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<int>(out var number)) return number;
        if (node is JsonObject objectNode)
            return objectNode.Select(item => FindNumber(item.Value)).FirstOrDefault(item => item.HasValue);
        if (node is JsonArray arrayNode)
            return arrayNode.Select(FindNumber).FirstOrDefault(item => item.HasValue);
        return null;
    }

}

/// <summary><see cref="ProtocolEventDecoder"/> 동작 옵션입니다.</summary>
public sealed record ProtocolEventDecoderOptions
{
    /// <summary>메시지 trailing 허용 기본값(비트)입니다.</summary>
    public const int DefaultMessageTrailingToleranceBits = 32;

    /// <summary>
    /// 메시지 스트림 EOF 근처에서 디코드 실패를 trailing 노이즈로 간주할 허용 비트 수입니다.
    /// 기본값은 32비트(4바이트)입니다.
    /// </summary>
    public int MessageTrailingToleranceBits { get; init; } = DefaultMessageTrailingToleranceBits;
}
