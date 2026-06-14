using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenPenPortable.Services;

/// <summary>
/// FR-16: 화이트보드/블랙보드 불투명 배경을 제어한다.
/// MainWindow 의 <c>Border x:Name="BackdropLayer"</c>(InkSurface 뒤, 기본 Collapsed)를 받아
/// 불투명 색으로 채우거나 숨긴다. 특정 모니터 영역으로 제한(<see cref="SetTargetArea"/>)하거나
/// 가상화면 전체(<see cref="SetFullVirtual"/>)로 복귀할 수 있다.
///
/// 색은 호출자(lead)가 <see cref="Color"/> 로 변환해 주입한다. 이 클래스는 AppSettings 에 의존하지 않는다.
/// </summary>
public sealed class WhiteboardController
{
    /// <summary>백드롭 표시 상태.</summary>
    public enum BoardMode { Off, White, Black }

    private readonly Border _backdrop;

    /// <summary>현재 백드롭 모드.</summary>
    public BoardMode Mode { get; private set; } = BoardMode.Off;

    /// <param name="backdrop">
    /// InkSurface 뒤에 놓인 백드롭 Border(<c>x:Name="BackdropLayer"</c>). 기본 Collapsed 상태로 가정한다.
    /// </param>
    public WhiteboardController(Border backdrop)
    {
        _backdrop = backdrop;
    }

    /// <summary>화이트보드를 불투명 <paramref name="color"/> 로 표시한다.</summary>
    public void ShowWhite(Color color)
    {
        Apply(color);
        Mode = BoardMode.White;
    }

    /// <summary>블랙보드를 불투명 <paramref name="color"/> 로 표시한다.</summary>
    public void ShowBlack(Color color)
    {
        Apply(color);
        Mode = BoardMode.Black;
    }

    /// <summary>백드롭을 숨긴다(투명한 오버레이로 복귀).</summary>
    public void Hide()
    {
        _backdrop.Visibility = Visibility.Collapsed;
        _backdrop.Background = null;
        Mode = BoardMode.Off;
    }

    /// <summary>
    /// Off → White → Black → Off 순으로 모드를 순환하고 새 모드를 반환한다.
    /// 화이트/블랙보드 색은 인자로 받는다.
    /// </summary>
    public BoardMode Cycle(Color white, Color black)
    {
        switch (Mode)
        {
            case BoardMode.Off:
                ShowWhite(white);
                break;
            case BoardMode.White:
                ShowBlack(black);
                break;
            default: // Black
                Hide();
                break;
        }
        return Mode;
    }

    /// <summary>
    /// FR-16: 백드롭을 가상화면 내 특정 사각형으로 제한한다.
    /// 인자(<paramref name="left"/>/<paramref name="top"/>)는 <b>이미 가상화면 원점 기준 DIP 오프셋</b>이다
    /// (즉 Margin 으로 직접 사용된다). lead 가 모니터 rect 와 virtualLeft/Top 의 차이를 미리 계산해 넘긴다.
    /// 표시 중이 아니어도 다음 표시를 위해 레이아웃만 설정해 둔다(Visibility 는 건드리지 않음).
    /// </summary>
    /// <param name="left">가상화면 원점 기준 좌측 오프셋(DIP).</param>
    /// <param name="top">가상화면 원점 기준 상단 오프셋(DIP).</param>
    /// <param name="width">대상 영역 너비(DIP).</param>
    /// <param name="height">대상 영역 높이(DIP).</param>
    public void SetTargetArea(double left, double top, double width, double height)
    {
        _backdrop.HorizontalAlignment = HorizontalAlignment.Left;
        _backdrop.VerticalAlignment = VerticalAlignment.Top;
        _backdrop.Margin = new Thickness(left, top, 0, 0);
        _backdrop.Width = width;
        _backdrop.Height = height;
    }

    /// <summary>백드롭을 가상화면 전체(Grid 전체 Stretch)로 복귀시킨다.</summary>
    public void SetFullVirtual()
    {
        _backdrop.HorizontalAlignment = HorizontalAlignment.Stretch;
        _backdrop.VerticalAlignment = VerticalAlignment.Stretch;
        _backdrop.Margin = new Thickness(0);
        _backdrop.Width = double.NaN;
        _backdrop.Height = double.NaN;
    }

    private void Apply(Color color)
    {
        _backdrop.Background = new SolidColorBrush(color);
        _backdrop.Visibility = Visibility.Visible;
    }
}
