using Biz.Bizadm.SC2ReplayTrace;
using Biz.Bizadm.SC2ReplayTrace.Protocol;
using Biz.Bizadm.SC2ReplayTrace.Protocol.Generated;
using System.Text.Json.Nodes;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: ReplayInspector <replayPath>");
    return 1;
}

var replayPath = args[0];
string? parseAsyncError = null;
Biz.Bizadm.SC2ReplayTrace.Models.ReplayTrace? parsedTrace = null;
try
{
    parsedTrace = await Sc2ReplayParser.ParseAsync(replayPath);
}
catch (Exception ex)
{
    parseAsyncError = $"{ex.GetType().Name}: {ex.Message}";
}
await using var stream = File.OpenRead(replayPath);
var raw = await Sc2ReplayParser.ParseRawAsync(stream);

var streamStats = raw.Files
    .OrderBy(kv => kv.Key)
    .Select(kv => new
    {
        name = kv.Key,
        length = kv.Value.Length,
        first8 = BitConverter.ToString(kv.Value.Take(8).ToArray())
    })
    .ToArray();

int? baseBuild = null;
string? headerError = null;
try
{
    using var latestSchema = ProtocolSchemas.Load(ProtocolSchemas.SupportedBuilds.Max());
    var header = new SchemaValueDecoder(latestSchema, raw.Files["replay.header"], isVersioned: true)
        .Decode("NNet.Replay.SHeader");
    baseBuild = FindInt(header, "m_baseBuild", "baseBuild", "m_baseBuildNum");
}
catch (Exception ex)
{
    headerError = $"{ex.GetType().Name}: {ex.Message}";
}

var build = baseBuild ?? ProtocolSchemas.SupportedBuilds.Max();
using var schema = ProtocolSchemas.Load(build);
var decoder = new ProtocolEventDecoder();

var game = DecodeSafely(() => decoder.DecodeGame(schema, raw.Files["replay.game.events"]));
var message = raw.Files.TryGetValue("replay.message.events", out var messageBytes)
    ? DecodeSafely(() => decoder.DecodeMessage(schema, messageBytes))
    : new DecodeResult([], null);
var tracker = raw.Files.TryGetValue("replay.tracker.events", out var trackerBytes)
    ? DecodeSafely(() => decoder.DecodeTracker(schema, trackerBytes))
    : new DecodeResult([], null);

var gameTopNames = game.Events
    .GroupBy(e => e.EventName)
    .OrderByDescending(g => g.Count())
    .Take(12)
    .Select(g => new { eventName = g.Key, count = g.Count() })
    .ToArray();

var trackerTopNames = tracker.Events
    .GroupBy(e => e.EventName)
    .OrderByDescending(g => g.Count())
    .Take(12)
    .Select(g => new { eventName = g.Key, count = g.Count() })
    .ToArray();

var firstGame10 = game.Events
    .Take(10)
    .Select(e => new { gameLoop = e.GameLoop, userId = e.UserId, eventName = e.EventName })
    .ToArray();

var playersFromTracker = tracker.Events
    .Where(e => e.EventName.EndsWith("SPlayerSetupEvent", StringComparison.Ordinal))
    .Select(e => new
    {
        playerId = FindInt(e.Data, "m_playerId"),
        userId = FindInt(e.Data, "m_userId"),
        name = FindString(e.Data, "m_name"),
        race = FindString(e.Data, "m_race")
    })
    .ToArray();

var userToPlayer = playersFromTracker
    .Where(p => p.userId.HasValue && p.playerId.HasValue)
    .ToDictionary(p => p.userId!.Value, p => p.playerId!.Value);

var distinctUsersFromGame = game.Events
    .Where(e => e.UserId.HasValue)
    .Select(e => e.UserId!.Value)
    .Distinct()
    .OrderBy(id => id)
    .ToArray();
var distinctPlayers = playersFromTracker
    .Where(p => p.playerId.HasValue && p.playerId.Value > 0)
    .Select(p => p.playerId!.Value)
    .Distinct()
    .OrderBy(id => id)
    .ToArray();
var mappedPlayers = userToPlayer.Values.ToHashSet();
var unmappedPlayers = distinctPlayers.Where(id => !mappedPlayers.Contains(id)).ToArray();
var unmappedUsers = distinctUsersFromGame.Where(id => !userToPlayer.ContainsKey(id)).ToArray();
for (var i = 0; i < Math.Min(unmappedPlayers.Length, unmappedUsers.Length); i++)
    userToPlayer[unmappedUsers[i]] = unmappedPlayers[i];

var maxLoop = Math.Max(
    game.Events.Count == 0 ? 0 : game.Events.Max(e => e.GameLoop),
    tracker.Events.Count == 0 ? 0 : tracker.Events.Max(e => e.GameLoop));

