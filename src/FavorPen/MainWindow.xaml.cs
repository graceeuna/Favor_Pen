using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using FavorPen.Settings;
using FavorPen.Drawing;
using FavorPen.Services;
using FavorPen.UI;

namespace FavorPen;

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
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // 툴바를 최상단(topmost)으로 재확정해 전체화면 오버레이 뒤로 가려지는 것을 막는다.
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    private enum Hk
    {
        Toggle = 1, Pen, Highlighter, Eraser, Undo, Redo, Clear, Screenshot, Exit,
        Whiteboard, Ghost, Magnifier, Halo, Timer, Random, Noise, Recover
    }

    private IntPtr _hwnd;
    private bool _passThrough;
    private bool _ghost;
    private readonly List<string> _hotkeyFailures = new(); // 등록 실패한 핫키(타앱 점유)
    private AppSettings _settings = new();
    private ToolKind _tool = ToolKind.Pen;

    // 서브시스템(OnLoaded 에서 생성)
    private readonly UndoStack _undo = new();
    private UndoRedoManager? _strokeUndo;
    private ObjectLayer? _objects;
    private WhiteboardController? _whiteboard;
    private HighlightCursor? _halo;
    private MagnifierWindow? _magnifier;
    private TimerWindow? _timerWin;
    private RandomPickerWindow? _randomWin;
    private NoiseMeterWindow? _noiseWin;
    private HaloSettingsWindow? _haloSettings;
    private ToolbarWindow? _toolbar;
    private TrayService? _tray;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        CoverVirtualScreen();
        Loaded += OnLoaded;
        Closed += OnClosed;
        // 그릴 때(오버레이 활성화) 툴바가 전체화면 오버레이 뒤로 가려지지 않도록 최상단 재확정.
        Activated += (_, _) => EnsureToolbarTop();
    }

    /// <summary>툴바가 보이는 상태면 z-order 최상단으로 재확정(가려짐 방지, 깜빡임 없음).</summary>
    private void EnsureToolbarTop()
    {
        if (_toolbar == null || _ghost || !_toolbar.IsVisible) return;
        IntPtr h = new WindowInteropHelper(_toolbar).Handle;
        if (h != IntPtr.Zero)
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>툴바를 어떤 상태(숨김·화면 밖·접힘·고스트·가려짐)에서든 되살린다(트레이 복구용).</summary>
    private void RecoverToolbar()
    {
        if (_toolbar == null) return;
        _ghost = false;
        var (l, t) = ClampToScreen(_toolbar.Left, _toolbar.Top);
        _toolbar.Left = l;
        _toolbar.Top = t;
        _toolbar.Expand();
        _toolbar.Show();
        _toolbar.Topmost = true;
        _toolbar.Activate();
        EnsureToolbarTop();
        _tray?.SetModeText("툴바 복구됨");
    }

    private void CoverVirtualScreen()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    /// <summary>주어진 창 위치가 현재 가상 화면(모든 모니터) 밖이면 보이는 기본 위치로 보정한다.
    /// 듀얼모니터에서 저장한 좌표가 단일 모니터에선 화면 밖이라 툴바가 안 보이는 문제를 막는다.
    /// 최소 <c>margin</c> 만큼은 화면 안에 들어오도록 보장한다.</summary>
    private static (double left, double top) ClampToScreen(double left, double top)
    {
        double vsLeft = SystemParameters.VirtualScreenLeft;
        double vsTop = SystemParameters.VirtualScreenTop;
        double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
        double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;
        const double margin = 90; // 최소한 드래그 핸들/버튼이 화면 안에 보이도록.

        if (double.IsNaN(left) || left < vsLeft || left > vsRight - margin)
            left = vsLeft + 40;
        if (double.IsNaN(top) || top < vsTop || top > vsBottom - margin)
            top = vsTop + 40;
        return (left, top);
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
        // 그리기 중 커서가 안 보이는 문제 방지: InkCanvas 기본 잉크 커서(펜 굵기만 한 작은 점)
        // 대신 항상 또렷이 보이는 십자(+) 커서를 쓴다.
        InkSurface.UseCustomCursor = true;
        InkSurface.Cursor = Cursors.Cross;

        // 그리기/객체/효과 서브시스템(모두 공유 _undo 타임라인에 합류)
        _strokeUndo = new UndoRedoManager(InkSurface, _undo);
        _objects = new ObjectLayer(InkSurface, OverlayLayer, _undo);
        _whiteboard = new WhiteboardController(BackdropLayer);
        _halo = new HighlightCursor(OverlayLayer, this);
        _undo.Changed += () => _toolbar?.SetUndoRedoEnabled(_undo.CanUndo, _undo.CanRedo);

        // 트레이 상주
        _tray = new TrayService("FavorPen");
        _tray.ToggleRequested += () => Dispatcher.Invoke(ToggleToolbar);
        _tray.ScreenshotRequested += () => Dispatcher.Invoke(LaunchWindowsSnip);
        _tray.ClearRequested += () => Dispatcher.Invoke(ClearAllAnnotations);
        _tray.ExitRequested += () => Dispatcher.Invoke(Close);

        // 플로팅 툴바. 저장된 위치가 현재 화면 밖이면 보정한다
        // (듀얼모니터에서 둘째 화면에 두고 저장 → 단일 모니터에서 켜면 화면 밖이라 안 보이던 문제).
        var (tbLeft, tbTop) = ClampToScreen(_settings.ToolbarLeft, _settings.ToolbarTop);
        _toolbar = new ToolbarWindow
        {
            Left = tbLeft,
            Top = tbTop,
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
        _toolbar.ScreenshotRequested += LaunchWindowsSnip;
        _toolbar.PassThroughToggled += TogglePassThrough;
        _toolbar.ExitRequested += Close;
        // M2/M3 명령
        _toolbar.FillCycleRequested += CycleFill;
        _toolbar.WhiteboardToggleRequested += ToggleWhiteboard;
        _toolbar.MagnifierToggleRequested += ToggleMagnifier;
        _toolbar.MagnifierOffRequested += MagnifierOff;
        _toolbar.HaloToggleRequested += ToggleHalo;
        _toolbar.HaloSettingsRequested += OpenHaloSettings;
        _toolbar.TimerToggleRequested += ToggleTimer;
        _toolbar.RandomToggleRequested += ToggleRandom;
        _toolbar.NoiseToggleRequested += ToggleNoise;

        _toolbar.SetQuickColors(_settings.QuickColors);
        _toolbar.Show();

        // 마지막 도구·상태 복원
        _tool = _settings.LastTool;
        ApplyTool();
        _toolbar.SetActiveTool(_tool);
        _toolbar.SetThickness(GetToolWidth(_tool));
        RefreshCurrentColor();
        _toolbar.SetPassThrough(false);
        _toolbar.SetFillMode(_settings.LastFillMode);
        _toolbar.SetUndoRedoEnabled(_undo.CanUndo, _undo.CanRedo);

        // 영속 토글 복원(FR-18/21)
        if (_settings.HighlightCursorEnabled)
        {
            _halo.Enable(ParseColor(_settings.HighlightCursorColor), _settings.HighlightCursorRadius);
            _toolbar.SetHaloActive(true);
        }

        // 일부 전역 단축키가 다른 앱에 점유되어 등록 실패했으면 트레이로 알린다.
        if (_hotkeyFailures.Count > 0)
            _tray.SetModeText($"일부 단축키 등록 실패(다른 앱 점유): {string.Join(", ", _hotkeyFailures)}");
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
        // 도구를 고르면 = 그리려는 의도. 마우스(통과) 모드였다면 그리기 모드로 자동 복귀
        // → 펜 옆 마우스 버튼으로 마우스를 쓰다가 도구를 누르면 바로 그릴 수 있다.
        if (_passThrough) TogglePassThrough();

        // 도구를 바꾸기 전 진행 중이던 텍스트는 확정하고 도형 드래그는 취소한다.
        _objects?.EndActiveTextEditing(true);
        _objects?.CancelActiveDrag();

        _tool = t;
        _settings.LastTool = t;
        ApplyTool();
        _toolbar?.SetActiveTool(t);
        // 텍스트 도구는 굵기 슬라이더를 폰트 크기(8~200)로, 그 외 도구는 1~40 으로.
        _toolbar?.SetThicknessRange(t == ToolKind.Text ? 8 : 1, t == ToolKind.Text ? 200 : 40);
        _toolbar?.SetThickness(GetToolWidth(t));
        RefreshCurrentColor();
    }

    /// <summary>현재 활성 도구의 색을 툴바(현재 색 막대·스와치 선택 링)에 반영한다.</summary>
    private void RefreshCurrentColor() => _toolbar?.SetCurrentColor(ParseColor(GetToolColorHex(_tool)));

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
        RefreshCurrentColor();
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

        // 진행 중인 텍스트 편집은 취소, 도형 드래그는 취소(지운 화면에 부활·미리보기 잔류 방지).
        _objects?.EndActiveTextEditing(false);
        _objects?.CancelActiveDrag();

        var strokes = new StrokeCollection(InkSurface.Strokes);
        var children = InkSurface.Children.Cast<UIElement>().ToList();
        if (strokes.Count == 0 && children.Count == 0) return;

        int prevNext = _objects?.NextNumber ?? 1; // 번호 카운터를 undo로 복원하기 위해 저장

        // 지금 비운다(스트로크는 히스토리 억제로 직접 제거).
        _strokeUndo.ClearStrokesWithoutHistory();
        InkSurface.Children.Clear();
        _objects?.ResetNumbering(); // 전체지우기 후 번호는 1부터

        // Undo/Redo 는 _undo.IsApplying=true 상태에서 실행되므로
        // 내부의 스트로크 add/clear 가 UndoRedoManager 에 다시 기록되지 않는다.
        _undo.Push(
            undo: () =>
            {
                InkSurface.Strokes.Add(strokes);
                foreach (UIElement c in children)
                    if (!InkSurface.Children.Contains(c))
                        InkSurface.Children.Add(c);
                if (_objects != null) _objects.NextNumber = prevNext;
            },
            redo: () =>
            {
                InkSurface.Strokes.Clear();
                InkSurface.Children.Clear();
                _objects?.ResetNumbering();
            });
    }

    // ── 캡처: Windows 캡처 도구(편집기 포함) 연동 ───────────
    /// <summary>화면 카메라 버튼 / Ctrl+Alt+S → Windows 캡처 도구를 띄운다.
    /// 단순 클립(ms-screenclip)이 아니라 <b>캡처 후 이미지를 편집할 수 있는 편집기 창이 유지되는</b>
    /// 캡처 도구를 연다: 도구에서 '새로 만들기'로 영역을 캡처하면 그 이미지가 편집기에 들어오고
    /// 창이 그대로 남아 펜·형광펜 등으로 수정·저장할 수 있다.
    /// 구형 Snipping Tool 실행이 막히면 최신 Snip &amp; Sketch(ms-screensketch)로 폴백한다.</summary>
    private void LaunchWindowsSnip()
    {
        try
        {
            Process.Start(new ProcessStartInfo("snippingtool.exe") { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("ms-screensketch:") { UseShellExecute = true }); }
            catch { /* 캡처 도구 실행 실패는 무시 */ }
        }
    }

    // ── 그리기/통과 토글 ───────────────────────────────────
    private void TogglePassThrough()
    {
        _objects?.CancelActiveDrag(); // 통과 전환 시 진행 중 도형 드래그 정리
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
        // 제대로 보이는 상태면 숨기고, 아니면(숨김·화면 밖·고스트·가려짐) 무조건 복구한다.
        if (_toolbar.IsVisible && !_ghost && IsToolbarOnScreen())
            _toolbar.Hide();
        else
            RecoverToolbar();
    }

    /// <summary>툴바 좌상단이 현재 가상 화면 안쪽에 있는지(=실제로 보이는지) 판정.</summary>
    private bool IsToolbarOnScreen()
    {
        if (_toolbar == null) return false;
        double vsL = SystemParameters.VirtualScreenLeft, vsT = SystemParameters.VirtualScreenTop;
        double vsR = vsL + SystemParameters.VirtualScreenWidth, vsB = vsT + SystemParameters.VirtualScreenHeight;
        return _toolbar.Left >= vsL && _toolbar.Top >= vsT
            && _toolbar.Left <= vsR - 50 && _toolbar.Top <= vsB - 50;
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
        // 보드를 새로 켤 때(Off→표시)만 대상 모니터 영역을 설정(매 토글 재계산·풀스크린 리셋 방지).
        if (_whiteboard.Mode == WhiteboardController.BoardMode.Off)
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
    private bool _magnifierOn;
    private void ToggleMagnifier()
    {
        if (_magnifier == null)
        {
            _magnifier = new MagnifierWindow { Owner = this };
            _magnifier.SetZoom(_settings.MagnifierZoom);
            _magnifier.SetViewSize(_settings.MagnifierSize);
        }

        // 명시적 on/off 상태로 토글한다(돋보기가 매 프레임 자기 숨김을 하므로
        // IsVisible 에 의존하지 않고 한 번 누르면 켜고, 다시 누르면 확실히 끈다).
        _magnifierOn = !_magnifierOn;
        if (_magnifierOn) _magnifier.ShowMagnifier();
        else _magnifier.HideMagnifier();
        _toolbar?.SetMagnifierActive(_magnifierOn);
    }

    /// <summary>돋보기 강제 끄기(툴바 돋보기 버튼 우클릭). 좌클릭 토글이 막히는 상황의 확실한 해제 경로.</summary>
    private void MagnifierOff()
    {
        if (!_magnifierOn) return;
        _magnifierOn = false;
        _magnifier?.HideMagnifier();
        _toolbar?.SetMagnifierActive(false);
    }

    // ── 타이머(화면 중앙 카운트다운) ──────────────────────
    private void ToggleTimer()
    {
        if (_timerWin == null)
        {
            _timerWin = new TimerWindow { Owner = this };
            _timerWin.SetFontSize(_settings.TimerFontSize);
            _timerWin.SetDuration(_settings.TimerDurationSeconds);
            // 사용자가 ✕(닫기)로 숨겨도 툴바 강조가 동기화되도록.
            _timerWin.IsVisibleChanged += (_, _) => _toolbar?.SetTimerActive(_timerWin!.IsVisible);
        }
        _timerWin.Toggle();
        _toolbar?.SetTimerActive(_timerWin.IsVisible);
    }

    // ── 랜덤 뽑기(발표용 번호 추첨) ────────────────────────
    private void ToggleRandom()
    {
        if (_randomWin == null)
        {
            _randomWin = new RandomPickerWindow { Owner = this };
            _randomWin.RangesText = _settings.RandomRanges;
            _randomWin.Count = _settings.RandomCount;
            _randomWin.IsVisibleChanged += (_, _) => _toolbar?.SetRandomActive(_randomWin!.IsVisible);
        }
        _randomWin.Toggle();
        _toolbar?.SetRandomActive(_randomWin.IsVisible);
    }

    // ── 소음 신호등(모둠활동 소음 측정) ────────────────────
    private void ToggleNoise()
    {
        if (_noiseWin == null)
        {
            _noiseWin = new NoiseMeterWindow { Owner = this };
            _noiseWin.Sensitivity = _settings.NoiseSensitivity;
            _noiseWin.QuietRef = _settings.NoiseQuietRef;
            _noiseWin.LoudRef = _settings.NoiseLoudRef;
            _noiseWin.IsVisibleChanged += (_, _) => _toolbar?.SetNoiseActive(_noiseWin!.IsVisible);
        }
        _noiseWin.Toggle();
        _toolbar?.SetNoiseActive(_noiseWin.IsVisible);
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

    /// <summary>헤일로 색·크기 설정 팝업(헤일로 버튼 우클릭). 변경은 실시간 반영·영속화.</summary>
    private void OpenHaloSettings()
    {
        if (_halo == null) return;

        // 미리보기를 위해 헤일로를 켠다(꺼져 있었다면).
        if (!_halo.IsEnabled)
        {
            _halo.Enable(ParseColor(_settings.HighlightCursorColor), _settings.HighlightCursorRadius);
            _settings.HighlightCursorEnabled = true;
            _toolbar?.SetHaloActive(true);
        }

        if (_haloSettings == null)
        {
            _haloSettings = new HaloSettingsWindow { Owner = this };
            _haloSettings.ColorPicked += c =>
            {
                _settings.HighlightCursorColor = c.ToString();
                _halo!.Enable(c, _settings.HighlightCursorRadius); // 실시간 갱신
            };
            _haloSettings.SizePicked += r =>
            {
                _settings.HighlightCursorRadius = r;
                _halo!.Enable(ParseColor(_settings.HighlightCursorColor), r);
            };
        }

        _haloSettings.SetInitial(ParseColor(_settings.HighlightCursorColor), _settings.HighlightCursorRadius);

        // 툴바 근처에 띄우되, 화면 밖으로 나가지 않게 보정한다.
        if (_toolbar != null)
        {
            var (hl, ht) = ClampToScreen(_toolbar.Left + _toolbar.ActualWidth + 8, _toolbar.Top);
            _haloSettings.Left = hl;
            _haloSettings.Top = ht;
        }
        _haloSettings.Show();
        _haloSettings.Activate();
    }

    // ── 핫키 ───────────────────────────────────────────────
    private void RegisterHotkeys()
    {
        // 누르기 쉬운 Ctrl+Shift 를 기본으로 쓰되, 흔한 앱 단축키와 겹치는 것만 Ctrl+Alt 로 둔다
        // (Ctrl+Shift+Z=Redo, +S=다른이름저장, +W=브라우저 창 닫기, +Y=Redo).
        uint shift = MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT;
        uint alt = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;
        void Reg(Hk id, Key k, uint mod)
        {
            // 등록 실패(타앱이 같은 핫키 점유)는 조용히 넘기지 말고 수집 → 트레이로 알림.
            if (!RegisterHotKey(_hwnd, (int)id, mod, (uint)KeyInterop.VirtualKeyFromKey(k)))
                _hotkeyFailures.Add(id.ToString());
        }

        // ── Ctrl+Shift (충돌 적은 동작) ──
        Reg(Hk.Toggle, Key.D, shift);
        Reg(Hk.Pen, Key.D1, shift);
        Reg(Hk.Highlighter, Key.D2, shift);
        Reg(Hk.Eraser, Key.D3, shift);
        Reg(Hk.Clear, Key.E, shift);
        Reg(Hk.Ghost, Key.G, shift);
        Reg(Hk.Magnifier, Key.M, shift);
        Reg(Hk.Halo, Key.H, shift);
        Reg(Hk.Timer, Key.C, shift);
        Reg(Hk.Random, Key.R, shift);
        Reg(Hk.Noise, Key.N, shift);
        Reg(Hk.Exit, Key.Q, shift);

        // ── Ctrl+Alt (흔한 앱 단축키와 겹쳐 유지) ──
        Reg(Hk.Undo, Key.Z, alt);
        Reg(Hk.Redo, Key.Y, alt);
        Reg(Hk.Screenshot, Key.S, alt);
        Reg(Hk.Whiteboard, Key.W, alt);
        Reg(Hk.Recover, Key.T, alt); // 툴바 복구(T=Toolbar). 브라우저 Ctrl+Shift+T 회피 위해 Ctrl+Alt.
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
            case Hk.Screenshot: LaunchWindowsSnip(); break;
            case Hk.Exit: Close(); break;
            case Hk.Whiteboard: ToggleWhiteboard(); break;
            case Hk.Ghost: ToggleGhost(); break;
            case Hk.Magnifier: ToggleMagnifier(); break;
            case Hk.Halo: ToggleHalo(); break;
            case Hk.Timer: ToggleTimer(); break;
            case Hk.Random: ToggleRandom(); break;
            case Hk.Noise: ToggleNoise(); break;
            case Hk.Recover: RecoverToolbar(); break;
            default: handled = false; break;
        }
        return IntPtr.Zero;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // 텍스트 입력 중(TextBox 포커스)에는 가로채지 않는다(텍스트의 Esc는 ObjectLayer가 처리).
        if (Keyboard.FocusedElement is TextBox)
            return;

        switch (e.Key)
        {
            case Key.Escape:
                // Esc 는 진행 중인 도형 드래그/텍스트만 취소한다.
                // (실수로 앱이 종료되지 않도록 — 종료는 Ctrl+Alt+Q · 트레이 · 툴바 ✕.)
                _objects?.CancelActiveDrag();
                _objects?.EndActiveTextEditing(false);
                break;
            case Key.Delete:
                ClearAllAnnotations();
                break;
        }
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.Invoke(() =>
    {
        CoverVirtualScreen();
        // 모니터 분리(예: 노트북 도킹 해제)로 툴바가 화면 밖에 남는 것을 막는다.
        if (_toolbar != null)
        {
            var (l, t) = ClampToScreen(_toolbar.Left, _toolbar.Top);
            _toolbar.Left = l;
            _toolbar.Top = t;
        }
    });

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_toolbar != null)
        {
            _settings.ToolbarLeft = _toolbar.Left;
            _settings.ToolbarTop = _toolbar.Top;
        }
        if (_timerWin != null)
        {
            _settings.TimerDurationSeconds = _timerWin.DurationSeconds;
            _settings.TimerFontSize = _timerWin.FontSizeValue;
        }
        if (_randomWin != null)
        {
            _settings.RandomRanges = _randomWin.RangesText;
            _settings.RandomCount = _randomWin.Count;
        }
        if (_noiseWin != null)
        {
            _settings.NoiseSensitivity = _noiseWin.Sensitivity;
            _settings.NoiseQuietRef = _noiseWin.QuietRef;
            _settings.NoiseLoudRef = _noiseWin.LoudRef;
        }
        SettingsStore.Save(_settings);

        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        if (_hwnd != IntPtr.Zero)
            foreach (Hk id in Enum.GetValues<Hk>())
                UnregisterHotKey(_hwnd, (int)id);

        _halo?.Disable();
        _magnifier?.Close();
        _timerWin?.Close();
        _randomWin?.Close();
        _noiseWin?.Close();
        _haloSettings?.Close();
        _tray?.Dispose();
        _toolbar?.Close();
    }
}
