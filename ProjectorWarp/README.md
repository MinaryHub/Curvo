# ProjectorWarp

굴곡진 벽면·기둥·아치 구조물에 프로젝터를 투사할 때, 원본 화면을 실시간으로 **역왜곡(warping)** 시켜
실제 벽면에서 직선이 직선으로 보이도록 맞추는 Windows 데스크톱 유틸리티입니다.

소스는 두 가지 방식으로 받을 수 있습니다.

- **내장 재생** — 동영상 파일이나 PPT·PDF·이미지를 앱에 직접 열어 재생합니다. 외부 플레이어나 PowerPoint 를 띄워 둘 필요가 없습니다.
- **화면 캡처** — 이미 실행 중인 창을 캡처합니다.

어느 쪽이든 베지어 곡면 + 코너 핀으로 기하 보정한 뒤 프로젝터가 연결된 디스플레이에 전체화면으로 출력합니다.

---

## 동작 방식

```
[동영상 파일]  → Media Foundation Media Engine (하드웨어 디코딩) ┐
[PPT/PDF/이미지] → 슬라이드 이미지 → D3D11 텍스처              ├→ ShaderResourceView
[소스 창]      → Windows.Graphics.Capture FramePool            ┘
   → 워핑 메시 렌더 (VS: 베지어 곡면 정점 / PS: 텍스처 샘플 + 색보정 + 블렌딩)
   → DXGI Flip-model SwapChain
   → [프로젝터 출력 창 (borderless fullscreen)]
```

동영상과 캡처는 **전 구간 GPU 상주**입니다. 프레임을 CPU 로 내리거나 비트맵으로 변환하지 않으며,
디코딩·캡처·렌더링이 동일한 `ID3D11Device` 를 공유합니다
(슬라이드는 정지 이미지라 장을 넘길 때 한 번만 업로드합니다).
동영상 오디오는 Media Engine 이 기본 출력 장치로 직접 재생합니다.

---

## 요구 사항

| 항목 | 값 |
|---|---|
| OS | Windows 10 1903(빌드 18362) 이상 · Windows 11 권장 |
| 런타임 | .NET 8 (단일 실행파일 배포 시 불필요) |
| 그래픽 | Direct3D 11 지원 GPU (실패 시 WARP 소프트웨어 렌더러로 자동 폴백) |

`GraphicsCaptureSession.IsSupported()` 가 false 인 환경에서는 시작 시 안내 후 종료합니다.

---

## 빌드 · 실행

```powershell
# 개발 실행
dotnet run

# 단일 실행파일 배포 (self-contained, 압축 포함 약 70MB)
dotnet publish -c Release -o publish
```

`ProjectorWarp.csproj` 에 `PublishSingleFile` / `SelfContained` / `win-x64` 가 이미 지정되어 있습니다.

### 검증 스크립트

컨트롤 패널 없이 파이프라인 전체를 점검합니다.

```powershell
# 기본 검증
dotnet run --project tests\ProjectorWarp.Checks

# 내장 재생까지 검증 (샘플 동영상 파일과, slide-*.png / *.pdf / *.pptx 가 든 폴더를 넘긴다)
dotnet run --project tests\ProjectorWarp.Checks -- "D:\sample.mp4" "D:\slides"
```

확인 항목:

- HLSL 셰이더 컴파일 (warp / overlay, VS·PS)
- 베지어 항등성(3×3~6×6), 차수 상승이 곡면을 보존하는지
- 호모그래피 코너 매핑과 역변환 왕복 오차
- 메시 생성(무보정 항등 · 코너 핀 모서리 일치), 인덱스 버퍼 범위
- 제어점 직렬화 왕복, 실행 취소/다시 실행
- D3D11 디바이스 + WinRT 상호 운용 디바이스 생성
- 워핑 렌더러 리소스(셰이더·입력 레이아웃·상태 객체) 생성
- 모니터 열거, 캡처 아이템 생성, 실제 WGC 프레임 수신
- 출력 창 스왑체인 생성 · 프레젠트 · 캡처 제외 적용
- 앱 설정 저장/복원, 로그온 자동 실행 등록/해제, 프리셋 파일 왕복
- 자동 업데이트: 릴리스 JSON 해석 · 태그 버전 비교 · 저장소 표기 정규화
- 내장 동영상: 파일 열기 · 프레임을 D3D11 텍스처로 전송 · 실제 픽셀 내용 · 길이 · 일시정지
- 슬라이드: 이미지 목록 / PDF 변환(캐시 포함) / PPTX 변환(PowerPoint)
- 내장 미디어 프레임을 워핑해 출력 창에 실제로 프레젠트

