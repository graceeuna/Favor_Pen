using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using FavorPen.Services;

namespace FavorPen.UI;

/// <summary>
/// 교실 모둠활동용 소음 신호등. 마이크로 소음 크기를 측정해 초록/노랑/빨강 신호등으로 보여 준다.
/// 경고 기준(노랑·빨강 dB)은 직접 숫자로 입력하거나, 자동 학습으로 채울 수 있다.
/// 빨강이 잠시 지속되면 경고음을 내고 경고 횟수를 센다.
///
/// 표시되는 dB 는 공인 소음계(SPL)가 아니라 이 마이크 기준의 상대값이다.
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
    private static readonly Brush WindowedBg = Frozen(0xE6, 0x10, 0x10, 0x10);   // 창 모드(반투명)
    private static readonly Brush FullscreenBg = Frozen(0xFF, 0x0E, 0x11, 0x16); // 전체화면(불투명)

    private enum Level3 { Off, Green, Yellow, Red }

    private readonly NoiseMonitor _monitor = new();
    private readonly DispatcherTimer _poll;

    private int _warnCount;
    private bool _micOn;
    private bool _userMoved;

    // 경고 기준(dB): 이 값 이상이면 노랑 / 빨강. 직접 입력 또는 자동 학습으로 설정.
    private double _yellowAt = 75;
    private double _redAt = 88;

    // 자동 학습.
    private bool _autoLearn;
    private double _autoQuiet = -1, _autoLoud = -1;
    private bool _syncingBoxes; // 자동이 입력칸을 갱신할 때 TextChanged 되먹임 방지
    private const double AutoQuietRise = 0.010; // 틱당(66ms) 상승
    private const double AutoLoudDecay = 0.010; // 틱당 하강

    private Level3 _state = Level3.Off;
    private int _redTicks;
    private bool _warned;

    // 폴링 주기 ~66ms. 빨강이 약 1.2초(≈18틱) 지속되면 경고.
    private const int RedWarnTicks = 18;

    // 표시부 크기(배율) / 전체화면.
    private double _scale = 1.0;
    private bool _fullscreen;
    private double _savedLeft, _savedTop;
    private const double DisplayBaseHeight = 290;
    private const double ScaleMin = 0.7, ScaleMax = 3.0, ScaleStep = 0.2;

    public NoiseMeterWindow()
    {
        InitializeComponent();

        _poll = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(66)
        };
        _poll.Tick += OnPoll;

        SizeChanged += (_, _) => { if (!_userMoved && !_fullscreen) CenterOnPrimary(); };
        SetLamps(Level3.Off);
        SyncBoxesFromValues();
        UpdateCalStatus();
        ApplyScale();
    }

    // ── 외부(지휘자) API ───────────────────────────────────────
    /// <summary>노랑 경고 기준(dB). 종료 시 영속화.</summary>
    public double YellowAt
    {
        get => _yellowAt;
        set { _yellowAt = Math.Clamp(value, 20, 110); SyncBoxesFromValues(); }
    }

    /// <summary>빨강 경고 기준(dB). 종료 시 영속화.</summary>
    public double RedAt
    {
        get => _redAt;
        set { _redAt = Math.Clamp(value, 20, 110); SyncBoxesFromValues(); }
    }

    /// <summary>자동 기준 학습 사용 여부. 종료 시 영속화.</summary>
    public bool AutoLearn
    {
        get => _autoLearn;
        set
        {
            _autoLearn = value;
            if (AutoCheck != null) AutoCheck.IsChecked = value;
            if (YellowBox != null) YellowBox.IsEnabled = !value;
            if (RedBox != null) RedBox.IsEnabled = !value;
            if (value) { _autoQuiet = -1; _autoLoud = -1; }
            UpdateCalStatus();
        }
    }

    /// <summary>표시부 크기 배율(0.7~3.0). 종료 시 영속화.</summary>
    public double Scale
    {
        get => _scale;
        set { _scale = Math.Clamp(value, ScaleMin, ScaleMax); ApplyScale(); }
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
        if (_fullscreen) ExitFullscreen();
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
        UpdateCalStatus();
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

        // 레벨 막대(대략 40~95 dB → 0~폭).
        LevelBar.Width = Math.Clamp((level - 40) / 55.0, 0, 1) * 210.0;

        if (_autoLearn)
            AutoAdapt(level);

        double redAt = Math.Max(_redAt, _yellowAt);
        Level3 lvl = level < _yellowAt ? Level3.Green
                   : level < redAt ? Level3.Yellow
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

    // ── 경고 기준(dB) ──────────────────────────────────────────
    private void OnThresholdChanged(object sender, TextChangedEventArgs e)
    {
        // 초기화 중(다른 칸이 아직 생성 전)·자동 갱신 중에는 무시.
        if (_syncingBoxes || _autoLearn || YellowBox == null || RedBox == null) return;
        if (double.TryParse(YellowBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double y))
            _yellowAt = Math.Clamp(y, 20, 110);
        if (double.TryParse(RedBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double r))
            _redAt = Math.Clamp(r, 20, 110);
    }

    private void OnAutoToggle(object sender, RoutedEventArgs e)
    {
        AutoLearn = AutoCheck.IsChecked == true;
    }

    /// <summary>자동 학습: 관찰된 최저(조용)/최고(시끄러움)를 따라가며 노랑·빨강 기준을 채운다.
    /// 조용은 서서히 오르고, 시끄러움은 서서히 내려 교실 변화에 적응한다.</summary>
    private void AutoAdapt(double level)
    {
        if (_autoQuiet < 0) { _autoQuiet = level; _autoLoud = level; }

        _autoQuiet = level < _autoQuiet ? level : _autoQuiet + AutoQuietRise;
        _autoLoud = level > _autoLoud ? level : _autoLoud - AutoLoudDecay;
        if (_autoLoud < _autoQuiet) _autoLoud = _autoQuiet;

        double range = _autoLoud - _autoQuiet;
        if (range >= 4)
        {
            _yellowAt = _autoQuiet + range * 0.45;
            _redAt = _autoQuiet + range * 0.78;
            SyncBoxesFromValues();
            UpdateCalStatus();
        }
    }

    private void SyncBoxesFromValues()
    {
        if (YellowBox == null || RedBox == null) return;
        _syncingBoxes = true;
        YellowBox.Text = ((int)Math.Round(_yellowAt)).ToString();
        RedBox.Text = ((int)Math.Round(_redAt)).ToString();
        _syncingBoxes = false;
    }

    private void UpdateCalStatus()
    {
        if (CalStatus == null) return;
        CalStatus.Text = _autoLearn
            ? $"자동 학습 중 (노랑 {Math.Round(_yellowAt)} · 빨강 {Math.Round(_redAt)})"
            : "현재 소리를 보며 숫자를 정하세요";
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

    private void OnCloseClick(object sender, RoutedEventArgs e) => HideMeter();

    // ── 크기(배율) ─────────────────────────────────────────────
    private void OnSizeBigger(object sender, RoutedEventArgs e) => Scale = _scale + ScaleStep;
    private void OnSizeSmaller(object sender, RoutedEventArgs e) => Scale = _scale - ScaleStep;

    private void ApplyScale()
    {
        if (SizeText != null)
            SizeText.Text = $"{Math.Round(_scale * 100)}%";
        if (DisplayBox != null && !_fullscreen)
            DisplayBox.Height = DisplayBaseHeight * _scale;
    }

    // ── 전체화면 ───────────────────────────────────────────────
    private void OnToggleFull(object sender, RoutedEventArgs e)
    {
        if (_fullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        if (_fullscreen) return;
        _fullscreen = true;
        _savedLeft = Left;
        _savedTop = Top;

        var p = System.Windows.Forms.Screen.PrimaryScreen;
        var src = PresentationSource.FromVisual(this);
        double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        SizeToContent = SizeToContent.Manual;
        if (p != null)
        {
            var b = p.Bounds;
            Left = b.Left / sx; Top = b.Top / sy;
            Width = b.Width / sx; Height = b.Height / sy;
        }
        else
        {
            Left = 0; Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
        }

        HeaderText.Visibility = Visibility.Collapsed;
        ControlPanel.Visibility = Visibility.Collapsed;
        Root.CornerRadius = new CornerRadius(0);
        Root.Padding = new Thickness(48);
        Root.Background = FullscreenBg;
        DisplayRow.Height = new GridLength(1, GridUnitType.Star);
        DisplayBox.Height = double.NaN;
        DisplayBox.VerticalAlignment = VerticalAlignment.Stretch;
        FullBtn.Content = "🡼 작게";
        Topmost = true;
        Activate();
    }

    private void ExitFullscreen()
    {
        if (!_fullscreen) return;
        _fullscreen = false;

        HeaderText.Visibility = Visibility.Visible;
        ControlPanel.Visibility = Visibility.Visible;
        Root.CornerRadius = new CornerRadius(18);
        Root.Padding = new Thickness(22, 18, 22, 18);
        Root.Background = WindowedBg;
        DisplayRow.Height = GridLength.Auto;
        DisplayBox.VerticalAlignment = VerticalAlignment.Center;
        FullBtn.Content = "⛶ 전체화면";
        SizeToContent = SizeToContent.WidthAndHeight;
        ApplyScale();

        Left = _savedLeft; Top = _savedTop;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Left < 0 || Top < 0 || Left > SystemParameters.VirtualScreenWidth || Top > SystemParameters.VirtualScreenHeight)
                CenterOnPrimary();
        }), DispatcherPriority.Loaded);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _fullscreen)
        {
            ExitFullscreen();
            e.Handled = true;
        }
    }

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
        if (_fullscreen) return; // 전체화면에서는 드래그 이동 금지
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
