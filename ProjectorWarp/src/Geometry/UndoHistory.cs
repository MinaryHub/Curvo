namespace Curvo.Geometry;

/// <summary>워핑 상태 스냅샷 기반 실행 취소 / 다시 실행.</summary>
internal sealed class UndoHistory
{
    private readonly List<WarpSettings> _undoStack = new();
    private readonly List<WarpSettings> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;

    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>상태를 바꾸기 <b>직전</b>에 호출한다.</summary>
    public void Push(WarpSettings current)
    {
        _undoStack.Add(current.Clone());
        if (_undoStack.Count > AppConfig.UndoStackDepth) _undoStack.RemoveAt(0);
        _redoStack.Clear();
    }

    public bool Undo(WarpSettings live)
    {
        if (_undoStack.Count == 0) return false;
        WarpSettings snapshot = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _redoStack.Add(live.Clone());
        live.CopyFrom(snapshot);
        return true;
    }

    public bool Redo(WarpSettings live)
    {
        if (_redoStack.Count == 0) return false;
        WarpSettings snapshot = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _undoStack.Add(live.Clone());
        live.CopyFrom(snapshot);
        return true;
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }
}
