# SC2ReplayTrace

Blizzard `s2protocol`의 공식 JSON 스키마를 빌드 시 고정 커밋에서 내려받아
사용하는 순수 .NET SC2Replay 파서입니다. 비상업적 프로젝트이며, Blizzard Entertainment와 제휴하거나
공식적으로 보증받지 않습니다.

## 현재 API

## NuGet 패키지 사용

배포된 NuGet 패키지는 스키마와 생성된 강한 타입을 라이브러리 어셈블리에 포함하므로,
패키지를 설치한 소비자 프로젝트에는 `Tools`, Python, 스키마 다운로드 단계가 필요하지 않습니다.

```xml
<PackageReference Include="Biz.Bizadm.SC2ReplayTrace" Version="1.0.0" />
```

```csharp
var parser = new Sc2ReplayParser();
var trace = await parser.ParseAsync("game.SC2Replay");
```

`Tools\SchemaFetcher`와 `Tools\SchemaGenerator`는 저장소에서 패키지를 빌드할 때만
사용되는 개발용 도구입니다. 패키지 설치 후 소비자 프로젝트의 빌드에는 실행되지 않습니다.

```csharp
var parser = new Sc2ReplayParser();
await using var stream = File.OpenRead("game.SC2Replay");

// MPQ 내부의 공식 replay.* 스트림 추출
ReplayStreams files = await parser.ParseRawAsync(stream);

// 공식 프로토콜 버전 선택 및 스키마 기반 값 디코딩
var schema = ProtocolSchemas.Load(97563);
var header = new SchemaValueDecoder(
    schema,
    files.Files["replay.header"],
    isVersioned: true).Decode("NNet.Replay.SHeader");
```

`ParseAsync`는 정형 `ReplayTrace` API를 제공하며, `ParseRawAsync`는 공식 스트림을
손실 없이 후속 디코더에서 사용할 수 있도록 반환합니다. 공식 스키마는 base build
이하에서 가장 가까운 버전을 자동 선택합니다.

빌드 시 Blizzard `s2protocol`의 커밋
`fbb98e80aee825d6deeabd7b48b51cbecebde062`에서 스키마를 다운로드합니다.
`SchemaGenerator`가 tracker 이벤트의 강한 타입 C# DTO를 생성하며, 생성 파일과
다운로드 파일은 `obj` 아래에만 생성되어 저장소에는 포함하지 않습니다.

`ParseAsync`는 먼저 `replay.header`에서 실제 `m_baseBuild`를 읽은 뒤 해당 빌드에
맞는 스키마를 선택합니다. details/initdata와 game/message 이벤트는 원시 JSON 및
이벤트명·게임 루프·사용자 ID를 보존하며, tracker 위치 이벤트는 공식 delta unit
index와 좌표 배율 4 규칙을 적용합니다.

## 라이선스 및 제3자 고지

SC2ReplayTrace 자체의 소스 코드는 루트의 `LICENSE`에 명시된 라이선스를
따릅니다. Blizzard `s2protocol`에서 제공되거나 포팅된 프로토콜 스키마와
디코딩 규칙에는 원본 MIT 라이선스가 적용됩니다.

Blizzard 저작권 및 MIT 라이선스 전문은
`SC2ReplayTrace/Protocol/S2Protocol.LICENSE`에 포함되어 있으며, 적용 범위와
상표 비제휴 고지는 `THIRD-PARTY-NOTICES.md`에서 확인할 수 있습니다.

StarCraft II 및 Blizzard Entertainment는 Blizzard Entertainment, Inc.의
상표 또는 등록 상표입니다.
