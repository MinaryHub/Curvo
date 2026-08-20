using System.Numerics;

namespace ProjectorWarp.Geometry;

/// <summary>출력 화면에 겹쳐 그리는 테스트 패턴 종류.</summary>
internal enum TestPattern
{
    None = 0,
    Grid = 1,
    Checker = 2,
    Rings = 3,
    ColorBars = 4,
    WhiteField = 5,
    BlackField = 6,
}

/// <summary>
/// 기하 보정 + 색상 보정 전체 상태. 프리셋 저장 단위이자 실행 취소 스냅샷 단위.
/// 모든 좌표는 출력 화면 기준 정규화(0~1) 좌표이다.
/// </summary>
internal sealed class WarpSettings
{
    // ---- 1단계: 코너 핀 / 키스톤 -----------------------------------------
    public bool CornerPinEnabled { get; set; }

    /// <summary>좌상, 우상, 우하, 좌하 순서.</summary>
    public Vector2[] CornerPoints { get; private set; } = CreateDefaultCorners();

    // ---- 2단계: 베지어 곡면 ----------------------------------------------
    public bool BezierEnabled { get; set; } = true;

    public ControlPointGrid Grid { get; private set; } = new();

    private int _tessellation = AppConfig.DefaultTessellation;

    public int Tessellation
    {
        get => _tessellation;
        set => _tessellation = Math.Clamp(value, AppConfig.MinTessellation, AppConfig.MaxTessellation);
    }

    // ---- 3단계: 마스킹 ----------------------------------------------------


    // ---- 색상 보정 --------------------------------------------------------
    public bool ColorEnabled { get; set; }

    public float Brightness { get; set; } = AppConfig.DefaultBrightness;

    public float Contrast { get; set; } = AppConfig.DefaultContrast;

    public float Gamma { get; set; } = AppConfig.DefaultGamma;

    // ---- 엣지 블렌딩 ------------------------------------------------------
    public bool EdgeBlendEnabled { get; set; }

    public float EdgeBlendLeft { get; set; }

    public float EdgeBlendRight { get; set; }

    public float EdgeBlendTop { get; set; }

    public float EdgeBlendBottom { get; set; }

    public float EdgeBlendGamma { get; set; } = AppConfig.DefaultEdgeBlendGamma;

    public static Vector2[] CreateDefaultCorners() => new[]
    {
        new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
    };

    public void SetCornerPoints(IReadOnlyList<Vector2> corners)
    {
        if (corners.Count != 4) return;
        CornerPoints = corners.ToArray();
    }

    public Homography BuildHomography() => CornerPinEnabled
        ? Homography.FromUnitSquare(CornerPoints[0], CornerPoints[1], CornerPoints[2], CornerPoints[3])
        : Homography.Identity;

    /// <summary>기하 보정만 초기화한다(색상/블렌딩 값은 유지).</summary>
    public void ResetGeometry()
    {
        CornerPoints = CreateDefaultCorners();
        Grid.Reset();
    }

    public WarpSettings Clone()
    {
        var clone = new WarpSettings
        {
            CornerPinEnabled = CornerPinEnabled,
            BezierEnabled = BezierEnabled,
            Tessellation = Tessellation,
            ColorEnabled = ColorEnabled,
            Brightness = Brightness,
            Contrast = Contrast,
            Gamma = Gamma,
            EdgeBlendEnabled = EdgeBlendEnabled,
            EdgeBlendLeft = EdgeBlendLeft,
            EdgeBlendRight = EdgeBlendRight,
            EdgeBlendTop = EdgeBlendTop,
            EdgeBlendBottom = EdgeBlendBottom,
            EdgeBlendGamma = EdgeBlendGamma,
            CornerPoints = (Vector2[])CornerPoints.Clone(),
            Grid = Grid.Clone(),
        };
        return clone;
    }

    /// <summary>다른 상태의 값을 그대로 덮어쓴다(실행 취소 복원용).</summary>
    public void CopyFrom(WarpSettings other)
    {
        CornerPinEnabled = other.CornerPinEnabled;
        CornerPoints = (Vector2[])other.CornerPoints.Clone();
        BezierEnabled = other.BezierEnabled;
        Grid = other.Grid.Clone();
        Tessellation = other.Tessellation;
        ColorEnabled = other.ColorEnabled;
        Brightness = other.Brightness;
        Contrast = other.Contrast;
        Gamma = other.Gamma;
        EdgeBlendEnabled = other.EdgeBlendEnabled;
        EdgeBlendLeft = other.EdgeBlendLeft;
        EdgeBlendRight = other.EdgeBlendRight;
        EdgeBlendTop = other.EdgeBlendTop;
        EdgeBlendBottom = other.EdgeBlendBottom;
        EdgeBlendGamma = other.EdgeBlendGamma;
    }

    public void ReplaceGrid(ControlPointGrid grid) => Grid = grid;
}
