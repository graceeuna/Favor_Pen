using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ScreenPenPortable.UI;

/// <summary>
/// FR-21: 헤일로(반투명 원) 커서. 발표 중 마우스 위치를 강조한다.
///
/// MainWindow 의 <c>Canvas x:Name="OverlayLayer"</c>(InkSurface 위, IsHitTestVisible=False)에
/// 부드러운 RadialGradient 로 채워진 Ellipse 하나를 올려 커서를 따라 이동시킨다.
///
/// 추적 경로 2가지:
///  - 그리기 모드: <c>host.MouseMove</c>(WPF 입력 이벤트).
///  - 통과(click-through) 모드: WPF 가 입력을 못 받으므로 ~16ms <see cref="DispatcherTimer"/> 로
///    Win32 <c>GetCursorPos</c> 를 폴링 → 화면좌표를 <c>host.PointFromScreen</c> 으로 host 좌표 변환.
///
/// 오버레이는 IsHitTestVisible=False 라 입력을 가로채지 않는다(시각 전용).
/// 색/반지름은 호출자(lead)가 주입한다.
/// </summary>
public sealed class HighlightCursor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private readonly Canvas _overlay;
    private readonly Window _host;
    private readonly DispatcherTimer _timer;

    private Ellipse? _halo;
    private double _radius;

    /// <summary>헤일로 표시 여부.</summary>
    public bool IsEnabled { get; private set; }

    /// <param name="overlay">헤일로를 올릴 오버레이 캔버스(<c>x:Name="OverlayLayer"</c>).</param>
    /// <param name="host">커서 좌표 변환 기준이 되는 오버레이 윈도우(가상화면 전체를 덮는 창).</param>
    public HighlightCursor(Canvas overlay, Window host)
    {
        _overlay = overlay;
        _host = host;
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += OnTick;
    }

    /// <summary>헤일로를 켠다. 이미 켜져 있으면 색/반지름만 갱신한다.</summary>
    public void Enable(Color color, double radius)
    {
        _radius = radius;

        if (_halo == null)
        {
            _halo = new Ellipse { IsHitTestVisible = false };
            _overlay.Children.Add(_halo);
        }

        ApplyAppearance(color, radius);

        if (!IsEnabled)
        {
            IsEnabled = true;
            _host.MouseMove += OnHostMouseMove;
            _timer.Start();
        }

        // 즉시 한 번 위치 갱신(커서가 멈춰 있어도 바로 나타나도록).
        UpdateFromGlobalCursor();
    }

    /// <summary>헤일로를 끄고 오버레이에서 제거한다.</summary>
    public void Disable()
    {
        if (!IsEnabled) return;
        IsEnabled = false;

        _timer.Stop();
        _host.MouseMove -= OnHostMouseMove;

        if (_halo != null)
        {
            _overlay.Children.Remove(_halo);
            _halo = null;
        }
    }

    private void ApplyAppearance(Color color, double radius)
    {
        if (_halo == null) return;

        double d = radius * 2;
        _halo.Width = d;
        _halo.Height = d;

        // 중심은 진하고 가장자리로 갈수록 투명해지는 부드러운 헤일로.
        var center = color;
        var edge = Color.FromArgb(0, color.R, color.G, color.B);
        _halo.Fill = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(center, 0.0),
                new GradientStop(Color.FromArgb((byte)(color.A * 0.6), color.R, color.G, color.B), 0.6),
                new GradientStop(edge, 1.0)
            }
        };
    }

    private void OnHostMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_halo == null) return;
        MoveTo(e.GetPosition(_overlay));
    }

    private void OnTick(object? sender, EventArgs e) => UpdateFromGlobalCursor();

    private void UpdateFromGlobalCursor()
    {
        if (_halo == null) return;
        if (!GetCursorPos(out POINT p)) return;

        // 화면(픽셀) 좌표 → host 의 DIP 좌표. host 가 가상화면 전체를 덮으므로
        // 결과는 오버레이 좌표와 동일 원점이다.
        try
        {
            Point local = _host.PointFromScreen(new Point(p.X, p.Y));
            MoveTo(local);
        }
        catch
        {
            // host 가 아직 표시되지 않았거나 source 가 없으면 변환 실패 가능 — 무시.
        }
    }

    private void MoveTo(Point overlayPoint)
    {
        if (_halo == null) return;
        Canvas.SetLeft(_halo, overlayPoint.X - _radius);
        Canvas.SetTop(_halo, overlayPoint.Y - _radius);
    }
}
