using System.Numerics;
using Curvo.Geometry;
using Curvo.Interop;
using Curvo.Rendering;

namespace Curvo.UI;

/// <summary>
/// 출력 창 위에서 이뤄지는 편집 조작(제어점 드래그, 단축키)을 처리한다.
/// 상태 변경은 모두 ProjectionEngine.Settings 에 반영되고 즉시 다시 그린다.
/// </summary>
internal sealed class OverlayEditor
{
    private const int VirtualKeyS = 0x53;
    private const int VirtualKeyO = 0x4F;
    private const int VirtualKeyR = 0x52;
    private const int VirtualKeyY = 0x59;
    private const int VirtualKeyZ = 0x5A;
    private const int VirtualKeySpace = 0x20;
    private const int VirtualKeyPageUp = 0x21;
    private const int VirtualKeyPageDown = 0x22;

    /// <summary>제어점을 화면 밖으로 조금 끌어낼 수 있도록 허용하는 범위.</summary>
    private const float MinCoordinate = -0.5f;
    private const float MaxCoordinate = 1.5f;

    private enum DragKind
    {
        None,
        Corner,
        ControlPoint,
    }

    private readonly ProjectionEngine _engine;
    private OutputWindow? _window;
    private DragKind _drag = DragKind.None;
    private int _dragIndex = -1;

    public OverlayEditor(ProjectionEngine engine) => _engine = engine;

    public event Action? SavePresetRequested;

    public event Action? OpenPresetRequested;

    /// <summary>편집 상태가 바뀌어 컨트롤 패널을 갱신해야 할 때.</summary>
    public event Action? StateChanged;

    public void Attach(OutputWindow window)
    {
        Detach();
        _window = window;
        window.MouseDown += OnMouseDown;
        window.MouseMove += OnMouseMove;
        window.MouseUp += OnMouseUp;
        window.KeyDown += OnKeyDown;
    }

    public void Detach()
    {
        if (_window is null) return;
        _window.MouseDown -= OnMouseDown;
        _window.MouseMove -= OnMouseMove;
        _window.MouseUp -= OnMouseUp;
        _window.KeyDown -= OnKeyDown;
        _window = null;
    }

    private WarpSettings Settings => _engine.Settings;

    private OverlayState Overlay => _engine.Overlay;

    // ---- 마우스 ----------------------------------------------------------
    private void OnMouseDown(Vector2 point, OutputMouseButton button)
    {
        if (!Overlay.EditMode || _window is null) return;

        if (button != OutputMouseButton.Left) return;

        if (TryBeginCornerDrag(point)) return;
        TryBeginControlPointDrag(point);
    }

    private bool TryBeginCornerDrag(Vector2 point)
    {
        if (!Settings.CornerPinEnabled) return false;

        int index = FindNearest(Settings.CornerPoints, point);
        if (index < 0) return false;

        _engine.History.Push(Settings);
        _drag = DragKind.Corner;
        _dragIndex = index;
        Overlay.ClearSelection();
        Overlay.SelectedCorner = index;
        _engine.RequestRender();
        StateChanged?.Invoke();
        return true;
    }

    private bool TryBeginControlPointDrag(Vector2 point)
    {
        if (!Settings.BezierEnabled || !Overlay.ShowControlGrid) return false;

        ControlPointGrid grid = Settings.Grid;
        Vector2[] projected = new Vector2[grid.Count];
        Homography homography = Settings.BuildHomography();
        for (int i = 0; i < grid.Count; i++)
            projected[i] = Settings.CornerPinEnabled ? homography.Transform(grid[i]) : grid[i];

        int index = FindNearest(projected, point);
        if (index < 0) return false;

        _engine.History.Push(Settings);
        _drag = DragKind.ControlPoint;
        _dragIndex = index;
        Overlay.ClearSelection();
        Overlay.SelectedControlPoint = index;
        _engine.RequestRender();
        StateChanged?.Invoke();
        return true;
    }

    private void OnMouseMove(Vector2 point)
    {
        if (_drag == DragKind.None || _window is null) return;

        switch (_drag)
        {
            case DragKind.Corner:
                Settings.CornerPoints[_dragIndex] = Clamp(point);
                break;

            case DragKind.ControlPoint:
                Settings.Grid[_dragIndex] = Clamp(ToWarpSpace(point));
                break;
        }

        _engine.InvalidateGeometry();
    }

    private void OnMouseUp(Vector2 point, OutputMouseButton button)
    {
        if (button != OutputMouseButton.Left) return;
        _drag = DragKind.None;
        _dragIndex = -1;
    }