> 마지막 항목은 마지막 모니터에 테스트 패턴을 **약 0.5초간 표시**한 뒤 닫습니다.

---

## 사용 순서

1. **소스 선택** — 두 가지 탭 중 하나를 씁니다.
   - **[내장 재생]** — [동영상 열기] 또는 [슬라이드 열기] 로 파일을 고르면 즉시 재생이 시작됩니다(권장, 기본 탭).
   - **[창]** — 실행 중인 창을 캡처합니다. `PowerPoint` / `플레이어` 버튼으로 바로 찾을 수 있고, 상단에 실시간 미리보기가 표시됩니다.
2. **출력 모니터 선택** 후 [출력 시작] — 해당 모니터에 borderless 전체화면 창이 열립니다.
3. **[캡처 시작]** — 소스 화면이 프로젝터로 나가기 시작합니다.
4. **F1** 로 편집 모드를 켜고 제어점을 드래그해 곡면을 맞춥니다. **F2** 로 테스트 패턴을 띄우면 정렬이 쉽습니다.
5. **[현재 설정 저장]** 또는 **Ctrl+S** 로 저장합니다. 앱을 끄면 마지막 상태가 자동 저장되어 다음 실행 때 복원됩니다.
6. 매번 같은 환경에서 쓴다면 **[8. 설정 저장 · 자동 시작]** 에서 로그온 자동 실행과 자동 투사 시작을 켜 두면
   PC 를 켜는 것만으로 투사가 시작됩니다.

### 기하 보정 3단계

| 단계 | 내용 |
|---|---|
| 1. 코너 핀 / 키스톤 | 4개 모서리를 드래그해 3×3 호모그래피를 산출. 텍스처 좌표를 투영 좌표 `(u·w, v·w, w)` 로 전달해 원근 보간 왜곡을 제거합니다. |
| 2. 베지어 곡면 | 기본 4×4(16개) 제어점의 bicubic 곡면. 3×3 ~ 6×6 격자, 테셀레이션 16×16 ~ 128×128(기본 64×64) 조절 가능. |
| 3. 마스킹 | 다각형 블랙 마스크를 추가/삭제. 편집 모드에서 꼭짓점 드래그, 오른쪽 클릭으로 꼭짓점 삭제. |

격자 크기를 **늘릴 때는 차수 상승(degree elevation)** 으로 곡면 형상을 그대로 보존합니다.
줄일 때는 기존 곡면을 새 격자 위치에서 샘플링하므로 형상이 근사됩니다.

---

## 내장 재생 (외부 프로그램 없이)

[내장 재생] 탭에서 파일을 열면 앱이 직접 디코딩·표시합니다. 플레이어나 PowerPoint 창을 캡처할 필요가 없어
창 제목 변화, 렌더러 설정, 창 크기 변경 같은 변수에서 자유롭습니다.

### 동영상

- Media Foundation Media Engine 으로 **하드웨어 디코딩**합니다. Windows 에서 재생되는 코덱이면 그대로 재생됩니다
  (MP4/H.264·H.265, MOV, WMV, AVI, MKV, WebM 등 — 설치된 코덱에 따름).
- 오디오도 같이 재생됩니다. 음량은 컨트롤 패널의 [음량] 슬라이더로 조절합니다.
- 반복 재생, 위치 이동(시크), 일시정지를 지원합니다.

### 슬라이드 (PPT · PDF · 이미지)

