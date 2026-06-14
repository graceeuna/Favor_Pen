using System.Drawing;
using System.Windows.Forms;

namespace ScreenPenPortable.Services;

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
        menu.Items.Add("보이기/숨기기", null, (_, _) => ToggleRequested?.Invoke());
        menu.Items.Add("스크린샷", null, (_, _) => ScreenshotRequested?.Invoke());
        menu.Items.Add("전체 지우기", null, (_, _) => ClearRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new NotifyIcon
        {
            // SystemIcons.Application: OS 기본 애플리케이션 아이콘(파일 의존 없음).
            Icon = SystemIcons.Application,
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
