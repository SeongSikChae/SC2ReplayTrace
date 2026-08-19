using System.Text.Json.Nodes;

namespace Biz.Bizadm.SC2ReplayTrace.Models;

public enum Race
{
    Unknown,
    Terran,
    Zerg,
    Protoss,
    Random
}

public enum MatchResult
{
    Unknown,
    Win,
    Loss,
    Draw
}

public enum TraceEventKind
{
    UnitCreated,
    UnitConstructionStarted,
    UnitCompleted,
    UnitTransformed,
    UnitDied,
    UnitOwnerChanged,
    UnitPosition,
    UnitMoved,
    UpgradeStarted,
    UpgradeCompleted
}

public sealed record PlayerColor(byte Red, byte Green, byte Blue, byte Alpha = 255);

public sealed record ReplayPlayer(
    int PlayerId,
    string Name,
    Race Race,
    MatchResult Result,
    PlayerColor? Color,
    int? TeamId = null);

public sealed record MapInfo(string? Name, string? FileName);

public sealed record UnitPosition(float X, float Y);

public sealed record TraceEvent(
    TraceEventKind Kind,
    int GameLoop,
    TimeSpan GameTime,
    int? PlayerId = null,
    ulong? UnitTag = null,
    string? UnitType = null,
    string? PreviousUnitType = null,
    string? UpgradeType = null,
    int? OtherPlayerId = null,
    ulong? OtherUnitTag = null,
    UnitPosition? Position = null,
    string? AbilityName = null);

public sealed record ReplayTrace(
    MapInfo Map,
    string? GameVersion,
    int? BaseBuild,
    TimeSpan Duration,
    int TotalGameLoops,
    IReadOnlyList<ReplayPlayer> Players,
    IReadOnlyList<TraceEvent> Events,
    ReplayRawData? RawData = null)
{
    public IEnumerable<TraceEvent> EventsForUnit(ulong unitTag) =>
        Events.Where(item => item.UnitTag == unitTag);

    public IEnumerable<TraceEvent> EventsOfKind(TraceEventKind kind) =>
        Events.Where(item => item.Kind == kind);
}

public sealed record ReplayRawData(
    JsonNode? Header,
    JsonNode? Details,
    JsonNode? InitData,
    IReadOnlyList<RawReplayEvent> GameEvents,
    IReadOnlyList<RawReplayEvent> MessageEvents,
    JsonNode? Attributes);

public sealed record RawReplayEvent(
    int GameLoop,
    int? UserId,
    string EventName,
    JsonNode? Data);
