using Biz.Bizadm.SC2ReplayTrace.Models;
using Biz.Bizadm.SC2ReplayTrace.Mpq;
using Biz.Bizadm.SC2ReplayTrace.Protocol;
using System.Text.Json.Nodes;

namespace Biz.Bizadm.SC2ReplayTrace;

/// <summary>Blizzard s2protocol 기반 SC2Replay 파서의 진입점입니다.</summary>
public sealed class Sc2ReplayParser
{
    /// <summary>파일 경로에서 리플레이를 읽습니다.</summary>
    public static async Task<ReplayTrace> ParseAsync(string replayPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replayPath);
        await using var stream = File.OpenRead(replayPath);
        return await ParseAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>호출자가 소유한 스트림에서 리플레이를 읽습니다.</summary>
    public static async Task<ReplayTrace> ParseAsync(Stream replayStream, CancellationToken cancellationToken = default)
    {
        var streams = await ParseRawAsync(replayStream, cancellationToken).ConfigureAwait(false);
        return TraceNormalizer.Normalize(new RawReplay(streams.Files));
    }

    /// <summary>MPQ 컨테이너에서 공식 s2protocol 스트림을 추출합니다.</summary>
    public static async Task<ReplayStreams> ParseRawAsync(Stream replayStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replayStream);
        if (!replayStream.CanRead || !replayStream.CanSeek)
            throw new ArgumentException("리플레이 스트림은 읽기 및 검색이 가능해야 합니다.", nameof(replayStream));
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = new MpqArchive(replayStream);
        var raw = ReplayArchiveReader.Read(archive);
        cancellationToken.ThrowIfCancellationRequested();
        return new ReplayStreams(raw.Streams);
    }
}

internal static class ReplayArchiveReader
{
    public static RawReplay Read(MpqArchive archive)
    {
        var streams = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (archive.UserData is { Length: > 0 } userData)
            streams["replay.header"] = userData;
        foreach (var name in new[] { "replay.header", "replay.details", "replay.initData", "replay.game.events", "replay.message.events", "replay.tracker.events", "replay.attributes.events" })
        {
            try { streams[name] = archive.ReadFile(name); }
            catch (FileNotFoundException) { }
        }
        return new RawReplay(streams);
    }
}

internal static class TraceNormalizer
{
    public static ReplayTrace Normalize(RawReplay raw)
    {
        var latestBuild = ProtocolSchemas.SupportedBuilds.Max();
        using var latestSchema = ProtocolSchemas.Load(latestBuild);
        var header = DecodeFirst(latestSchema, raw.Streams, "replay.header", "NNet.Replay.SHeader");
        var baseBuild = FindInt(header, "m_baseBuild", "baseBuild", "m_baseBuildNum") ?? latestBuild;
        using var schema = ProtocolSchemas.Load(baseBuild);
        var details = DecodeFirst(schema, raw.Streams, "replay.details", "NNet.Game.SDetails", "NNet.Replay.SDetails");
        var initData = DecodeFirst(schema, raw.Streams, "replay.initData", "NNet.Replay.SInitData");
        var attributes = DecodeAttributes(raw.Streams);
        var events = new List<TraceEvent>();
        var positionState = new PositionState();
        var eventDecoder = new ProtocolEventDecoder();
        var gameEvents = DecodeEvents(schema, raw.Streams, "replay.game.events", eventDecoder.DecodeGame);
        var messageEvents = DecodeEvents(schema, raw.Streams, "replay.message.events", eventDecoder.DecodeMessage);
        var startLocations = new Dictionary<int, UnitPosition>();
        var raceHints = new Dictionary<int, Race>();
        if (raw.Streams.TryGetValue("replay.tracker.events", out var tracker))
        {
            foreach (var item in new ProtocolEventDecoder().DecodeTracker(schema, tracker))
            {
                TryCaptureStartLocation(startLocations, item);
                TryCaptureRaceHint(raceHints, item);
                AddEvent(events, item, positionState);
            }
        }

        events.Sort(static (left, right) => left.GameLoop.CompareTo(right.GameLoop));
        var loops = events.Count == 0 ? 0 : events[^1].GameLoop;
        var duration = FindDuration(details, header, loops);
        return new ReplayTrace(
            new MapInfo(
                FindString(details, "m_title", "m_mapName", "title"),
                FindString(details, "m_mapFileName", "mapFileName")),
            FindString(header, "m_version", "m_gameVersion") ?? FindString(details, "m_gameVersion"),
            baseBuild,
            duration,
            loops,
            ReadPlayers(details, startLocations, raceHints),
            events.AsReadOnly(),
            new ReplayRawData(header, details, initData, gameEvents, messageEvents, attributes));
    }