| 형식 | 처리 방식 |
|---|---|
| 이미지 (PNG·JPG·BMP·GIF·TIFF) | 그대로 슬라이드로 사용. 여러 장을 한 번에 선택하면 파일명 순서로 정렬됩니다. |
| PDF | **Windows 내장 PDF 렌더러**로 페이지별 이미지 생성. 외부 프로그램이 전혀 필요 없습니다. |
| PPTX · PPT · PPSX | **가져올 때 한 번** PowerPoint 로 PNG 내보내기를 수행합니다. 이후 재생은 PowerPoint 없이 이뤄집니다. |

변환 결과는 `%AppData%\ProjectorWarp\SlideCache\` 에 캐시되어, 같은 파일을 다시 열면 즉시 로드됩니다
(파일이 수정되면 자동으로 다시 변환합니다).

> **PowerPoint 가 없는 PC** 에서는 PPT 를 변환할 수 없습니다. 발표 자료가 있는 PC 에서
> [파일 → 내보내기 → PDF] 로 저장해 그 PDF 를 여세요. 애니메이션·전환 효과는 이미지 변환 과정에서 사라집니다.

### 재생 제어

컨트롤 패널의 [재생 제어] 그룹 또는 출력 창 단축키로 조작합니다.

| 조작 | 단축키 |
|---|---|
| 재생 / 일시정지 (슬라이드는 다음 장) | `Space` |
| 이전 / 다음 슬라이드 | `PgUp` / `PgDn` |
| 처음부터 | 컨트롤 패널 [처음부터] |
| 자동 전환 | [자동 전환] 슬라이더 (0 = 수동, 최대 120초) |

---

## 단축키 (출력 창)

| 키 | 동작 |
|---|---|
| `F1` | 편집 모드 토글 |
| `F2` | 테스트 패턴 순환 (없음 → 격자 → 체커보드 → 원형 링 → 컬러바 → 화이트 → 블랙) |
| `F3` | 참조 그리드 토글 |
| `F4` | 대각선 토글 |
| `Ctrl+S` / `Ctrl+O` | 프리셋 저장 / 열기 |
| `Ctrl+R` | 워핑 초기화 |
| `Ctrl+Z` / `Ctrl+Y` | 실행 취소 / 다시 실행 |
| `Esc` | 전체화면 해제 (창 모드 ↔ 전체화면 전환) |
| 방향키 | 선택한 점 1px 이동 (`Shift` 함께 누르면 10px) |
| `M` / `Del` | 마스크 추가 / 선택 마스크 삭제 |
| `Space` | 재생 / 일시정지 (슬라이드는 다음 장) |
| `PgUp` / `PgDn` | 이전 / 다음 슬라이드 |

마우스: 왼쪽 드래그로 제어점·코너·마스크 꼭짓점 이동, 오른쪽 클릭으로 마스크 꼭짓점 삭제.

---

## 프리셋

`%AppData%\ProjectorWarp\` 에 JSON 으로 저장합니다. 종료 시 `last-session.json` 이 자동 저장됩니다.

```json
{
  "version": 1,
  "name": "회의실 곡면벽",
  "source": { "type": "window", "matchTitle": "PowerPoint 슬라이드 쇼", "matchProcess": "POWERPNT" },
  "output": { "monitorDeviceName": "\\\\.\\DISPLAY2" },
  "cornerPin": { "enabled": true, "points": [[0,0],[1,0],[1,1],[0,1]] },
  "bezier": { "enabled": true, "gridSize": 4, "tessellation": 64, "controlPoints": [[0,0], "... 16개"] },
  "color": { "enabled": false, "brightness": 1.0, "contrast": 1.0, "gamma": 1.0 },
  "edgeBlend": { "enabled": false, "left": 0.0, "right": 0.0, "top": 0.0, "bottom": 0.0, "gamma": 2.2 },
  "masks": { "enabled": false, "polygons": [] },
  "media": { "kind": "video", "path": "D:\\loop.mp4", "loop": true, "volume": 0.8, "slideIntervalSeconds": 0 }
}
```

`media.kind` 가 `video` 또는 `slides` 면 캡처 대신 그 파일을 재생합니다(`none` 이면 캡처 소스를 사용).

프리셋을 불러오면 기록된 모니터와 창(제목/프로세스 힌트)을 다시 찾아 자동으로 연결합니다.
창을 찾지 못하면 상태 표시줄에 안내가 나오며 목록에서 직접 고르면 됩니다.

---

## 설정 저장 · 자동 시작

### 저장되는 것

| 파일 | 내용 | 저장 시점 |
|---|---|---|
| `%AppData%\ProjectorWarp\last-session.json` | 보정값 · 캡처 소스 · 출력 모니터 | 앱 종료 시 자동, [현재 설정 저장] 클릭 시 |
| `%AppData%\ProjectorWarp\app-settings.json` | 자동 시작 옵션, 항상 위 여부(기본 꺼짐), 시작 프리셋 경로 | 옵션을 바꿀 때마다 즉시 |
| 임의 위치 `*.json` | 프리셋 (Ctrl+S 로 다른 이름으로 저장) | 수동 |

**[현재 설정 저장]** 은 보정값을 *시작 시 사용할 프리셋* 으로 지정된 파일에 씁니다.
지정된 프리셋이 없으면 `last-session.json` 에 씁니다. 즉 저장 → 재실행하면 그대로 복원됩니다.

### 자동 시작 옵션

| 옵션 | 동작 |
|---|---|
| Windows 로그온 시 자동 실행 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 에 `"exe경로" --autostart` 를 등록합니다. 관리자 권한이 필요 없고, 체크를 풀면 즉시 삭제됩니다. |
| 앱 시작 시 자동으로 출력·캡처 시작 | 저장된 출력 모니터에 출력 창을 열고, 저장된 캡처 소스에 자동으로 연결합니다. |
| 컨트롤 패널을 최소화 상태로 시작 | 창을 최소화한 채 시작합니다(무인 운영용). |
| 재시도 시간 | 캡처 대상 창이 아직 실행되지 않았을 때 2초 간격으로 재시도하는 최대 시간(기본 60초, 0~300초). |

동작 순서:

1. 로그온 자동 실행으로 시작된 경우(`--autostart`) 디스플레이와 대상 앱이 준비될 시간을 두기 위해 **5초** 기다립니다.
2. 2초 간격으로 목록을 새로 읽어 저장된 출력 모니터 → 캡처 소스 순으로 연결합니다.
3. 재시도 시간이 지나도 대상을 못 찾으면 중단하고 상태 표시줄에 이유를 표시합니다.
4. 도중에 사용자가 직접 [캡처 시작]/[출력 중지] 를 누르거나 [자동 시작 취소] 를 누르면 재시도를 멈춥니다.

> 자동 실행 경로는 앱을 다른 폴더로 옮기면 다음 실행 때 자동으로 갱신됩니다.
> 단일 실행파일(`publish\ProjectorWarp.exe`)을 고정 위치에 두고 등록하는 것을 권장합니다.

---

## 버전 · 자동 업데이트

컨트롤 패널 제목과 **[9. 버전 · 업데이트]** 에 현재 버전이 표시됩니다.
버전 값은 `ProjectorWarp.csproj` 의 `<Version>` 이고, **GitHub 릴리스 태그와 이 값을 맞춰야** 새 버전으로 인식됩니다.

### 사용하는 쪽

설정할 것이 없습니다. 배포처는 빌드에 고정되어 있습니다(`AppConfig.UpdateRepository`).

1. `앱을 시작할 때 새 버전 확인` 을 켜 두면 시작 5초 뒤에 조용히 확인하고, 새 버전이 있을 때만 알립니다.
   패널에는 확인 대상 저장소가 읽기 전용으로 표시됩니다.
2. 새 버전이 있으면 **[설치 후 재시작]** 버튼이 나타납니다. 누르면 내려받아 교체하고 앱을 다시 시작합니다.
   투사 중에 저절로 재시작되는 일은 없습니다 — 누르지 않으면 아무 일도 일어나지 않습니다.

### 배포하는 쪽

```powershell
# 1. 버전을 올린다 (ProjectorWarp.csproj)
#    <Version>1.0.1</Version>

