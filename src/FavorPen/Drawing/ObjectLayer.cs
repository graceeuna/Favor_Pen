using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FavorPen.Settings;

namespace FavorPen.Drawing;

/// <summary>
/// 벡터 객체 도구 서브시스템(FR-14/15/22/23). 잉크(펜·하이라이터·지우개)와 달리
/// 한 번의 드래그/클릭으로 <b>하나의 객체</b>(선·화살표·사각형·타원·텍스트·번호)를
/// 생성해 <see cref="InkCanvas.Children"/> 에 추가한다.
///
/// 설계:
///  - 호스트(<see cref="InkCanvas"/>)의 PreviewMouseLeft* 를 구독한다. 객체 도구가 아닐 때
///    (<see cref="ActiveTool"/> ∈ {Pen,Highlighter,Eraser})는 모든 핸들러가 무동작이므로
///    InkCanvas 가 정상적으로 스트로크를 그린다(지휘자가 객체 도구일 때만 EditingMode=None 으로 둠).
///  - 도형은 드래그 중 <see cref="_overlay"/>(시각 전용, 입력 비차단)에 미리보기를 그리고,
///    마우스 업에서 호스트로 확정 이동한다.
///  - 텍스트/번호는 클릭 즉시 확정한다(텍스트는 편집용 TextBox 를 띄움).
///  - 각 추가/삭제는 <see cref="UndoStack"/> 에 역연산을 기록해 공유 타임라인에 합류한다.
///    <see cref="UndoStack.IsApplying"/> 가 true 인 동안(Undo/Redo 재진입)에는 기록을 건너뛴다.
/// </summary>
public sealed class ObjectLayer
{
    private readonly InkCanvas _host;
    private readonly Canvas _overlay;
    private readonly UndoStack _undo;

    // ── 드래그 상태(도형 도구) ─────────────────────────────────
    private bool _dragging;
    private Point _start;
    private Shape? _preview;   // _overlay 에 떠 있는 미리보기 도형

    // ── 번호 카운터(FR-22) ─────────────────────────────────────
    private int _nextNumber = 1;

    /// <summary>넘버링 태그 숫자에 쓰는 임베드 메이플스토리체(앱 동봉, 무설치 이식성).</summary>
    private static readonly FontFamily NumberFont =
        new(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Maplestory");

    // 편집 중인 텍스트의 강제 종료 훅(있을 때만). ClearAll/도구전환에서 호출.
    private Action<bool>? _activeTextCommit;

    // ── 지휘자가 세팅하는 공개 상태 ────────────────────────────
    public ToolKind ActiveTool { get; set; } = ToolKind.Pen;
    public Color StrokeColor { get; set; } = Colors.Red;
    public double StrokeWidth { get; set; } = 3;
    public FillMode Fill { get; set; } = FillMode.None;
    public double FontSize { get; set; } = 24;
    public string FontFamily { get; set; } = "Segoe UI";

    public ObjectLayer(InkCanvas host, Canvas overlay, UndoStack undo)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _undo = undo ?? throw new ArgumentNullException(nameof(undo));

        _host.PreviewMouseLeftButtonDown += OnMouseDown;
        _host.PreviewMouseMove += OnMouseMove;
        _host.PreviewMouseLeftButtonUp += OnMouseUp;
        // 안전망: EditingMode 변경 등으로 마우스 캡처가 풀리면(=OnMouseUp 미수신) 드래그 정리.
        _host.LostMouseCapture += (_, _) => CancelActiveDrag();
    }

    // ── 도구 분류 헬퍼 ─────────────────────────────────────────
    private static bool IsObjectTool(ToolKind t) =>
        t is ToolKind.Line or ToolKind.Arrow or ToolKind.Rectangle
          or ToolKind.Ellipse or ToolKind.Text or ToolKind.Number;

