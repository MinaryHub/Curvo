using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Curvo.Geometry;

namespace Curvo.Rendering;

/// <summary>워핑 셰이더 상수 버퍼 레이아웃(warp.hlsl 의 WarpConstants 와 일치해야 한다).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WarpConstants
{
    public Vector4 ColorParams;
    public Vector4 EdgeBlendWidth;
    public Vector4 EdgeBlendParams;
    public Vector4 PatternParams;
    public Vector4 SourceParams;

    public const int SizeInBytes = 80;
}

/// <summary>
/// 캡처 텍스처를 워핑 메시에 그려 출력 창 스왑체인으로 내보낸다.
/// 모든 처리는 GPU 에서 이뤄지며 CPU 로 픽셀을 내리지 않는다.
/// </summary>
internal sealed class WarpRenderer : IDisposable
{
    private const string WarpShaderFile = "warp.hlsl";
    private const string OverlayShaderFile = "overlay.hlsl";
    private const string VertexEntryPoint = "VSMain";
    private const string PixelEntryPoint = "PSMain";

    /// <summary>SRV 캐시 상한. 프레임 풀 재생성 시 오래된 항목을 정리한다.</summary>
    private const int MaxCachedSourceViews = 8;

    private static readonly Color4 ClearColor = new(0.0f, 0.0f, 0.0f, 1.0f);

    // 오버레이 색상
    private static readonly Vector4 ControlGridColor = new(0.15f, 0.85f, 1.0f, 0.75f);
    private static readonly Vector4 ReferenceGridColor = new(1.0f, 1.0f, 1.0f, 0.25f);
    private static readonly Vector4 DiagonalColor = new(1.0f, 0.85f, 0.2f, 0.6f);
    private static readonly Vector4 HandleColor = new(0.15f, 0.85f, 1.0f, 0.95f);
    private static readonly Vector4 SelectedHandleColor = new(1.0f, 0.35f, 0.2f, 1.0f);
    private static readonly Vector4 CornerHandleColor = new(0.4f, 1.0f, 0.4f, 0.95f);

    private readonly GraphicsDevice _graphics;
    private readonly OverlayBuilder _overlayBuilder = new();
    private readonly Dictionary<IntPtr, ID3D11ShaderResourceView> _sourceViews = new();

    private ID3D11VertexShader _warpVertexShader = null!;
    private ID3D11PixelShader _warpPixelShader = null!;
    private ID3D11InputLayout _warpInputLayout = null!;
    private ID3D11VertexShader _overlayVertexShader = null!;
    private ID3D11PixelShader _overlayPixelShader = null!;
    private ID3D11InputLayout _overlayInputLayout = null!;

    private ID3D11Buffer _constantBuffer = null!;
    private ID3D11SamplerState _sampler = null!;
    private ID3D11BlendState _opaqueBlend = null!;
    private ID3D11BlendState _alphaBlend = null!;
    private ID3D11RasterizerState _rasterizer = null!;

    private ID3D11Buffer? _warpVertexBuffer;
    private ID3D11Buffer? _warpIndexBuffer;
    private ID3D11Buffer? _overlayVertexBuffer;
    private int _overlayVertexCapacity;
    private int _meshTessellation = -1;
    private int _indexCount;
    private WarpVertex[] _meshVertices = Array.Empty<WarpVertex>();
    private bool _meshDirty = true;

    private ID3D11ShaderResourceView? _currentSourceView;
    private Vector2 _sourceUvScale = Vector2.One;
    private bool _disposed;

    public WarpRenderer(GraphicsDevice graphics)
    {
        _graphics = graphics;
        CreateShaders();
        CreateStates();
    }

    /// <summary>제어점/설정이 바뀌어 메시를 다시 만들어야 함을 표시한다.</summary>
    public void InvalidateMesh() => _meshDirty = true;

