using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ScreenPenPortable.UI;

/// <summary>
/// FR-24 돋보기 / 줌: 커서 주변 화면 영역을 확대해 보여 주는 작은 항상-위 프레임리스 창.
///
/// 동작 원리:
///  - <see cref="DispatcherTimer"/>(~40ms)로 커서 좌표(<see cref="GetCursorPos"/>)를 읽는다.
///  - 커서를 중심으로 (뷰 한 변 / zoom) 크기의 작은 소스 영역을 <see cref="Graphics.CopyFromScreen"/>
///    로 캡처하고, <see cref="Image"/> 에 Fill 로 늘려 그려 확대 효과를 낸다.
///  - 캡처한 GDI <see cref="Bitmap"/> 은 <c>GetHbitmap()</c>→<c>CreateBitmapSourceFromHBitmap</c>
///    로 WPF BitmapSource 로 변환한다. HBITMAP 은 반드시 <c>DeleteObject</c> 로 해제(누수 방지).
///    (Services/ScreenshotService.cs 의 변환 패턴과 동일.)
///
/// 자기 캡처(재귀) 방지:
///  - 소스 영역은 "커서 중심"으로 잡고, 창은 커서에서 일정 오프셋만큼 떨어뜨려 배치한다.
///    소스 영역과 창이 겹치지 않으면 자기 자신을 캡처하지 않는다.
///  - 그래도 겹칠 수 있는 화면 가장자리 상황을 대비해, 캡처 직전 창을 잠깐 숨겼다가
///    (Visibility=Hidden) 캡처 후 다시 표시한다. 깜빡임 최소화를 위해 Hidden 만 사용한다.
///
/// 입력:
///  - 창 자체는 IsHitTestVisible=False / Focusable=False(XAML)로 입력을 전혀 가로채지 않는다.
///    오버레이의 그리기/통과 동작에 영향을 주지 않는다.
///
/// 주의(좌표계): GetCursorPos 와 CopyFromScreen 은 모두 물리 픽셀(가상 화면 좌표)을 쓴다.
///  반면 WPF Left/Top 은 DIP(논리 좌표)다. 시스템 DPI 스케일을 반영해 창 위치를 환산한다.
/// </summary>
public partial class MagnifierWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>창과 커서 사이 오프셋(물리 px). 소스 영역과 창이 겹치지 않게 한다.</summary>
    private const int CursorOffsetPx = 28;

    private readonly DispatcherTimer _timer;

    private double _zoom = 2.0;
    private int _viewSizePx = 320; // 정사각 한 변(물리 px 기준).

    // DPI 스케일(물리 px → DIP 환산). OnSourceInitialized 에서 갱신.
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;

    public MagnifierWindow()
    {
        InitializeComponent();

        Width = _viewSizePx;
        Height = _viewSizePx;

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _timer.Tick += OnTick;

        SourceInitialized += (_, _) => UpdateDpiScale();
        Closed += OnClosedCleanup;
    }

    /// <summary>확대 배율을 설정한다(예: 2.0 = 2배). 0 이하는 무시.</summary>
    public void SetZoom(double zoom)
    {
        if (zoom > 0)
            _zoom = zoom;
    }

    /// <summary>돋보기 뷰의 정사각 한 변 크기(px)를 설정한다.</summary>
    public void SetViewSize(int px)
    {
        if (px <= 0)
            return;

        _viewSizePx = px;
        // 창 크기는 DIP 이므로 물리 px → DIP 환산.
        Width = _viewSizePx / _dpiScaleX;
        Height = _viewSizePx / _dpiScaleY;
    }

    /// <summary>표시/숨김을 토글한다. 표시 시 캡처 타이머를 돌리고, 숨김 시 멈춘다.</summary>
    public void Toggle()
    {
        if (IsVisible)
            HideMagnifier();
        else
            ShowMagnifier();
    }

    /// <summary>돋보기를 표시하고 캡처를 시작한다.</summary>
    public void ShowMagnifier()
    {
        Show();
        // 첫 프레임을 즉시 그려 빈 창이 잠깐 보이는 것을 막는다.
        CaptureAndRender();
        _timer.Start();
    }

    /// <summary>돋보기를 숨기고 캡처를 멈춘다.</summary>
    public void HideMagnifier()
    {
        _timer.Stop();
        Hide();
    }

    private void OnTick(object? sender, EventArgs e) => CaptureAndRender();

    private void CaptureAndRender()
    {
        if (!GetCursorPos(out POINT cursor))
            return;

        // 소스 영역: 커서 중심, (뷰 한 변 / zoom) 크기. zoom 이 클수록 좁은 영역을 본다.
        int srcSize = Math.Max(1, (int)Math.Round(_viewSizePx / _zoom));
        int srcLeft = cursor.X - srcSize / 2;
        int srcTop = cursor.Y - srcSize / 2;

        // 창 위치: 커서에서 우하단으로 오프셋(소스 영역과 겹치지 않게).
        PositionWindowNear(cursor, srcSize);

        // 자기 캡처 방지: 캡처 직전 잠깐 숨김(겹침 상황 대비). Hidden 으로 깜빡임 최소화.
        bool wasVisible = Visibility == Visibility.Visible;
        if (wasVisible)
            Visibility = Visibility.Hidden;

        BitmapSource? rendered = null;
        try
        {
            rendered = CaptureRegion(srcLeft, srcTop, srcSize, srcSize);
        }
        catch
        {
            // 캡처 실패(영역이 화면 밖 등)는 무시하고 이전 프레임 유지.
        }
        finally
        {
            if (wasVisible)
                Visibility = Visibility.Visible;
        }

        if (rendered != null)
            ZoomImage.Source = rendered;
    }

    /// <summary>
    /// 지정 물리 px 영역을 캡처해 frozen BitmapSource 로 반환한다.
    /// HBITMAP 은 변환 직후 DeleteObject 로 해제한다(누수 방지).
    /// </summary>
    private static BitmapSource CaptureRegion(int left, int top, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
        }

        IntPtr hBitmap = bitmap.GetHbitmap();
        try
        {
            BitmapSource source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    /// <summary>
    /// 창을 커서 근처(우하단 오프셋)에 둔다. 물리 px → DIP 환산 후 Left/Top 설정.
    /// </summary>
    private void PositionWindowNear(POINT cursor, int srcSize)
    {
        // 소스 영역 우하단 모서리 바깥에 창을 배치 → 소스와 겹치지 않음.
        int physLeft = cursor.X + srcSize / 2 + CursorOffsetPx;
        int physTop = cursor.Y + srcSize / 2 + CursorOffsetPx;

        // 물리 px → DIP.
        Left = physLeft / _dpiScaleX;
        Top = physTop / _dpiScaleY;
    }

    private void UpdateDpiScale()
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            _dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
            _dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
        }

        // DPI 확정 후 창 크기를 DIP 기준으로 재설정.
        Width = _viewSizePx / _dpiScaleX;
        Height = _viewSizePx / _dpiScaleY;
    }

    private void OnClosedCleanup(object? sender, EventArgs e)
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