    private static bool ShiftDown => (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

    /// <summary>주어진 요소가 어떤 <see cref="TextBox"/> 내부(또는 자신)인지 비주얼 트리를 거슬러 판정.</summary>
    private static bool IsInsideTextBox(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is TextBox)
                return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    // ── 공개 메서드 ────────────────────────────────────────────

    /// <summary>채움 상태를 다음 단계로 순환시키고 새 값을 반환한다(FR-23).
    /// None → ColorFill → Outline → WhiteFill → BlackFill → None …</summary>
    public FillMode CycleFill()
    {
        Fill = Fill switch
        {
            FillMode.None => FillMode.ColorFill,
            FillMode.ColorFill => FillMode.Outline,
            FillMode.Outline => FillMode.WhiteFill,
            FillMode.WhiteFill => FillMode.BlackFill,
            _ => FillMode.None
        };
        return Fill;
    }

    /// <summary>번호 도구(FR-22)의 카운터를 1로 리셋한다.</summary>
    public void ResetNumbering() => _nextNumber = 1;

    /// <summary>번호 카운터 현재값. ClearAll의 undo 복원용. 1 미만은 1로 보정.</summary>
    public int NextNumber
    {
        get => _nextNumber;
        set => _nextNumber = value < 1 ? 1 : value;
    }

    /// <summary>진행 중인 도형 드래그를 취소한다(미리보기 제거·캡처 해제·상태 초기화).
    /// 도구/통과 모드 전환·Esc·ClearAll 시 호출해 미리보기 잔류와 드래그 고착을 막는다.</summary>
    public void CancelActiveDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        if (_preview != null)
        {
            _overlay.Children.Remove(_preview);
            _preview = null;
        }
        if (_host.IsMouseCaptured)
            _host.ReleaseMouseCapture();
    }

    /// <summary>편집 중인 텍스트 입력을 강제로 끝낸다(commit=true 확정, false 취소). 편집 중이 아니면 무동작.
    /// ClearAll/도구 전환 시 활성 TextBox가 지워진 화면에 부활하는 문제를 막는다.</summary>
    public void EndActiveTextEditing(bool commit) => _activeTextCommit?.Invoke(commit);

