# ScreenPen Portable

무설치(Portable) **화면 주석 도구** — 데스크탑 화면 위에 펜·도형·텍스트로 그리고, 필요하면 화면을 통과시켜 아래 앱을 그대로 조작할 수 있는 항상-위(always-on-top) 투명 오버레이 도구입니다. [Epic Pen](https://epicpen.com)의 기능을 참고해 라이선스/구독 분기 없이 단일 무설치 빌드로 제공하는 것을 목표로 합니다.

> 상세 기능 정의는 [기능명세서.md](기능명세서.md) 참고. (Epic Pen 공식 문서 딥리서치 기반, 검증 22건)

## 현재 상태: M0 PoC

핵심 기술 게이트(투명 오버레이 + 클릭관통 토글)를 검증하는 최소 동작본입니다.

- ✅ 가상 화면 전체를 덮는 투명·항상-위 오버레이 (`AllowsTransparency` + `WindowStyle=None` + `Background=Transparent`)
- ✅ 펜(프리핸드) 드로잉 — `InkCanvas`
- ✅ **그리기 / 통과(click-through) 모드 토글** — 전역 단축키 `Ctrl+Alt+D` (통과 모드에서 `WS_EX_TRANSPARENT` 부여)
- ✅ 전체 지우기(`Delete`), 종료(`Esc`)
- ✅ 작업표시줄/Alt+Tab 숨김 (`WS_EX_TOOLWINDOW`)

### 단축키 (M0)
| 키 | 동작 |
|---|---|
| `Ctrl+Alt+D` (전역) | 그리기 ↔ 통과 모드 전환 |
| `Delete` | 전체 지우기 |
| `Esc` | 종료 |

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
- 무설치: self-contained + PublishSingleFile, 설정은 실행파일 옆 파일에 저장 예정(FR-13)

## 로드맵 (요약)
| 마일스톤 | 내용 |
|---|---|
| **M0 (현재)** | 투명 오버레이 + 클릭관통 토글 PoC |
| M1 (MVP) | 하이라이터·지우개·색상·굵기·Undo/Redo·툴바·스크린샷·트레이·설정영속성 + **듀얼/다중 모니터(필수)** |
| M2 (v1.0) | 도형·텍스트·화이트보드/블랙보드·고스트 모드 |
| M3 (v2) | 페이딩 잉크·하이라이트 커서·넘버링·도형 채움순환·돋보기 |

## 프로젝트 구조
```
screen-pen-portable/
├─ ScreenPenPortable.sln
├─ README.md
├─ 기능명세서.md                # 기능 명세 (설계 문서)
└─ src/
   └─ ScreenPenPortable/        # WPF 앱
      ├─ App.xaml(.cs)
      ├─ MainWindow.xaml(.cs)   # M0: 투명 오버레이 + 클릭관통
      └─ ScreenPenPortable.csproj
```

## 라이선스
TBD