var unitOwnerByTag = new Dictionary<ulong, int>();
var ownerKills = new Dictionary<int, int>();
var ownerLosses = new Dictionary<int, int>();
var ownerBorn = new Dictionary<int, int>();
var ownerUpgrades = new Dictionary<int, int>();
var playerStats = new Dictionary<int, List<PlayerStatPoint>>();

foreach (var evt in tracker.Events)
{
    if (evt.EventName.EndsWith("SUnitBornEvent", StringComparison.Ordinal) ||
        evt.EventName.EndsWith("SUnitInitEvent", StringComparison.Ordinal))
    {
        var owner = FindInt(evt.Data, "m_controlPlayerId", "m_playerId");
        var tag = FindUnitTag(evt.Data);
        if (owner.HasValue)
        {
            Increment(ownerBorn, owner.Value);
            if (tag.HasValue) unitOwnerByTag[tag.Value] = owner.Value;
        }
    }
    else if (evt.EventName.EndsWith("SUnitOwnerChangeEvent", StringComparison.Ordinal))
    {
        var owner = FindInt(evt.Data, "m_controlPlayerId", "m_playerId");
        var tag = FindUnitTag(evt.Data);
        if (owner.HasValue && tag.HasValue) unitOwnerByTag[tag.Value] = owner.Value;
    }
    else if (evt.EventName.EndsWith("SUnitDiedEvent", StringComparison.Ordinal))
    {
        var killer = FindInt(evt.Data, "m_killerPlayerId");
        var tag = FindUnitTag(evt.Data);
        if (killer.HasValue && killer.Value > 0) Increment(ownerKills, killer.Value);
        if (tag.HasValue && unitOwnerByTag.TryGetValue(tag.Value, out var victimOwner) && victimOwner > 0)
            Increment(ownerLosses, victimOwner);
    }
    else if (evt.EventName.EndsWith("SUpgradeEvent", StringComparison.Ordinal))
    {
        var owner = FindInt(evt.Data, "m_playerId", "m_controlPlayerId");
        if (owner.HasValue && owner.Value > 0) Increment(ownerUpgrades, owner.Value);
    }
    else if (evt.EventName.EndsWith("SPlayerStatsEvent", StringComparison.Ordinal))
    {
        var owner = FindInt(evt.Data, "m_playerId");
        if (!owner.HasValue || owner.Value <= 0) continue;
        if (!playerStats.TryGetValue(owner.Value, out var list))
        {
            list = [];
            playerStats[owner.Value] = list;
        }
        list.Add(new PlayerStatPoint(
            evt.GameLoop,
            FindInt(evt.Data, "m_scoreValueFoodUsed"),
            FindInt(evt.Data, "m_scoreValueFoodMade"),
            FindInt(evt.Data, "m_scoreValueWorkersActiveCount"),
            FindInt(evt.Data, "m_scoreValueMineralsCurrent"),
            FindInt(evt.Data, "m_scoreValueVespeneCurrent"),
            FindInt(evt.Data, "m_scoreValueMineralsCollectionRate"),
            FindInt(evt.Data, "m_scoreValueVespeneCollectionRate")));
    }
}

var gameActionsByPlayerAndPhase = BuildGameActionPhases(game.Events, userToPlayer, maxLoop);

var allPlayerIds = playersFromTracker
    .Select(p => p.playerId)
    .Where(id => id.HasValue && id.Value > 0)
    .Select(id => id!.Value)
    .Distinct()
    .OrderBy(id => id)
    .ToArray();

var playerTrends = allPlayerIds
    .Select(playerId =>
    {
        playerStats.TryGetValue(playerId, out var stats);
        var orderedStats = (stats ?? []).OrderBy(s => s.GameLoop).ToArray();
        var firstStat = orderedStats.FirstOrDefault();
        var lastStat = orderedStats.LastOrDefault();
        gameActionsByPlayerAndPhase.TryGetValue(playerId, out var phases);
        return new
        {
            playerId,
            setup = playersFromTracker.FirstOrDefault(p => p.playerId == playerId),
            unitsBorn = ownerBorn.GetValueOrDefault(playerId),
            unitsLost = ownerLosses.GetValueOrDefault(playerId),
            kills = ownerKills.GetValueOrDefault(playerId),
            upgrades = ownerUpgrades.GetValueOrDefault(playerId),
            gameActionPhaseCounts = phases ?? new GameActionPhases(0, 0, 0),
            economy = new
            {
                first = firstStat,
                last = lastStat,
                deltas = firstStat is null || lastStat is null ? null : new
                {
                    foodUsed = Delta(firstStat.FoodUsed, lastStat.FoodUsed),
                    foodMade = Delta(firstStat.FoodMade, lastStat.FoodMade),
                    workers = Delta(firstStat.WorkersActive, lastStat.WorkersActive),
                    minerals = Delta(firstStat.MineralsCurrent, lastStat.MineralsCurrent),
                    vespene = Delta(firstStat.VespeneCurrent, lastStat.VespeneCurrent),
                    mineralRate = Delta(firstStat.MineralRate, lastStat.MineralRate),
                    vespeneRate = Delta(firstStat.VespeneRate, lastStat.VespeneRate)
                }
            }
        };
    })
    .ToArray();