# 2. 단일 실행파일을 만든다
dotnet publish -c Release -o publish

# 3. GitHub 에 태그 v1.0.1 로 릴리스를 만들고 publish\ProjectorWarp.exe 를 자산으로 올린다
```

| 항목 | 규칙 |
|---|---|
| 릴리스 태그 | `v1.0.1` 또는 `1.0.1` (`-beta` 같은 접미사는 무시하고 숫자만 비교) |
| 자산 이름 | `ProjectorWarp.exe` (없으면 릴리스의 첫 번째 `.exe`) |
| 조회 주소 | `https://api.github.com/repos/{owner}/{repo}/releases/latest` |
| 배포처 변경 | `src/AppConfig.cs` 의 `UpdateRepository` 상수 한 줄 |
| 비공개 저장소 | 아래 참고 — 실행하는 환경에 토큰이 필요합니다 |

### 비공개 저장소인 경우

공개 릴리스는 인증 없이 동작합니다. 저장소가 비공개면 GitHub 이 릴리스 조회에 `404` 를 돌려주므로
읽기 권한(`contents: read`) 토큰을 **환경 변수**로 넘겨야 합니다.

```powershell
# 배포 시 한 번 (사용자가 앱에서 입력할 값이 아니다)
setx PROJECTORWARP_GITHUB_TOKEN "github_pat_..."
```

