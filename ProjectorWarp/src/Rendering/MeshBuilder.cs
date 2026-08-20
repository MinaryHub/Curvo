using System.Numerics;
using System.Runtime.InteropServices;
using ProjectorWarp.Geometry;

namespace ProjectorWarp.Rendering;

/// <summary>워핑 메시 정점. 위치는 정규화 출력 좌표, 텍스처 좌표는 투영 좌표(u*w, v*w, w).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WarpVertex
{
    public Vector2 Position;
    public Vector3 TexCoord;

    public const int SizeInBytes = 20;
}

/// <summary>오버레이/마스크 정점.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OverlayVertex
{
    public Vector2 Position;
    public Vector4 Color;

    public const int SizeInBytes = 24;

    public OverlayVertex(Vector2 position, Vector4 color)
    {
        Position = position;
        Color = color;
    }
}

/// <summary>베지어 곡면 + 코너 핀을 적용한 테셀레이션 메시를 만든다.</summary>
internal static class MeshBuilder
{
    private const float MinimumHomogeneousW = 1e-6f;

    public static int VertexCount(int tessellation) => (tessellation + 1) * (tessellation + 1);

    public static int IndexCount(int tessellation) => tessellation * tessellation * 6;

    /// <summary>정점 위치와 텍스처 좌표를 계산해 destination 에 채운다.</summary>
    public static void BuildVertices(WarpSettings settings, Span<WarpVertex> destination)
    {
        int tessellation = settings.Tessellation;
        int side = tessellation + 1;
        float step = 1.0f / tessellation;

        Homography homography = settings.BuildHomography();
        bool useCornerPin = settings.CornerPinEnabled;
        bool useBezier = settings.BezierEnabled;
        ReadOnlySpan<Vector2> control = settings.Grid.Points;
        int gridSize = settings.Grid.GridSize;
        int degree = gridSize - 1;

        // u 는 열 인덱스, v 는 행 인덱스로만 결정되므로 번스타인 기저를 정점마다 다시 구하지 않고
        // 격자선마다 한 번씩만 구해 재사용한다(제어점을 드래그할 때 매번 도는 경로다).
        Span<float> basisTable = stackalloc float[(AppConfig.MaxTessellation + 1) * AppConfig.MaxGridSize];
        if (useBezier)
        {
            for (int i = 0; i < side; i++)
                Bezier.ComputeBasis(degree, i * step, basisTable.Slice(i * gridSize, gridSize));
        }

        for (int row = 0; row < side; row++)
        {
            float v = row * step;
            ReadOnlySpan<float> basisV = useBezier ? basisTable.Slice(row * gridSize, gridSize) : default;
            for (int column = 0; column < side; column++)
            {
                float u = column * step;

                Vector2 surfacePoint = useBezier
                    ? Bezier.Combine(control, gridSize, basisTable.Slice(column * gridSize, gridSize), basisV)
                    : new Vector2(u, v);

                Vector2 position;
                Vector3 texCoord;

                if (useCornerPin)
                {
                    Vector3 projected = homography.TransformHomogeneous(surfacePoint);
                    float w = MathF.Abs(projected.Z) < MinimumHomogeneousW ? MinimumHomogeneousW : projected.Z;
                    position = new Vector2(projected.X / w, projected.Y / w);
                    // 원근 보간 왜곡을 막기 위해 투영 좌표로 전달한다.
                    texCoord = new Vector3(u * w, v * w, w);
                }
                else
                {
                    position = surfacePoint;
                    texCoord = new Vector3(u, v, 1.0f);
                }

                destination[row * side + column] = new WarpVertex { Position = position, TexCoord = texCoord };
            }
        }
    }

