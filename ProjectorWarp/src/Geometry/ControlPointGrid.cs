using System.Numerics;

namespace ProjectorWarp.Geometry;

/// <summary>
/// 정규화 좌표(0~1)로 표현된 베지어 제어점 격자.
/// 인덱스는 row(v) * GridSize + column(u) 이다.
/// </summary>
internal sealed class ControlPointGrid
{
    private Vector2[] _points;

    public ControlPointGrid(int gridSize = AppConfig.DefaultGridSize)
    {
        GridSize = ClampGridSize(gridSize);
        _points = CreateUniform(GridSize);
    }

    private ControlPointGrid(int gridSize, Vector2[] points)
    {
        GridSize = gridSize;
        _points = points;
    }

    public int GridSize { get; private set; }

    public int Count => _points.Length;

    public ReadOnlySpan<Vector2> Points => _points;

    public Vector2 this[int index]
    {
        get => _points[index];
        set => _points[index] = value;
    }

    public Vector2 Get(int column, int row) => _points[row * GridSize + column];

    public void Set(int column, int row, Vector2 value) => _points[row * GridSize + column] = value;

    public static int ClampGridSize(int gridSize)
        => Math.Clamp(gridSize, AppConfig.MinGridSize, AppConfig.MaxGridSize);

    private static Vector2[] CreateUniform(int gridSize)
    {
        var points = new Vector2[gridSize * gridSize];
        float step = 1.0f / (gridSize - 1);
        for (int row = 0; row < gridSize; row++)
        {
            for (int column = 0; column < gridSize; column++)
                points[row * gridSize + column] = new Vector2(column * step, row * step);
        }
        return points;
    }

    /// <summary>제어점을 균일 격자로 되돌린다.</summary>
    public void Reset() => _points = CreateUniform(GridSize);

    /// <summary>
    /// 격자 크기를 변경한다. 늘릴 때는 차수 상승으로 형상을 완전히 보존하고,
    /// 줄일 때는 기존 곡면을 새 격자 위치에서 샘플링한다(근사).
    /// </summary>
    public void Resize(int newGridSize)
    {
        newGridSize = ClampGridSize(newGridSize);
        if (newGridSize == GridSize) return;

        if (newGridSize > GridSize)
        {
            var current = _points;
            int size = GridSize;
            while (size < newGridSize)
            {
                current = Bezier.ElevateDegree(current, size);
                size++;
            }
            _points = current;
        }
        else
        {
            var sampled = new Vector2[newGridSize * newGridSize];
            float step = 1.0f / (newGridSize - 1);
            for (int row = 0; row < newGridSize; row++)
            {
                for (int column = 0; column < newGridSize; column++)
                    sampled[row * newGridSize + column] =
                        Bezier.Evaluate(_points, GridSize, column * step, row * step);
            }
            _points = sampled;
        }
        GridSize = newGridSize;
    }

    /// <summary>주어진 정규화 좌표에서 가장 가까운 제어점 인덱스. 반경 밖이면 -1.</summary>
    public int HitTest(Vector2 normalizedPoint, float radius)
    {
        int best = -1;
        float bestDistanceSquared = radius * radius;
        for (int i = 0; i < _points.Length; i++)
        {
            float distanceSquared = Vector2.DistanceSquared(_points[i], normalizedPoint);
            if (distanceSquared <= bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                best = i;
            }
        }
        return best;
    }

    public ControlPointGrid Clone() => new(GridSize, (Vector2[])_points.Clone());

    public float[] ToFlatArray()
    {
        var flat = new float[_points.Length * 2];
        for (int i = 0; i < _points.Length; i++)
        {
            flat[i * 2] = _points[i].X;
            flat[i * 2 + 1] = _points[i].Y;
        }
        return flat;
    }

    public static ControlPointGrid FromFlatArray(int gridSize, IReadOnlyList<float> flat)
    {
        gridSize = ClampGridSize(gridSize);
        var grid = new ControlPointGrid(gridSize);
        int expected = gridSize * gridSize * 2;
        if (flat.Count != expected) return grid; // 손상된 데이터는 기본 격자로 대체
        for (int i = 0; i < gridSize * gridSize; i++)
            grid._points[i] = new Vector2(flat[i * 2], flat[i * 2 + 1]);
        return grid;
    }
}
