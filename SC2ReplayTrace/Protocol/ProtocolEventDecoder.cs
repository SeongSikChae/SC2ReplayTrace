using System.Text.Json.Nodes;

namespace Biz.Bizadm.SC2ReplayTrace.Protocol;

public sealed record ProtocolEvent(int GameLoop, int? UserId, string EventName, JsonNode? Data);
public sealed record TypedProtocolEvent(int GameLoop, int? UserId, string EventName, Generated.IGeneratedTrackerEvent Data);

/// <summary>공식 tracker/game/message 이벤트 스트림의 공통 프리픽스를 해석합니다.</summary>
public sealed class ProtocolEventDecoder
{
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

    public IEnumerable<ProtocolEvent> DecodeGame(
        ProtocolSchema schema,
        ReadOnlyMemory<byte> contents) =>
        DecodeMapped(schema, contents, Generated.GeneratedProtocolMaps.GameEventTypes,
            "NNet.Game.EEventId", isVersioned: false, decodeUserId: true);

    public IEnumerable<ProtocolEvent> DecodeMessage(
        ProtocolSchema schema,
        ReadOnlyMemory<byte> contents) =>
        DecodeMapped(schema, contents, Generated.GeneratedProtocolMaps.MessageEventTypes,
            "NNet.Game.EMessageId", isVersioned: false, decodeUserId: true);

    private static IEnumerable<ProtocolEvent> DecodeMapped(
        ProtocolSchema schema,
        ReadOnlyMemory<byte> contents,
        IReadOnlyDictionary<int, (int TypeId, string Name)> map,
        string eventIdType,
        bool isVersioned,
        bool decodeUserId)
    {
        var decoder = new SchemaValueDecoder(schema, contents, isVersioned);
        var loop = 0;
        while (!decoder.IsDone)
        {
            loop += FindNumber(decoder.DecodeNext("NNet.SVarUint32")) ?? 0;
            var userId = decodeUserId ? FindNumber(decoder.DecodeNext("NNet.Replay.SGameUserId")) : null;
            var eventId = FindNumber(decoder.DecodeNext(eventIdType))
                ?? throw new InvalidDataException("이벤트 ID를 읽을 수 없습니다.");
            if (!map.TryGetValue(eventId, out var eventInfo))
                throw new InvalidDataException($"알 수 없는 이벤트 ID: {eventId}");
            yield return new ProtocolEvent(loop, userId, eventInfo.Name, decoder.DecodeNext(eventInfo.Name));
        }
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
