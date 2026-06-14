using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenPenPortable.Settings;
using ScreenPenPortable.Drawing;
using ScreenPenPortable.Services;
using ScreenPenPortable.UI;

namespace ScreenPenPortable;

/// <summary>
/// 가상 화면 전체를 덮는 단일 투명·항상-위 오버레이(멀티모니터 = FR-17 A안).
/// M1(펜/하이라이터/지우개·색·굵기·Undo/Redo·툴바·스크린샷·트레이·설정·핫키·통과토글)에 더해
/// M2/M3 를 통합한다: 도형(FR-14)·채움순환(FR-23)·텍스트(FR-15)·넘버링(FR-22)·
/// 화이트보드(FR-16)·도구별 기억(FR-18)·고스트(FR-19)·페이딩(FR-20)·헤일로(FR-21)·돋보기(FR-24).
/// 서브시스템은 팀원이 작성한 독립 클래스를 배선만 한다(단일 통합 writer = 지휘자).
/// </summary>
public partial class MainWindow : Window
{
    // ── Win32 ──────────────────────────────────────────────
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);

    private enum Hk
    {
        Toggle = 1, Pen, Highlighter, Eraser, Undo, Redo, Clear, Screenshot, Toolbar, Exit,
        Whiteboard, Ghost, Magnifier, Fade, Halo
    }

    private IntPtr _hwnd;
    private bool _passThrough;
    private bool _ghost;
    private AppSettings _settings = new();
    private ToolKind _tool = ToolKind.Pen;

    // 서브시스템(OnLoaded 에서 생성)
    private readonly UndoStack _undo = new();
    private UndoRedoManager? _strokeUndo;
    private ObjectLayer? _objects;
    private WhiteboardController? _whiteboard;
    private HighlightCursor? _halo;
    private FadingInkService? _fade;
    private MagnifierWindow? _magnifier;
    private ToolbarWindow? _toolbar;
    private TrayService? _tray;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        CoverVirtualScreen();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void CoverVirtualScreen()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW); // Alt+Tab 숨김

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        RegisterHotkeys();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged; // 멀티모니터 변경 대응
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 그리기/객체/효과 서브시스템(모두 공유 _undo 타임라인에 합류)
        _strokeUndo = new UndoRedoManager(InkSurface, _undo);
        _objects = new ObjectLayer(InkSurface, OverlayLayer, _undo);
        _whiteboard = new WhiteboardController(BackdropLayer);
        _halo = new HighlightCursor(OverlayLayer, this);
        _fade = new FadingInkService(InkSurface, _strokeUndo.RemoveStrokeWithoutHistory);
        _undo.Changed += () => _toolbar?.SetUndoRedoEnabled(_undo.CanUndo, _undo.CanRedo);

        // 트레이 상주
        _tray = new TrayService("ScreenPen Portable");
        _tray.ToggleRequested += () => Dispatcher.Invoke(ToggleToolbar);
        _tray.ScreenshotRequested += () => Dispatcher.Invoke(TakeScreenshot);
        _tray.ClearRequested += () => Dispatcher.Invoke(ClearAllAnnotations);
        _tray.ExitRequested += () => Dispatcher.Invoke(Close);

        // 플로팅 툴바
        _toolbar = new ToolbarWindow
        {
            Left = _settings.ToolbarLeft,
            Top = _settings.ToolbarTop,
            // 오버레이가 전체 화면 입력을 잡으므로, 툴바를 오버레이의 소유(owned) 창으로 둬서
            // 항상 오버레이 위에 떠 클릭 가능하게 한다(그리기 모드에서도 툴바 조작 가능).
            Owner = this
        };
        _toolbar.ToolSelected += SetTool;
        _toolbar.ColorSelected += SetColor;
        _toolbar.ThicknessChanged += SetThickness;
        _toolbar.UndoRequested += () => _undo.Undo();
        _toolbar.RedoRequested += () => _undo.Redo();
        _toolbar.ClearRequested += ClearAllAnnotations;
        _toolbar.ScreenshotRequested += TakeScreenshot;
        _toolbar.PassThroughToggled += TogglePassThrough;
        _toolbar.ExitRequested += Close;
        // M2/M3 명령
        _toolbar.FillCycleRequested += CycleFill;
        _toolbar.WhiteboardToggleRequested += ToggleWhiteboard;
        _toolbar.GhostToggleRequested += ToggleGhost;
        _toolbar.MagnifierToggleRequested += ToggleMagnifier;
        _toolbar.FadeToggleRequested += ToggleFade;
        _toolbar.HaloToggleRequested += ToggleHalo;

        _toolbar.SetQuickColors(_settings.QuickColors);
        _toolbar.Show();

        // 마지막 도구·상태 복원
        _tool = _settings.LastTool;
        ApplyTool();
        _toolbar.SetActiveTool(_tool);
        _toolbar.SetThickness(GetToolWidth(_tool));
        _toolbar.SetPassThrough(false);
        _toolbar.SetFillMode(_settings.LastFillMode);
        _toolbar.SetUndoRedoEnabled(_undo.CanUndo, _undo.CanRedo);

        // 영속 토글 복원(FR-18/20/21)
        if (_settings.FadingInkEnabled)
        {
            _fade.Enable(_settings.FadeSeconds);
            _toolbar.SetFadeActive(true);
        }
        if (_settings.HighlightCursorEnabled)
        {
            _halo.Enable(ParseColor(_settings.HighlightCursorColor), _settings.HighlightCursorRadius);
            _toolbar.SetHaloActive(true);
        }
    }

    // ── 도구별 색상·굵기 기억(FR-18) ───────────────────────
    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private string GetToolColorHex(ToolKind t)
    {
        if (_settings.ToolColors.TryGetValue(t.ToString(), out var c) && !string.IsNullOrWhiteSpace(c))
            return c;
        return t switch
        {
            ToolKind.Highlighter => _settings.HighlighterColor,
            _ => _settings.PenColor
        };
    }

    private double GetToolWidth(ToolKind t)
    {
        if (_settings.ToolWidths.TryGetValue(t.ToString(), out var w) && w > 0)
            return w;
        return t switch
        {
            ToolKind.Highlighter => _settings.HighlighterWidth,
            ToolKind.Eraser => _settings.EraserWidth,
            ToolKind.Text => _settings.TextFontSize,
            _ => _settings.PenWidth
        };
    }

    private void SetToolColorHex(ToolKind t, string hex)
    {
        _settings.ToolColors[t.ToString()] = hex;
        // 펜/하이라이터는 레거시 필드도 동기화(기존 동작·호환 유지).
        if (t == ToolKind.Pen) _settings.PenColor = hex;
        else if (t == ToolKind.Highlighter) _settings.HighlighterColor = hex;
    }

    private void SetToolWidth(ToolKind t, double w)
    {
        _settings.ToolWidths[t.ToString()] = w;
        if (t == ToolKind.Pen) _settings.PenWidth = w;
        else if (t == ToolKind.Highlighter) _settings.HighlighterWidth = w;
        else if (t == ToolKind.Eraser) _settings.EraserWidth = w;
        else if (t == ToolKind.Text) _settings.TextFontSize = w;
    }

    // ── 도구 적용 ──────────────────────────────────────────
    private static bool IsInkTool(ToolKind t) =>
        t is ToolKind.Pen or ToolKind.Highlighter or ToolKind.Eraser;

    private void ApplyTool()
    {
        if (IsInkTool(_tool))
        {
            // 잉크 도구 → InkCanvas 가 처리. ObjectLayer 는 비활성(no-op).
            if (_objects != null) _objects.ActiveTool = _tool;

            if (_tool == ToolKind.Eraser)
            {
                InkSurface.EditingMode = InkCanvasEditingMode.EraseByStroke;
                return;
            }

            InkSurface.EditingMode = InkCanvasEditingMode.Ink;
            var da = new DrawingAttributes { FitToCurve = true };
            Color col = ParseColor(GetToolColorHex(_tool));
            if (_tool == ToolKind.Highlighter)
            {
                da.IsHighlighter = true;
                if (col.A == 0xFF) col.A = 0x80; // 하이라이터 반투명 보장
                da.Color = col;
            }
            else
            {
                da.Color = col;
            }
            da.Width = da.Height = GetToolWidth(_tool);
            InkSurface.DefaultDrawingAttributes = da;
        }
        else
        {
            // 객체 도구 → InkCanvas 입력은 끄고 ObjectLayer 가 마우스를 처리.
            InkSurface.EditingMode = InkCanvasEditingMode.None;
            if (_objects != null)
            {
                _objects.ActiveTool = _tool;
                _objects.StrokeColor = ParseColor(GetToolColorHex(_tool));
                _objects.StrokeWidth = GetToolWidth(_tool);
                _objects.Fill = _settings.LastFillMode;
                _objects.FontSize = _settings.TextFontSize;
                _objects.FontFamily = _settings.TextFontFamily;
            }
        }
    }

    private void SetTool(ToolKind t)
    {
        _tool = t;
        _settings.LastTool = t;
        ApplyTool();
        _toolbar?.SetActiveTool(t);
        _toolbar?.SetThickness(GetToolWidth(t));
    }

    private void SetColor(Color c)
    {
        if (_tool == ToolKind.Highlighter)
        {
            if (c.A == 0xFF) c.A = 0x80; // 하이라이터는 반투명 유지
            SetToolColorHex(ToolKind.Highlighter, c.ToString());
        }
        else if (_tool == ToolKind.Eraser)
        {
            // 지우개에서 색을 고르면 펜으로 전환 후 적용.
            SetToolColorHex(ToolKind.Pen, c.ToString());
            SetTool(ToolKind.Pen);
            return;
        }
        else
        {
            SetToolColorHex(_tool, c.ToString());
        }
        ApplyTool();
    }

    private void SetThickness(double w)
    {
        SetToolWidth(_tool, w);
        ApplyTool();
    }

    private void CycleFill()
    {
        if (_objects == null) return;
        FillMode m = _objects.CycleFill();
        _settings.LastFillMode = m;
        _toolbar?.SetFillMode(m);
    }

    // ── 전체 지우기: 스트로크 + 벡터 객체를 하나의 Undo 항목으로 ──
    private void ClearAllAnnotations()
    {
        if (_strokeUndo == null) return;

        var strokes = new StrokeCollection(InkSurface.Strokes);
        var children = InkSurface.Children.Cast<UIElement>().ToList();
        if (strokes.Count == 0 && children.Count == 0) return;

        // 지금 비운다(스트로크는 히스토리 억제로 직접 제거).
        _strokeUndo.ClearStrokesWithoutHistory();
        InkSurface.Children.Clear();

        // Undo/Redo 는 _undo.IsApplying=true 상태에서 실행되므로
        // 내부의 스트로크 add/clear 가 UndoRedoManager 에 다시 기록되지 않는다.
        _undo.Push(
            undo: () =>
            {
                InkSurface.Strokes.Add(strokes);
                foreach (UIElement c in children)
                    if (!InkSurface.Children.Contains(c))
                        InkSurface.Children.Add(c);
            },
            redo: () =>
            {
                InkSurface.Strokes.Clear();
                InkSurface.Children.Clear();
            });
    }

    // ── 스크린샷 ───────────────────────────────────────────
    private void TakeScreenshot()
    {
        bool tbVisible = _toolbar?.IsVisible ?? false;
        if (tbVisible) _toolbar!.Hide();
        bool magVisible = _magnifier?.IsVisible ?? false;
        if (magVisible) _magnifier!.HideMagnifier();

        // 툴바/돋보기가 화면에서 사라진 뒤 캡처(논블로킹).
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            try
            {
                string? dir = string.IsNullOrEmpty(_settings.LastScreenshotDir) ? null : _settings.LastScreenshotDir;
                string path = ScreenshotService.CaptureVirtualScreenToFileAndClipboard(dir);
                _settings.LastScreenshotDir = System.IO.Path.GetDirectoryName(path) ?? "";
                _tray?.SetModeText($"저장됨: {System.IO.Path.GetFileName(path)}");
            }
            catch { /* 캡처 실패는 무시 */ }
            if (tbVisible) _toolbar!.Show();
            if (magVisible) _magnifier!.ShowMagnifier();
        };
        timer.Start();
    }

    // ── 그리기/통과 토글 ───────────────────────────────────
    private void TogglePassThrough()
    {
        _passThrough = !_passThrough;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex = _passThrough ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);

        InkSurface.IsHitTestVisible = !_passThrough;
        _toolbar?.SetPassThrough(_passThrough);
        _tray?.SetModeText(_passThrough ? "통과 모드" : "그리기 모드");
    }

    private void ToggleToolbar()
    {
        if (_toolbar == null) return;
        if (_toolbar.IsVisible) _toolbar.Hide(); else _toolbar.Show();
    }

    // ── 고스트 모드(FR-19): 툴바·UI 숨기고 핫키만 ──────────
    private void ToggleGhost()
    {
        _ghost = !_ghost;
        if (_ghost) _toolbar?.Hide();
        else _toolbar?.Show();
        _tray?.SetModeText(_ghost ? "고스트 모드 (핫키만)" : "그리기 모드");
    }

    // ── 화이트보드/블랙보드(FR-16) ─────────────────────────
    private void ToggleWhiteboard()
    {
        if (_whiteboard == null) return;
        ApplyWhiteboardTarget();
        var mode = _whiteboard.Cycle(
            ParseColor(_settings.WhiteboardColor),
            ParseColor(_settings.BlackboardColor));
        _toolbar?.SetWhiteboardActive(mode != WhiteboardController.BoardMode.Off);
    }

    /// <summary>화이트보드 표시 대상(전체 가상화면 또는 특정 모니터)을 설정한다.</summary>
    private void ApplyWhiteboardTarget()
    {
        if (_whiteboard == null) return;
        int idx = _settings.WhiteboardMonitorIndex;
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (idx < 0 || idx >= screens.Length)
        {
            _whiteboard.SetFullVirtual();
            return;
        }

        // 물리 px → DIP 환산(이 창의 DPI 기준) 후 가상화면 원점 기준 오프셋으로 변환.
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        var b = screens[idx].Bounds; // 물리 px
        double left = b.Left / dpi.DpiScaleX - SystemParameters.VirtualScreenLeft;
        double top = b.Top / dpi.DpiScaleY - SystemParameters.VirtualScreenTop;
        double w = b.Width / dpi.DpiScaleX;
        double h = b.Height / dpi.DpiScaleY;
        _whiteboard.SetTargetArea(left, top, w, h);
    }

    // ── 돋보기(FR-24) ──────────────────────────────────────
    private void ToggleMagnifier()
    {
        if (_magnifier == null)
        {
            _magnifier = new MagnifierWindow { Owner = this };
            _magnifier.SetZoom(_settings.MagnifierZoom);
            _magnifier.SetViewSize(_settings.MagnifierSize);
        }
        _magnifier.Toggle();
        _toolbar?.SetMagnifierActive(_magnifier.IsVisible);
    }

    // ── 페이딩 잉크(FR-20) ─────────────────────────────────
    private void ToggleFade()
    {
        if (_fade == null) return;
        if (_fade.IsEnabled) _fade.Disable();
        else _fade.Enable(_settings.FadeSeconds);
        _settings.FadingInkEnabled = _fade.IsEnabled;
        _toolbar?.SetFadeActive(_fade.IsEnabled);
    }

    // ── 하이라이트 커서/헤일로(FR-21) ──────────────────────
    private void ToggleHalo()
    {
        if (_halo == null) return;
        if (_halo.IsEnabled) _halo.Disable();
        else _halo.Enable(ParseColor(_settings.HighlightCursorColor), _settings.HighlightCursorRadius);
        _settings.HighlightCursorEnabled = _halo.IsEnabled;
        _toolbar?.SetHaloActive(_halo.IsEnabled);
    }

    // ── 핫키 ───────────────────────────────────────────────
    private void RegisterHotkeys()
    {
        uint cm = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;
        void Reg(Hk id, Key k) => RegisterHotKey(_hwnd, (int)id, cm, (uint)KeyInterop.VirtualKeyFromKey(k));

        Reg(Hk.Toggle, Key.D);
        Reg(Hk.Pen, Key.D1);
        Reg(Hk.Highlighter, Key.D2);
        Reg(Hk.Eraser, Key.D3);
        Reg(Hk.Undo, Key.Z);
        Reg(Hk.Redo, Key.Y);
        Reg(Hk.Clear, Key.E);
        Reg(Hk.Screenshot, Key.S);
        Reg(Hk.Toolbar, Key.T);
        Reg(Hk.Exit, Key.Q);
        Reg(Hk.Whiteboard, Key.W);
        Reg(Hk.Ghost, Key.G);
        Reg(Hk.Magnifier, Key.M);
        Reg(Hk.Fade, Key.F);
        Reg(Hk.Halo, Key.H);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        handled = true;
        switch ((Hk)wParam.ToInt32())
        {
            case Hk.Toggle: TogglePassThrough(); break;
            case Hk.Pen: SetTool(ToolKind.Pen); break;
            case Hk.Highlighter: SetTool(ToolKind.Highlighter); break;
            case Hk.Eraser: SetTool(ToolKind.Eraser); break;
            case Hk.Undo: _undo.Undo(); break;
            case Hk.Redo: _undo.Redo(); break;
            case Hk.Clear: ClearAllAnnotations(); break;
            case Hk.Screenshot: TakeScreenshot(); break;
            case Hk.Toolbar: ToggleToolbar(); break;
            case Hk.Exit: Close(); break;
            case Hk.Whiteboard: ToggleWhiteboard(); break;
            case Hk.Ghost: ToggleGhost(); break;
            case Hk.Magnifier: ToggleMagnifier(); break;
            case Hk.Fade: ToggleFade(); break;
            case Hk.Halo: ToggleHalo(); break;
            default: handled = false; break;
        }
        return IntPtr.Zero;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // 그리기 모드(포커스 보유 시)에서의 보조 키.
        // 텍스트 입력 중(TextBox 포커스)에는 가로채지 않는다.
        if (Keyboard.FocusedElement is TextBox)
            return;

        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Delete: ClearAllAnnotations(); break;
        }
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.Invoke(CoverVirtualScreen);

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_toolbar != null)
        {
            _settings.ToolbarLeft = _toolbar.Left;
            _settings.ToolbarTop = _toolbar.Top;
        }
        SettingsStore.Save(_settings);

        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        if (_hwnd != IntPtr.Zero)
            foreach (Hk id in Enum.GetValues<Hk>())
                UnregisterHotKey(_hwnd, (int)id);

        _fade?.Disable();
        _halo?.Disable();
        _magnifier?.Close();
        _tray?.Dispose();
        _toolbar?.Close();
    }
}
