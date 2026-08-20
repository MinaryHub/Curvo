using Curvo.Geometry;

namespace Curvo.Rendering;

/// <summary>편집 오버레이 표시 상태(프리셋에는 저장하지 않는 화면 전용 상태).</summary>
internal sealed class OverlayState
{
    /// <summary>편집 모드. 끄면 오버레이를 전혀 그리지 않는다.</summary>
    public bool EditMode { get; set; }

    /// <summary>제어점 격자선 표시.</summary>
    public bool ShowControlGrid { get; set; } = true;

    /// <summary>참조 그리드 표시.</summary>
    public bool ShowReferenceGrid { get; set; }

    /// <summary>대각선 표시.</summary>
    public bool ShowDiagonals { get; set; }

    public TestPattern Pattern { get; set; } = TestPattern.None;

    /// <summary>선택된 베지어 제어점 인덱스(-1 = 없음).</summary>
    public int SelectedControlPoint { get; set; } = -1;

    /// <summary>선택된 코너 핀 인덱스(-1 = 없음).</summary>
    public int SelectedCorner { get; set; } = -1;

    public void ClearSelection()
    {
        SelectedControlPoint = -1;
        SelectedCorner = -1;
    }
}
