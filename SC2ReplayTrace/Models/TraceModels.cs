using System.Text.Json.Nodes;

namespace Biz.Bizadm.SC2ReplayTrace.Models;

/// <summary>플레이어 종족입니다.</summary>
public enum Race
{
    /// <summary>알 수 없는 종족입니다.</summary>
    Unknown,
    /// <summary>테란입니다.</summary>
    Terran,
    /// <summary>저그입니다.</summary>
    Zerg,
    /// <summary>프로토스입니다.</summary>
    Protoss,
    /// <summary>무작위 종족입니다.</summary>
    Random
}

/// <summary>플레이어의 경기 결과입니다.</summary>
public enum MatchResult
{
    /// <summary>알 수 없는 결과입니다.</summary>
    Unknown,
    /// <summary>승리입니다.</summary>
    Win,
    /// <summary>패배입니다.</summary>
    Loss,
    /// <summary>무승부입니다.</summary>
    Draw
}

/// <summary>추적 이벤트의 종류입니다.</summary>
public enum TraceEventKind
{
    /// <summary>유닛 생성 이벤트입니다.</summary>
    UnitCreated,
    /// <summary>유닛 건설 시작 이벤트입니다.</summary>
    UnitConstructionStarted,
    /// <summary>유닛 완성 이벤트입니다.</summary>
    UnitCompleted,
    /// <summary>유닛 변환 이벤트입니다.</summary>
    UnitTransformed,
    /// <summary>유닛 사망 이벤트입니다.</summary>
    UnitDied,
    /// <summary>유닛 소유자 변경 이벤트입니다.</summary>
    UnitOwnerChanged,
    /// <summary>유닛 위치 이벤트입니다.</summary>
    UnitPosition,
    /// <summary>유닛 이동 이벤트입니다.</summary>
    UnitMoved,
    /// <summary>업그레이드 시작 이벤트입니다.</summary>
    UpgradeStarted,
    /// <summary>업그레이드 완료 이벤트입니다.</summary>
    UpgradeCompleted
}

/// <summary>플레이어 색상입니다.</summary>
/// <param name="Red">빨강 채널입니다.</param>
/// <param name="Green">초록 채널입니다.</param>
/// <param name="Blue">파랑 채널입니다.</param>
/// <param name="Alpha">알파 채널입니다.</param>
public sealed record PlayerColor(byte Red, byte Green, byte Blue, byte Alpha = 255);

/// <summary>리플레이에 참여한 플레이어입니다.</summary>
/// <param name="PlayerId">플레이어 식별자입니다.</param>
/// <param name="Name">플레이어 이름입니다.</param>
/// <param name="Race">플레이어 종족입니다.</param>
/// <param name="Result">경기 결과입니다.</param>
/// <param name="Color">플레이어 색상입니다.</param>
/// <param name="TeamId">팀 식별자입니다.</param>
public sealed record ReplayPlayer(
    int PlayerId,
    string Name,
    Race Race,
    MatchResult Result,
    PlayerColor? Color,
    int? TeamId = null);

/// <summary>맵 정보입니다.</summary>
/// <param name="Name">맵 이름입니다.</param>
/// <param name="FileName">맵 파일 이름입니다.</param>
public sealed record MapInfo(string? Name, string? FileName);

/// <summary>게임 내 유닛 위치입니다.</summary>
/// <param name="X">X 좌표입니다.</param>
/// <param name="Y">Y 좌표입니다.</param>
public sealed record UnitPosition(float X, float Y);

/// <summary>정규화된 추적 이벤트입니다.</summary>
/// <param name="Kind">이벤트 종류입니다.</param>
/// <param name="GameLoop">이벤트 게임 루프입니다.</param>
/// <param name="GameTime">이벤트 게임 시간입니다.</param>
/// <param name="PlayerId">관련 플레이어 식별자입니다.</param>
/// <param name="UnitTag">관련 유닛 태그입니다.</param>
/// <param name="UnitType">유닛 유형입니다.</param>
/// <param name="PreviousUnitType">이전 유닛 유형입니다.</param>
/// <param name="UpgradeType">업그레이드 유형입니다.</param>
/// <param name="OtherPlayerId">상대 플레이어 식별자입니다.</param>
/// <param name="OtherUnitTag">상대 유닛 태그입니다.</param>
/// <param name="Position">이벤트 위치입니다.</param>
/// <param name="AbilityName">관련 능력 이름입니다.</param>
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

/// <summary>리플레이에서 추출한 정형 추적 데이터입니다.</summary>
/// <param name="Map">맵 정보입니다.</param>
/// <param name="GameVersion">게임 버전입니다.</param>
/// <param name="BaseBuild">기본 빌드 번호입니다.</param>
/// <param name="Duration">게임 시간입니다.</param>
/// <param name="TotalGameLoops">전체 게임 루프 수입니다.</param>
/// <param name="Players">플레이어 목록입니다.</param>
/// <param name="Events">추적 이벤트 목록입니다.</param>
/// <param name="RawData">원시 디코딩 데이터입니다.</param>
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
    /// <summary>지정한 유닛의 이벤트를 반환합니다.</summary>
    /// <param name="unitTag">유닛 태그입니다.</param>
    public IEnumerable<TraceEvent> EventsForUnit(ulong unitTag) =>
        Events.Where(item => item.UnitTag == unitTag);

    /// <summary>지정한 종류의 이벤트를 반환합니다.</summary>
    /// <param name="kind">이벤트 종류입니다.</param>
    public IEnumerable<TraceEvent> EventsOfKind(TraceEventKind kind) =>
        Events.Where(item => item.Kind == kind);
}

/// <summary>리플레이의 원시 디코딩 데이터입니다.</summary>
/// <param name="Header">헤더 데이터입니다.</param>
/// <param name="Details">상세 데이터입니다.</param>
/// <param name="InitData">초기화 데이터입니다.</param>
/// <param name="GameEvents">게임 이벤트 목록입니다.</param>
/// <param name="MessageEvents">메시지 이벤트 목록입니다.</param>
/// <param name="Attributes">속성 데이터입니다.</param>
public sealed record ReplayRawData(
    JsonNode? Header,
    JsonNode? Details,
    JsonNode? InitData,
    IReadOnlyList<RawReplayEvent> GameEvents,
    IReadOnlyList<RawReplayEvent> MessageEvents,
    JsonNode? Attributes);

/// <summary>원시 프로토콜 이벤트입니다.</summary>
/// <param name="GameLoop">이벤트 게임 루프입니다.</param>
/// <param name="UserId">사용자 식별자입니다.</param>
/// <param name="EventName">이벤트 이름입니다.</param>
/// <param name="Data">이벤트 데이터입니다.</param>
public sealed record RawReplayEvent(
    int GameLoop,
    int? UserId,
    string EventName,
    JsonNode? Data);
