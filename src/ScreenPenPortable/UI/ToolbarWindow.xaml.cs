using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenPenPortable.Settings;

namespace ScreenPenPortable.UI;

/// <summary>
/// 프레임리스 항상-위 플로팅 툴바. 도구/색상/굵기/편집 명령을 이벤트로 노출하고,
/// 외부(지휘자)가 상태를 역방향으로 동기화할 수 있도록 Set* 메서드를 제공한다.
/// 모든 명령은 클릭/슬라이더 조작 시 이벤트로 발행되며, Set* 메서드는
/// 이벤트를 재발생시키지 않는다(피드백 루프 방지).
/// </summary>
public partial class ToolbarWindow : Window
{
    /// <summary>활성 도구 버튼 하이라이트 색.</summary>
    private static readonly Brush ActiveBrush =
        (Brush)new BrushConverter().ConvertFromString("#FF3D7DFF")!;
    private static readonly Brush IdleBrush = Brushes.Transparent;

    /// <summary>SetThickness 호출 중 ThicknessChanged 재발생을 막는 가드.</summary>
    private bool _suppressThicknessEvent;

    /// <summary>현재 통과(클릭-스루) 모드 여부.</summary>
    private bool _passThrough;

    // ── 공개 이벤트(지휘자가 구독) ────────────────────────────────
    public event Action<ToolKind>? ToolSelected;
    public event Action<Color>? ColorSelected;
    public event Action<double>? ThicknessChanged;
    public event Action? UndoRequested;
    public event Action? RedoRequested;
    public event Action? ClearRequested;
    public event Action? ScreenshotRequested;
    public event Action? PassThroughToggled;
    public event Action? ExitRequested;

    public ToolbarWindow()
    {
        InitializeComponent();
    }

    // ── 빈 영역 드래그로 창 이동 ──────────────────────────────────
    private void OnDragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 마우스를 누른 채 시작하면 창을 끌어 이동. 버튼/슬라이더 위에서 누른
        // 경우는 해당 컨트롤이 이벤트를 먼저 처리하므로 여기로 버블링되지 않는다.
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    // ── 도구 버튼 ─────────────────────────────────────────────────
    private void OnPenClick(object sender, RoutedEventArgs e)
    {
        SetActiveTool(ToolKind.Pen);
        ToolSelected?.Invoke(ToolKind.Pen);
    }

    private void OnHighlighterClick(object sender, RoutedEventArgs e)
    {
        SetActiveTool(ToolKind.Highlighter);
        ToolSelected?.Invoke(ToolKind.Highlighter);
    }

    private void OnEraserClick(object sender, RoutedEventArgs e)
    {
        SetActiveTool(ToolKind.Eraser);
        ToolSelected?.Invoke(ToolKind.Eraser);
    }

    // ── 편집 버튼 ─────────────────────────────────────────────────
    private void OnUndoClick(object sender, RoutedEventArgs e) => UndoRequested?.Invoke();
    private void OnRedoClick(object sender, RoutedEventArgs e) => RedoRequested?.Invoke();
    private void OnClearClick(object sender, RoutedEventArgs e) => ClearRequested?.Invoke();
    private void OnScreenshotClick(object sender, RoutedEventArgs e) => ScreenshotRequested?.Invoke();

    private void OnPassThroughClick(object sender, RoutedEventArgs e)
    {
        // 토글 자체는 지휘자가 실제 모드를 바꾼 뒤 SetPassThrough 로 확정한다.
        // 여기서는 의도만 알리고, 라벨은 즉시 낙관적으로 갱신한다.
        SetPassThrough(!_passThrough);
        PassThroughToggled?.Invoke();
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    // ── 굵기 슬라이더 ─────────────────────────────────────────────
    private void OnThicknessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThicknessLabel != null)
        {
            ThicknessLabel.Text = ((int)Math.Round(e.NewValue)).ToString();
        }

        if (_suppressThicknessEvent)
        {
            return;
        }
        ThicknessChanged?.Invoke(e.NewValue);
    }

    // ── 공개 API: 외부 → 툴바 상태 동기화 ─────────────────────────

    /// <summary>Quick 색상 스와치 6칸을 "#AARRGGBB" 문자열들로 채운다.</summary>
    public void SetQuickColors(IEnumerable<string> argbHex)
    {
        ColorSwatches.Items.Clear();
        foreach (var hex in argbHex)
        {
            Color color;
            try
            {
                color = (Color)ColorConverter.ConvertFromString(hex)!;
            }
            catch
            {
                // 잘못된 색 문자열은 건너뛴다(나머지 스와치는 정상 표시).
                continue;
            }

            var swatch = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                ToolTip = hex,
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Tag = color
            };
            // 둥근 스와치 템플릿(테두리 + 단색 채움).
            swatch.Template = BuildSwatchTemplate();
            swatch.Click += OnSwatchClick;
            ColorSwatches.Items.Add(swatch);
        }
    }

    private void OnSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Color color })
        {
            ColorSelected?.Invoke(color);
        }
    }

    /// <summary>활성 도구 버튼을 하이라이트한다(이벤트 미발생).</summary>
    public void SetActiveTool(ToolKind tool)
    {
        PenButton.Background = tool == ToolKind.Pen ? ActiveBrush : IdleBrush;
        HighlighterButton.Background = tool == ToolKind.Highlighter ? ActiveBrush : IdleBrush;
        EraserButton.Background = tool == ToolKind.Eraser ? ActiveBrush : IdleBrush;
    }

    /// <summary>슬라이더 값을 동기화한다(ThicknessChanged 재발생 안 함).</summary>
    public void SetThickness(double width)
    {
        _suppressThicknessEvent = true;
        try
        {
            ThicknessSlider.Value = Math.Clamp(width, ThicknessSlider.Minimum, ThicknessSlider.Maximum);
        }
        finally
        {
            _suppressThicknessEvent = false;
        }
    }

    /// <summary>통과 모드 토글 버튼의 상태/라벨을 갱신한다(이벤트 미발생).</summary>
    public void SetPassThrough(bool on)
    {
        _passThrough = on;
        // 통과 모드: 손바닥(통과) / 그리기 모드: 연필.
        PassThroughButton.Content = on ? "\U0001F590" : "✋";
        PassThroughButton.ToolTip = on ? "통과 모드 (클릭하면 그리기)" : "그리기 모드 (클릭하면 통과)";
        PassThroughButton.Background = on ? ActiveBrush : IdleBrush;
    }

    /// <summary>색상 스와치용 둥근 버튼 템플릿(테두리 + 채움).</summary>
    private static ControlTemplate BuildSwatchTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetBinding(Border.BackgroundProperty,
            new System.Windows.Data.Binding(nameof(Background))
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty,
            new System.Windows.Data.Binding(nameof(BorderBrush))
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty,
            new System.Windows.Data.Binding(nameof(BorderThickness))
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        return template;
    }
}
