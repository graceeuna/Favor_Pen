using System;
using System.Collections.Generic;

namespace ScreenPenPortable.Drawing;

/// <summary>
/// 스트로크 서브시스템(<see cref="UndoRedoManager"/>)과 벡터 객체 서브시스템
/// (도형·텍스트·넘버링 = ObjectLayer)이 <b>공유하는 단일 Undo/Redo 타임라인</b>.
///
/// 각 작업(Op)은 이미 정의된 한 쌍의 역연산(undo/redo Action)으로 표현된다.
///  - <see cref="Push"/>: 호출자가 <b>이미 수행한</b> 작업을 기록한다(새 분기 → redo 무효화).
///  - <see cref="Do"/>: redo 를 지금 실행한 뒤 기록한다.
///  - <see cref="Undo"/>/<see cref="Redo"/>: 저장된 Action 을 역/정방향으로 실행한다.
///
/// 재진입 가드(<see cref="IsApplying"/>): Undo/Redo 실행 중 호출자의 컬렉션 변경이
/// 다시 Push 를 유발해도 무시하여 스택 오염을 막는다. 스트로크/객체 서브시스템은
/// 자신의 변경 이벤트 핸들러에서 <see cref="IsApplying"/> 가 true 면 기록을 건너뛰면 된다.
/// </summary>
public sealed class UndoStack
{
    private readonly record struct Op(Action Undo, Action Redo);

    private readonly Stack<Op> _undo = new();
    private readonly Stack<Op> _redo = new();
    private bool _busy;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Undo/Redo 실행 중이면 true. 변경 이벤트 핸들러는 이때 기록을 건너뛴다.</summary>
    public bool IsApplying => _busy;

    /// <summary>스택이 바뀔 때마다 발생(버튼 활성화·상태 갱신용).</summary>
    public event Action? Changed;

    /// <summary>이미 수행된 작업을 기록한다. <paramref name="undo"/> 는 작업을 되돌리고,
    /// <paramref name="redo"/> 는 다시 적용한다.</summary>
    public void Push(Action undo, Action redo)
    {
        if (_busy) return; // Undo/Redo 가 유발한 변경은 기록하지 않는다.
        _undo.Push(new Op(undo, redo));
        _redo.Clear();
        Changed?.Invoke();
    }

    /// <summary><paramref name="redo"/> 를 즉시 실행한 뒤 기록한다.</summary>
    public void Do(Action redo, Action undo)
    {
        redo();
        Push(undo, redo);
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        Op op = _undo.Pop();
        _busy = true;
        try { op.Undo(); } finally { _busy = false; }
        _redo.Push(op);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        Op op = _redo.Pop();
        _busy = true;
        try { op.Redo(); } finally { _busy = false; }
        _undo.Push(op);
        Changed?.Invoke();
    }

    /// <summary>전체 히스토리를 비운다(앱 종료/리셋용).</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }
}
