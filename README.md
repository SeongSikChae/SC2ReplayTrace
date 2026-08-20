# SC2ReplayTrace

Blizzard `s2protocol` 기반의 순수 .NET SC2Replay 파서입니다.
리플레이 MPQ에서 공식 `replay.*` 스트림을 읽고, 정규화된 `ReplayTrace` 모델과
원시 이벤트 데이터를 함께 제공합니다.

이 프로젝트는 비상업적 프로젝트이며 Blizzard Entertainment와 제휴하거나
공식적으로 보증받지 않습니다.

## NuGet 패키지 사용

```xml
<PackageReference Include="Biz.Bizadm.SC2ReplayTrace" Version="1.0.2" />
```

패키지에는 `protocol*.json` 스키마 리소스가 포함되어 있으므로, 소비자 프로젝트에서
`Tools`, Python, 스키마 다운로드 단계를 별도로 실행할 필요가 없습니다.

## 빠른 시작

```csharp
using Biz.Bizadm.SC2ReplayTrace;
using Biz.Bizadm.SC2ReplayTrace.Models;

ReplayTrace trace = await Sc2ReplayParser.ParseAsync("game.SC2Replay");

Console.WriteLine($"Map: {trace.Map.Name}");
Console.WriteLine($"BaseBuild: {trace.BaseBuild}");
Console.WriteLine($"Players: {trace.Players.Count}");
Console.WriteLine($"Events: {trace.Events.Count}");
Console.WriteLine($"P1 Start: {trace.Players[0].StartLocation?.X}, {trace.Players[0].StartLocation?.Y}");

var moves = trace.EventsOfKind(TraceEventKind.UnitMoved);
```

## 원시 스트림 + 스키마 디코딩

```csharp
using Biz.Bizadm.SC2ReplayTrace;
using Biz.Bizadm.SC2ReplayTrace.Protocol;

await using var replayStream = File.OpenRead("game.SC2Replay");
ReplayStreams raw = await Sc2ReplayParser.ParseRawAsync(replayStream);

using var schema = ProtocolSchemas.Load(97563);
var header = new SchemaValueDecoder(
    schema,
    raw.Files["replay.header"],
    isVersioned: true).Decode("NNet.Replay.SHeader");
```

`ProtocolSchemas.Load(baseBuild)`는 요청한 빌드 이하에서 가장 가까운 내장 스키마를
자동 선택합니다.

## 현재 API

- `Sc2ReplayParser.ParseAsync(string|Stream)`:
  리플레이를 읽어 정규화된 `ReplayTrace`를 반환합니다.
- `Sc2ReplayParser.ParseRawAsync(Stream)`:
  MPQ 내부의 `replay.*` 원시 스트림을 `ReplayStreams`로 반환합니다.
- `ReplayTrace`:
  맵/버전/플레이어/이벤트와 `RawData`(details, initData, game/message 이벤트 등)를 제공합니다.
- `ReplayPlayer.StartLocation`:
  트래커 초기 본진 생성 이벤트 기준 플레이어 시작 좌표(`UnitPosition`)를 제공합니다.
- `ReplayTrace.EventsOfKind(...)`, `ReplayTrace.EventsForUnit(...)`:
  공통 조회 시나리오용 헬퍼입니다.

## 스키마/생성 코드 관리

- 저장소에는 `SC2ReplayTrace/Protocol/Schemas/protocol*.json`이 포함되어 있습니다.
- tracker 이벤트용 생성 코드 `SC2ReplayTrace/Protocol/Generated/GeneratedProtocolTypes.g.cs`가 포함되어 있습니다.
- `Tools/SchemaFetcher`, `Tools/SchemaGenerator`, `Tools/ReplayInspector`는 저장소 유지보수/검증을 위한 개발 도구입니다.

## 라이선스 및 제3자 고지

SC2ReplayTrace 자체 소스 코드는 루트의 `LICENSE`를 따릅니다.
Blizzard `s2protocol`에서 제공되거나 포팅된 스키마/디코딩 규칙은 원본 MIT 라이선스가 적용됩니다.

- Blizzard MIT 라이선스 전문: `SC2ReplayTrace/Protocol/S2Protocol.LICENSE`
- 적용 범위 및 상표 비제휴 고지: `THIRD-PARTY-NOTICES.md`

StarCraft II 및 Blizzard Entertainment는 Blizzard Entertainment, Inc.의 상표 또는 등록 상표입니다.
