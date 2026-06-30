using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FavorPen.Settings;

namespace FavorPen.UI;

/// <summary>
/// 프레임리스 항상-위 플로팅 툴바. 도구/색상/굵기/편집/효과 명령을 이벤트로 노출하고,
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

    /// <summary>현재 색과 일치하는 스와치를 강조하는 선택 링 / 평상시 테두리.</summary>
    private static readonly Brush SelectedSwatchBrush = Brushes.White;
    private static readonly Brush DefaultSwatchBrush =
        new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));

    /// <summary>SetThickness 호출 중 ThicknessChanged 재발생을 막는 가드.</summary>
    private bool _suppressThicknessEvent;

    /// <summary>현재 통과(클릭-스루) 모드 여부.</summary>
    private bool _passThrough;

    /// <summary>현재 활성 도구(넘버링 등 토글 동작 판정용).</summary>
    private ToolKind _activeTool = ToolKind.Pen;

    /// <summary>각 묶음에서 마지막으로 고른 도구(짧게 탭 시 바로 사용할 도구).</summary>
    private ToolKind _lastWritingTool = ToolKind.Pen;
    private ToolKind _lastShapeTool = ToolKind.Line;

    // ── 묶음 버튼 길게 누름 감지 ───────────────────────────────────
    private DispatcherTimer? _holdTimer;
    private Button? _holdButton;
    private bool _holdFired;

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
    // ── M2/M3 추가 이벤트 ─────────────────────────────────────────
    public event Action? FillCycleRequested;        // FR-23 도형 채움 순환
    public event Action? WhiteboardToggleRequested; // FR-16 화이트/블랙보드 순환
    public event Action? MagnifierToggleRequested;   // FR-24 돋보기
    public event Action? MagnifierOffRequested;      // 돋보기 강제 끄기(우클릭)
    public event Action? HaloToggleRequested;        // FR-21 하이라이트 커서
    public event Action? HaloSettingsRequested;      // 헤일로 색·크기 설정(우클릭)
    public event Action? TimerToggleRequested;       // 타이머(카운트다운)
    public event Action? RandomToggleRequested;      // 랜덤 뽑기

    public ToolbarWindow()
    {
        InitializeComponent();
    }

    // ── 빈 영역 드래그로 창 이동 ──────────────────────────────────
    private void OnDragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 마우스를 누른 채 시작하면 창을 끌어 이동. 버튼/슬라이더 위에서 누른
        // 경우는 해당 컨트롤이 이벤트를 먼저 처리하므로 여기로 버블링되지 않는다.
        // 전용 드래그 핸들(DragHandle)과 바깥 Border 가 같은 핸들러를 공유하므로,
        // e.Handled 로 부모(바깥 Border)에서 DragMove 가 한 번 더 호출되는 것을 막는다
        // (해제된 버튼으로 DragMove 재호출 시 예외 방지).
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            e.Handled = true;
            DragMove();
        }
    }

    // ── 도구 버튼: 잉크 + 도형 + 텍스트/넘버 ──────────────────────
    private void SelectTool(ToolKind tool)
    {
        SetActiveTool(tool);
        ToolSelected?.Invoke(tool);
    }

    private void OnPenClick(object sender, RoutedEventArgs e) => SelectWriting(ToolKind.Pen);
    private void OnHighlighterClick(object sender, RoutedEventArgs e) => SelectWriting(ToolKind.Highlighter);
    private void OnEraserClick(object sender, RoutedEventArgs e) => SelectTool(ToolKind.Eraser);
    private void OnLineClick(object sender, RoutedEventArgs e) => SelectShape(ToolKind.Line);
    private void OnArrowClick(object sender, RoutedEventArgs e) => SelectShape(ToolKind.Arrow);
    private void OnRectClick(object sender, RoutedEventArgs e) => SelectShape(ToolKind.Rectangle);
    private void OnEllipseClick(object sender, RoutedEventArgs e) => SelectShape(ToolKind.Ellipse);
    private void OnTextClick(object sender, RoutedEventArgs e) => SelectWriting(ToolKind.Text);

    /// <summary>넘버링 태그도 토글: 이미 넘버링이면 다시 누르면 해제(펜으로 복귀).</summary>
    private void OnNumberClick(object sender, RoutedEventArgs e) =>
        SelectTool(_activeTool == ToolKind.Number ? ToolKind.Pen : ToolKind.Number);

    /// <summary>묶음 버튼(펜/도형)을 누르면 길게 누름 타이머를 시작한다(짧게 떼면 탭, 길게 누르면 팝업).</summary>
    private void OnGroupDown(object sender, MouseButtonEventArgs e)
    {
        _holdButton = sender as Button;
        _holdFired = false;
        _holdTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _holdTimer.Stop();
        _holdTimer.Tick -= OnHoldTick;
        _holdTimer.Tick += OnHoldTick;
        _holdTimer.Start();
        e.Handled = true; // 기본 Click 억제 — 탭/길게 누름을 직접 처리.
    }

    /// <summary>길게 누름 임계시간(400ms) 도달 → 해당 묶음 선택 팝업을 연다.</summary>
    private void OnHoldTick(object? sender, EventArgs e)
    {
        _holdTimer?.Stop();
        _holdFired = true;
        if (_holdButton == PenGroupButton) PenPopup.IsOpen = true;
        else if (_holdButton == ShapesButton) ShapesPopup.IsOpen = true;
    }

    /// <summary>버튼을 떼면: 길게 누름이 이미 팝업을 열었으면 무동작,
    /// 아니면 짧은 탭 → 묶음의 마지막 도구를 바로 사용한다.</summary>
    private void OnGroupUp(object sender, MouseButtonEventArgs e)
    {
        _holdTimer?.Stop();
        e.Handled = true;
        if (_holdFired) { _holdFired = false; return; }
        if (sender == PenGroupButton) SelectTool(_lastWritingTool);
        else if (sender == ShapesButton) SelectTool(_lastShapeTool);
    }

    /// <summary>펜 팝업에서 쓰기 도구(펜·형광펜·텍스트)를 고르면 선택하고 팝업을 닫는다.</summary>
    private void SelectWriting(ToolKind tool)
    {
        _lastWritingTool = tool;
        SelectTool(tool);
        PenPopup.IsOpen = false;
    }

    /// <summary>도형 팝업에서 도형 하나를 고르면 해당 도형 도구를 선택하고 팝업을 닫는다.</summary>
    private void SelectShape(ToolKind tool)
    {
        _lastShapeTool = tool;
        SelectTool(tool);
        ShapesPopup.IsOpen = false;
    }

    private void OnFillClick(object sender, RoutedEventArgs e) => FillCycleRequested?.Invoke();

    // ── 편집 버튼 ─────────────────────────────────────────────────
    private void OnUndoClick(object sender, RoutedEventArgs e) => UndoRequested?.Invoke();
    private void OnRedoClick(object sender, RoutedEventArgs e) => RedoRequested?.Invoke();
    private void OnClearClick(object sender, RoutedEventArgs e) => ClearRequested?.Invoke();
    private void OnScreenshotClick(object sender, RoutedEventArgs e) => ScreenshotRequested?.Invoke();

    /// <summary>펜 옆 마우스 버튼: 펜↔마우스(통과) 모드를 즉시 전환.
    /// 토글 자체는 지휘자가 실제 모드를 바꾼 뒤 SetPassThrough 로 확정하므로
    /// 여기서는 의도만 알리고 버튼 강조는 낙관적으로 즉시 갱신한다.</summary>
    private void OnMouseModeClick(object sender, RoutedEventArgs e)
    {
        SetPassThrough(!_passThrough);
        PassThroughToggled?.Invoke();
    }

    /// <summary>눈 버튼: 툴바 본체(CollapsibleContent)를 접거나 펼친다.
    /// 접으면 드래그 핸들과 눈 버튼만 남아 화면을 거의 가리지 않는다(SizeToContent 가 창을 줄임).</summary>
    /// <summary>접혀 있으면 펼친다(외부에서 툴바를 복구할 때 호출).</summary>
    public void Expand()
    {
        CollapsibleContent.Visibility = Visibility.Visible;
        CollapseButton.Content = "\U0001F441"; // 뜬 눈
        CollapseButton.ToolTip = "툴바 접기 (눈 뜸 = 펼쳐짐)";
    }

    private void OnCollapseClick(object sender, RoutedEventArgs e)
    {
        bool collapsing = CollapsibleContent.Visibility == Visibility.Visible;
        CollapsibleContent.Visibility = collapsing ? Visibility.Collapsed : Visibility.Visible;
        // 펼침 = 뜬 눈(이모지 👁) / 접힘 = 감은 눈(이모지 폰트에 없어 벡터로 직접 그림).
        CollapseButton.Content = collapsing ? MakeClosedEyeIcon() : "\U0001F441";
        CollapseButton.ToolTip = collapsing ? "툴바 펼치기 (눈 감음 = 접힘)" : "툴바 접기 (눈 뜸 = 펼쳐짐)";
    }

    /// <summary>감은 눈 아이콘(아래로 휜 눈꺼풀 + 속눈썹)을 벡터로 그린다.
    /// 이모지 폰트에 단독 '감은 눈' 글리프가 없어 환경 무관하게 보이도록 직접 그린다.</summary>
    private static System.Windows.Shapes.Path MakeClosedEyeIcon() => new()
    {
        Width = 18,
        Height = 18,
        Stretch = Stretch.Uniform,
        Stroke = Brushes.White,
        StrokeThickness = 1.5,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Data = Geometry.Parse("M2,7 C5,11 11,11 14,7 M4,9.5 L3,11 M8,10.2 L8,12 M12,9.5 L13,11")
    };

    // ── 보드/효과 토글 버튼 ───────────────────────────────────────
    private void OnWhiteboardClick(object sender, RoutedEventArgs e) => WhiteboardToggleRequested?.Invoke();
    private void OnMagnifierClick(object sender, RoutedEventArgs e) => MagnifierToggleRequested?.Invoke();
    private void OnMagnifierRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        MagnifierOffRequested?.Invoke(); // 우클릭은 항상 끄기(좌클릭 토글이 안 먹는 상황 대비).
    }
    private void OnHaloClick(object sender, RoutedEventArgs e) => HaloToggleRequested?.Invoke();
    private void OnHaloRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HaloSettingsRequested?.Invoke();
    }
    private void OnTimerClick(object sender, RoutedEventArgs e) => TimerToggleRequested?.Invoke();
    private void OnRandomClick(object sender, RoutedEventArgs e) => RandomToggleRequested?.Invoke();

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

    /// <summary>Quick 색상 스와치를 "#AARRGGBB" 문자열들로 채운다.</summary>
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
                // 폭 72 컨테이너에 3개씩(3열 2행) 들어가도록 22→18로 축소(항목 폭 18+좌우2=22, 3×22=66).
                Width = 18,
                Height = 18,
                Margin = new Thickness(2, 1, 2, 1),
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

    /// <summary>현재 선택된(활성 도구) 색과 일치하는 Quick 색상 스와치에 흰색 선택 링을 둘러
    /// 어느 색이 활성인지 직관적으로 보이게 한다.</summary>
    public void SetCurrentColor(Color c)
    {
        foreach (var item in ColorSwatches.Items)
        {
            if (item is Button { Tag: Color sc } swatch)
            {
                bool match = sc == c;
                swatch.BorderBrush = match ? SelectedSwatchBrush : DefaultSwatchBrush;
                swatch.BorderThickness = new Thickness(match ? 3 : 1);
            }
        }
    }

    /// <summary>활성 도구 버튼을 하이라이트한다(이벤트 미발생).</summary>
    public void SetActiveTool(ToolKind tool)
    {
        _activeTool = tool;
        // 묶음의 마지막 도구를 갱신(버튼 글리프·탭 동작이 현재 도구와 일치하도록).
        if (IsWritingTool(tool)) _lastWritingTool = tool;
        else if (IsShapeTool(tool)) _lastShapeTool = tool;

        PenButton.Background = tool == ToolKind.Pen ? ActiveBrush : IdleBrush;
        HighlighterButton.Background = tool == ToolKind.Highlighter ? ActiveBrush : IdleBrush;
        EraserButton.Background = tool == ToolKind.Eraser ? ActiveBrush : IdleBrush;
        LineButton.Background = tool == ToolKind.Line ? ActiveBrush : IdleBrush;
        ArrowButton.Background = tool == ToolKind.Arrow ? ActiveBrush : IdleBrush;
        RectButton.Background = tool == ToolKind.Rectangle ? ActiveBrush : IdleBrush;
        EllipseButton.Background = tool == ToolKind.Ellipse ? ActiveBrush : IdleBrush;
        TextButton.Background = tool == ToolKind.Text ? ActiveBrush : IdleBrush;
        NumberButton.Background = tool == ToolKind.Number ? ActiveBrush : IdleBrush;

        // 펜 묶음 버튼: 쓰기 도구(펜·형광펜·텍스트)가 활성이면 강조하고, 현재 도구 글리프를 표시한다.
        PenGroupButton.Background = IsWritingTool(tool) ? ActiveBrush : IdleBrush;
        PenGroupButton.Content = tool switch
        {
            ToolKind.Highlighter => "\U0001F58D",
            ToolKind.Text => "T",
            _ => "✎"   // 펜(및 비쓰기 도구 기본): 연필
        };

        // 도형 묶음 버튼: 도형 도구가 활성이면 강조하고, 현재 도형 글리프를 표시한다.
        // (도형이 아닐 땐 기본 묶음 아이콘 ▢.)
        ShapesButton.Background = IsShapeTool(tool) ? ActiveBrush : IdleBrush;
        ShapesButton.Content = tool switch
        {
            ToolKind.Line => "╱",
            ToolKind.Arrow => "↗",
            ToolKind.Rectangle => "▭",
            ToolKind.Ellipse => "◯",
            _ => "▢"
        };
    }

    private static bool IsShapeTool(ToolKind t) =>
        t is ToolKind.Line or ToolKind.Arrow or ToolKind.Rectangle or ToolKind.Ellipse;

    private static bool IsWritingTool(ToolKind t) =>
        t is ToolKind.Pen or ToolKind.Highlighter or ToolKind.Text;

    /// <summary>굵기 슬라이더의 허용 범위를 바꾼다(예: 텍스트 도구는 폰트크기 8~200).
    /// ThicknessChanged 재발생 안 함.</summary>
    public void SetThicknessRange(double min, double max)
    {
        _suppressThicknessEvent = true;
        try
        {
            ThicknessSlider.Minimum = min;
            ThicknessSlider.Maximum = max;
        }
        finally { _suppressThicknessEvent = false; }
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
        // 펜 옆 마우스 버튼이 그리기/통과 모드를 표시(통과 모드일 때 강조).
        MouseButton.Background = on ? ActiveBrush : IdleBrush;
        MouseButton.ToolTip = on
            ? "마우스(통과) 모드 활성 — 펜·도구를 누르면 그리기 모드로"
            : "마우스(통과) 모드 — 펜과 즉시 전환 (Ctrl+Shift+D)";

        // 마우스(통과) 모드에선 다른 도구 선택 강조를 모두 해제하고,
        // 빠져나오면 현재 도구를 다시 강조한다.
        if (on) ClearToolHighlights();
        else SetActiveTool(_activeTool);
    }

    /// <summary>모든 도구 버튼의 활성 강조를 끈다(마우스 모드 진입 시 도구 선택 해제 표시).</summary>
    private void ClearToolHighlights()
    {
        foreach (var b in new[]
        {
            PenGroupButton, PenButton, HighlighterButton, TextButton, EraserButton,
            ShapesButton, LineButton, ArrowButton, RectButton, EllipseButton, NumberButton
        })
            b.Background = IdleBrush;
    }

    // ── 토글 상태 표시(이벤트 미발생) ─────────────────────────────
    public void SetWhiteboardActive(bool on) => WhiteboardButton.Background = on ? ActiveBrush : IdleBrush;
    public void SetMagnifierActive(bool on) => MagnifierButton.Background = on ? ActiveBrush : IdleBrush;
    public void SetHaloActive(bool on) => HaloButton.Background = on ? ActiveBrush : IdleBrush;
    public void SetTimerActive(bool on) => TimerButton.Background = on ? ActiveBrush : IdleBrush;
    public void SetRandomActive(bool on) => RandomButton.Background = on ? ActiveBrush : IdleBrush;

    /// <summary>현재 도형 채움 상태를 글리프로 표시(없음/컬러/외곽선/흰/검).</summary>
    public void SetFillMode(FillMode mode)
    {
        FillButton.Content = mode switch
        {
            FillMode.None => "○",      // ○ 빈 원
            FillMode.ColorFill => "●", // ● 채운 원
            FillMode.Outline => "◎",   // ◎ 외곽선
            FillMode.WhiteFill => "◐", // ◐ 흰
            FillMode.BlackFill => "◑", // ◑ 검
            _ => "◑"
        };
    }

    /// <summary>Undo/Redo 버튼 활성화 상태를 갱신한다.</summary>
    public void SetUndoRedoEnabled(bool canUndo, bool canRedo)
    {
        UndoButton.IsEnabled = canUndo;
        RedoButton.IsEnabled = canRedo;
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