토큰이 있으면 자산도 `browser_download_url` 대신 릴리스 자산 API 로 내려받습니다(비공개 저장소는 전자로 받을 수 없음).

> 토큰을 실행파일이나 소스에 넣지 마세요. 배포된 exe 에서 그대로 추출됩니다.
> 앱은 토큰을 저장하지도, 화면에 표시하지도 않습니다.

교체 방식 — 단일 실행파일은 실행 중 자기 자신을 덮어쓸 수 없으므로 **새 exe 가 교체를 수행합니다.**

```
새 exe 를 %LocalAppData%\ProjectorWarp\Update\ 로 내려받기
  → 새 exe 를 `--apply-update <대상 exe> <이전 PID>` 로 실행
  → 앱이 마지막 상태를 저장하며 종료
  → 새 프로세스가 이전 PID 종료를 기다린 뒤 자신을 대상 경로로 복사(실패하면 .bak 복원)
  → 대상 exe 를 다시 실행
```

> 앱이 쓰기 권한 없는 폴더(예: `C:\Program Files`)에 있으면 교체가 실패하고 이유가 표시됩니다.
> 사용자 폴더에 두고 쓰는 것을 권장합니다.

---

## 알려진 제약

- **DRM 보호 콘텐츠**(Netflix, Disney+, Amazon Prime Video 등)는 캡처 시 검은 화면으로 나오고,
  내장 재생에서도 열리지 않습니다. 우회 방법은 제공하지 않습니다.
- **코덱** — 내장 재생은 Windows 에 설치된 코덱을 사용합니다. 재생되지 않는 파일은
  MP4(H.264) 로 변환하면 대부분 해결됩니다.
- **PPT 변환에는 PowerPoint 가 필요합니다**(가져올 때 한 번만). 없으면 PDF 로 내보내 사용하세요.
  애니메이션·화면 전환·비디오가 들어간 슬라이드는 정적 이미지로 변환됩니다.
- **피드백 루프** — 출력 창에는 `WDA_EXCLUDEFROMCAPTURE` 를 적용해 캡처 대상에서 제외합니다.
  적용에 실패하면 상태 표시줄에 경고가 표시됩니다. 소스와 출력이 같은 모니터이면 컨트롤 패널에 경고가 뜹니다.
- **노란 캡처 테두리** — Windows 11(빌드 22000+)에서만 제거됩니다(`IsBorderRequired = false`).
  Windows 10 에서는 OS 제약으로 테두리가 남습니다.
- **최소화된 창**은 캡처할 수 없어 목록에서 제외됩니다. 캡처 중 소스 창을 최소화하면 프레임이 멈췄다가
  복원 시 자동으로 재개됩니다(크래시 없음).

---

