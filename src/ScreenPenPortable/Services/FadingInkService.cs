using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Threading;

namespace ScreenPenPortable.Services;

/// <summary>
/// FR-20 페이딩 잉크: 그린 스트로크가 일정 시간(<c>seconds</c>) 유지된 뒤
/// 약 1초에 걸쳐 알파가 선형 감소하며 사라지는 효과를 제공한다.
///
/// 동작 원리:
///  - <see cref="Enable"/> 시 <see cref="InkCanvas.Strokes"/> 의 StrokesChanged(Added) 를
///    구독해 새로 추가된 각 스트로크의 생성 시각(UtcNow)을 기록한다.
///  - <see cref="DispatcherTimer"/>(틱 ~60ms)로 추적 중인 스트로크의 경과 시간을 확인한다.
///      · 경과 &lt; seconds            → 아직 손대지 않음(완전 불투명 유지)
///      · seconds ≤ 경과 &lt; seconds+1 → 알파를 1→0 으로 선형 감소
///      · 경과 ≥ seconds+1            → 알파 0 도달, <see cref="_safeRemove"/> 로 제거 후 추적 해제
///
/// 중요(Undo 오염 방지):
///  - 페이드로 사라진 스트로크는 절대 <c>host.Strokes.Remove</c> 로 직접 지우지 않는다.
///    그렇게 하면 <see cref="ScreenPenPortable.Drawing.UndoRedoManager"/> 의 StrokesChanged
///    핸들러가 "사용자 삭제"로 오인해 undo 스택을 오염시킨다. 대신 lead 가 주입한
///    <see cref="_safeRemove"/>(undo 기록을 억제한 채 제거하는 콜백)만 사용한다.
///  - 알파 감소도 새 변경 항목을 만들지 않도록 <see cref="DrawingAttributes.Color"/> 의
///    A 채널만 in-place 로 바꾼다(DrawingAttributes 는 가변; StrokesChanged 가 아니라
///    AttributeChanged 만 발생하므로 undo 스택에 영향 없음).
///
/// 방어적 설계:
///  - Undo/Redo 로 (이미 페이드 완료로 제거됐던) 스트로크가 다시 Added 될 수 있다.
///    이때도 단순히 "Added 된 시점부터 새로 시간을 잰다"로 처리하면 일관적이다.
///    (재추가 = 재카운트). 이미 추적 중인 스트로크가 또 Added 로 들어오면 시각을 갱신한다.
///  - Removed 이벤트로 추적 목록에서도 제거해 사라진 스트로크를 계속 만지지 않는다.
/// </summary>
public sealed class FadingInkService
{
    /// <summary>알파 감소(페이드아웃)에 걸리는 시간(초). seconds 경과 후 이 시간 동안 1→0.</summary>
    private const double FadeDurationSeconds = 1.0;

    /// <summary>타이머 틱 간격(ms). 60ms ≈ 16fps 로 충분히 부드럽다.</summary>
    private const int TickMs = 60;

    private readonly InkCanvas _host;
    private readonly Action<Stroke> _safeRemove;
    private readonly DispatcherTimer _timer;

    /// <summary>추적 중인 스트로크 → (생성시각, 초기 알파). 초기 알파를 기억해 거기서부터 감쇠한다.</summary>
    private readonly Dictionary<Stroke, TrackInfo> _tracked = new();

    private double _holdSeconds = 3.0;
    private bool _enabled;

    private readonly record struct TrackInfo(DateTime CreatedUtc, byte InitialAlpha);

