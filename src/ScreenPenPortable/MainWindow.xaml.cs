using System;
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
/// M1: 가상 화면 전체를 덮는 단일 투명·항상-위 오버레이(멀티모니터 동작 = FR-17 구현 A안).
/// 펜/하이라이터/지우개, 색상·굵기, Undo/Redo, 플로팅 툴바, 스크린샷, 트레이 상주,
/// 설정 영속성, 전역 핫키, 그리기/통과(click-through) 토글, 모니터 변경 자동 리사이즈를 통합한다.
/// 서비스/UI는 팀원이 작성한 독립 클래스를 배선만 한다.
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

    private enum Hk { Toggle = 1, Pen, Highlighter, Eraser, Undo, Redo, Clear, Screenshot, Toolbar, Exit }

    private IntPtr _hwnd;
    private bool _passThrough;
    private AppSettings _settings = new();
    private UndoRedoManager? _undo;
    private ToolbarWindow? _toolbar;
    private TrayService? _tray;
    private ToolKind _tool = ToolKind.Pen;

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
        _undo = new UndoRedoManager(InkSurface);

        // 트레이 상주
        _tray = new TrayService("ScreenPen Portable");
        _tray.ToggleRequested += () => Dispatcher.Invoke(ToggleToolbar);
        _tray.ScreenshotRequested += () => Dispatcher.Invoke(TakeScreenshot);
        _tray.ClearRequested += () => Dispatcher.Invoke(() => _undo?.ClearAll());
        _tray.ExitRequested += () => Dispatcher.Invoke(Close);

        // 플로팅 툴바
        _toolbar = new ToolbarWindow
        {
            Left = _settings.ToolbarLeft,
            Top = _settings.ToolbarTop
        };
        _toolbar.ToolSelected += SetTool;
        _toolbar.ColorSelected += SetColor;
        _toolbar.ThicknessChanged += SetThickness;
        _toolbar.UndoRequested += () => _undo?.Undo();
        _toolbar.RedoRequested += () => _undo?.Redo();
        _toolbar.ClearRequested += () => _undo?.ClearAll();
        _toolbar.ScreenshotRequested += TakeScreenshot;
        _toolbar.PassThroughToggled += TogglePassThrough;
        _toolbar.ExitRequested += Close;
        _toolbar.SetQuickColors(_settings.QuickColors);
        _toolbar.Show();

        // 마지막 도구 복원
        _tool = _settings.LastTool;
        ApplyTool();
        _toolbar.SetActiveTool(_tool);
        _toolbar.SetThickness(CurrentWidth());
        _toolbar.SetPassThrough(false);
    }

    // ── 도구/색상/굵기 ─────────────────────────────────────
    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private double CurrentWidth() => _tool switch
    {
        ToolKind.Pen => _settings.PenWidth,
        ToolKind.Highlighter => _settings.HighlighterWidth,
        _ => _settings.EraserWidth
    };

    private void ApplyTool()
    {
        if (_tool == ToolKind.Eraser)
        {
            InkSurface.EditingMode = InkCanvasEditingMode.EraseByStroke;
            return;
        }

        InkSurface.EditingMode = InkCanvasEditingMode.Ink;
        var da = new DrawingAttributes { FitToCurve = true };
        if (_tool == ToolKind.Highlighter)
        {
            da.IsHighlighter = true;
            da.Color = ParseColor(_settings.HighlighterColor);
            da.Width = da.Height = _settings.HighlighterWidth;
        }
        else
        {
            da.Color = ParseColor(_settings.PenColor);
            da.Width = da.Height = _settings.PenWidth;
        }
        InkSurface.DefaultDrawingAttributes = da;
    }

    private void SetTool(ToolKind t)
    {
        _tool = t;
        _settings.LastTool = t;
        ApplyTool();
        _toolbar?.SetActiveTool(t);
        _toolbar?.SetThickness(CurrentWidth());
    }

    private void SetColor(Color c)
    {
        if (_tool == ToolKind.Highlighter)
        {
            c.A = 0x80; // 하이라이터는 반투명 유지
            _settings.HighlighterColor = c.ToString();
        }
        else
        {
            // 지우개에서 색을 고르면 펜으로 전환
            if (_tool == ToolKind.Eraser) { SetTool(ToolKind.Pen); }
            _settings.PenColor = c.ToString();
        }
        ApplyTool();
    }

    private void SetThickness(double w)
    {
        switch (_tool)
        {
            case ToolKind.Pen: _settings.PenWidth = w; break;
            case ToolKind.Highlighter: _settings.HighlighterWidth = w; break;
            default: _settings.EraserWidth = w; break;
        }
        ApplyTool();
    }

    // ── 스크린샷 ───────────────────────────────────────────
    private void TakeScreenshot()
    {
        bool tbVisible = _toolbar?.IsVisible ?? false;
        if (tbVisible) _toolbar!.Hide();

        // 툴바가 화면에서 사라진 뒤 캡처(논블로킹)
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            try
            {
                string dir = string.IsNullOrEmpty(_settings.LastScreenshotDir) ? null! : _settings.LastScreenshotDir;
                string path = ScreenshotService.CaptureVirtualScreenToFileAndClipboard(dir);
                _settings.LastScreenshotDir = System.IO.Path.GetDirectoryName(path) ?? "";
                _tray?.SetModeText($"저장됨: {System.IO.Path.GetFileName(path)}");
            }
            catch { /* 캡처 실패는 무시 */ }
            if (tbVisible) _toolbar!.Show();
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

    // ── 핫키 ───────────────────────────────────────────────
    private void RegisterHotkeys()
    {
        uint cm = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;
        RegisterHotKey(_hwnd, (int)Hk.Toggle, cm, (uint)KeyInterop.VirtualKeyFromKey(Key.D));
        RegisterHotKey(_hwnd, (int)Hk.Pen, cm, (uint)KeyInterop.VirtualKeyFromKey(Key.D1));
        RegisterHotKey(_hwnd, (int)Hk.Highlighter, cm, (uint)KeyInterop.VirtualKeyFromKey(Key.D2));
        RegisterHotKey(_hwnd, (int)Hk.Eraser, cm, (uint)KeyInterop.VirtualKeyFromKey(Key.D3));
        RegisterHotKey(_hwnd, (int)Hk.Undo, cm, (uint)KeyInterop.VirtualKeyFromKey(Key.Z));
        RegisterHotKey(_hwnd, (int)Hk.Redo, cm, (uint)KeyInterop.VirtualKeyFromKey(Key.Y));
        RegisterHotKey(_hwnd, (int)Hk.Clear, cm, (uint)KeyInterop.VirtualKeyFromKey(Key.E));
        RegisterHotKey(_hwnd, (int)Hk.Screenshot, cm, (uint)KeyInterop.VirtualKeyFromKey(Key.S));
        RegisterHotKey(_hwnd, (int)Hk.Toolbar, cm, (uint)KeyInterop.VirtualKeyFromKey(Key.T));
        RegisterHotKey(_hwnd, (int)Hk.Exit, cm, (uint)KeyInterop.VirtualKeyFromKey(Key.Q));
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
            case Hk.Undo: _undo?.Undo(); break;
            case Hk.Redo: _undo?.Redo(); break;
            case Hk.Clear: _undo?.ClearAll(); break;
            case Hk.Screenshot: TakeScreenshot(); break;
            case Hk.Toolbar: ToggleToolbar(); break;
            case Hk.Exit: Close(); break;
            default: handled = false; break;
        }
        return IntPtr.Zero;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // 그리기 모드(포커스 보유 시)에서의 보조 키
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Delete: _undo?.ClearAll(); break;
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

        _tray?.Dispose();
        _toolbar?.Close();
    }
}
