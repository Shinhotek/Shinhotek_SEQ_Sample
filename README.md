# Shinhotek_SEQ_Sample

`Shinhotek_SEQ_Sample` 는 **[Shinhotek/LumositySWInterface](https://github.com/Shinhotek/LumositySWInterface)** 의 `XMLInterface` 를 사용할 때,
현장에서 문제가 자주 발생하는 "타이밍 / 프레임 동기화 / 설정 반영 순서" 를 피하고 **안정적으로 동작하는 호출 Sequence (모범 사용 순서)** 를 보여주는 샘플 콘솔 앱입니다.

핵심은 아래 패턴입니다.

- **[설정 변경]** → **[다음 프레임 / 평가 도착을 이벤트로 대기]** → **[GetEvaluationResult로 결과 읽기]**
- 이미지 저장 / 평가 읽기 등 **결과를 사용하는 시점에는 항상 "최신 프레임 도착"을 보장**

---

## 요구사항
- `.NET Framework 4.8`
- Lumosity Software 실행 및 XML Remote Control 사용 가능 상태
- 기본 접속 정보: `127.0.0.1:4096` (샘플의 기본값)

---

## 실행 방법

### 1) 기본 실행
- `Shinhotek_SEQ_Sample` 실행 시, 샘플은 무한 루프 (`while(true)`) 형태로 아래 전체 순서 (Sequence) 를 반복 수행합니다.

### 2) Config 파일 적용 (선택)
샘플은 실행 인자로 Config를 주면 `LoadConfigFile`을 수행합니다.

- `--conf <path>` 또는 `-c <path>`

예:
- `Shinhotek_SEQ_Sample.exe --conf C:\path\to\config.ini`

---

## 이벤트 기반 동기화 설계 (중요)

샘플은 `XMLInterface.FrameEvaluations` 이벤트를 통해 최신 프레임 도착을 감지하고,
`AutoResetEvent`(`_frameArrived`)로 **동기화 (대기 / 타임아웃)** 를 구현합니다.

- 이벤트 핸들러: `OnFrameEvaluations`
- 대기 함수: `WaitFrameArrived(title, timeoutMs)`

이 구조 덕분에,
- 설정 변경 직후 결과를 즉시 읽어 "이전 프레임 값" 을 읽는 문제
- 장비 / PC 성능 차이로 인한 동시 실행 타이밍 충돌 문제
을 크게 줄일 수 있습니다.

---

## 권장 안정 시퀀스 (Program.cs 흐름 요약)

샘플의 전체 흐름은 `Program.Main`을 그대로 따라가면 됩니다.

### [1/7] Connect
1. `Connect(ip, port)`
2. 이벤트 핸들러 연결:
   - `FrameEvaluations += OnFrameEvaluations`
   - `Disconnected += OnDisconnected`
   - `ErrorOccurred += OnErrorOccurred`

### [2/7] 장비 정보 조회
1. `CmdGetInfomation()`
2. `Version`, `CamID`, `CamWidth/CamHeight`, `Exposure` 등 출력

### [3/7] Config 로드 (선택 사항)
1. 인자 `--conf/-c`가 있으면 `LoadConfigFile(path)` 수행

### [4/7] 평가 항목 선정 + Continuous 설정
1. `ClearUseEvaluations()`
2. `AddUseEvaluation(...)`로 필요한 평가 항목만 선택  
   (예: `FRAME_NUMBER`, `FRAME_DATE`, `FRAME_TIME`, `FRAME_BEAMWIDTH_LONG/SHORT`,  
   `VERTICAL_1090_LENGTH_ASCENT/DESCENT`, `ROI2D_UNIFORMITY/MEAN/STDDEV`)
3. 연속 평가 / 프레임 제한 등 핵심 옵션 설정
   - `NumberRestrictionEnable = true`
   - `NumberRestrictionValue = _frameCount`
   - `IsEvaluationContinuous = true`

### [5/7] Start + 초기 프레임 수신 (워밍업)
1. `Start()` 호출
2. `_currframeNumber`가 `_frameCount`에 도달할 때까지 대기 (안전장치 타임아웃 포함)
3. `Stop()` 호출

이 단계는 **"초기 안정화"** 목적이며, 이후 단계에서 설정 반영 / 결과 읽기가 보다 안정적입니다.

### 이미지 저장 (대표적인 안전 패턴)
1. `WaitFrameArrived("이미지 저장 전 최신 프레임 대기", 3000)`
2. `SaveImageTif(path)`

### Evaluation 결과 읽기 (대표적인 안전 패턴)
- **반드시** 프레임 도착을 보장한 뒤 `GetEvaluationResult(key)` 호출
- 예: Beam size
  - `FRAME_BEAMWIDTH_LONG`
  - `FRAME_BEAMWIDTH_SHORT`

### Steepness 측정 (크로스 섹션 위치를 바꾸며 반복 측정)
- 공통 패턴:
  1. 크로스 섹션 설정 변경 (`FrameCrossSectionRow/Col`)
  2. `WaitFrameArrived("... 결과 대기", 3000)`
  3. `GetEvaluationResult("VERTICAL_1090_LENGTH_ASCENT/DESCENT")`로 읽기

샘플은 20 × 20mm 영역에서 5 개 지점을 측정하는 예를 제공합니다.

### Uniformity ROI 측정 (ROI 설정 후 결과 읽기)
- 공통 패턴:
  1. ROI / Blur 설정 변경
     - `BlurEnable = true`, `BlurKernelValue = 3`
     - `FrameROILeft/Top/Width/Height`, `FrameROIActive = true`
  2. `WaitFrameArrived("Uniformity 결과 대기", 3000)`
  3. `ROI2D_UNIFORMITY`, `ROI2D_MEAN`, `ROI2D_STDDEV` 읽기

### Disconnect
- `Disconnect()` 호출로 세션 종료

---

## 이 샘플이 강조하는 Best Practice

1. **설정 변경 직후 즉시 결과를 읽지 말 것**
   - 장비는 "다음 프레임"부터 설정이 반영될 수 있음
   - 따라서 `WaitFrameArrived(...)`로 다음 프레임 도착을 보장

2. **이벤트 기반 동기화를 사용할 것**
   - Sleep 기반 폴링보다 안정적
   - 타임아웃을 반드시 두어 무한 대기를 방지

3. **초기 프레임을 받은 뒤 본 측정을 수행할 것**
   - Start 직후 몇 프레임은 내부 상태가 안정화되지 않은 경우가 발생 할 수 있음

---

## 관련 프로젝트

- Lumosity XML 인터페이스 라이브러리 / 예제:
  - https://github.com/Shinhotek/LumositySWInterface

---

## 라이선스
원본 라이브러리 / 프로젝트의 라이선스를 따릅니다. (상세는 원본 저장소 참고)

