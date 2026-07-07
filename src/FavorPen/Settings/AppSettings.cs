using System.Collections.Generic;

namespace FavorPen.Settings;

/// <summary>
/// 현재 선택된 도구 종류(잉크 + 벡터 객체 통합).
///  - 잉크 도구(InkCanvas EditingMode): <see cref="Pen"/>/<see cref="Highlighter"/>/<see cref="Eraser"/>
///  - 객체 도구(ObjectLayer 가 처리): <see cref="Line"/>/<see cref="Arrow"/>/<see cref="Rectangle"/>/<see cref="Ellipse"/>/<see cref="Text"/>/<see cref="Number"/>
/// </summary>
public enum ToolKind
{
    Pen,
    Highlighter,
    Eraser,
    Line,
    Arrow,
    Rectangle,
    Ellipse,
    Text,
    Number
}

/// <summary>도형 채움 상태 순환(FR-23): 없음 → 컬러채움 → 외곽선만 → 흰색채움 → 검정채움.</summary>
public enum FillMode
{
    None,
    ColorFill,
    Outline,
    WhiteFill,
    BlackFill
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
    public double PenWidth { get; set; } = 9;

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

    // ── FR-18 도구별 독립 색상·굵기 기억 ───────────────────────
    // 키 = ToolKind 이름(예: "Line"). 없으면 펜 기본값으로 폴백한다.
    public Dictionary<string, string> ToolColors { get; set; } = new();
    public Dictionary<string, double> ToolWidths { get; set; } = new();

    // ── FR-23 마지막 도형 채움 상태 ────────────────────────────
    public FillMode LastFillMode { get; set; } = FillMode.None;

    // ── FR-15 텍스트 도구 ──────────────────────────────────────
    public string TextFontFamily { get; set; } = "Segoe UI";
    public double TextFontSize { get; set; } = 24;

    // ── FR-16 화이트보드 / 블랙보드 ────────────────────────────
    public string WhiteboardColor { get; set; } = "#FFFFFFFF";
    public string BlackboardColor { get; set; } = "#FF1A1A1A";
    /// <summary>화이트/블랙보드를 표시할 대상 모니터 인덱스(-1 = 가상화면 전체).</summary>
    public int WhiteboardMonitorIndex { get; set; } = -1;

    // ── FR-20 페이딩 잉크(자동 사라짐) ─────────────────────────
    public bool FadingInkEnabled { get; set; } = false;
    /// <summary>스트로크가 사라지기 시작할 때까지의 유지 시간(초). 1~10 권장.</summary>
    public double FadeSeconds { get; set; } = 3;

    // ── FR-21 하이라이트 커서(헤일로) ──────────────────────────
    public bool HighlightCursorEnabled { get; set; } = false;
    public string HighlightCursorColor { get; set; } = "#80FFD000";
    public double HighlightCursorRadius { get; set; } = 28;

    // ── FR-24 돋보기 / 줌 ──────────────────────────────────────
    public double MagnifierZoom { get; set; } = 2.0;
    public int MagnifierSize { get; set; } = 320;

    // ── 타이머(카운트다운) ─────────────────────────────────────
    /// <summary>타이머 설정 시간(초). 기본 5분.</summary>
    public int TimerDurationSeconds { get; set; } = 300;
    /// <summary>타이머 숫자 글자 크기(사용자 지정).</summary>
    public double TimerFontSize { get; set; } = 140;

    // ── 랜덤 뽑기 ──────────────────────────────────────────────
    /// <summary>마지막 번호 범위 입력(예: "1-20, 41-60").</summary>
    public string RandomRanges { get; set; } = "1-20";
    /// <summary>마지막 뽑을 인원.</summary>
    public int RandomCount { get; set; } = 1;

    // ── 소음 신호등 ────────────────────────────────────────────
    /// <summary>소음 신호등 민감도(1=둔감 ~ 5=예민). 교실 기준(캘리브레이션) 미설정 시 사용.</summary>
    public int NoiseSensitivity { get; set; } = 3;

    /// <summary>교실 기준 — 조용할 때 측정한 레벨(0~100). -1이면 미설정.</summary>
    public double NoiseQuietRef { get; set; } = -1;
    /// <summary>교실 기준 — 시끄러울 때 측정한 레벨(0~100). -1이면 미설정.</summary>
    public double NoiseLoudRef { get; set; } = -1;
    /// <summary>소음 신호등 표시부 크기 배율(0.7~3.0).</summary>
    public double NoiseScale { get; set; } = 1.0;
    /// <summary>소음 신호등 자동 기준 학습(관찰된 최저/최고로 기준 자동 조정).</summary>
    public bool NoiseAuto { get; set; } = false;
}
