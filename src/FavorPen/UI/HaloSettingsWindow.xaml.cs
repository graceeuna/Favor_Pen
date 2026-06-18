using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FavorPen.UI;

/// <summary>
/// 마우스 하이라이트(헤일로, FR-21)의 <b>색상·크기</b>를 바꾸는 작은 설정 팝업.
/// 색 스와치 클릭/크기 슬라이더 조작 시 이벤트를 발행하고, 지휘자가 헤일로에 실시간 반영한다.
/// </summary>
public partial class HaloSettingsWindow : Window
{
    // 알파를 넣어 가시성을 확보한 프리셋(가장자리는 RadialGradient 로 자연 감쇠).
    private static readonly string[] Presets =
    {
        "#CCFFD000", "#CCFF9500", "#CCFF3B30", "#CCFF2D55", "#CCAF52DE",
        "#CC34C759", "#CC32ADE6", "#CC0A84FF", "#CCFFFFFF", "#CC000000"
    };

    private bool _suppress;

    /// <summary>색 스와치 선택 시 발생(실시간 적용용).</summary>
    public event Action<Color>? ColorPicked;

    /// <summary>크기(반지름) 변경 시 발생(실시간 적용용).</summary>
    public event Action<double>? SizePicked;

    public HaloSettingsWindow()
    {
        InitializeComponent();
        BuildSwatches();
    }

    /// <summary>현재 설정값으로 UI를 동기화한다(이벤트 미발생).</summary>
    public void SetInitial(Color color, double radius)
    {
        _suppress = true;
        try
        {
            SizeSlider.Value = Math.Clamp(radius, SizeSlider.Minimum, SizeSlider.Maximum);
            SizeLabel.Text = $"{(int)Math.Round(SizeSlider.Value)} px";
            HighlightSelected(color);
        }
        finally { _suppress = false; }
    }

    private void BuildSwatches()
    {
        Swatches.Children.Clear();
        foreach (string hex in Presets)
        {
            Color c;
            try { c = (Color)ColorConverter.ConvertFromString(hex)!; }
            catch { continue; }

            var sw = new Button
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(c),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Tag = c,
                ToolTip = hex
            };
            sw.Template = BuildSwatchTemplate();
            sw.Click += OnSwatchClick;
            Swatches.Children.Add(sw);
        }
    }

    private void OnSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Color c })
        {
            HighlightSelected(c);
            ColorPicked?.Invoke(c);
        }
    }

    /// <summary>선택된 색 스와치에 굵은 테두리 표시.</summary>
    private void HighlightSelected(Color color)
    {
        foreach (object child in Swatches.Children)
        {
            if (child is Button b && b.Tag is Color c)
            {
                bool sel = c == color;
                b.BorderBrush = sel
                    ? new SolidColorBrush(Color.FromRgb(0x3D, 0x7D, 0xFF))
                    : new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
                b.BorderThickness = new Thickness(sel ? 2.5 : 1);
            }
        }
    }

    private void OnSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SizeLabel != null)
            SizeLabel.Text = $"{(int)Math.Round(e.NewValue)} px";
        if (_suppress) return;
        SizePicked?.Invoke(e.NewValue);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Hide();

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            e.Handled = true;
            DragMove();
        }
    }

    private static ControlTemplate BuildSwatchTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetBinding(Border.BackgroundProperty,
            new System.Windows.Data.Binding(nameof(Background))
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty,
            new System.Windows.Data.Binding(nameof(BorderBrush))
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty,
            new System.Windows.Data.Binding(nameof(BorderThickness))
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }
}