    /// <param name="host">스트로크가 그려지는 InkCanvas.</param>
    /// <param name="safeRemove">
    /// undo 기록 없이 스트로크를 안전하게 제거하는 lead 제공 콜백.
    /// 페이드 완료 스트로크는 반드시 이 콜백으로만 제거한다(직접 Remove 금지).
    /// </param>
    public FadingInkService(InkCanvas host, Action<Stroke> safeRemove)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _safeRemove = safeRemove ?? throw new ArgumentNullException(nameof(safeRemove));

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(TickMs)
        };
        _timer.Tick += OnTick;
    }

    /// <summary>현재 페이딩 잉크가 활성 상태인지 여부.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>
    /// 페이딩 잉크를 켠다. <paramref name="seconds"/> 는 스트로크가 사라지기 시작할 때까지의
    /// 유지 시간(초). 이미 켜져 있으면 유지 시간만 갱신한다.
    /// </summary>
    public void Enable(double seconds)
    {
        _holdSeconds = seconds > 0 ? seconds : 0.0;

        if (_enabled)
            return;

        _enabled = true;
        _host.Strokes.StrokesChanged += OnStrokesChanged;
        _timer.Start();
    }

    /// <summary>
    /// 페이딩 잉크를 끈다. 타이머를 멈추고 추적을 비운다.
    /// 이미 화면에 그려진(추적 중이던) 스트로크는 그대로 둔다(되살리지 않음).
    /// </summary>
    public void Disable()
    {
        if (!_enabled)
            return;

        _enabled = false;
        _host.Strokes.StrokesChanged -= OnStrokesChanged;
        _timer.Stop();
        _tracked.Clear();
    }

    private void OnStrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        // 새로 추가된 스트로크는 추적 시작(또는 재추가 시 시각 갱신 = 재카운트).
        foreach (Stroke s in e.Added)
        {
            byte alpha = s.DrawingAttributes?.Color.A ?? (byte)255;
            // 알파가 이미 0 인(어떤 이유로든) 스트로크는 즉시 제거 대상으로만 둔다.
            _tracked[s] = new TrackInfo(DateTime.UtcNow, alpha == 0 ? (byte)255 : alpha);
        }

        // 사용자가 지우개 등으로 지운 스트로크는 더 이상 추적하지 않는다.
        foreach (Stroke s in e.Removed)
            _tracked.Remove(s);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_tracked.Count == 0)
            return;

        DateTime now = DateTime.UtcNow;

        // 순회 중 컬렉션을 변경하므로 완료 대상은 따로 모았다가 제거한다.
        List<Stroke>? toRemove = null;

        foreach (var kvp in _tracked)
        {
            Stroke stroke = kvp.Key;
            TrackInfo info = kvp.Value;

            double elapsed = (now - info.CreatedUtc).TotalSeconds;

            if (elapsed < _holdSeconds)
                continue; // 아직 유지 구간 — 손대지 않음.

            double fadeT = (elapsed - _holdSeconds) / FadeDurationSeconds; // 0→1

            if (fadeT >= 1.0)
            {
                (toRemove ??= new List<Stroke>()).Add(stroke);
                continue;
            }

            // 알파를 초기값에서 0 까지 선형 감소시킨다(in-place; undo 영향 없음).
            byte newAlpha = (byte)Math.Round(info.InitialAlpha * (1.0 - fadeT));
            ApplyAlpha(stroke, newAlpha);
        }

        if (toRemove != null)
        {
            foreach (Stroke stroke in toRemove)
            {
                _tracked.Remove(stroke);
                // 추적에서 먼저 빼서, safeRemove 가 유발하는 Removed 이벤트로 인한
                // 중복 처리/예외를 피한다. host 에 실제로 존재할 때만 제거 시도.
                if (_host.Strokes.Contains(stroke))
                    _safeRemove(stroke);
            }
        }
    }

    /// <summary>
    /// 스트로크의 색 알파만 바꾼다. DrawingAttributes 는 가변이므로 Color 만 교체하면
    /// AttributeChanged 만 발생하고 StrokesChanged 는 일어나지 않는다(undo 스택 안전).
    /// </summary>
    private static void ApplyAlpha(Stroke stroke, byte alpha)
    {
        DrawingAttributes? da = stroke.DrawingAttributes;
        if (da == null)
            return;

        System.Windows.Media.Color c = da.Color;
        if (c.A == alpha)
            return;

        c.A = alpha;
        da.Color = c;
    }
}
