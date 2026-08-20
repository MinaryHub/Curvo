using System.Numerics;

namespace Curvo.Geometry;

/// <summary>
/// 단위 정사각형 (0,0)-(1,0)-(1,1)-(0,1) 을 임의의 사각형으로 보내는 3x3 호모그래피.
/// 코너 핀(키스톤) 보정에 사용한다.
/// </summary>
internal readonly struct Homography
{
    // 행 우선 3x3. 생성 후 절대 수정하지 않는다(Identity 가 배열을 공유한다).
    private readonly float[] _m;

    private static readonly float[] IdentityMatrix = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

    /// <summary>단위 변환. 배열을 공유하므로 호출마다 할당하지 않는다.</summary>
    public static Homography Identity => new(IdentityMatrix);

    private Homography(float[] m) => _m = m;

    /// <summary>
    /// 코너 4점(좌상, 우상, 우하, 좌하)으로 호모그래피를 계산한다.
    /// h33 = 1 로 고정하고 8x8 선형계를 가우스 소거로 푼다.
    /// </summary>
    public static Homography FromUnitSquare(Vector2 topLeft, Vector2 topRight, Vector2 bottomRight, Vector2 bottomLeft)
    {
        Span<Vector2> source = stackalloc Vector2[4]
        {
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1)
        };
        Span<Vector2> target = stackalloc Vector2[4] { topLeft, topRight, bottomRight, bottomLeft };

        // A * h = b, h = [h11 h12 h13 h21 h22 h23 h31 h32]
        Span<float> a = stackalloc float[8 * 9]; // 확대 행렬 (마지막 열이 b)
        for (int k = 0; k < 4; k++)
        {
            float x = source[k].X, y = source[k].Y;
            float u = target[k].X, v = target[k].Y;

            int r0 = (k * 2) * 9;
            a[r0 + 0] = x; a[r0 + 1] = y; a[r0 + 2] = 1;
            a[r0 + 3] = 0; a[r0 + 4] = 0; a[r0 + 5] = 0;
            a[r0 + 6] = -x * u; a[r0 + 7] = -y * u; a[r0 + 8] = u;

            int r1 = (k * 2 + 1) * 9;
            a[r1 + 0] = 0; a[r1 + 1] = 0; a[r1 + 2] = 0;
            a[r1 + 3] = x; a[r1 + 4] = y; a[r1 + 5] = 1;
            a[r1 + 6] = -x * v; a[r1 + 7] = -y * v; a[r1 + 8] = v;
        }

        Span<float> h = stackalloc float[8];
        if (!SolveGauss(a, 8, h))
            return Identity; // 퇴화된 사각형이면 보정을 적용하지 않는다.

        return new Homography(new[]
        {
            h[0], h[1], h[2],
            h[3], h[4], h[5],
            h[6], h[7], 1.0f
        });
    }

    /// <summary>부분 피벗팅 가우스 소거. 실패(특이 행렬) 시 false.</summary>
    private static bool SolveGauss(Span<float> augmented, int n, Span<float> solution)
    {
        const float Epsilon = 1e-9f;
        int stride = n + 1;

        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            float best = MathF.Abs(augmented[col * stride + col]);
            for (int row = col + 1; row < n; row++)
            {
                float value = MathF.Abs(augmented[row * stride + col]);
                if (value > best) { best = value; pivot = row; }
            }
            if (best < Epsilon) return false;

            if (pivot != col)
            {
                for (int k = col; k < stride; k++)
                    (augmented[col * stride + k], augmented[pivot * stride + k]) =
                        (augmented[pivot * stride + k], augmented[col * stride + k]);
            }

            float diagonal = augmented[col * stride + col];
            for (int row = col + 1; row < n; row++)
            {
                float factor = augmented[row * stride + col] / diagonal;
                if (factor == 0.0f) continue;
                for (int k = col; k < stride; k++)
                    augmented[row * stride + k] -= factor * augmented[col * stride + k];
            }
        }

        for (int row = n - 1; row >= 0; row--)
        {
            float sum = augmented[row * stride + n];
            for (int k = row + 1; k < n; k++)
                sum -= augmented[row * stride + k] * solution[k];
            solution[row] = sum / augmented[row * stride + row];
        }
        return true;
    }

    /// <summary>동차 좌표로 변환한다. 반환값 z 가 원근 나눗셈용 w 이다.</summary>
    public Vector3 TransformHomogeneous(Vector2 point)
    {
        if (_m is null) return new Vector3(point.X, point.Y, 1.0f);
        float x = _m[0] * point.X + _m[1] * point.Y + _m[2];
        float y = _m[3] * point.X + _m[4] * point.Y + _m[5];
        float w = _m[6] * point.X + _m[7] * point.Y + _m[8];
        return new Vector3(x, y, w);
    }

    /// <summary>원근 나눗셈까지 적용한 2D 변환.</summary>
    public Vector2 Transform(Vector2 point)
    {
        Vector3 projected = TransformHomogeneous(point);
        float w = MathF.Abs(projected.Z) < 1e-6f ? 1e-6f : projected.Z;
        return new Vector2(projected.X / w, projected.Y / w);
    }

    /// <summary>역변환 행렬. 출력 좌표를 워핑 좌표로 되돌릴 때 사용한다.</summary>
    public Homography Invert()
    {
        if (_m is null) return Identity;

        float a = _m[0], b = _m[1], c = _m[2];
        float d = _m[3], e = _m[4], f = _m[5];
        float g = _m[6], h = _m[7], i = _m[8];

        float cofactor00 = e * i - f * h;
        float cofactor01 = -(d * i - f * g);
        float cofactor02 = d * h - e * g;

        float determinant = a * cofactor00 + b * cofactor01 + c * cofactor02;
        if (MathF.Abs(determinant) < 1e-12f) return Identity;

        float inverse = 1.0f / determinant;
        return new Homography(new[]
        {
            cofactor00 * inverse, (c * h - b * i) * inverse, (b * f - c * e) * inverse,
            cofactor01 * inverse, (a * i - c * g) * inverse, (c * d - a * f) * inverse,
            cofactor02 * inverse, (b * g - a * h) * inverse, (a * e - b * d) * inverse,
        });
    }
}