var output = new
{
    replayPath = Path.GetFullPath(replayPath),
    parseAsyncError,
    streamStats,
    baseBuild = build,
    headerDecodeError = headerError,
    gameEventsDecoded = game.Events.Count,
    gameDecodeError = game.Error,
    messageEventsDecoded = message.Events.Count,
    messageDecodeError = message.Error,
    trackerEventsDecoded = tracker.Events.Count,
    trackerDecodeError = tracker.Error,
    firstGame10,
    topGameEventNames = gameTopNames,
    topTrackerEventNames = trackerTopNames,
    trackerPlayers = playersFromTracker,
    parsedTracePlayers = parsedTrace?.Players.Select(p => new
    {
        p.PlayerId,
        p.Name,
        race = p.Race.ToString(),
        result = p.Result.ToString()
    }).ToArray(),
    maxGameLoop = maxLoop,
    playerTrends
};

Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(output, new System.Text.Json.JsonSerializerOptions
{
    WriteIndented = true
}));

return 0;

static DecodeResult DecodeSafely(Func<IEnumerable<ProtocolEvent>> run)
{
    var list = new List<ProtocolEvent>();
    try
    {
        foreach (var item in run()) list.Add(item);
        return new DecodeResult(list, null);
    }
    catch (Exception ex)
    {
        return new DecodeResult(list, $"{ex.GetType().Name}: {ex.Message}");
    }
}

static int? FindInt(JsonNode? node, params string[] names)
{
    var value = FindNode(node, names);
    if (value is JsonValue v)
    {
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return unchecked((int)l);
    }
    return null;
}

static int? FindNumber(JsonNode? node)
{
    if (node is JsonValue value && value.TryGetValue<int>(out var number)) return number;
    if (node is JsonObject objectNode)
        return objectNode.Select(item => FindNumber(item.Value)).FirstOrDefault(item => item.HasValue);
    if (node is JsonArray arrayNode)
        return arrayNode.Select(FindNumber).FirstOrDefault(item => item.HasValue);
    return null;
}

static string? FindString(JsonNode? node, params string[] names) =>
    FindNode(node, names) is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

static JsonNode? FindNode(JsonNode? node, params string[] names)
{
    if (node is JsonObject obj)
    {
        foreach (var n in names)
            if (obj.TryGetPropertyValue(n, out var value))
                return value;
        foreach (var child in obj.Select(x => x.Value))
        {
            var found = FindNode(child, names);
            if (found is not null) return found;
        }
    }
    return null;
}

static ulong? FindUnitTag(JsonNode? data)
{
    var index = FindInt(data, "m_unitTagIndex", "m_unitIndex");
    var recycle = FindInt(data, "m_unitTagRecycle");
    if (!index.HasValue) return null;
    return ((ulong)index.Value << 18) | (uint)(recycle ?? 0);
}

static void Increment(Dictionary<int, int> map, int key)
{
    map[key] = map.TryGetValue(key, out var value) ? value + 1 : 1;
}

static int? Delta(int? from, int? to) =>
    from.HasValue && to.HasValue ? to.Value - from.Value : null;

static Dictionary<int, GameActionPhases> BuildGameActionPhases(
    IReadOnlyList<ProtocolEvent> gameEvents,
    IReadOnlyDictionary<int, int> userToPlayer,
    int maxLoop)
{
    var byPlayer = new Dictionary<int, GameActionPhasesAccumulator>();
    if (maxLoop <= 0) return [];

    var firstCut = maxLoop / 3;
    var secondCut = (maxLoop * 2) / 3;

    foreach (var e in gameEvents)
    {
        if (!e.UserId.HasValue || !userToPlayer.TryGetValue(e.UserId.Value, out var playerId)) continue;
        if (!byPlayer.TryGetValue(playerId, out var acc))
        {
            acc = new GameActionPhasesAccumulator();
            byPlayer[playerId] = acc;
        }
        if (e.GameLoop <= firstCut) acc.Early += 1;
        else if (e.GameLoop <= secondCut) acc.Mid += 1;
        else acc.Late += 1;
    }

    return byPlayer.ToDictionary(
        kv => kv.Key,
        kv => new GameActionPhases(kv.Value.Early, kv.Value.Mid, kv.Value.Late));
}

internal sealed record DecodeResult(List<ProtocolEvent> Events, string? Error);
internal sealed record PlayerStatPoint(
    int GameLoop,
    int? FoodUsed,
    int? FoodMade,
    int? WorkersActive,
    int? MineralsCurrent,
    int? VespeneCurrent,
    int? MineralRate,
    int? VespeneRate);
internal sealed record GameActionPhases(int Early, int Mid, int Late);
internal sealed class GameActionPhasesAccumulator
{
    public int Early { get; set; }
    public int Mid { get; set; }
    public int Late { get; set; }
}
