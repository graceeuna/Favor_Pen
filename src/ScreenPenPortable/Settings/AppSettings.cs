namespace ScreenPenPortable.Settings;

/// <summary>
/// 현재 선택된 그리기 도구 종류. UI 팀원이 도구 전환/직렬화에 사용한다.
/// </summary>
public enum ToolKind
{
    Pen,
    Highlighter,
    Eraser
}

/// <summary>
/// 앱 세션 간에 영속되는 사용자 설정.
/// 모든 프로퍼티는 기본값을 가지므로 <c>new AppSettings()</c> 만으로 정상 동작한다.
/// 색상은 WPF <c>#AARRGGBB</c> 16진 문자열 형식이다.
/// </summary>
public class AppSettings
{
    // ── 펜 ─────────────────────────────────────────────────────
    public string PenColor { get; set; } = "#FFFF0000";
    public double PenWidth { get; set; } = 3;

    // ── 형광펜 ─────────────────────────────────────────────────
    public string HighlighterColor { get; set; } = "#80FFFF00";
    public double HighlighterWidth { get; set; } = 18;

    // ── 지우개 ─────────────────────────────────────────────────
    public double EraserWidth { get; set; } = 24;

    // ── 마지막 상태 ────────────────────────────────────────────
    public ToolKind LastTool { get; set; } = ToolKind.Pen;

    /// <summary>툴바의 빠른 색상 팔레트(#AARRGGBB).</summary>
    public string[] QuickColors { get; set; } =
    {
        "#FFFF0000", // 빨강
        "#FF00B050", // 초록
        "#FF0070C0", // 파랑
        "#FFFFFF00", // 노랑
        "#FF000000", // 검정
        "#FFFFFFFF"  // 흰색
    };

    public string LastScreenshotDir { get; set; } = "";

    // ── 툴바 위치 ──────────────────────────────────────────────
    public double ToolbarLeft { get; set; } = 40;
    public double ToolbarTop { get; set; } = 40;
}
