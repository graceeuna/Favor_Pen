using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace FavorPen.Services;

/// <summary>
/// 가상 화면(모든 모니터를 포함하는 전체 영역) 캡처 서비스.
/// PNG 파일로 저장하고 동시에 클립보드에도 복사한다.
/// </summary>
public static class ScreenshotService
{
    /// <summary>
    /// 가상 화면 전체를 캡처해 PNG로 저장하고 클립보드에도 복사한다.
    /// </summary>
    /// <param name="targetDir">
    /// 저장 폴더. null/빈 문자열이면 "내 그림"(MyPictures) 폴더를 사용한다.
    /// </param>
    /// <returns>저장된 PNG 파일의 전체 경로.</returns>
    /// <remarks>
    /// 중요: 클립보드 복사(<see cref="System.Windows.Clipboard.SetImage"/>)는
    /// STA 스레드(=WPF UI 스레드)에서 호출해야 한다. 다른 스레드에서 호출하면
    /// COM 예외(또는 무시)가 발생할 수 있으므로, 호출자는 이 메서드를 UI 스레드
    /// (예: Dispatcher.Invoke) 에서 실행해야 한다.
    /// 또한 캡처 직전에 도구 자신의 UI(주석 오버레이/창)를 숨기는 것은 호출자 책임이다.
    /// 이 메서드는 캡처/저장/클립보드 복사만 담당한다.
    /// </remarks>
    public static string CaptureVirtualScreenToFileAndClipboard(string? targetDir)
    {
        // 가상 화면 = 모든 모니터를 포함하는 경계 사각형 (음수 좌표일 수 있음).
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            // 가상 화면의 좌상단(X,Y)부터 복사. 멀티모니터에서 X/Y가 음수일 수 있다.
            graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
        }

        // 저장 폴더 결정: 지정이 없으면 "내 그림" 폴더.
        var dir = string.IsNullOrWhiteSpace(targetDir)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            : targetDir;
        Directory.CreateDirectory(dir);

        var fileName = $"ScreenPen_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        var fullPath = Path.Combine(dir, fileName);

        bitmap.Save(fullPath, ImageFormat.Png);

        // 클립보드 복사: Bitmap → BitmapSource(WPF) 변환 후 WPF Clipboard에 설정.
        // (STA/UI 스레드에서 호출되어야 함 — remarks 참고.)
        // copy:true 로 클립보드에 데이터를 '영속화'한다 → 무설치 앱이 곧 종료돼도
        // 다른 앱에서 붙여넣기 가능(SetImage 의 기본 copy:false 는 종료 시 소실 위험).
        var source = ConvertToBitmapSource(bitmap);
        var data = new System.Windows.DataObject();
        data.SetImage(source);
        System.Windows.Clipboard.SetDataObject(data, true);

        return fullPath;
    }

    /// <summary>
    /// System.Drawing.Bitmap → WPF BitmapSource 변환.
    /// HBITMAP을 생성해 Imaging.CreateBitmapSourceFromHBitmap으로 변환하고,
    /// 변환 직후 freeze하여 다른 스레드에서도 안전하게 사용할 수 있게 한다.
    /// </summary>
    private static BitmapSource ConvertToBitmapSource(Bitmap bitmap)
    {
        IntPtr hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            // GetHbitmap()이 만든 GDI 객체는 반드시 해제해야 누수가 없다.
            NativeMethods.DeleteObject(hBitmap);
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr hObject);
    }
}