    private static RawReplayEvent[] DecodeEvents(
        ProtocolSchema schema,
        IReadOnlyDictionary<string, byte[]> streams,
        string streamName,
        Func<ProtocolSchema, ReadOnlyMemory<byte>, IEnumerable<ProtocolEvent>> decoder)
    {
        if (!streams.TryGetValue(streamName, out var bytes)) return [];
        return decoder(schema, bytes)
            .Select(item => new RawReplayEvent(item.GameLoop, item.UserId, item.EventName, item.Data))
            .ToArray();
    }

    private static JsonNode? DecodeFirst(
        ProtocolSchema schema,
        IReadOnlyDictionary<string, byte[]> streams,
        string streamName,
        params string[] typeNames)
    {
        if (!streams.TryGetValue(streamName, out var contents)) return null;
        foreach (var typeName in typeNames)
        {
            try { return new SchemaValueDecoder(schema, contents, streamName != "replay.initData").Decode(typeName); }
            catch (InvalidOperationException) { }
            catch (InvalidDataException) { }
        }
        return null;
    }

    private static JsonValue? DecodeAttributes(IReadOnlyDictionary<string, byte[]> streams) =>
        streams.ContainsKey("replay.attributes.events")
            ? JsonValue.Create(Convert.ToBase64String(streams["replay.attributes.events"]))
            : null;

    private static ReplayPlayer[] ReadPlayers(
        JsonNode? details,
        IReadOnlyDictionary<int, UnitPosition>? startLocations = null,
        IReadOnlyDictionary<int, Race>? raceHints = null)
    {
        var players = FindArray(details, "m_playerList", "m_players", "players");
        return players.Select((player, index) =>
        {
            var parsedId = FindInt(player, "m_playerId", "m_id");
            var playerId = parsedId is > 0 ? parsedId.Value : index + 1;
            var parsedRace = ParseRace(
                FindString(player, "m_race", "m_assignedRace", "race"),
                raceHints is not null && raceHints.TryGetValue(playerId, out var hintRace) ? hintRace : null);
            var parsedResult = ParseResult(
                FindString(player, "m_result", "result"),
                FindInt(player, "m_result", "result"));
            return new ReplayPlayer(
                playerId,
                FindString(player, "m_name", "name") ?? $"Player {index + 1}",
                parsedRace,
                parsedResult,
                ReadColor(player),
                FindInt(player, "m_teamId", "teamId"),
                ReadStartLocation(startLocations, playerId));
        }).ToArray();
    }

    private static PlayerColor? ReadColor(JsonNode? player)
    {
        var color = FindNode(player, "m_color", "color");
        if (color is null) return null;
        return new PlayerColor(
            (byte)(FindInt(color, "m_r", "r", "m_red") ?? 0),
            (byte)(FindInt(color, "m_g", "g", "m_green") ?? 0),
            (byte)(FindInt(color, "m_b", "b", "m_blue") ?? 0),
            (byte)(FindInt(color, "m_a", "a", "m_alpha") ?? 255));
    }

    private static TimeSpan FindDuration(JsonNode? details, JsonNode? header, int loops)
    {
        var seconds = FindInt(details, "m_gameDuration", "m_duration");
        if (seconds is not null) return TimeSpan.FromSeconds(seconds.Value);
        var headerLoops = FindInt(header, "m_elapsedGameLoops");
        return TimeSpan.FromSeconds((headerLoops ?? loops) / 22d);
    }

