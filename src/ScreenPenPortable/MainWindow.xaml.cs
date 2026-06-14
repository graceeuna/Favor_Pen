using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ScreenPenPortable;

/// <summary>
/// M0 PoC: 가상 화면 전체를 덮는 투명·항상-위 오버레이.
/// 명세서 §7.1(투명 오버레이) + FR-09(그리기/통과 모드 토글)의 기술 게이트를 검증한다.
///
/// 동작 원리:
///  - AllowsTransparency=True + Background=Transparent + WindowStyle=None 으로 픽셀 단위 알파 오버레이.
///  - "그리기 모드": 일반 창처럼 마우스를 캡처 → InkCanvas가 펜 스트로크를 그린다.
///  - "통과 모드": 윈도우에 WS_EX_TRANSPARENT 를 부여 → 모든 마우스 입력이 아래 앱으로 전달된다.
///    (전역 단축키 Ctrl+Alt+D 로만 복귀 가능. 통과 모드에서는 창이 키보드 포커스를 받지 못하기 때문.)
/// </summary>
public partial class MainWindow : Window
{
    // ── Win32 상수 ─────────────────────────────────────────────
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020; // 마우스 입력 통과
    private const int WS_EX_TOOLWINDOW = 0x00000080;  // Alt+Tab 목록에서 숨김
    private const int WM_HOTKEY = 0x0312;

    private const int HOTKEY_ID_TOGGLE = 0x9001;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr _hwnd;
    private bool _passThrough;

    public MainWindow()
    {
        InitializeComponent();

        // 가상 화면(모든 모니터를 포함하는 경계) 전체를 덮는다.
        // TODO(M2): 모니터별 오버레이 1개씩 + Per-Monitor V2 DPI 대응 (명세서 §8.2)
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        // Alt+Tab 에서 숨김
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);

        // 전역 단축키 Ctrl+Alt+D = 그리기/통과 토글
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        RegisterHotKey(_hwnd, HOTKEY_ID_TOGGLE,
            MOD_CONTROL | MOD_ALT | MOD_NOREPEAT,
            (uint)KeyInterop.VirtualKeyFromKey(Key.D));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID_TOGGLE)
        {
            TogglePassThrough();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void TogglePassThrough()
    {
        _passThrough = !_passThrough;

        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex = _passThrough ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);

        InkSurface.IsHitTestVisible = !_passThrough;
        ModeText.Text = _passThrough ? "통과 모드 (클릭이 아래 앱으로 전달됨)" : "그리기 모드";
        ModeText.Foreground = _passThrough
            ? System.Windows.Media.Brushes.OrangeRed
            : System.Windows.Media.Brushes.LightGreen;
        Hud.Opacity = _passThrough ? 0.45 : 1.0;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Delete:
                InkSurface.Strokes.Clear();
                break;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hwnd != IntPtr.Zero)
            UnregisterHotKey(_hwnd, HOTKEY_ID_TOGGLE);
        base.OnClosed(e);
    }
}
