using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FavorPen.Services;

/// <summary>
/// 시스템 트레이(알림 영역) 아이콘 래퍼.
/// 내부적으로 <see cref="System.Windows.Forms.NotifyIcon"/>를 사용한다.
/// 아이콘은 .ico 파일 의존 없이 코드로 생성(SystemIcons)한다.
/// </summary>
public class TrayService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private bool _disposed;

    /// <summary>보이기/숨기기 메뉴 또는 더블클릭 시 발생.</summary>
    public event Action? ToggleRequested;

    /// <summary>스크린샷 메뉴 클릭 시 발생.</summary>
    public event Action? ScreenshotRequested;

    /// <summary>전체 지우기 메뉴 클릭 시 발생.</summary>
    public event Action? ClearRequested;

    /// <summary>종료 메뉴 클릭 시 발생.</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// 트레이 아이콘을 생성하고 즉시 표시한다.
    /// </summary>
    /// <param name="tooltip">초기 툴팁/Text.</param>
    public TrayService(string tooltip)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("툴바 복구/숨기기", null, (_, _) => ToggleRequested?.Invoke());
        menu.Items.Add("스크린샷", null, (_, _) => ScreenshotRequested?.Invoke());
        menu.Items.Add("전체 지우기", null, (_, _) => ClearRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new NotifyIcon
        {
            // 알아보기 쉽게 직접 그린 아이콘(파란 원 + 흰 펜). .ico 파일 의존 없음.
            Icon = BuildTrayIcon(),
            Visible = true,
            Text = Truncate(tooltip),
            ContextMenuStrip = menu,
        };

        // 더블클릭으로 빠르게 보이기/숨기기 토글.
        _notifyIcon.DoubleClick += (_, _) => ToggleRequested?.Invoke();
    }

    /// <summary>
    /// 트레이 아이콘의 Text(툴팁)를 갱신한다.
    /// NotifyIcon.Text는 최대 63자 제한이 있어 초과분은 잘라낸다.
    /// </summary>
    public void SetModeText(string text)
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.Text = Truncate(text);
    }

    /// <summary>
    /// NotifyIcon.Text의 63자 제한에 맞춰 안전하게 자른다.
    /// </summary>
    private static string Truncate(string text)
    {
        const int maxLength = 63;
        text ??= string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>트레이용 아이콘을 코드로 그린다: 파란 원 배경 + 흰색 펜(✎) 글리프.
    /// 다른 기본 회색 아이콘들과 한눈에 구분되도록.</summary>
    private static Icon BuildTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using (var bg = new SolidBrush(Color.FromArgb(0xFF, 0x3D, 0x7D, 0xFF)))
                g.FillEllipse(bg, 1, 1, 30, 30);
            using var font = new Font("Segoe UI Symbol", 17f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(Color.White);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("✎", font, fg, new RectangleF(0, 1, 32, 32), sf); // ✎ 펜
        }

        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone(); // 핸들과 독립된 관리 복사본
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        GC.SuppressFinalize(this);
    }
}
