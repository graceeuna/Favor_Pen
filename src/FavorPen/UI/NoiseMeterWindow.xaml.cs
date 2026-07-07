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
    private static readonly Brush WindowedBg = Frozen(0xE6, 0x10, 0x10, 0x10);   // 창 모드(반투명)
    private static readonly Brush FullscreenBg = Frozen(0xFF, 0x0E, 0x11, 0x16); // 전체화면(불투명)

    private enum Level3 { Off, Green, Yellow, Red }

    private readonly NoiseMonitor _monitor = new();
    private readonly DispatcherTimer _poll;

    private int _sensitivity = 3;   // 1(둔감)~5(예민)
    private int _warnCount;
    private bool _micOn;
    private bool _userMoved;

    // 교실 기준(캘리브레이션). -1 = 미설정. 둘 다 유효하면 민감도 대신 이 기준을 쓴다.
    // 자동 학습·수동 버튼이 이 같은 값을 공유한다(자동=계속 조정, 수동=그 순간 고정).
    private double _quietRef = -1;
    private double _loudRef = -1;
    private bool _autoLearn;

    // 자동 학습 시 조용은 서서히 오르고(과거 최저에 갇히지 않게), 시끄러움은 서서히 내린다.
    private const double AutoQuietRise = 0.010; // 틱당(66ms) 상승
    private const double AutoLoudDecay = 0.010; // 틱당 하강

    // 기준 측정(캡처) 상태: 0=없음, 1=조용, 2=시끄러움.
    private int _capTarget;
    private double _capSum;
    private int _capCount;
    private int _capTicksLeft;
    private const int CapTicks = 18; // ~1.2초 평균

    private Level3 _state = Level3.Off;
    private int _redTicks;          // 빨강 연속 지속 폴 수
    private bool _warned;           // 현재 빨강 구간에서 이미 경고했는지

    // 폴링 주기 ~66ms. 빨강이 약 1.2초(≈18틱) 지속되면 경고.
    private const int RedWarnTicks = 18;

    // 표시부 크기(배율) / 전체화면.
    private double _scale = 1.0;
    private bool _fullscreen;
    private double _savedLeft, _savedTop;
    private const double DisplayBaseHeight = 290; // 배율 1.0 일 때 표시부 높이(px)
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
        UpdateSensText();
        UpdateCalStatus();
        ApplyScale();
    }

    // ── 외부(지휘자) API ───────────────────────────────────────
    /// <summary>민감도(1~5). 종료 시 영속화에 사용.</summary>
    public int Sensitivity
    {
        get => _sensitivity;
        set { _sensitivity = Math.Clamp(value, 1, 5); UpdateSensText(); }
    }

    /// <summary>교실 기준 — 조용 레벨(0~100, -1=미설정). 종료 시 영속화.</summary>
    public double QuietRef
    {
        get => _quietRef;
        set { _quietRef = value; UpdateCalStatus(); }
    }

    /// <summary>교실 기준 — 시끄러움 레벨(0~100, -1=미설정). 종료 시 영속화.</summary>
    public double LoudRef
    {
        get => _loudRef;
        set { _loudRef = value; UpdateCalStatus(); }
    }

    /// <summary>두 기준이 모두 유효하고 순서가 맞으면 캘리브레이션 사용.</summary>
    private bool IsCalibrated => _quietRef >= 0 && _loudRef > _quietRef + 3;

    /// <summary>자동 기준 학습 사용 여부. 종료 시 영속화.</summary>
    public bool AutoLearn
    {
        get => _autoLearn;
        set
        {
            _autoLearn = value;
            if (AutoCheck != null) AutoCheck.IsChecked = value;
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
        CancelCapture();
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

        // 레벨 막대(최대 폭 210).
        LevelBar.Width = Math.Clamp(level / 100.0 * 210.0, 0, 210);

        // 기준 측정 중이면 레벨을 누적하고, 신호등 판정은 잠시 건너뛴다.
        if (_capTarget != 0)
        {
            _capSum += level;
            _capCount++;
            if (--_capTicksLeft <= 0)
                FinishCapture();
            return;
        }

        if (_autoLearn)
            AutoAdapt(level);

        var (greenMax, yellowMax) = CurrentThresholds();
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

    /// <summary>현재 임계값(0~100). 교실 기준이 있으면 그것을, 없으면 민감도를 사용한다.</summary>
    private (double greenMax, double yellowMax) CurrentThresholds()
    {
        if (IsCalibrated)
        {
            double range = _loudRef - _quietRef;
            // 조용~시끄러움 구간을 초록(하위 40%)/노랑(40~72%)/빨강(상위)으로 나눈다.
            double greenMax = _quietRef + range * 0.40;
            double yellowMax = _quietRef + range * 0.72;
            return (greenMax, yellowMax);
        }
        return Thresholds(_sensitivity);
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

    // ── 교실 기준 맞추기(캘리브레이션) ─────────────────────────
    private void OnCalQuiet(object sender, RoutedEventArgs e) => StartCapture(1);
    private void OnCalLoud(object sender, RoutedEventArgs e) => StartCapture(2);

    private void OnAutoToggle(object sender, RoutedEventArgs e)
    {
        _autoLearn = AutoCheck.IsChecked == true;
        // 자동을 처음 켤 때 기준이 비어 있으면 현재 레벨로 씨앗을 심는다(범위는 관찰하며 벌어진다).
        if (_autoLearn && (_quietRef < 0 || _loudRef < 0))
        {
            double now = _monitor.Level;
            _quietRef = now;
            _loudRef = now;
        }
        UpdateCalStatus();
    }

    /// <summary>자동 학습: 조용은 관찰된 최저를 따라가되 서서히 오르고,
    /// 시끄러움은 관찰된 최고를 따라가되 서서히 내린다. 수동 버튼으로 고정한 값도 여기서 이어 조정된다.</summary>
    private void AutoAdapt(double level)
    {
        if (_quietRef < 0) _quietRef = level;
        if (_loudRef < 0) _loudRef = level;

        // 조용 기준: 더 낮은 소리를 만나면 즉시 내리고, 아니면 조금씩 올린다.
        _quietRef = level < _quietRef ? level : _quietRef + AutoQuietRise;
        // 시끄러움 기준: 더 큰 소리를 만나면 즉시 올리고, 아니면 조금씩 내린다.
        _loudRef = level > _loudRef ? level : _loudRef - AutoLoudDecay;

        _quietRef = Math.Clamp(_quietRef, 0, 100);
        _loudRef = Math.Clamp(_loudRef, 0, 100);
        if (_loudRef < _quietRef) _loudRef = _quietRef;

        UpdateCalStatus();
    }

    private void OnCalReset(object sender, RoutedEventArgs e)
    {
        _quietRef = -1;
        _loudRef = -1;
        _capTarget = 0;
        UpdateCalStatus();
    }

    private void StartCapture(int target)
    {
        if (!_micOn)
        {
            CalStatus.Text = "먼저 ▶ 시작을 누르세요";
            return;
        }
        _capTarget = target;
        _capSum = 0;
        _capCount = 0;
        _capTicksLeft = CapTicks;
        CalQuietBtn.IsEnabled = false;
        CalLoudBtn.IsEnabled = false;
        CalStatus.Text = target == 1 ? "조용 기준 측정 중…" : "시끄러움 기준 측정 중…";
    }

    private void FinishCapture()
    {
        double avg = _capSum / Math.Max(1, _capCount);
        if (_capTarget == 1) _quietRef = avg;
        else if (_capTarget == 2) _loudRef = avg;

        _capTarget = 0;
        CalQuietBtn.IsEnabled = true;
        CalLoudBtn.IsEnabled = true;
        UpdateCalStatus();
    }

    private void CancelCapture()
    {
        _capTarget = 0;
        if (CalQuietBtn != null) CalQuietBtn.IsEnabled = true;
        if (CalLoudBtn != null) CalLoudBtn.IsEnabled = true;
    }

    private void UpdateCalStatus()
    {
        if (CalStatus == null) return;
        if (_capTarget != 0) return; // 측정 중 문구 유지

        if (IsCalibrated)
            CalStatus.Text = _autoLearn
                ? $"자동 학습 중 (조용 {_quietRef:0} · 시끄러움 {_loudRef:0})"
                : $"교실 기준 사용 중 (조용 {_quietRef:0} · 시끄러움 {_loudRef:0})";
        else if (_autoLearn)
            CalStatus.Text = "자동 학습 중… 소음 범위 익히는 중";
        else if (_quietRef >= 0 && _loudRef >= 0)
            CalStatus.Text = "‘시끄러움’이 ‘조용’보다 커야 해요 — 다시 잡아주세요";
        else if (_quietRef >= 0 || _loudRef >= 0)
            CalStatus.Text = "나머지 기준도 잡아주세요";
        else
            CalStatus.Text = "기준 미설정 — 민감도로 동작";
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

    // ── 크기(배율) ─────────────────────────────────────────────
    private void OnSizeBigger(object sender, RoutedEventArgs e) => Scale = _scale + ScaleStep;
    private void OnSizeSmaller(object sender, RoutedEventArgs e) => Scale = _scale - ScaleStep;

    private void ApplyScale()
    {
        if (SizeText != null)
            SizeText.Text = $"{Math.Round(_scale * 100)}%";
        // 전체화면에서는 Viewbox 가 화면을 꽉 채우므로 높이를 고정하지 않는다.
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

        // 주모니터를 꽉 채운다(가상화면 좌표 → DIP 환산).
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

        // 큰 화면용 레이아웃: 조작부/헤더 숨기고 표시부를 화면 가득 확대.
        HeaderText.Visibility = Visibility.Collapsed;
        ControlPanel.Visibility = Visibility.Collapsed;
        Root.CornerRadius = new CornerRadius(0);
        Root.Padding = new Thickness(48);
        Root.Background = FullscreenBg; // 교실 표시용 불투명 배경
        DisplayRow.Height = new GridLength(1, GridUnitType.Star);
        DisplayBox.Height = double.NaN;                 // Viewbox 가 남는 공간을 채우도록
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

        // 원래 창 위치로 복귀(레이아웃 후 보정).
        Left = _savedLeft; Top = _savedTop;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var (l, t) = (Left, Top);
            if (l < 0 || t < 0 || l > SystemParameters.VirtualScreenWidth || t > SystemParameters.VirtualScreenHeight)
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
