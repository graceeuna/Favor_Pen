using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FavorPen.UI;

/// <summary>
/// 화면 중앙에 표시되는 카운트다운 타이머. 사용자가 시간(±분/±초)과 글자 크기(A−/A+)를
/// 직접 설정하고 시작/일시정지/리셋할 수 있다. 0 도달 시 빨간색 점멸 + 비프.
///
/// 입력 가능한 일반 창(컨트롤 클릭 필요)이며, 지휘자가 Owner=오버레이로 띄워
/// 그리기 모드에서도 버튼이 클릭되게 한다(툴바와 동일 패턴).
/// </summary>
public partial class TimerWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);

    private const int MaxSeconds = 99 * 60 + 59; // 99:59
    private static readonly Brush NormalBrush = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xED));
    private static readonly Brush DoneBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x52, 0x52));

    private readonly DispatcherTimer _tick;
    private DispatcherTimer? _blink;
    private int _blinkCount;

    private int _duration = 300; // 설정 시간(초)
    private int _remaining = 300; // 남은 시간(초)
    private bool _running;
    private double _fontSize = 140;
    private bool _userMoved;

    public TimerWindow()
    {
        InitializeComponent();
        NormalBrush.Freeze();
        DoneBrush.Freeze();

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += OnTick;

        SizeChanged += (_, _) => { if (!_userMoved) CenterOnPrimary(); };
        UpdateDisplay();
    }

    /// <summary>설정 시간(초). 종료 시 영속화에 사용.</summary>
    public int DurationSeconds => _duration;

    /// <summary>현재 글자 크기. 종료 시 영속화에 사용.</summary>
    public double FontSizeValue => _fontSize;

    // ── 외부(지휘자) API ───────────────────────────────────────
    public void SetDuration(int seconds)
    {
        _duration = Math.Clamp(seconds, 0, MaxSeconds);
        if (!_running) _remaining = _duration;
        UpdateDisplay();
    }

    public void SetFontSize(double size)
    {
        _fontSize = Math.Clamp(size, 40, 400);
        if (TimeText != null) TimeText.FontSize = _fontSize;
    }

    public void Toggle()
    {
        if (IsVisible) HideTimer();
        else ShowTimer();
    }

    public void ShowTimer()
    {
        _userMoved = false;
        Show();
        Activate();
        // 레이아웃 완료 후 중앙 배치.
        Dispatcher.BeginInvoke(new Action(CenterOnPrimary), DispatcherPriority.Loaded);
    }

    public void HideTimer()
    {
        StopBlink(); // 점멸 중 숨겨도 _blink 타이머가 계속 돌지 않도록 정리
        Hide();
    }

    // ── 카운트다운 ─────────────────────────────────────────────
    private void OnTick(object? sender, EventArgs e)
    {
        if (!_running) return;
        if (_remaining > 0)
        {
            _remaining--;
            UpdateDisplay();
            if (_remaining == 0) OnFinished();
        }
    }

    private void OnFinished()
    {
        _running = false;
        _tick.Stop();
        StartButton.Content = "▶ 시작";
        TimeText.Foreground = DoneBrush;
        try { MessageBeep(0xFFFFFFFF); } catch { /* 무시 */ }
        StartBlink();
    }

    private void OnStartPause(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _running = false;
            _tick.Stop();
            StartButton.Content = "▶ 시작";
        }
        else
        {
            if (_remaining <= 0) _remaining = _duration;
            if (_remaining <= 0) return; // 0초는 시작 불가
            _running = true;
            StopBlink();
            TimeText.Foreground = NormalBrush;
            StartButton.Content = "⏸ 일시정지";
            _tick.Start();
            UpdateDisplay();
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _running = false;
        _tick.Stop();
        StopBlink();
        _remaining = _duration;
        TimeText.Foreground = NormalBrush;
        StartButton.Content = "▶ 시작";
        UpdateDisplay();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => HideTimer();

    // ── 시간 설정 ──────────────────────────────────────────────
    private void OnPlusMin(object sender, RoutedEventArgs e) => AdjustDuration(+60);
    private void OnMinusMin(object sender, RoutedEventArgs e) => AdjustDuration(-60);
    private void OnPlusSec(object sender, RoutedEventArgs e) => AdjustDuration(+10);
    private void OnMinusSec(object sender, RoutedEventArgs e) => AdjustDuration(-10);

    private void AdjustDuration(int deltaSeconds)
    {
        _duration = Math.Clamp(_duration + deltaSeconds, 0, MaxSeconds);
        // 설정을 바꾸면 카운트다운도 새 설정값으로 맞춘다(정지·재설정 흐름).
        _running = false;
        _tick.Stop();
        StopBlink();
        _remaining = _duration;
        TimeText.Foreground = NormalBrush;
        StartButton.Content = "▶ 시작";
        UpdateDisplay();
    }

    // ── 글자 크기 ──────────────────────────────────────────────
    private void OnLarger(object sender, RoutedEventArgs e) => SetFontSize(_fontSize + 16);
    private void OnSmaller(object sender, RoutedEventArgs e) => SetFontSize(_fontSize - 16);

    // ── 표시/위치 ──────────────────────────────────────────────
    private void UpdateDisplay()
    {
        if (TimeText == null) return;
        int s = _remaining;
        int h = s / 3600, m = (s % 3600) / 60, sec = s % 60;
        TimeText.Text = h > 0 ? $"{h}:{m:00}:{sec:00}" : $"{m:00}:{sec:00}";
    }

    private void CenterOnPrimary()
    {
        // 주모니터의 절대 위치(가상화면 좌표)를 써야 한다. SystemParameters.PrimaryScreenWidth/Height
        // 만으로 (0,0) 기준 중앙을 잡으면 주모니터가 가상화면 원점이 아닐 때(좌측에 보조모니터 →
        // VirtualScreenLeft<0) 엉뚱한 모니터로 빗나간다.
        var p = System.Windows.Forms.Screen.PrimaryScreen;
        if (p == null)
        {
            Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
            Top = (SystemParameters.PrimaryScreenHeight - ActualHeight) / 2;
            return;
        }

        var b = p.Bounds; // 물리 px
        var src = PresentationSource.FromVisual(this);
        double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        double leftDip = b.Left / sx, topDip = b.Top / sy;
        double wDip = b.Width / sx, hDip = b.Height / sy;
        Left = leftDip + (wDip - ActualWidth) / 2;
        Top = topDip + (hDip - ActualHeight) / 2;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            _userMoved = true;
            DragMove();
        }
    }

    // ── 0 도달 점멸 ────────────────────────────────────────────
    private void StartBlink()
    {
        StopBlink();
        _blinkCount = 0;
        _blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _blink.Tick += (_, _) =>
        {
            TimeText.Opacity = TimeText.Opacity < 1.0 ? 1.0 : 0.25;
            if (++_blinkCount >= 10) StopBlink();
        };
        _blink.Start();
    }

    private void StopBlink()
    {
        _blink?.Stop();
        _blink = null;
        if (TimeText != null) TimeText.Opacity = 1.0;
    }
}