    // ---- 키보드 ----------------------------------------------------------
    private void OnKeyDown(int virtualKey)
    {
        bool control = Win32.IsControlDown();

        if (control)
        {
            switch (virtualKey)
            {
                case VirtualKeyS: SavePresetRequested?.Invoke(); return;
                case VirtualKeyO: OpenPresetRequested?.Invoke(); return;
                case VirtualKeyR: ResetGeometry(); return;
                case VirtualKeyZ: ApplyHistory(undo: true); return;
                case VirtualKeyY: ApplyHistory(undo: false); return;
            }
        }

        switch (virtualKey)
        {
            case Win32.VK_F1:
                Overlay.EditMode = !Overlay.EditMode;
                Notify();
                return;

            case Win32.VK_F2:
                Overlay.Pattern = NextPattern(Overlay.Pattern);
                Notify();
                return;

            case Win32.VK_F3:
                Overlay.ShowReferenceGrid = !Overlay.ShowReferenceGrid;
                Notify();
                return;

            case Win32.VK_F4:
                Overlay.ShowDiagonals = !Overlay.ShowDiagonals;
                Notify();
                return;

            case Win32.VK_ESCAPE:
                _window?.ToggleFullscreen();
                return;

            case Win32.VK_LEFT: Nudge(-1, 0); return;
            case Win32.VK_RIGHT: Nudge(1, 0); return;
            case Win32.VK_UP: Nudge(0, -1); return;
            case Win32.VK_DOWN: Nudge(0, 1); return;

            // 내장 재생 제어
            case VirtualKeySpace: _engine.ToggleMediaPlayback(); return;
            case VirtualKeyPageUp: _engine.PreviousSlide(); return;
            case VirtualKeyPageDown: _engine.NextSlide(); return;
        }
    }

    private void ResetGeometry()
    {
        _engine.History.Push(Settings);
        Settings.ResetGeometry();
        Overlay.ClearSelection();
        Notify();
    }

    private void ApplyHistory(bool undo)
    {
        bool changed = undo ? _engine.History.Undo(Settings) : _engine.History.Redo(Settings);
        if (!changed) return;
        Overlay.ClearSelection();
        Notify();
    }

    private void Nudge(int directionX, int directionY)
    {
        if (_window is null) return;

        float step = Win32.IsShiftDown() ? AppConfig.NudgePixelsFast : AppConfig.NudgePixels;
        var delta = new Vector2(directionX * step / Math.Max(1, _window.Width),
                                directionY * step / Math.Max(1, _window.Height));

        if (Overlay.SelectedCorner >= 0)
        {
            _engine.History.Push(Settings);
            Settings.CornerPoints[Overlay.SelectedCorner] = Clamp(Settings.CornerPoints[Overlay.SelectedCorner] + delta);
        }
        else if (Overlay.SelectedControlPoint >= 0 && Overlay.SelectedControlPoint < Settings.Grid.Count)
        {
            _engine.History.Push(Settings);
            Settings.Grid[Overlay.SelectedControlPoint] = Clamp(Settings.Grid[Overlay.SelectedControlPoint] + delta);
        }
        else
        {
            return;
        }

        _engine.InvalidateGeometry();
        StateChanged?.Invoke();
    }

    private void Notify()
    {
        _engine.InvalidateGeometry();
        StateChanged?.Invoke();
    }

    public static TestPattern NextPattern(TestPattern current)
    {
        var values = Enum.GetValues<TestPattern>();
        int index = Array.IndexOf(values, current);
        return values[(index + 1) % values.Length];
    }

    /// <summary>출력 좌표를 워핑(제어점) 좌표로 되돌린다.</summary>
    private Vector2 ToWarpSpace(Vector2 outputPoint)
        => Settings.CornerPinEnabled ? Settings.BuildHomography().Invert().Transform(outputPoint) : outputPoint;

    private static Vector2 Clamp(Vector2 point) => new(
        Math.Clamp(point.X, MinCoordinate, MaxCoordinate),
        Math.Clamp(point.Y, MinCoordinate, MaxCoordinate));

    /// <summary>픽셀 반경 안에서 가장 가까운 점의 인덱스. 없으면 -1.</summary>
    private int FindNearest(IReadOnlyList<Vector2> points, Vector2 target)
    {
        if (_window is null) return -1;

        float width = Math.Max(1, _window.Width);
        float height = Math.Max(1, _window.Height);
        float bestDistance = AppConfig.HandlePickRadiusPixels;
        int best = -1;

        for (int i = 0; i < points.Count; i++)
        {
            float dx = (points[i].X - target.X) * width;
            float dy = (points[i].Y - target.Y) * height;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }
}