    private static JsonNode? FindNode(JsonNode? node, params string[] names)
    {
        if (node is JsonObject objectNode)
        {
            foreach (var name in names)
                if (objectNode.TryGetPropertyValue(name, out var value)) return value;
            foreach (var child in objectNode.Select(item => item.Value))
            {
                var found = FindNode(child, names);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private static JsonArray FindArray(JsonNode? node, params string[] names)
    {
        var value = FindNode(node, names);
        return value is JsonArray array ? array : [];
    }

    private static int? FindInt(JsonNode? node, params string[] names)
    {
        var value = FindNode(node, names);
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<int>(out var integer)) return integer;
            if (jsonValue.TryGetValue<long>(out var longValue)) return checked((int)longValue);
        }
        return null;
    }

    private static string? FindString(JsonNode? node, params string[] names) =>
        FindNode(node, names) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static Race ParseRace(string? value, Race? fallback = null) => value?.ToLowerInvariant() switch
    {
        "terran" or "terr" => Race.Terran,
        "zerg" => Race.Zerg,
        "protoss" or "prot" => Race.Protoss,
        "random" or "rand" => Race.Random,
        _ => fallback ?? Race.Unknown
    };

    private static MatchResult ParseResult(string? value, int? numericValue = null) => value?.ToLowerInvariant() switch
    {
        "win" or "won" => MatchResult.Win,
        "loss" or "lost" => MatchResult.Loss,
        "draw" => MatchResult.Draw,
        _ => numericValue switch
        {
            1 => MatchResult.Win,
            2 => MatchResult.Loss,
            3 => MatchResult.Draw,
            _ => MatchResult.Unknown
        }
    };

    private static void AddEvent(List<TraceEvent> target, ProtocolEvent item, PositionState positionState)
    {
        var loop = item.GameLoop;
        var time = TimeSpan.FromSeconds(loop / 22d);
        var name = item.EventName;
        var node = item.Data;
        var tag = Tag(node);
        if (name.EndsWith("SUnitBornEvent", StringComparison.Ordinal))
            target.Add(new TraceEvent(TraceEventKind.UnitCreated, loop, time, Number(node, "m_controlPlayerId"), tag, Text(node, "m_unitTypeName"), Position: Position(node), AbilityName: Text(node, "m_creatorAbilityName")));
        else if (name.EndsWith("SUnitInitEvent", StringComparison.Ordinal))
            target.Add(new TraceEvent(TraceEventKind.UnitConstructionStarted, loop, time, Number(node, "m_controlPlayerId"), tag, Text(node, "m_unitTypeName"), Position: Position(node)));
        else if (name.EndsWith("SUnitDoneEvent", StringComparison.Ordinal))
            target.Add(new TraceEvent(TraceEventKind.UnitCompleted, loop, time, UnitTag: tag));
        else if (name.EndsWith("SUnitDiedEvent", StringComparison.Ordinal))
            target.Add(new TraceEvent(TraceEventKind.UnitDied, loop, time, UnitTag: tag, OtherPlayerId: Number(node, "m_killerPlayerId"), Position: Position(node)));
        else if (name.EndsWith("SUnitTypeChangeEvent", StringComparison.Ordinal))
            target.Add(new TraceEvent(TraceEventKind.UnitTransformed, loop, time, UnitTag: tag, UnitType: Text(node, "m_unitTypeName")));
        else if (name.EndsWith("SUnitOwnerChangeEvent", StringComparison.Ordinal))
            target.Add(new TraceEvent(TraceEventKind.UnitOwnerChanged, loop, time, Number(node, "m_controlPlayerId"), tag));
        else if (name.EndsWith("SUnitPositionsEvent", StringComparison.Ordinal))
            AddPositionEvents(target, loop, time, node, positionState);
        else if (name.EndsWith("SUpgradeEvent", StringComparison.Ordinal))
            target.Add(new TraceEvent(Number(node, "m_count") <= 1 ? TraceEventKind.UpgradeStarted : TraceEventKind.UpgradeCompleted, loop, time, UpgradeType: Text(node, "m_upgradeTypeName")));
    }

    private static UnitPosition? ReadStartLocation(
        IReadOnlyDictionary<int, UnitPosition>? startLocations,
        int playerId) =>
        startLocations is not null && startLocations.TryGetValue(playerId, out var location) ? location : null;

    private static void TryCaptureStartLocation(
        IDictionary<int, UnitPosition> startLocations,
        ProtocolEvent item)
    {
        if (!item.EventName.EndsWith("SUnitBornEvent", StringComparison.Ordinal)) return;
        if (item.Data is not JsonObject node) return;

        var unitType = Text(node, "m_unitTypeName");
        if (unitType is not ("CommandCenter" or "Nexus" or "Hatchery")) return;

        var playerId = Number(node, "m_controlPlayerId");
        if (!playerId.HasValue || playerId.Value <= 0) return;
        if (startLocations.ContainsKey(playerId.Value)) return;

        var position = Position(node);
        if (position is null) return;
        startLocations[playerId.Value] = position;
    }

    private static void TryCaptureRaceHint(
        IDictionary<int, Race> raceHints,
        ProtocolEvent item)
    {
        if (!item.EventName.EndsWith("SUnitBornEvent", StringComparison.Ordinal) &&
            !item.EventName.EndsWith("SUnitInitEvent", StringComparison.Ordinal))
            return;
        if (item.Data is not JsonObject node) return;

        var playerId = Number(node, "m_controlPlayerId");
        if (!playerId.HasValue || playerId.Value <= 0) return;
        if (raceHints.ContainsKey(playerId.Value)) return;

        var unitType = Text(node, "m_unitTypeName");
        var race = unitType switch
        {
            "CommandCenter" or "OrbitalCommand" or "PlanetaryFortress" or "SCV" => Race.Terran,
            "Nexus" or "Probe" => Race.Protoss,
            "Hatchery" or "Lair" or "Hive" or "Drone" or "Overlord" => Race.Zerg,
            _ => Race.Unknown
        };
        if (race == Race.Unknown) return;
        raceHints[playerId.Value] = race;
    }

    private static int? Number(JsonNode? node, string name) => node?[name]?.GetValue<int>();
    private static string? Text(JsonNode? node, string name) => node?[name]?.GetValue<string>();
    private static ulong? Tag(JsonNode? node)
    {
        var index = Number(node, "m_unitTagIndex") ?? Number(node, "m_unitIndex");
        var recycle = Number(node, "m_unitTagRecycle") ?? 0;
        return index is null ? null : ((ulong)index.Value << 18) | (uint)recycle;
    }
    private static UnitPosition? Position(JsonNode? node) =>
        node is null ? null : new UnitPosition(Number(node, "m_x") ?? 0, Number(node, "m_y") ?? 0);

    private static void AddPositionEvents(
        List<TraceEvent> target,
        int loop,
        TimeSpan time,
        JsonNode? node,
        PositionState state)
    {
        var first = FindInt(node, "m_firstUnitIndex") ?? 0;
        if (FindNode(node, "m_items") is not JsonArray items) return;
        var unitIndex = first;
        for (var offset = 0; offset + 2 < items.Count; offset += 3)
        {
            unitIndex += items[offset]?.GetValue<int>() ?? 0;
            var x = ReadNumeric(items[offset + 1]) * 4;
            var y = ReadNumeric(items[offset + 2]) * 4;
            var tag = ((ulong)unitIndex << 18) | (uint)(state.Recycles.TryGetValue(unitIndex, out var recycle) ? recycle : 0);
            var position = new UnitPosition(x, y);
            target.Add(new TraceEvent(TraceEventKind.UnitPosition, loop, time, UnitTag: tag, Position: position));
            if (state.LastPositions.TryGetValue(tag, out var previous) && previous != position)
                target.Add(new TraceEvent(TraceEventKind.UnitMoved, loop, time, UnitTag: tag, Position: position));
            state.LastPositions[tag] = position;
        }
    }

    private sealed class PositionState
    {
        public Dictionary<int, int> Recycles { get; } = [];
        public Dictionary<ulong, UnitPosition> LastPositions { get; } = [];
    }

    private static float ReadNumeric(JsonNode? node)
    {
        if (node is not JsonValue value) return 0;
        if (value.TryGetValue<float>(out var asFloat)) return asFloat;
        if (value.TryGetValue<double>(out var asDouble)) return (float)asDouble;
        if (value.TryGetValue<int>(out var asInt)) return asInt;
        if (value.TryGetValue<long>(out var asLong)) return asLong;
        return 0;
    }
}

/// <summary>MPQ에서 추출한 리플레이 스트림입니다.</summary>
/// <param name="Files">스트림 이름과 원시 바이트의 매핑입니다.</param>
public sealed record ReplayStreams(IReadOnlyDictionary<string, byte[]> Files);

internal sealed record RawReplay(IReadOnlyDictionary<string, byte[]> Streams);
