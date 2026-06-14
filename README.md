# ScreenPen Portable

무설치(Portable) **화면 주석 도구** — 데스크탑 화면 위에 펜·도형·텍스트로 그리고, 필요하면 화면을 통과시켜 아래 앱을 그대로 조작할 수 있는 항상-위(always-on-top) 투명 오버레이 도구입니다. [Epic Pen](https://epicpen.com)의 기능을 참고해 라이선스/구독 분기 없이 단일 무설치 빌드로 제공하는 것을 목표로 합니다.

> 상세 기능 정의는 [기능명세서.md](기능명세서.md) 참고. (Epic Pen 공식 문서 딥리서치 기반, 검증 22건)
> 📖 **사용 설명서(스크린샷 포함): [docs/manual.html](docs/manual.html)** — 브라우저로 열면 됩니다.

## 현재 상태: M3까지 구현 완료 (기능명세서 FR-01~24 전체)

투명 오버레이·클릭관통(M0)부터 Epic Pen Pro 핵심(M2)·차별화 기능(M3)까지 명세서의 모든 기능을 구현했습니다.

### 드로잉 & 편집
- ✅ 가상 화면 전체를 덮는 투명·항상-위 오버레이 (`AllowsTransparency` + `WindowStyle=None`) — 멀티모니터 동작 (FR-01/17)
- ✅ 펜 / 하이라이터(반투명) / 지우개(스트로크 단위) — `InkCanvas` (FR-02/03/04)
- ✅ 색상 팔레트(Quick Colors) + 굵기 슬라이더(1~40) (FR-05/06)
- ✅ Undo / Redo — 스트로크와 벡터 객체를 하나의 공유 타임라인(`UndoStack`)으로 통합 (FR-07)
- ✅ **도형: 직선 / 화살표 / 사각형 / 타원** — 드래그 미리보기, `Shift` 각도 스냅·정사각형/정원 (FR-14)
- ✅ **도형 채움 순환**: 없음 → 컬러 → 외곽선 → 흰색 → 검정 (FR-23)
- ✅ **텍스트 입력** — 클릭 위치에 편집 박스, 색·크기 지정 (FR-15)
- ✅ **넘버링 태그** — 클릭마다 1,2,3… 원형 번호 (FR-22)

### 모드 & 효과
- ✅ **그리기 / 통과(click-through) 모드 토글** — `Ctrl+Alt+D` (통과 모드에서 `WS_EX_TRANSPARENT`) (FR-09)
- ✅ **화이트보드 / 블랙보드** — 불투명 배경 순환, 특정 모니터 지정 가능 (FR-16)
- ✅ **고스트 모드** — 툴바를 숨기고 전역 단축키만으로 사용 (FR-19)
- ✅ **페이딩 잉크** — 그린 뒤 일정 시간 후 자동 사라짐 (FR-20)
- ✅ **하이라이트 커서(헤일로)** — 커서 위치 강조 원 (FR-21)
- ✅ **돋보기 / 줌** — 커서 주변 화면 확대 창 (FR-24)
- ✅ **도구별 독립 색상·굵기 기억** (FR-18)

### 시스템
- ✅ 플로팅 툴바(드래그 이동) (FR-08) · 트레이 상주(우클릭 메뉴) (FR-12)
- ✅ 스크린샷 캡처 → PNG 저장 + 클립보드 복사(툴바/돋보기 자동 숨김 후 촬영) (FR-11)
- ✅ 무설치 설정 영속성 — 실행파일 옆 `settings.json` (FR-13)
- ✅ 전역 단축키 + 작업표시줄/Alt+Tab 숨김(`WS_EX_TOOLWINDOW`) (FR-10)

### 전역 단축키 (기본값, 모두 `Ctrl+Alt`)
| 키 | 동작 | 키 | 동작 |
|---|---|---|---|
| `D` | 그리기 ↔ 통과 모드 | `S` | 스크린샷 |
| `1` / `2` / `3` | 펜 / 하이라이터 / 지우개 | `T` | 툴바 표시/숨김 |
| `Z` / `Y` | Undo / Redo | `W` | 화이트보드/블랙보드 순환 |
| `E` | 전체 지우기 | `G` | 고스트 모드 |
| `Q` | 종료 | `M` | 돋보기 |
| `F` | 페이딩 잉크 | `H` | 하이라이트 커서(헤일로) |

> 도형·텍스트·넘버링 도구와 도형 채움 순환은 플로팅 툴바에서 선택합니다.

## 빌드 & 실행

```powershell
# 개발 실행
dotnet run --project src/ScreenPenPortable

# 무설치 단일 실행파일 배포 (.NET 런타임 미설치 PC에서도 실행)
dotnet publish src/ScreenPenPortable -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
# 결과물: src/ScreenPenPortable/bin/Release/net9.0-windows/win-x64/publish/ScreenPenPortable.exe
```

## 기술 스택
- **WPF / .NET 9 (C#)** — 명세서 §7.1 권장 스택. 참조 오픈소스: [gInk](https://github.com/geovens/gInk), [ppInk](https://github.com/AntonyGarand/ppInk)
- 무설치: self-contained + PublishSingleFile, 설정은 실행파일 옆 `settings.json`(FR-13)
- 트레이·스크린샷·돋보기에 WinForms/System.Drawing 병용 (csproj에서 전역 using 충돌 회피 처리)

## 로드맵
| 마일스톤 | 내용 | 상태 |
|---|---|:--:|
| **M0** | 투명 오버레이 + 클릭관통 토글 PoC | ✅ |
| **M1 (MVP)** | 하이라이터·지우개·색상·굵기·Undo/Redo·툴바·스크린샷·트레이·설정영속성 + 듀얼/다중 모니터(필수) | ✅ |
| **M2 (v1.0)** | 도형·텍스트·화이트보드/블랙보드·고스트 모드·도구별 기억 | ✅ |
| **M3 (v2)** | 페이딩 잉크·하이라이트 커서·넘버링·도형 채움순환·돋보기 | ✅ |

## 프로젝트 구조
```
screen-pen-portable/
├─ ScreenPenPortable.sln
├─ README.md
├─ 기능명세서.md                  # 기능 명세 (설계 문서)
└─ src/
   └─ ScreenPenPortable/          # WPF 앱
      ├─ App.xaml(.cs)
      ├─ MainWindow.xaml(.cs)     # 통합 허브: 오버레이 + 도구 디스패치 + 핫키 + 모드
      ├─ Settings/                # AppSettings, SettingsStore (settings.json)
      ├─ Drawing/                 # UndoStack(공유 타임라인), UndoRedoManager(스트로크), ObjectLayer(도형/텍스트/넘버)
      ├─ Services/                # ScreenshotService, TrayService, WhiteboardController, FadingInkService
      └─ UI/                      # ToolbarWindow(플로팅 툴바), HighlightCursor(헤일로), MagnifierWindow(돋보기)
```

## 라이선스
TBD