    // ── 마우스 핸들러 ──────────────────────────────────────────
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ActiveTool == ToolKind.Eraser)
        {
            // 지우개로 벡터 객체(도형·텍스트·번호)도 지운다. InkCanvas 는 스트로크를
            // 동시에 지우므로 e.Handled 는 두지 않는다(잉크+객체가 함께 지워짐).
            EraseObjectsAt(e.GetPosition(_host));
            return;
        }

        if (!IsObjectTool(ActiveTool))
            return; // 잉크 도구 → InkCanvas 가 처리하도록 양보

        Point p = e.GetPosition(_host);

        switch (ActiveTool)
        {
            case ToolKind.Text:
                // 편집 중인 TextBox 안을 클릭한 경우엔 새 입력을 시작하지 않고
                // 해당 TextBox 가 캐럿을 옮기도록 양보한다(중복 생성 방지).
                if (e.OriginalSource is DependencyObject src && IsInsideTextBox(src))
                    return;
                BeginTextEntry(p);
                e.Handled = true;
                break;

            case ToolKind.Number:
                PlaceNumber(p);
                e.Handled = true;
                break;

            default: // 드래그 도형(Line/Arrow/Rectangle/Ellipse)
                _dragging = true;
                _start = p;
                _preview = CreateShape();
                _overlay.Children.Add(_preview);
                UpdateShape(_preview, _start, _start);
                _host.CaptureMouse();
                e.Handled = true;
                break;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (ActiveTool == ToolKind.Eraser)
        {
            // 누른 채 끌면 지나가는 객체를 연속으로 지운다(스트로크 지우개와 동일한 느낌).
            if (e.LeftButton == MouseButtonState.Pressed)
                EraseObjectsAt(e.GetPosition(_host));
            return;
        }

        if (!_dragging || _preview == null)
            return;

        Point cur = ConstrainEnd(_start, e.GetPosition(_host));
        UpdateShape(_preview, _start, cur);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        _host.ReleaseMouseCapture();

        Shape? preview = _preview;
        _preview = null;
        if (preview != null)
            _overlay.Children.Remove(preview);

        Point end = ConstrainEnd(_start, e.GetPosition(_host));

        // 클릭(드래그 없음)으로 생긴 0-크기 도형은 버린다.
        if (Math.Abs(end.X - _start.X) < 1 && Math.Abs(end.Y - _start.Y) < 1)
            return;

        // 확정 도형을 새로 만들어(미리보기와 독립) 호스트에 추가.
        Shape final = CreateShape();
        UpdateShape(final, _start, end);
        CommitChild(final);

        e.Handled = true;
    }

    // ── 지우개: 벡터 객체 삭제(FR-04 확장) ─────────────────────

    /// <summary>지정 지점 아래의 벡터 객체(도형·텍스트·번호)를 찾아 제거한다(Undo 기록).
    /// <see cref="UIElement.InputHitTest"/> 로 실제 커서 아래 요소를 찾으므로
    /// 드래그 중 마우스 캡처 상태에서도 정확히 짚는다.</summary>
    private void EraseObjectsAt(Point p)
    {
        // 위(나중에 추가된 것)부터 검사해 점을 포함하는 가장 위 객체 하나를 지운다.
        for (int i = _host.Children.Count - 1; i >= 0; i--)
        {
            UIElement child = _host.Children[i];
            if (child is TextBox)
                continue; // 편집 중인 텍스트는 제외(확정 전 객체).

            Rect b = GetChildBounds(child);
            b.Inflate(4, 4); // 얇은 선·작은 객체도 쉽게 지우도록 약간 여유.
            if (b.Contains(p))
            {
                RemoveChildWithUndo(child);
                return; // 드래그로 연속 호출되므로 한 번에 하나만 제거.
            }
        }
    }

    /// <summary>InkCanvas 자식의 호스트 좌표 경계 사각형을 구한다.
    /// Rectangle/Ellipse/Text/번호는 SetLeft/Top + 크기로, Line/화살표는 절대 좌표 기하로
    /// 배치되는데, 둘 다 <see cref="VisualTreeHelper.GetDescendantBounds"/> 로 통일해 처리한다.</summary>
    private static Rect GetChildBounds(UIElement child)
    {
        double left = InkCanvas.GetLeft(child);
        double top = InkCanvas.GetTop(child);
        left = double.IsNaN(left) ? 0 : left;
        top = double.IsNaN(top) ? 0 : top;

        Rect local = VisualTreeHelper.GetDescendantBounds((Visual)child);
        if (local.IsEmpty)
            local = new Rect(child.RenderSize);
        return new Rect(left + local.X, top + local.Y, local.Width, local.Height);
    }

    /// <summary>객체를 제거하고 역연산(복원)을 Undo 스택에 기록한다(공유 타임라인 합류).</summary>
    private void RemoveChildWithUndo(UIElement element)
    {
        _host.Children.Remove(element);
        if (_undo.IsApplying)
            return;
        _undo.Push(
            undo: () => { if (!_host.Children.Contains(element)) _host.Children.Add(element); },
            redo: () => _host.Children.Remove(element));
    }

    // ── 도형 생성/갱신 ─────────────────────────────────────────

    /// <summary>현재 도구에 맞는 빈 도형(스타일만 적용)을 만든다. 좌표는 <see cref="UpdateShape"/> 가 채운다.</summary>
    private Shape CreateShape()
    {
        var stroke = new SolidColorBrush(StrokeColor);

        switch (ActiveTool)
        {
            case ToolKind.Line:
                return new Line
                {
                    Stroke = stroke,
                    StrokeThickness = StrokeWidth,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };

            case ToolKind.Arrow:
                return new Path
                {
                    Stroke = stroke,
                    StrokeThickness = StrokeWidth,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round
                };

            case ToolKind.Rectangle:
                return ApplyFill(new Rectangle
                {
                    Stroke = stroke,
                    StrokeThickness = StrokeWidth
                });

            case ToolKind.Ellipse:
                return ApplyFill(new Ellipse
                {
                    Stroke = stroke,
                    StrokeThickness = StrokeWidth
                });

            default:
                // 안전망(여기에 도달하면 안 됨).
                return new Line { Stroke = stroke, StrokeThickness = StrokeWidth };
        }
    }

    /// <summary>FR-23 채움 모드를 도형에 적용한다(Rectangle/Ellipse 전용).</summary>
    private Shape ApplyFill(Shape shape)
    {
        shape.Fill = Fill switch
        {
            FillMode.ColorFill => new SolidColorBrush(StrokeColor),
            FillMode.WhiteFill => Brushes.White,
            FillMode.BlackFill => Brushes.Black,
            _ => null! // None / Outline → 채움 없음(외곽선만)
        };
        return shape;
    }

    /// <summary>시작점·현재점으로 도형의 형상을 갱신한다(드래그 미리보기·확정 공용).</summary>
    private void UpdateShape(Shape shape, Point a, Point b)
    {
        switch (shape)
        {
            case Line line:
                line.X1 = a.X; line.Y1 = a.Y;
                line.X2 = b.X; line.Y2 = b.Y;
                break;

            case Path path: // Arrow
                path.Data = BuildArrowGeometry(a, b);
                break;

            case Rectangle:
            case Ellipse:
                double left = Math.Min(a.X, b.X);
                double top = Math.Min(a.Y, b.Y);
                double w = Math.Abs(b.X - a.X);
                double h = Math.Abs(b.Y - a.Y);
                shape.Width = w;
                shape.Height = h;
                InkCanvas.SetLeft(shape, left);
                InkCanvas.SetTop(shape, top);
                Canvas.SetLeft(shape, left); // 미리보기(Canvas) 좌표도 함께 세팅
                Canvas.SetTop(shape, top);
                break;
        }
    }

    /// <summary>Shift 보정(FR-14): Line/Arrow=15° 스냅, Rectangle/Ellipse=정사각형/정원.</summary>
    private Point ConstrainEnd(Point a, Point b)
    {
        if (!ShiftDown)
            return b;

        if (ActiveTool is ToolKind.Line or ToolKind.Arrow)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6)
                return b;

            const double step = Math.PI / 12; // 15°
            double angle = Math.Atan2(dy, dx);
            double snapped = Math.Round(angle / step) * step;
            return new Point(a.X + Math.Cos(snapped) * len, a.Y + Math.Sin(snapped) * len);
        }

        if (ActiveTool is ToolKind.Rectangle or ToolKind.Ellipse)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            double sx = dx < 0 ? -1 : 1;
            double sy = dy < 0 ? -1 : 1;
            return new Point(a.X + side * sx, a.Y + side * sy);
        }

        return b;
    }

    /// <summary>화살표를 단일 <see cref="PathGeometry"/> 로 구성한다: shaft(본선) + 화살촉 2선.
    /// 화살촉 크기는 선 굵기와 길이에 비례한다.</summary>
    private Geometry BuildArrowGeometry(Point a, Point b)
    {
        var geo = new PathGeometry();

        // 본선
        var shaft = new PathFigure { StartPoint = a, IsClosed = false };
        shaft.Segments.Add(new LineSegment(b, true));
        geo.Figures.Add(shaft);

        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6)
            return geo;

        // 화살촉 길이: 굵기에 비례하되 선 길이를 넘지 않게 제한.
        double headLen = Math.Min(len, Math.Max(StrokeWidth * 4, 12));
        const double headAngle = Math.PI / 7; // 약 25.7°

        double ang = Math.Atan2(dy, dx);
        var wing1 = new Point(
            b.X - headLen * Math.Cos(ang - headAngle),
            b.Y - headLen * Math.Sin(ang - headAngle));
        var wing2 = new Point(
            b.X - headLen * Math.Cos(ang + headAngle),
            b.Y - headLen * Math.Sin(ang + headAngle));

        var head1 = new PathFigure { StartPoint = b, IsClosed = false };
        head1.Segments.Add(new LineSegment(wing1, true));
        geo.Figures.Add(head1);

        var head2 = new PathFigure { StartPoint = b, IsClosed = false };
        head2.Segments.Add(new LineSegment(wing2, true));
        geo.Figures.Add(head2);

        return geo;
    }

    // ── 텍스트 도구(FR-15) ─────────────────────────────────────
    private void BeginTextEntry(Point p)
    {
        var box = new TextBox
        {
            MinWidth = 60,
            FontSize = FontSize,
            FontFamily = new System.Windows.Media.FontFamily(FontFamily),
            Foreground = new SolidColorBrush(StrokeColor),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CaretBrush = new SolidColorBrush(StrokeColor),
            AcceptsReturn = true,           // Shift+Enter 줄바꿈
            TextWrapping = TextWrapping.NoWrap,
            Padding = new Thickness(2, 0, 2, 0)
        };

        InkCanvas.SetLeft(box, p.X);
        InkCanvas.SetTop(box, p.Y);

        bool finalized = false;

        void CommitText(bool commit)
        {
            if (finalized)
                return;
            finalized = true;
            _activeTextCommit = null; // 더 이상 활성 편집 아님

            // 이벤트 해제(재진입 방지).
            box.LostKeyboardFocus -= OnLostFocus;
            box.PreviewKeyDown -= OnKey;

            // 편집 중이던 TextBox 는 항상 제거한다.
            _host.Children.Remove(box);

            string text = box.Text;
            if (!commit || string.IsNullOrWhiteSpace(text))
                return; // 취소이거나 빈 텍스트 → 아무 것도 남기지 않음

            // 확정: 정적 TextBlock 으로 교체해 추가.
            var label = new TextBlock
            {
                Text = text,
                FontSize = FontSize,
                FontFamily = box.FontFamily,
                Foreground = new SolidColorBrush(StrokeColor),
                Padding = new Thickness(2, 0, 2, 0)
            };
            InkCanvas.SetLeft(label, p.X);
            InkCanvas.SetTop(label, p.Y);
            CommitChild(label);
        }

        void OnLostFocus(object? s, RoutedEventArgs e) => CommitText(commit: true);

        void OnKey(object? s, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift)
            {
                e.Handled = true;
                CommitText(commit: true);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CommitText(commit: false);
            }
        }

        box.LostKeyboardFocus += OnLostFocus;
        box.PreviewKeyDown += OnKey;
        _activeTextCommit = CommitText; // 외부 강제 종료(ClearAll/도구전환)용 훅 등록

        _host.Children.Add(box);
        // 레이아웃 완료 후 포커스(즉시 포커스는 아직 비주얼 트리에 없을 수 있음).
        box.Dispatcher.BeginInvoke(new Action(() =>
        {
            box.Focus();
            Keyboard.Focus(box);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    // ── 번호 도구(FR-22) ───────────────────────────────────────
    private void PlaceNumber(Point p)
    {
        int n = _nextNumber++;
        double diameter = Math.Max(StrokeWidth * 6, 26);
        var fillBrush = new SolidColorBrush(StrokeColor);

        // 원 + 가운데 숫자를 하나의 컨테이너(Grid)로 묶어 단일 자식으로 추가.
        var container = new Grid
        {
            Width = diameter,
            Height = diameter
        };

        var circle = new Ellipse
        {
            Width = diameter,
            Height = diameter,
            Fill = fillBrush
        };

        var label = new TextBlock
        {
            Text = n.ToString(CultureInfo.InvariantCulture),
            Foreground = Brushes.White,
            FontFamily = NumberFont,
            FontWeight = FontWeights.Bold,
            FontSize = diameter * 0.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        container.Children.Add(circle);
        container.Children.Add(label);

        // 클릭 지점이 태그의 중심이 되도록 보정.
        InkCanvas.SetLeft(container, p.X - diameter / 2);
        InkCanvas.SetTop(container, p.Y - diameter / 2);

        // Undo 로 이 번호를 되돌리면 카운터도 함께 복원/재증가되게 처리.
        CommitChild(container, onUndo: () => _nextNumber = n, onRedo: () => _nextNumber = n + 1);
    }

    // ── 호스트 자식 커밋 + Undo 기록 ───────────────────────────

    /// <summary>객체를 <see cref="InkCanvas.Children"/> 에 추가하고 역연산(제거)을 Undo 스택에 기록한다.
    /// <paramref name="onUndo"/>/<paramref name="onRedo"/> 로 부가 상태(번호 카운터 등)도 함께 되돌린다.</summary>
    private void CommitChild(UIElement element, Action? onUndo = null, Action? onRedo = null)
    {
        _host.Children.Add(element);

        // Undo/Redo 재진입(IsApplying) 중에는 기록하지 않는다.
        if (_undo.IsApplying)
            return;

        _undo.Push(
            undo: () =>
            {
                _host.Children.Remove(element);
                onUndo?.Invoke();
            },
            redo: () =>
            {
                if (!_host.Children.Contains(element))
                    _host.Children.Add(element);
                onRedo?.Invoke();
            });
    }
}
