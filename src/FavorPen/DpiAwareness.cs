using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FavorPen;

/// <summary>
/// 프로세스 DPI 인식을 WPF 초기화 전에 "완전 무시(Unaware)"로 고정한다.
///
/// 왜 필요한가:
///  - 혼합 DPI(배율이 다른 멀티모니터)에서 FavorPen 오버레이는 AllowsTransparency(레이어드) 창인데,
///    Per-Monitor/System 인식에서는 이 레이어드 표면이 배율 높은 보조 모니터를 끝까지 덮지 못해
///    화면 일부(예: 1/5~1/3)가 투명하게 남아 그림이 안 그려진다.
///  - DPI 완전 무시로 두면 모든 모니터가 동일한 96 DPI 좌표계가 되어 단일 오버레이가 가상 화면
///    전체를 덮고, DWM 이 각 모니터로 비트맵 스케일해 빈틈없이 표시된다(배율 높은 모니터는 약간 부드러움).
///
/// WPF 는 첫 HWND 생성 시 프로세스를 System 인식으로 '자동 승격'하므로, 그보다 먼저 실행되는
/// <see cref="ModuleInitializerAttribute"/>(엔트리포인트 이전 실행)에서 Unaware 컨텍스트를 설정한다.
/// 이미 설정돼 있으면 WPF 는 승격하지 않는다.
///
/// 전제: 매니페스트가 DPI 인식을 지정하지 않아야 한다(csproj ApplicationHighDpiMode=DpiUnaware →
/// 매니페스트에 dpiAware 항목을 넣지 않음). 매니페스트로 이미 정해지면 런타임 변경은 실패한다.
/// </summary>
internal static class DpiAwareness
{
    // DPI_AWARENESS_CONTEXT 핸들(음수 상수). UNAWARE = -1.
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_UNAWARE = new(-1);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [ModuleInitializer]
    internal static void Init()
    {
        // Win10 1703+ 에서 지원. 실패(구형 OS·이미 설정됨)해도 무시하고 기본 동작으로 진행.
        try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_UNAWARE); }
        catch { /* 무시 */ }
    }
}