## 구현 노트 (설계 결정)

- **Media Engine 소스는 `file://` URI 가 아니라 로컬 경로로 넘깁니다.** Media Engine 은 퍼센트 인코딩을
  UTF-8 이 아닌 ANSI 코드페이지로 되돌리기 때문에, `new Uri(path).AbsoluteUri` 를 주면 한글·일본어가 든
  파일명이 `0x80070002`(ERROR_FILE_NOT_FOUND) 로 실패하고 "지원하지 않는 형식" 으로 보고됩니다.
  경로를 그대로 넘기면 MF 소스 리졸버가 비ASCII·`#`·`%` 가 든 이름을 모두 엽니다.
- **Win32 상호 운용**: 소스 제너레이터(CsWin32) 대신 `src/Interop/Win32.cs` 에 손으로 작성한 P/Invoke 를 사용합니다.
  창 생성·WndProc·캡처 제외 등 사용 API 범위가 좁고, 생성 코드의 핸들 래핑 없이 원시 `HWND` 를 그대로 다루는 편이
  D3D/DXGI 호출과 맞물릴 때 단순합니다.
- **WinRT ↔ D3D11 상호 운용**: `IGraphicsCaptureItemInterop` 과 `IDirect3DDxgiInterfaceAccess` 는
  마샬링 모호성을 없애기 위해 vtable 직접 호출(`src/Interop/WinRTInterop.cs`)로 처리합니다.
- **내장 동영상**: Media Foundation **Media Engine** 을 사용합니다. DXGI 디바이스 매니저에 앱의 D3D11 디바이스를
  넘겨 `TransferVideoFrame` 으로 프레임을 우리 텍스처에 직접 받으므로 CPU 복사가 없습니다.
  이를 위해 D3D11 디바이스를 `VideoSupport` 플래그로 만듭니다(실패 시 해당 플래그 없이 재시도 — 캡처는 계속 동작).
- **PDF**: `Windows.Data.Pdf`(OS 내장)로 페이지를 렌더링하므로 별도 라이브러리가 없습니다.
- **셰이더**: `.hlsl` 파일로 분리 관리하되, 빌드 시 fxc 경로 의존성을 없애기 위해 임베디드 리소스로 포함한 뒤
  실행 시 `D3DCompile` 로 컴파일합니다. 한글 주석이 잘리지 않도록 소스는 UTF-8 **바이트**로 전달합니다.
- **TFM**: `net8.0-windows10.0.22621.0` 로 컴파일하되 `SupportedOSPlatformVersion` 은 `10.0.19041.0` 입니다.
  Windows 11 전용 API(`IsBorderRequired`)는 `ApiInformation` 으로 런타임 검사 후 사용합니다.

---

## 문서

- [docs/SETUP.md](docs/SETUP.md) — PowerPoint / 미디어 플레이어 / 디스플레이 설정
- [docs/CALIBRATION.md](docs/CALIBRATION.md) — 곡면 정렬 실무 절차

---

## 프로젝트 구조

```
ProjectorWarp/
├─ src/
│  ├─ AppConfig.cs        # 기본값 · 한계값 상수
│  ├─ Capture/            # WGC 래퍼, 창/모니터 열거
│  ├─ Media/              # 내장 동영상 재생(Media Foundation), 슬라이드 변환·표시
│  ├─ Rendering/          # D3D11 디바이스, 스왑체인, 메시 빌더, 출력 창
│  │  └─ Shaders/         # warp.hlsl, overlay.hlsl
│  ├─ Geometry/           # Bezier, Homography, ControlPointGrid, UndoHistory
│  ├─ UI/                 # WPF 컨트롤 패널, 오버레이 에디터
│  ├─ Presets/            # 프리셋 · 앱 설정 JSON 직렬화
│  ├─ Interop/            # Win32 / WinRT P/Invoke, 로그온 자동 실행 레지스트리
│  └─ Update/             # GitHub Releases 확인 · 다운로드 · 실행파일 교체
├─ tests/ProjectorWarp.Checks/   # 셰이더 컴파일 · 기하 계산 검증
└─ docs/
```
