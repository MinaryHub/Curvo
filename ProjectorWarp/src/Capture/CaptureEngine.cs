using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D11;
using Curvo.Interop;

namespace Curvo.Capture;

/// <summary>
/// Windows.Graphics.Capture 래퍼. 캡처 텍스처는 GPU 에 상주한 채로 전달된다.
/// FrameArrived 는 캡처 스레드에서 호출되므로 구독자가 동기화를 책임진다.
/// </summary>
internal sealed class CaptureEngine : IDisposable
{
    private const string CaptureSessionTypeName = "Windows.Graphics.Capture.GraphicsCaptureSession";

    private readonly IDirect3DDevice _winrtDevice;
    private readonly object _sync = new();

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private bool _disposed;

    public CaptureEngine(IDirect3DDevice winrtDevice) => _winrtDevice = winrtDevice;

    /// <summary>새 프레임 도착. (캡처 텍스처, 실제 콘텐츠 크기)</summary>
    public event Action<ID3D11Texture2D, SizeInt32>? FrameArrived;

    /// <summary>캡처 대상이 닫혔을 때.</summary>
    public event Action? SourceClosed;

    /// <summary>캡처 중 발생한 오류(재시작 필요).</summary>
    public event Action<Exception>? CaptureFailed;

    /// <summary>소스 크기 변경으로 프레임 풀을 다시 만들었을 때. 캐시된 텍스처 뷰를 버려야 한다.</summary>
    public event Action? FramePoolRecreated;

    public bool IsCapturing { get; private set; }

    public CaptureTarget? CurrentTarget { get; private set; }

    public static bool IsSupported
    {
        get
        {
            try
            {
                return GraphicsCaptureSession.IsSupported();
            }
            catch
            {
                return false;
            }
        }
    }

    public void Start(CaptureTarget target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();

        GraphicsCaptureItem item = target.Kind == CaptureSourceKind.Window
            ? WinRTInterop.CreateItemForWindow(target.Handle)
            : WinRTInterop.CreateItemForMonitor(target.Handle);

        lock (_sync)
        {
            _item = item;
            _poolSize = item.Size;
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, AppConfig.FramePoolBufferCount, _poolSize);
            _session = _framePool.CreateCaptureSession(item);

            ApplyOptionalSessionSettings(_session);

            _framePool.FrameArrived += OnFrameArrived;
            _item.Closed += OnItemClosed;
            _session.StartCapture();

            CurrentTarget = target;
            IsCapturing = true;
        }
    }

    /// <summary>OS 버전에 따라 존재하지 않을 수 있는 세션 옵션을 안전하게 적용한다.</summary>
    private static void ApplyOptionalSessionSettings(GraphicsCaptureSession session)
    {
        // 커서는 캡처하지 않는다(출력 화면의 커서와 이중 표시 방지).
        TrySet(nameof(GraphicsCaptureSession.IsCursorCaptureEnabled), () => session.IsCursorCaptureEnabled = false);
        // Windows 11(22000+): 노란 캡처 테두리 제거.
        TrySet(nameof(GraphicsCaptureSession.IsBorderRequired), () => session.IsBorderRequired = false);

        static void TrySet(string propertyName, Action apply)
        {
            try
            {
                if (!ApiInformation.IsPropertyPresent(CaptureSessionTypeName, propertyName)) return;
                apply();
            }
            catch
            {
                // 권한/버전 문제로 실패해도 캡처 자체는 계속 진행한다.
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            IsCapturing = false;
            CurrentTarget = null;

            if (_framePool is not null) _framePool.FrameArrived -= OnFrameArrived;
            if (_item is not null) _item.Closed -= OnItemClosed;

            _session?.Dispose();
            _framePool?.Dispose();
            _session = null;
            _framePool = null;
            _item = null;
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => SourceClosed?.Invoke();

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        bool needsRecreate = false;
        SizeInt32 contentSize;

        try
        {
            using Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
            if (frame is null) return;

            contentSize = frame.ContentSize;
            lock (_sync)
            {
                if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
                {
                    _poolSize = contentSize;
                    needsRecreate = true;
                }
            }

            using ID3D11Texture2D texture = WinRTInterop.GetTexture(frame.Surface);
            FrameArrived?.Invoke(texture, contentSize);
        }
        catch (Exception ex)
        {
            CaptureFailed?.Invoke(ex);
            return;
        }

        // 프레임을 반납한 뒤에 풀을 재생성해야 한다.
        if (!needsRecreate) return;

        bool recreated = false;
        try
        {
            lock (_sync)
            {
                if (_framePool is null || _poolSize.Width <= 0 || _poolSize.Height <= 0) return;
                _framePool.Recreate(
                    _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, AppConfig.FramePoolBufferCount, _poolSize);
                recreated = true;
            }
        }
        catch (Exception ex)
        {
            CaptureFailed?.Invoke(ex);
        }

        // 락 순서 역전을 피하기 위해 _sync 를 놓은 뒤에 알린다(구독자가 렌더 락을 잡는다).
        if (recreated) FramePoolRecreated?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
