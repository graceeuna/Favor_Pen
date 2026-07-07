using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using FavorPen.Services;

namespace FavorPen.UI;

/// <summary>
/// 교실 모둠활동용 소음 신호등. 마이크로 소음 크기를 측정해 초록/노랑/빨강 신호등으로 보여 준다.
/// 너무 시끄러운 상태(빨강)가 잠시 지속되면 경고음을 내고 경고 횟수를 센다.
///
/// 타이머/랜덤 창과 동일한 패턴: 입력 가능한 일반 창이며 지휘자가 Owner=오버레이로 띄운다.
/// 실제 측정은 <see cref="NoiseMonitor"/>(백그라운드 오디오)에 맡기고, 이 창은
/// <see cref="DispatcherTimer"/> 로 레벨을 폴링해 UI 를 갱신한다.
/// </summary>
public partial class NoiseMeterWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);

    // 램프 색(켜짐/꺼짐).
    private static readonly Brush RedOn = Frozen(0xFF, 0xE5, 0x39, 0x35);
    private static readonly Brush RedOff = Frozen(0x40, 0xE5, 0x39, 0x35);
    private static readonly Brush YellowOn = Frozen(0xFF, 0xFF, 0xC6, 0x1A);
    private static readonly Brush YellowOff = Frozen(0x40, 0xFF, 0xC6, 0x1A);
    private static readonly Brush GreenOn = Frozen(0xFF, 0x44, 0xD1, 0x3B);
    private static readonly Brush GreenOff = Frozen(0x40, 0x44, 0xD1, 0x3B);
    private static readonly Brush SubBrush = Frozen(0xFF, 0xB0, 0xB0, 0xB0);

    private enum Level3 { Off, Green, Yellow, Red }

    private readonly NoiseMonitor _monitor = new();
    private readonly DispatcherTimer _poll;

    private int _sensitivity = 3;   // 1(둔감)~5(예민)
    private int _warnCount;
    private bool _micOn;
    private bool _userMoved;

    private Level3 _state = Level3.Off;
    private int _redTicks;          // 빨강 연속 지속 폴 수
    private bool _warned;           // 현재 빨강 구간에서 이미 경고했는지

    // 폴링 주기 ~66ms. 빨강이 약 1.2초(≈18틱) 지속되면 경고.
    private const int RedWarnTicks = 18;

    public NoiseMeterWindow()
    {
        InitializeComponent();

        _poll = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(66)
        };
        _poll.Tick += OnPoll;

        SizeChanged += (_, _) => { if (!_userMoved) CenterOnPrimary(); };
        SetLamps(Level3.Off);
        UpdateSensText();
    }

    // ── 외부(지휘자) API ───────────────────────────────────────
    /// <summary>민감도(1~5). 종료 시 영속화에 사용.</summary>
    public int Sensitivity
    {
        get => _sensitivity;
        set { _sensitivity = Math.Clamp(value, 1, 5); UpdateSensText(); }
    }

    public void Toggle()
    {
        if (IsVisible) HideMeter();
        else ShowMeter();
    }

    public void ShowMeter()
    {
        _userMoved = false;
        Show();
        Activate();
        StartMic();
        Dispatcher.BeginInvoke(new Action(CenterOnPrimary), DispatcherPriority.Loaded);
    }

    public void HideMeter()
    {
        StopMic();
        Hide();
    }

    // ── 마이크 시작/중지 ───────────────────────────────────────
    private void StartMic()
    {
        _monitor.Start();
        if (_monitor.State == NoiseMonitor.MonitorState.NoDevice)
        {
            MicStatus.Text = "마이크를 찾을 수 없어요";
            MicButton.Content = "▶ 시작";
            _micOn = false;
            return;
        }
        if (_monitor.State == NoiseMonitor.MonitorState.Error)
        {
            MicStatus.Text = "마이크를 열 수 없어요";
            MicButton.Content = "▶ 시작";
            _micOn = false;
            return;
        }

        _micOn = true;
        _redTicks = 0;
        _warned = false;
        MicButton.Content = "⏸ 중지";
        MicStatus.Text = "측정 중…";
        _poll.Start();
    }

    private void StopMic()
    {
        _poll.Stop();
        _monitor.Stop();
        _micOn = false;
        MicButton.Content = "▶ 시작";
        MicStatus.Text = "";
        SetState(Level3.Off);
        LevelText.Text = "0";
        LevelBar.Width = 0;
    }

    private void OnToggleMic(object sender, RoutedEventArgs e)
    {
        if (_micOn) StopMic();
        else StartMic();
    }

    // ── 폴링(레벨 → 신호등) ────────────────────────────────────
    private void OnPoll(object? sender, EventArgs e)
    {
        double level = _monitor.Level;
        LevelText.Text = ((int)Math.Round(level)).ToString();

        // 레벨 막대(최대 폭 210).
        LevelBar.Width = Math.Clamp(level / 100.0 * 210.0, 0, 210);

        var (greenMax, yellowMax) = Thresholds(_sensitivity);
        Level3 lvl = level <= greenMax ? Level3.Green
                   : level <= yellowMax ? Level3.Yellow
                   : Level3.Red;
        SetState(lvl);

        // 경고: 빨강이 일정 시간 지속되면 1회 알림(빨강을 벗어나면 재무장).
        if (lvl == Level3.Red)
        {
            if (++_redTicks >= RedWarnTicks && !_warned)
            {
                _warned = true;
                _warnCount++;
                WarnText.Text = $"{_warnCount}회";
                if (SoundCheck.IsChecked == true)
                {
                    try { MessageBeep(0x30); } catch { /* 무시 */ }
                }
            }
        }
        else
        {
            _redTicks = 0;
            _warned = false;
        }
    }

    /// <summary>민감도(1~5) → (greenMax, yellowMax) 임계값(0~100).
    /// 예민할수록 임계값이 낮아 작은 소리에도 노랑/빨강으로 넘어간다.</summary>
    private static (double greenMax, double yellowMax) Thresholds(int sens)
    {
        double t = (sens - 1) / 4.0; // 0(둔감)~1(예민)
        double greenMax = 55 - 30 * t; // 55 → 25
        double yellowMax = 78 - 33 * t; // 78 → 45
        return (greenMax, yellowMax);
    }

    private void SetState(Level3 lvl)
    {
        if (lvl == _state) return;
        _state = lvl;
        SetLamps(lvl);

        (StatusText.Text, StatusText.Foreground, LevelBar.Background) = lvl switch
        {
            Level3.Green => ("조용해요!", GreenOn, GreenOn),
            Level3.Yellow => ("조금 시끄러워요", YellowOn, YellowOn),
            Level3.Red => ("너무 시끄러워요!", RedOn, RedOn),
            _ => ("멈춤", SubBrush, GreenOn),
        };
    }

    private void SetLamps(Level3 lvl)
    {
        LampRed.Fill = lvl == Level3.Red ? RedOn : RedOff;
        LampYellow.Fill = lvl == Level3.Yellow ? YellowOn : YellowOff;
        LampGreen.Fill = lvl == Level3.Green ? GreenOn : GreenOff;

        LampRed.Effect = lvl == Level3.Red ? Glow(0xE5, 0x39, 0x35) : null;
        LampYellow.Effect = lvl == Level3.Yellow ? Glow(0xFF, 0xC6, 0x1A) : null;
        LampGreen.Effect = lvl == Level3.Green ? Glow(0x44, 0xD1, 0x3B) : null;
    }

    // ── 민감도 조작 ────────────────────────────────────────────
    private void OnSensPlus(object sender, RoutedEventArgs e) => Sensitivity = _sensitivity + 1;
    private void OnSensMinus(object sender, RoutedEventArgs e) => Sensitivity = _sensitivity - 1;
    private void UpdateSensText() { if (SensText != null) SensText.Text = _sensitivity.ToString(); }

    private void OnCloseClick(object sender, RoutedEventArgs e) => HideMeter();

    // ── 위치 ───────────────────────────────────────────────────
    private void CenterOnPrimary()
    {
        var p = System.Windows.Forms.Screen.PrimaryScreen;
        if (p == null)
        {
            Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
            Top = (SystemParameters.PrimaryScreenHeight - ActualHeight) / 2;
            return;
        }
        var b = p.Bounds;
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

    protected override void OnClosed(EventArgs e)
    {
        _poll.Stop();
        _monitor.Dispose();
        base.OnClosed(e);
    }

    // ── 헬퍼 ───────────────────────────────────────────────────
    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }

    private static DropShadowEffect Glow(byte r, byte g, byte b) => new()
    {
        Color = Color.FromRgb(r, g, b),
        BlurRadius = 26,
        ShadowDepth = 0,
        Opacity = 0.9,
    };
}