    private void CreateShaders()
    {
        ID3D11Device device = _graphics.Device;

        ReadOnlyMemory<byte> warpVertexCode = ShaderLoader.CompileVertexShader(WarpShaderFile, VertexEntryPoint);
        ReadOnlyMemory<byte> warpPixelCode = ShaderLoader.CompilePixelShader(WarpShaderFile, PixelEntryPoint);
        _warpVertexShader = device.CreateVertexShader(warpVertexCode.Span);
        _warpPixelShader = device.CreatePixelShader(warpPixelCode.Span);
        _warpInputLayout = device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32B32_Float, 8, 0),
        }, warpVertexCode.Span);

        ReadOnlyMemory<byte> overlayVertexCode = ShaderLoader.CompileVertexShader(OverlayShaderFile, VertexEntryPoint);
        ReadOnlyMemory<byte> overlayPixelCode = ShaderLoader.CompilePixelShader(OverlayShaderFile, PixelEntryPoint);
        _overlayVertexShader = device.CreateVertexShader(overlayVertexCode.Span);
        _overlayPixelShader = device.CreatePixelShader(overlayPixelCode.Span);
        _overlayInputLayout = device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 8, 0),
        }, overlayVertexCode.Span);
    }

    private void CreateStates()
    {
        ID3D11Device device = _graphics.Device;

        _constantBuffer = device.CreateBuffer(new BufferDescription(
            WarpConstants.SizeInBytes, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

        _sampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
        _opaqueBlend = device.CreateBlendState(BlendDescription.Opaque);
        _alphaBlend = device.CreateBlendState(BlendDescription.NonPremultiplied);
        _rasterizer = device.CreateRasterizerState(RasterizerDescription.CullNone);
    }

    /// <summary>새 캡처 프레임을 셰이더 리소스로 등록한다(GPU 복사 없음).</summary>
    public void SetSourceTexture(ID3D11Texture2D texture, SizeInt32 contentSize)
    {
        IntPtr key = texture.NativePointer;
        if (!_sourceViews.TryGetValue(key, out ID3D11ShaderResourceView? view))
        {
            if (_sourceViews.Count >= MaxCachedSourceViews) ClearSourceViews();
            view = _graphics.Device.CreateShaderResourceView(texture);
            _sourceViews[key] = view;
        }

        Texture2DDescription description = texture.Description;
        float scaleX = description.Width > 0 ? contentSize.Width / (float)description.Width : 1.0f;
        float scaleY = description.Height > 0 ? contentSize.Height / (float)description.Height : 1.0f;

        _currentSourceView = view;
        _sourceUvScale = new Vector2(scaleX, scaleY);
    }

    public void ClearSource()
    {
        _currentSourceView = null;
        ClearSourceViews();
    }

    private void ClearSourceViews()
    {
        foreach (ID3D11ShaderResourceView view in _sourceViews.Values) view.Dispose();
        _sourceViews.Clear();
        _currentSourceView = null;
    }

    /// <summary>
    /// 한 프레임을 그린다. 호출자는 GraphicsDevice.RenderLock 을 잡고 있어야 한다.
    /// <para>
    /// Present 는 하지 않는다. vsync Present 는 최대 한 프레임(약 16ms) 동안 블록되므로
    /// 렌더 락을 쥔 채로 하면 캡처 스레드와 UI 스레드가 그만큼 함께 멈춘다.
    /// 호출자가 락을 놓은 뒤 <see cref="OutputWindow.Present"/> 를 부른다.
    /// </para>
    /// </summary>
    public void Render(OutputWindow window, WarpSettings settings, OverlayState overlay)
    {
        ID3D11RenderTargetView? renderTarget = window.RenderTargetView;
        if (renderTarget is null || window.Width <= 0 || window.Height <= 0) return;

        ID3D11DeviceContext context = _graphics.Context;
        context.OMSetRenderTargets(renderTarget);
        context.RSSetViewport(0, 0, window.Width, window.Height);
        context.ClearRenderTargetView(renderTarget, ClearColor);

        DrawWarpedSource(context, settings, window);

        if (overlay.EditMode)
            DrawOverlay(context, settings, overlay, window);

        RenderCount++;
    }

    private void DrawWarpedSource(ID3D11DeviceContext context, WarpSettings settings, OutputWindow window)
    {
        // 소스가 없어도 풀필드 테스트 패턴은 그려야 정렬 작업이 가능하다.
        if (_currentSourceView is null && CurrentPattern == TestPattern.None) return;

        EnsureMesh(settings);
        if (_warpVertexBuffer is null || _warpIndexBuffer is null) return;

        UpdateConstants(context, settings, window);

        context.IASetInputLayout(_warpInputLayout);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetVertexBuffer(0, _warpVertexBuffer, WarpVertex.SizeInBytes);
        context.IASetIndexBuffer(_warpIndexBuffer, Format.R16_UInt, 0);
        context.VSSetShader(_warpVertexShader);
        context.PSSetShader(_warpPixelShader);
        context.PSSetConstantBuffer(0, _constantBuffer);
        context.PSSetSampler(0, _sampler);
        context.PSSetShaderResource(0, _currentSourceView!);
        context.OMSetBlendState(_opaqueBlend);
        context.RSSetState(_rasterizer);
        context.DrawIndexed((uint)_indexCount, 0, 0);

        // 다음 패스에서 리소스 바인딩 충돌이 나지 않도록 해제한다.
        context.PSSetShaderResource(0, null!);
    }

    private void DrawOverlay(ID3D11DeviceContext context, WarpSettings settings, OverlayState overlay, OutputWindow window)
    {
        _overlayBuilder.Begin(window.Width, window.Height);
        BuildOverlayGeometry(settings, overlay);
        DrawOverlayBuffer(context, _alphaBlend);
    }

    private void BuildOverlayGeometry(WarpSettings settings, OverlayState overlay)
    {
        float lineWidth = AppConfig.OverlayLineWidthPixels;
        // 호모그래피는 8x8 가우스 소거로 만들어진다. 점마다 다시 만들면
        // 6x6 격자에서 프레임당 150회를 넘으므로 한 번만 만들어 재사용한다.
        Homography homography = settings.BuildHomography();

        if (overlay.ShowReferenceGrid)
        {
            int divisions = AppConfig.ReferenceGridDivisions;
            for (int i = 0; i <= divisions; i++)
            {
                float t = i / (float)divisions;
                _overlayBuilder.AddLine(new Vector2(t, 0), new Vector2(t, 1), ReferenceGridColor, lineWidth);
                _overlayBuilder.AddLine(new Vector2(0, t), new Vector2(1, t), ReferenceGridColor, lineWidth);
            }
        }

        if (overlay.ShowDiagonals)
        {
            _overlayBuilder.AddLine(new Vector2(0, 0), new Vector2(1, 1), DiagonalColor, lineWidth);
            _overlayBuilder.AddLine(new Vector2(1, 0), new Vector2(0, 1), DiagonalColor, lineWidth);
        }

        if (settings.CornerPinEnabled)
        {
            // 코너 핸들은 출력 좌표 그 자체이므로 변환하지 않는다.
            for (int i = 0; i < settings.CornerPoints.Length; i++)
            {
                Vector2 current = settings.CornerPoints[i];
                Vector2 next = settings.CornerPoints[(i + 1) % settings.CornerPoints.Length];
                _overlayBuilder.AddLine(current, next, CornerHandleColor, lineWidth);
                _overlayBuilder.AddHandle(current,
                    overlay.SelectedCorner == i ? SelectedHandleColor : CornerHandleColor,
                    AppConfig.HandleRadiusPixels * 1.1f);
            }
        }

        if (!settings.BezierEnabled || !overlay.ShowControlGrid) return;

        ControlPointGrid grid = settings.Grid;
        int size = grid.GridSize;

        bool cornerPin = settings.CornerPinEnabled;

        for (int row = 0; row < size; row++)
        {
            for (int column = 0; column < size - 1; column++)
                _overlayBuilder.AddLine(ToOutput(homography, cornerPin, grid.Get(column, row)),
                    ToOutput(homography, cornerPin, grid.Get(column + 1, row)), ControlGridColor, lineWidth);
        }
        for (int column = 0; column < size; column++)
        {
            for (int row = 0; row < size - 1; row++)
                _overlayBuilder.AddLine(ToOutput(homography, cornerPin, grid.Get(column, row)),
                    ToOutput(homography, cornerPin, grid.Get(column, row + 1)), ControlGridColor, lineWidth);
        }

        for (int i = 0; i < grid.Count; i++)
        {
            _overlayBuilder.AddHandle(ToOutput(homography, cornerPin, grid[i]),
                overlay.SelectedControlPoint == i ? SelectedHandleColor : HandleColor,
                AppConfig.HandleRadiusPixels);
        }
    }

    /// <summary>제어점(워핑 공간)을 실제 출력 좌표로 변환한다. 코너 핀이 켜져 있으면 함께 적용한다.</summary>
    private static Vector2 ToOutput(in Homography homography, bool cornerPinEnabled, Vector2 point)
    {
        if (!cornerPinEnabled) return point;
        Vector3 projected = homography.TransformHomogeneous(point);
        float w = MathF.Abs(projected.Z) < 1e-6f ? 1e-6f : projected.Z;
        return new Vector2(projected.X / w, projected.Y / w);
    }

    private void DrawOverlayBuffer(ID3D11DeviceContext context, ID3D11BlendState blendState)
    {
        int vertexCount = _overlayBuilder.Count;
        if (vertexCount == 0) return;

        EnsureOverlayBuffer(vertexCount);
        if (_overlayVertexBuffer is null) return;

        WriteBuffer(context, _overlayVertexBuffer, _overlayBuilder.Vertices, vertexCount);

        context.IASetInputLayout(_overlayInputLayout);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetVertexBuffer(0, _overlayVertexBuffer, OverlayVertex.SizeInBytes);
        context.VSSetShader(_overlayVertexShader);
        context.PSSetShader(_overlayPixelShader);
        context.OMSetBlendState(blendState);
        context.RSSetState(_rasterizer);
        context.Draw((uint)vertexCount, 0);
    }

    private static void WriteBuffer(ID3D11DeviceContext context, ID3D11Buffer buffer, IReadOnlyList<OverlayVertex> source, int count)
    {
        MappedSubresource mapped = context.Map(buffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        unsafe
        {
            var destination = new Span<OverlayVertex>(mapped.DataPointer.ToPointer(), count);
            for (int i = 0; i < count; i++) destination[i] = source[i];
        }
        context.Unmap(buffer, 0);
    }

    private void EnsureOverlayBuffer(int vertexCount)
    {
        if (_overlayVertexBuffer is not null && _overlayVertexCapacity >= vertexCount) return;

        _overlayVertexBuffer?.Dispose();
        _overlayVertexCapacity = Math.Max(vertexCount * 2, 1024);
        _overlayVertexBuffer = _graphics.Device.CreateBuffer(new BufferDescription(
            (uint)(_overlayVertexCapacity * OverlayVertex.SizeInBytes),
            BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
    }

    private void EnsureMesh(WarpSettings settings)
    {
        int tessellation = settings.Tessellation;
        if (tessellation != _meshTessellation)
        {
            _warpVertexBuffer?.Dispose();
            _warpIndexBuffer?.Dispose();

            int vertexCount = MeshBuilder.VertexCount(tessellation);
            _meshVertices = new WarpVertex[vertexCount];
            _warpVertexBuffer = _graphics.Device.CreateBuffer(new BufferDescription(
                (uint)(vertexCount * WarpVertex.SizeInBytes),
                BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

            ushort[] indices = MeshBuilder.BuildIndices(tessellation);
            _indexCount = indices.Length;
            _warpIndexBuffer = _graphics.Device.CreateBuffer(indices, BindFlags.IndexBuffer);

            _meshTessellation = tessellation;
            _meshDirty = true;
        }

        if (!_meshDirty || _warpVertexBuffer is null) return;

        MeshBuilder.BuildVertices(settings, _meshVertices);

        ID3D11DeviceContext context = _graphics.Context;
        MappedSubresource mapped = context.Map(_warpVertexBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        unsafe
        {
            var destination = new Span<WarpVertex>(mapped.DataPointer.ToPointer(), _meshVertices.Length);
            _meshVertices.AsSpan().CopyTo(destination);
        }
        context.Unmap(_warpVertexBuffer, 0);
        _meshDirty = false;
    }

    private void UpdateConstants(ID3D11DeviceContext context, WarpSettings settings, OutputWindow window)
    {
        float aspect = window.Height > 0 ? window.Width / (float)window.Height : 1.0f;

        var constants = new WarpConstants
        {
            ColorParams = new Vector4(settings.Brightness, settings.Contrast, settings.Gamma,
                settings.ColorEnabled ? 1.0f : 0.0f),
            EdgeBlendWidth = new Vector4(settings.EdgeBlendLeft, settings.EdgeBlendRight,
                settings.EdgeBlendTop, settings.EdgeBlendBottom),
            EdgeBlendParams = new Vector4(settings.EdgeBlendGamma, settings.EdgeBlendEnabled ? 1.0f : 0.0f, 0, 0),
            PatternParams = new Vector4((float)CurrentPattern, AppConfig.TestPatternGridDivisions,
                AppConfig.TestPatternCheckerDivisions, AppConfig.TestPatternRingCount),
            SourceParams = new Vector4(_sourceUvScale.X, _sourceUvScale.Y, aspect, 0),
        };

        MappedSubresource mapped = context.Map(_constantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        unsafe
        {
            *(WarpConstants*)mapped.DataPointer.ToPointer() = constants;
        }
        context.Unmap(_constantBuffer, 0);
    }

    /// <summary>렌더 직전에 설정되는 현재 테스트 패턴.</summary>
    public TestPattern CurrentPattern { get; set; } = TestPattern.None;

    /// <summary>지금까지 그린 프레임 수. 불필요한 재렌더가 없는지 검증할 때 쓴다.</summary>
    public long RenderCount { get; private set; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ClearSourceViews();
        _warpVertexBuffer?.Dispose();
        _warpIndexBuffer?.Dispose();
        _overlayVertexBuffer?.Dispose();
        _constantBuffer.Dispose();
        _sampler.Dispose();
        _opaqueBlend.Dispose();
        _alphaBlend.Dispose();
        _rasterizer.Dispose();
        _warpInputLayout.Dispose();
        _warpVertexShader.Dispose();
        _warpPixelShader.Dispose();
        _overlayInputLayout.Dispose();
        _overlayVertexShader.Dispose();
        _overlayPixelShader.Dispose();
    }
}
