using System.Numerics;

namespace ProjectorWarp.Geometry;

/// <summary>
/// 텐서곱 베지어 곡면 계산.
/// S(u,v) = sum_i sum_j B_i^n(u) * B_j^n(v) * P_ij
/// </summary>
internal static class Bezier
{
    /// <summary>번스타인 기저 다항식 B_i^n(t) 를 basis[0..n] 에 채운다.</summary>
    public static void ComputeBasis(int degree, float t, Span<float> basis)
    {
        if (basis.Length < degree + 1)
            throw new ArgumentException("기저 배열 크기가 차수보다 작습니다.", nameof(basis));

        // 드 카스텔죠 방식의 점화식으로 이항계수 누적 오차 없이 계산한다.
        basis[0] = 1.0f;
        float oneMinusT = 1.0f - t;
        for (int n = 1; n <= degree; n++)
        {
            float saved = 0.0f;
            for (int i = 0; i < n; i++)
            {
                float temp = basis[i];
                basis[i] = saved + oneMinusT * temp;
                saved = t * temp;
            }
            basis[n] = saved;
        }
    }

    /// <summary>제어점 격자(gridSize x gridSize)로 정의된 곡면 위의 점 S(u,v).</summary>
    public static Vector2 Evaluate(ReadOnlySpan<Vector2> control, int gridSize, float u, float v)
    {
        int degree = gridSize - 1;
        Span<float> basisU = stackalloc float[AppConfig.MaxGridSize];
        Span<float> basisV = stackalloc float[AppConfig.MaxGridSize];
        ComputeBasis(degree, u, basisU);
        ComputeBasis(degree, v, basisV);
        return Combine(control, gridSize, basisU, basisV);
    }

    /// <summary>
    /// 이미 구해 둔 기저로 텐서곱 합만 계산한다.
    /// 격자 전체를 훑을 때는 기저가 행/열마다 같으므로 이 형태로 재사용한다.
    /// </summary>
    public static Vector2 Combine(
        ReadOnlySpan<Vector2> control, int gridSize, ReadOnlySpan<float> basisU, ReadOnlySpan<float> basisV)
    {
        int degree = gridSize - 1;
        Vector2 result = Vector2.Zero;
        for (int j = 0; j <= degree; j++)
        {
            float bv = basisV[j];
            if (bv == 0.0f) continue;
            int rowOffset = j * gridSize;
            for (int i = 0; i <= degree; i++)
            {
                result += basisU[i] * bv * control[rowOffset + i];
            }
        }
        return result;
    }

    /// <summary>
    /// 차수 상승(degree elevation). 곡면 형상을 그대로 유지한 채 제어점을 한 단계 늘린다.
    /// </summary>
    public static Vector2[] ElevateDegree(ReadOnlySpan<Vector2> control, int gridSize)
    {
        int newSize = gridSize + 1;
        // 1) u 방향(각 행) 차수 상승
        var rowElevated = new Vector2[newSize * gridSize];
        Span<Vector2> line = stackalloc Vector2[AppConfig.MaxGridSize + 1];
        for (int j = 0; j < gridSize; j++)
        {
            ElevateLine(control.Slice(j * gridSize, gridSize), line[..newSize]);
            for (int i = 0; i < newSize; i++)
                rowElevated[j * newSize + i] = line[i];
        }

        // 2) v 방향(각 열) 차수 상승
        var result = new Vector2[newSize * newSize];
        Span<Vector2> column = stackalloc Vector2[AppConfig.MaxGridSize + 1];
        Span<Vector2> elevatedColumn = stackalloc Vector2[AppConfig.MaxGridSize + 1];
        for (int i = 0; i < newSize; i++)
        {
            for (int j = 0; j < gridSize; j++)
                column[j] = rowElevated[j * newSize + i];
            ElevateLine(column[..gridSize], elevatedColumn[..newSize]);
            for (int j = 0; j < newSize; j++)
                result[j * newSize + i] = elevatedColumn[j];
        }
        return result;
    }

    /// <summary>1차원 베지어 곡선의 차수를 1 올린다.</summary>
    private static void ElevateLine(ReadOnlySpan<Vector2> source, Span<Vector2> destination)
    {
        int n = source.Length - 1;
        destination[0] = source[0];
        destination[n + 1] = source[n];
        for (int i = 1; i <= n; i++)
        {
            float alpha = (float)i / (n + 1);
            destination[i] = alpha * source[i - 1] + (1.0f - alpha) * source[i];
        }
    }
}
