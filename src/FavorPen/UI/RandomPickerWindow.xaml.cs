using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace FavorPen.UI;

/// <summary>
/// 발표용 랜덤 뽑기 창(타이머와 같은 부류의 항상-위 도구).
/// 사용자가 번호 범위(예: "1-20, 41-60")와 뽑을 인원(1명·2명…)을 정하면
/// 그 안에서 무작위로 골라 큰 글씨로 보여 준다.
/// "뽑은 번호 제외" 옵션을 켜면 이전에 뽑힌 번호를 후보에서 빼(중복 방지) 차례로 시킬 수 있다.
///
/// 타이머 창과 동일하게 Owner=오버레이로 띄워 그리기 모드에서도 버튼이 클릭된다.
/// </summary>
public partial class RandomPickerWindow : Window
{
    private int _count = 1;
    private readonly HashSet<int> _excluded = new(); // "제외" 옵션에서 이미 뽑힌 번호
    private bool _userMoved;

    public RandomPickerWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => { if (!_userMoved) CenterOnPrimary(); };
        UpdateCount();
    }

    // ── 영속(설정 저장)용 접근자 ───────────────────────────────
    public string RangesText
    {
        get => RangeBox.Text;
        set { if (!string.IsNullOrWhiteSpace(value)) RangeBox.Text = value; }
    }

    public int Count
    {
        get => _count;
        set { _count = Math.Clamp(value, 1, 20); UpdateCount(); }
    }

    // ── 표시/토글 ──────────────────────────────────────────────
    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            _userMoved = false;
            Show();
            Activate();
            Dispatcher.BeginInvoke(new Action(CenterOnPrimary), DispatcherPriority.Loaded);
        }
    }

    // ── 인원 ───────────────────────────────────────────────────
    private void OnCountMinus(object sender, RoutedEventArgs e) { _count = Math.Max(1, _count - 1); UpdateCount(); }
    private void OnCountPlus(object sender, RoutedEventArgs e) { _count = Math.Min(20, _count + 1); UpdateCount(); }
    private void UpdateCount() { if (CountText != null) CountText.Text = $"{_count}명"; }

    private void OnResetExcluded(object sender, RoutedEventArgs e)
    {
        _excluded.Clear();
        StatusText.Text = "제외 목록을 비웠습니다.";
    }

    // ── 뽑기 ───────────────────────────────────────────────────
    private void OnDraw(object sender, RoutedEventArgs e)
    {
        List<int> pool;
        try
        {
            pool = ParseRanges(RangeBox.Text);
        }
        catch
        {
            StatusText.Text = "범위 형식이 올바르지 않습니다. 예: 1-20, 41-60";
            return;
        }

        if (pool.Count == 0)
        {
            StatusText.Text = "번호 범위를 입력하세요.";
            return;
        }

        bool exclude = ExcludeCheck.IsChecked == true;
        List<int> candidates = exclude ? pool.Where(n => !_excluded.Contains(n)).ToList() : pool;

        if (candidates.Count == 0)
        {
            ResultText.Text = "?";
            StatusText.Text = "남은 번호가 없습니다. ↺ 초기화 하세요.";
            return;
        }

        int pick = Math.Min(_count, candidates.Count);
        List<int> chosen = PickDistinct(candidates, pick);

        if (exclude)
            foreach (int n in chosen) _excluded.Add(n);

        // 인원이 많을수록 글자 크기를 줄여 화면에 맞춘다.
        ResultText.FontSize = chosen.Count <= 1 ? 110 : chosen.Count <= 3 ? 78 : chosen.Count <= 6 ? 54 : 40;
        ResultText.Text = string.Join("   ", chosen.OrderBy(n => n));

        StatusText.Text = exclude
            ? $"후보 {candidates.Count} · {pick}명 뽑음 · 누적 제외 {_excluded.Count}"
            : $"후보 {pool.Count} · {pick}명 뽑음";
    }

    /// <summary>중복 없이 k개를 무작위로 고른다(부분 Fisher–Yates 셔플).</summary>
    private static List<int> PickDistinct(List<int> source, int k)
    {
        int[] arr = source.ToArray();
        int n = arr.Length;
        var result = new List<int>(k);
        for (int i = 0; i < k && i < n; i++)
        {
            int j = i + Random.Shared.Next(n - i);
            (arr[i], arr[j]) = (arr[j], arr[i]);
            result.Add(arr[i]);
        }
        return result;
    }

    /// <summary>"1-20, 41~60, 7" 형태 문자열을 정수 목록(중복 제거·정렬)으로 변환한다.
    /// 그룹 구분: 쉼표/줄바꿈. 범위 구분: '-' '~' '～' '–'. 단일 숫자도 허용.</summary>
    private static List<int> ParseRanges(string text)
    {
        var set = new SortedSet<int>();
        if (string.IsNullOrWhiteSpace(text)) return new List<int>();

        string[] groups = text.Split(new[] { ',', '，', '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string raw in groups)
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;

            int sepIdx = part.IndexOfAny(new[] { '-', '~', '～', '–' }, 1); // 1부터: 음수 부호 오인 방지
            if (sepIdx > 0)
            {
                int a = int.Parse(part[..sepIdx].Trim(), CultureInfo.InvariantCulture);
                int b = int.Parse(part[(sepIdx + 1)..].Trim(), CultureInfo.InvariantCulture);
                if (a > b) (a, b) = (b, a);
                for (int n = a; n <= b; n++) set.Add(n);
            }
            else
            {
                set.Add(int.Parse(part, CultureInfo.InvariantCulture));
            }
        }
        return set.ToList();
    }

    // ── 위치/이동 ──────────────────────────────────────────────
    private void CenterOnPrimary()
    {
        var p = System.Windows.Forms.Screen.PrimaryScreen;
        if (p == null)
        {
            Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
            Top = (SystemParameters.PrimaryScreenHeight - ActualHeight) / 2;
            return;
        }

        var b = p.Bounds;
        var src = PresentationSource.FromVisual(this);
        double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        Left = b.Left / sx + (b.Width / sx - ActualWidth) / 2;
        Top = b.Top / sy + (b.Height / sy - ActualHeight) / 2;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) { _userMoved = true; DragMove(); }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();
}