    /// <summary>테셀레이션 격자의 삼각형 인덱스.</summary>
    public static ushort[] BuildIndices(int tessellation)
    {
        int side = tessellation + 1;
        var indices = new ushort[IndexCount(tessellation)];
        int cursor = 0;
        for (int row = 0; row < tessellation; row++)
        {
            for (int column = 0; column < tessellation; column++)
            {
                int topLeft = row * side + column;
                int topRight = topLeft + 1;
                int bottomLeft = topLeft + side;
                int bottomRight = bottomLeft + 1;

                indices[cursor++] = (ushort)topLeft;
                indices[cursor++] = (ushort)topRight;
                indices[cursor++] = (ushort)bottomLeft;

                indices[cursor++] = (ushort)topRight;
                indices[cursor++] = (ushort)bottomRight;
                indices[cursor++] = (ushort)bottomLeft;
            }
        }
        return indices;
    }
}

/// <summary>
/// 오버레이 도형을 삼각형 목록으로 쌓는다. 선 두께를 픽셀 단위로 유지하기 위해
/// 출력 해상도를 알고 있어야 한다.
/// </summary>
internal sealed class OverlayBuilder
{
    private readonly List<OverlayVertex> _vertices = new();
    private float _pixelWidth = 1.0f;
    private float _pixelHeight = 1.0f;

    public IReadOnlyList<OverlayVertex> Vertices => _vertices;

    public int Count => _vertices.Count;

    public void Begin(int outputWidth, int outputHeight)
    {
        _pixelWidth = Math.Max(1, outputWidth);
        _pixelHeight = Math.Max(1, outputHeight);
        _vertices.Clear();
    }

    public void AddLine(Vector2 start, Vector2 end, Vector4 color, float thicknessPixels)
    {
        Vector2 startPixels = ToPixels(start);
        Vector2 endPixels = ToPixels(end);
        Vector2 direction = endPixels - startPixels;
        float length = direction.Length();
        if (length < float.Epsilon) return;

        Vector2 normal = new Vector2(-direction.Y, direction.X) / length * (thicknessPixels * 0.5f);

        Vector2 a = ToNormalized(startPixels + normal);
        Vector2 b = ToNormalized(endPixels + normal);
        Vector2 c = ToNormalized(endPixels - normal);
        Vector2 d = ToNormalized(startPixels - normal);
        AddQuad(a, b, c, d, color);
    }

    /// <summary>정규화 좌표를 중심으로 하는 정사각형 핸들.</summary>
    public void AddHandle(Vector2 center, Vector4 color, float radiusPixels)
    {
        Vector2 centerPixels = ToPixels(center);
        Vector2 offset = new(radiusPixels, radiusPixels);
        Vector2 a = ToNormalized(centerPixels + new Vector2(-offset.X, -offset.Y));
        Vector2 b = ToNormalized(centerPixels + new Vector2(offset.X, -offset.Y));
        Vector2 c = ToNormalized(centerPixels + new Vector2(offset.X, offset.Y));
        Vector2 d = ToNormalized(centerPixels + new Vector2(-offset.X, offset.Y));
        AddQuad(a, b, c, d, color);
    }

    public void AddQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector4 color)
    {
        _vertices.Add(new OverlayVertex(a, color));
        _vertices.Add(new OverlayVertex(b, color));
        _vertices.Add(new OverlayVertex(c, color));
        _vertices.Add(new OverlayVertex(a, color));
        _vertices.Add(new OverlayVertex(c, color));
        _vertices.Add(new OverlayVertex(d, color));
    }

    /// <summary>단순 다각형을 삼각형 팬으로 채운다(볼록/약한 오목 다각형 대상).</summary>
    public void AddPolygon(IReadOnlyList<Vector2> polygon, Vector4 color)
    {
        if (polygon.Count < 3) return;
        for (int i = 1; i < polygon.Count - 1; i++)
        {
            _vertices.Add(new OverlayVertex(polygon[0], color));
            _vertices.Add(new OverlayVertex(polygon[i], color));
            _vertices.Add(new OverlayVertex(polygon[i + 1], color));
        }
    }

    private Vector2 ToPixels(Vector2 normalized) => new(normalized.X * _pixelWidth, normalized.Y * _pixelHeight);

    private Vector2 ToNormalized(Vector2 pixels) => new(pixels.X / _pixelWidth, pixels.Y / _pixelHeight);
}
