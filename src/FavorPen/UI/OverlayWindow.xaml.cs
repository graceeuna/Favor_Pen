using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using FavorPen.Drawing;
using FavorPen.Services;
using WinScreen = System.Windows.Forms.Screen;

namespace FavorPen.UI;

/// <summary>
/// 한 모니터를 덮는 투명·항상-위 그리기 오버레이 창.
///
/// 혼합 DPI(배율 다른 멀티모니터)에서 단일 AllowsTransparency 창은 고배율 보조 모니터를
/// 끝까지 덮지 못하므로(레이어드 표면이 한 DPI로만 렌더링), 모니터마다 이 창을 하나씩 띄운다.
/// Per-Monitor-V2 인식이라 각 창은 자기 모니터의 배율로 선명하게 전체를 덮는다.
///
/// 그리기 하위 시스템(잉크 Undo·객체 레이어·화이트보드·헤일로)은 이 창의 서피스에 대해
/// 각자 인스턴스화되며, Undo 타임라인(<see cref="UndoStack"/>)만 모든 창이 공유해
/// 실행취소가 모니터에 상관없이 시간순으로 동작한다. 도구 선택/색/굵기/지우기 등은
/// 지휘자(MainWindow)가 모든 오버레이에 브로드캐스트한다.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private IntPtr _hwnd;
    private readonly UndoStack _undo;

    /// <summary>이 오버레이가 덮는 모니터.</summary>
    public WinScreen Screen { get; }

    // 그리기 서피스 + 하위 시스템(공개: 지휘자가 도구/색/굵기를 적용).
    public InkCanvas Surface => InkSurface;
    public Canvas OverlayCanvas => OverlayLayer;
    public Border Backdrop => BackdropLayer;
    public UndoRedoManager StrokeUndo { get; private set; } = null!;
    public ObjectLayer Objects { get; private set; } = null!;
    public WhiteboardController Whiteboard { get; private set; } = null!;
    public HighlightCursor Halo { get; private set; } = null!;
    public IntPtr Handle => _hwnd;

    public OverlayWindow(UndoStack undo, WinScreen screen)
    {
        _undo = undo;
        Screen = screen;
        InitializeComponent();

        // 하위 시스템 배선(각자 이 창의 서피스/레이어 사용, Undo 타임라인만 공유).
        StrokeUndo = new UndoRedoManager(InkSurface, _undo);
        Objects = new ObjectLayer(InkSurface, OverlayLayer, _undo);
        Whiteboard = new WhiteboardController(BackdropLayer);
        Halo = new HighlightCursor(OverlayLayer, this);

        // 그리기 중 커서가 또렷이 보이도록 십자(+) 커서.
        InkSurface.UseCustomCursor = true;
        InkSurface.Cursor = Cursors.Cross;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW); // 작업표시줄/Alt+Tab 숨김
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        PlaceOnScreen();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DPICHANGED = 0x02E0;
        if (msg == WM_DPICHANGED)
        {
            // 다른 배율 모니터로 이동하면 WPF 가 '논리 크기 유지'로 창을 키운다(예: 1080→1620).
            // 담당 모니터의 물리 경계로 다시 맞춰 정확히 한 모니터만 덮게 한다.
            Dispatcher.BeginInvoke(new Action(PlaceOnScreen), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        return IntPtr.Zero;
    }

    /// <summary>이 창을 담당 모니터에 딱 맞춘다(Per-Monitor-V2 → 물리 px 좌표로 배치).</summary>
    public void PlaceOnScreen()
    {
        if (_hwnd == IntPtr.Zero) return;
        var b = Screen.Bounds; // 물리 px
        SetWindowPos(_hwnd, IntPtr.Zero, b.X, b.Y, b.Width, b.Height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>통과(클릭-스루) 모드 토글: OS 레벨 WS_EX_TRANSPARENT + InkCanvas 입력 차단 해제.</summary>
    public void SetPassThrough(bool on)
    {
        if (_hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex = on ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        InkSurface.IsHitTestVisible = !on;
    }

    /// <summary>진행 중이던 텍스트 편집 확정·도형 드래그 취소(도구 전환/지우기 전 정리).</summary>
    public void EndInProgress(bool commitText)
    {
        Objects?.EndActiveTextEditing(commitText);
        Objects?.CancelActiveDrag();
    }
}
