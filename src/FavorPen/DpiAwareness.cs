using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FavorPen;

/// <summary>
/// 프로세스 DPI 인식을 WPF 초기화 전에 <b>Per-Monitor-Aware V2</b>로 고정한다.
///
/// 모니터마다 오버레이 창을 하나씩 띄우는 구조라, 각 창이 자기 모니터의 배율로 선명하게
/// 렌더링되려면 Per-Monitor-V2 가 필요하다. 그러나 WPF 는 첫 HWND 생성 시 프로세스를
/// System 인식으로 '자동 승격/강등'하므로(csproj ApplicationHighDpiMode 만으로는 System 이 됨),
/// 그보다 먼저 실행되는 <see cref="ModuleInitializerAttribute"/>(엔트리포인트 이전 실행)에서
/// Per-Monitor-V2 컨텍스트를 설정한다. 이미 설정돼 있으면 WPF 는 바꾸지 않는다.
///
/// System 인식이어도 모니터별 창은 전체를 덮지만, 배율 다른 모니터는 비트맵 스케일로
/// 약간 부드러워진다. Per-Monitor-V2 면 그 모니터도 네이티브 배율로 선명하다.
/// </summary>
internal static class DpiAwareness
{
    // DPI_AWARENESS_CONTEXT 핸들(음수 상수). PER_MONITOR_AWARE_V2 = -4.
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [ModuleInitializer]
    internal static void Init()
    {
        // Win10 1703+ 지원. 실패(구형 OS·이미 설정됨)해도 무시 → 기본(System) 동작으로 진행(여전히 전체 커버).
        try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch { /* 무시 */ }
    }
}
