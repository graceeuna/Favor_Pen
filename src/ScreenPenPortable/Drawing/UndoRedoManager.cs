using System;
using System.Windows.Controls;
using System.Windows.Ink;

namespace ScreenPenPortable.Drawing;

/// <summary>
/// <see cref="InkCanvas"/> 의 스트로크 추가/삭제를 <b>공유 <see cref="UndoStack"/> 타임라인</b>에
/// 기록한다(벡터 객체 서브시스템과 동일한 Undo/Redo 스택을 공유 → 통합된 실행취소 순서).
///
/// 동작 원리:
///  - 사용자가 그리거나 지우면 <see cref="StrokeCollection.StrokesChanged"/> 가 발생한다.
///    이때 (추가/삭제) 한 쌍의 역연산을 만들어 <see cref="UndoStack.Push"/> 로 기록한다.
///  - Undo/Redo 실행 시 <see cref="UndoStack.IsApplying"/> 가 true 이므로, 그때 발생하는
///    StrokesChanged 는 다시 기록되지 않는다(스택 오염 방지). 추가로 <see cref="_suppress"/>
///    가드로 ClearAll/safe-remove 등 내부 조작도 기록에서 제외한다.
///  - <see cref="ClearAll"/> 는 전체 삭제를 하나의 변경 항목으로 기록하므로 Undo 로 복구 가능하다.
///  - <see cref="RemoveStrokeWithoutHistory"/> 는 페이딩 잉크(FR-20)가 만료 스트로크를
///    히스토리 오염 없이 제거하기 위한 콜백용이다.
/// </summary>
public class UndoRedoManager
{
    private readonly InkCanvas _canvas;
    private readonly UndoStack _stack;

    /// <summary>true 인 동안 발생하는 StrokesChanged 는 기록하지 않는다(내부 조작용).</summary>
    private bool _suppress;

    public UndoRedoManager(InkCanvas canvas, UndoStack stack)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _stack = stack ?? throw new ArgumentNullException(nameof(stack));
        _canvas.Strokes.StrokesChanged += OnStrokesChanged;
    }

    private void OnStrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        if (_suppress || _stack.IsApplying)
            return;

        // 사용자 동작으로 인한 변경 → 역연산 한 쌍을 공유 스택에 기록.
        var added = new StrokeCollection(e.Added);
        var removed = new StrokeCollection(e.Removed);
        _stack.Push(
            undo: () => ApplyStrokes(remove: added, add: removed),
            redo: () => ApplyStrokes(remove: removed, add: added));
    }

    /// <summary>모든 스트로크를 하나의 변경 항목으로 지운다(Undo 로 복구 가능).</summary>
    public void ClearAll()
    {
        if (_canvas.Strokes.Count == 0)
            return;

        var removed = new StrokeCollection(_canvas.Strokes);
        _suppress = true;
        try { _canvas.Strokes.Clear(); }
        finally { _suppress = false; }

        _stack.Push(
            undo: () => ApplyStrokes(remove: new StrokeCollection(), add: removed),
            redo: () => ApplyStrokes(remove: removed, add: new StrokeCollection()));
    }

    /// <summary>
    /// 히스토리(Undo 스택)를 건드리지 않고 단일 스트로크를 제거한다.
    /// 페이딩 잉크가 만료된 스트로크를 조용히 지울 때 사용한다(FR-20).
    /// </summary>
    public void RemoveStrokeWithoutHistory(Stroke stroke)
    {
        _suppress = true;
        try
        {
            if (_canvas.Strokes.Contains(stroke))
                _canvas.Strokes.Remove(stroke);
        }
        finally { _suppress = false; }
    }

    /// <summary>
    /// 히스토리 기록 없이 모든 스트로크를 제거한다. "전체 지우기"가 스트로크와 벡터 객체를
    /// <b>하나의</b> Undo 항목으로 묶을 때, 스트로크 부분을 지휘자가 직접 처리하기 위한 훅이다.
    /// (실제 Undo 항목은 지휘자가 공유 <see cref="UndoStack"/> 에 한 번 Push 한다.)
    /// </summary>
    public void ClearStrokesWithoutHistory()
    {
        _suppress = true;
        try { _canvas.Strokes.Clear(); }
        finally { _suppress = false; }
    }

    private void ApplyStrokes(StrokeCollection remove, StrokeCollection add)
    {
        _suppress = true;
        try
        {
            if (remove.Count > 0)
                _canvas.Strokes.Remove(remove);
            if (add.Count > 0)
                _canvas.Strokes.Add(add);
        }
        finally { _suppress = false; }
    }
}
