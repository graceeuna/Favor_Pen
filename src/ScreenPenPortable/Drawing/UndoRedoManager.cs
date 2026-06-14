using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Ink;

namespace ScreenPenPortable.Drawing;

/// <summary>
/// <see cref="InkCanvas"/> 의 스트로크 추가/삭제에 대한 Undo/Redo 를 제공한다.
///
/// 동작 원리:
///  - 사용자가 그리거나 지우면 <see cref="StrokeCollection.StrokesChanged"/> 가 발생한다.
///    이때 (추가된 스트로크, 삭제된 스트로크) 한 쌍을 하나의 변경 항목으로 undo 스택에 쌓는다.
///  - Undo 는 그 변경을 역으로 적용(추가→삭제, 삭제→추가)하고 redo 스택으로 옮긴다.
///  - Undo/Redo 가 스트로크를 조작할 때도 StrokesChanged 가 다시 발생하므로,
///    <see cref="_suppress"/> 재진입 가드로 그 이벤트를 무시한다(무한 루프/중복 기록 방지).
///  - <see cref="ClearAll"/> 는 전체 삭제를 하나의 변경 항목으로 기록하므로 Undo 로 복구 가능하다.
/// </summary>
public class UndoRedoManager
{
    /// <summary>한 번의 사용자 동작으로 발생한 스트로크 변경(추가/삭제) 묶음.</summary>
    private sealed record Change(StrokeCollection Added, StrokeCollection Removed);

    private readonly InkCanvas _canvas;
    private readonly Stack<Change> _undo = new();
    private readonly Stack<Change> _redo = new();

    /// <summary>true 인 동안 발생하는 StrokesChanged 는 기록하지 않는다(Undo/Redo 내부 조작용).</summary>
    private bool _suppress;

    public UndoRedoManager(InkCanvas canvas)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _canvas.Strokes.StrokesChanged += OnStrokesChanged;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Undo/Redo 가능 여부 또는 스택이 바뀔 때마다 발생한다(버튼 활성화 갱신용).</summary>
    public event Action? Changed;

    private void OnStrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        if (_suppress)
            return;

        // 사용자 동작으로 인한 변경 → 새 항목 기록. 새 분기 시작이므로 redo 무효화.
        _undo.Push(new Change(
            new StrokeCollection(e.Added),
            new StrokeCollection(e.Removed)));
        _redo.Clear();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (!CanUndo)
            return;

        Change change = _undo.Pop();
        Apply(remove: change.Added, add: change.Removed);
        _redo.Push(change);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo)
            return;

        Change change = _redo.Pop();
        Apply(remove: change.Removed, add: change.Added);
        _undo.Push(change);
        Changed?.Invoke();
    }

    /// <summary>
    /// 모든 스트로크를 지우되, 하나의 변경 항목으로 기록하여 Undo 로 되돌릴 수 있게 한다.
    /// </summary>
    public void ClearAll()
    {
        if (_canvas.Strokes.Count == 0)
            return;

        var removed = new StrokeCollection(_canvas.Strokes);
        _suppress = true;
        try
        {
            _canvas.Strokes.Clear();
        }
        finally
        {
            _suppress = false;
        }

        _undo.Push(new Change(new StrokeCollection(), removed));
        _redo.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// 가드를 켠 채 스트로크를 조작한다. 여기서 발생하는 StrokesChanged 는
    /// <see cref="OnStrokesChanged"/> 에서 무시되어 스택이 오염되지 않는다.
    /// </summary>
    private void Apply(StrokeCollection remove, StrokeCollection add)
    {
        _suppress = true;
        try
        {
            if (remove.Count > 0)
                _canvas.Strokes.Remove(remove);
            if (add.Count > 0)
                _canvas.Strokes.Add(add);
        }
        finally
        {
            _suppress = false;
        }
    }
}
