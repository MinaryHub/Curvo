using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ProjectorWarp.Capture;
using ProjectorWarp.Interop;

namespace ProjectorWarp.Rendering;

internal enum OutputMouseButton
{
    Left,
    Right,
}

/// <summary>
/// 프로젝터로 내보내는 borderless 출력 창.
/// WPF 와 렌더링 경로가 충돌하지 않도록 순수 Win32 창 + DXGI 플립 모델 스왑체인을 사용한다.
/// </summary>
internal sealed class OutputWindow : IDisposable
{
    private static ushort _classAtom;
    private static Win32.WndProcDelegate? _windowProcedure; // GC 방지를 위해 정적으로 보관
    private static readonly Dictionary<IntPtr, OutputWindow> Instances = new();

    private readonly GraphicsDevice _graphics;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _renderTargetView;
    private IntPtr _handle;
    private bool _disposed;
    private bool _isFullscreen = true;
    private MonitorInfo _monitor;

    public OutputWindow(GraphicsDevice graphics, MonitorInfo monitor)
    {
        _graphics = graphics;
        _monitor = monitor;
        EnsureWindowClass();
        CreateNativeWindow();
        CreateSwapChain();
    }

    public event Action<int>? KeyDown;

    public event Action<Vector2, OutputMouseButton>? MouseDown;

    public event Action<Vector2>? MouseMove;

    public event Action<Vector2, OutputMouseButton>? MouseUp;

    public event Action? SizeChanged;

    public event Action? Closed;

    public IntPtr Handle => _handle;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool IsFullscreen => _isFullscreen;

    public bool IsTopmost { get; private set; } = true;

    /// <summary>WDA_EXCLUDEFROMCAPTURE 적용 성공 여부. 실패 시 피드백 루프 경고가 필요하다.</summary>
    public bool IsExcludedFromCapture { get; private set; }

    public MonitorInfo Monitor => _monitor;

    public ID3D11RenderTargetView? RenderTargetView => _renderTargetView;

    private static void EnsureWindowClass()
    {
        if (_classAtom != 0) return;

        _windowProcedure = StaticWindowProcedure;
        IntPtr classNamePointer = Marshal.StringToHGlobalUni(AppConfig.OutputWindowClassName);
        var windowClass = new Win32.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEXW>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
            hInstance = Win32.GetModuleHandleW(null),
            hCursor = Win32.LoadCursorW(IntPtr.Zero, new IntPtr(Win32.IDC_ARROW)),
            hbrBackground = IntPtr.Zero,
            lpszClassName = classNamePointer,
        };

        _classAtom = Win32.RegisterClassExW(ref windowClass);
        if (_classAtom == 0)
            throw new InvalidOperationException($"출력 창 클래스 등록에 실패했습니다. (오류 {Marshal.GetLastWin32Error()})");
    }

    private void CreateNativeWindow()
    {
        Win32.RECT bounds = _monitor.Bounds;
        _handle = Win32.CreateWindowExW(
            Win32.WS_EX_TOPMOST,
            new IntPtr(_classAtom),
            AppConfig.OutputWindowTitle,
            Win32.WS_POPUP,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandleW(null), IntPtr.Zero);

        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException($"출력 창 생성에 실패했습니다. (오류 {Marshal.GetLastWin32Error()})");

        Instances[_handle] = this;
        Width = bounds.Width;
        Height = bounds.Height;

        // 모니터 캡처 시 무한 반사를 막기 위해 출력 창을 캡처 대상에서 제외한다.
        IsExcludedFromCapture = Win32.SetWindowDisplayAffinity(_handle, Win32.WDA_EXCLUDEFROMCAPTURE);

        Win32.ShowWindow(_handle, Win32.SW_SHOW);
        Win32.SetForegroundWindow(_handle);
        Win32.SetFocus(_handle);
    }

    private void CreateSwapChain()
    {
        var description = new SwapChainDescription1
        {
            Width = (uint)Math.Max(1, Width),
            Height = (uint)Math.Max(1, Height),
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };

        _swapChain = _graphics.Factory.CreateSwapChainForHwnd(_graphics.Device, _handle, description, null, null);
        // Alt+Enter 전체화면 전환은 직접 제어하므로 DXGI 자동 처리를 끈다.
        _graphics.Factory.MakeWindowAssociation(_handle, WindowAssociationFlags.IgnoreAltEnter);
        CreateRenderTarget();
    }

    private void CreateRenderTarget()
    {
        if (_swapChain is null) return;
        using ID3D11Texture2D backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTargetView = _graphics.Device.CreateRenderTargetView(backBuffer);
    }

    public void Present(bool verticalSync)
    {
        _swapChain?.Present(verticalSync ? 1u : 0u, PresentFlags.None);
    }

    private void Resize(int width, int height)
    {
        if (_swapChain is null || width <= 0 || height <= 0) return;
        if (width == Width && height == Height && _renderTargetView is not null) return;

        lock (_graphics.RenderLock)
        {
            Width = width;
            Height = height;

            _graphics.Context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
            _renderTargetView?.Dispose();
            _renderTargetView = null;

            _swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None);
            CreateRenderTarget();
        }
        SizeChanged?.Invoke();
    }

    /// <summary>전체화면 / 창 모드 전환. 창 모드는 전체화면에서 빠져나올 때의 안전장치이다.</summary>
    public void ToggleFullscreen()
    {
        Win32.RECT bounds = _monitor.Bounds;
        if (_isFullscreen)
        {
            int width = Math.Min(AppConfig.WindowedOutputWidth, bounds.Width);
            int height = Math.Min(AppConfig.WindowedOutputHeight, bounds.Height);
            int x = bounds.Left + (bounds.Width - width) / 2;
            int y = bounds.Top + (bounds.Height - height) / 2;
            Win32.SetWindowPos(_handle, IntPtr.Zero, x, y, width, height, Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED);
            _isFullscreen = false;
        }
        else
        {
            Win32.SetWindowPos(_handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED);
            _isFullscreen = true;
        }
    }

    public void SetTopmost(bool topmost)
    {
        IsTopmost = topmost;
        Win32.SetWindowPos(_handle, topmost ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    /// <summary>다른 모니터로 출력 창을 옮긴다.</summary>
    public void MoveToMonitor(MonitorInfo monitor)
    {
        _monitor = monitor;
        Win32.RECT bounds = monitor.Bounds;
        _isFullscreen = true;
        Win32.SetWindowPos(_handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED);
    }

    public void Focus()
    {
        Win32.SetForegroundWindow(_handle);
        Win32.SetFocus(_handle);
    }

    private static IntPtr StaticWindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (Instances.TryGetValue(hWnd, out OutputWindow? window))
            return window.WindowProcedure(hWnd, message, wParam, lParam);
        return Win32.DefWindowProcW(hWnd, message, wParam, lParam);
    }

    private IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case Win32.WM_SIZE:
                Resize(Win32.LoWord(lParam), Win32.HiWord(lParam));
                return IntPtr.Zero;

            case Win32.WM_ERASEBKGND:
                return new IntPtr(1); // 배경 지우기를 막아 깜빡임을 없앤다.

            case Win32.WM_KEYDOWN:
                KeyDown?.Invoke((int)wParam);
                return IntPtr.Zero;

            case Win32.WM_LBUTTONDOWN:
                Win32.SetCapture(hWnd);
                MouseDown?.Invoke(ToNormalized(lParam), OutputMouseButton.Left);
                return IntPtr.Zero;

            case Win32.WM_RBUTTONDOWN:
                MouseDown?.Invoke(ToNormalized(lParam), OutputMouseButton.Right);
                return IntPtr.Zero;

            case Win32.WM_MOUSEMOVE:
                MouseMove?.Invoke(ToNormalized(lParam));
                return IntPtr.Zero;

            case Win32.WM_LBUTTONUP:
                Win32.ReleaseCapture();
                MouseUp?.Invoke(ToNormalized(lParam), OutputMouseButton.Left);
                return IntPtr.Zero;

            case Win32.WM_RBUTTONUP:
                MouseUp?.Invoke(ToNormalized(lParam), OutputMouseButton.Right);
                return IntPtr.Zero;

            case Win32.WM_CLOSE:
                Closed?.Invoke();
                return IntPtr.Zero;

            case Win32.WM_DESTROY:
                Instances.Remove(hWnd);
                return IntPtr.Zero;
        }
        return Win32.DefWindowProcW(hWnd, message, wParam, lParam);
    }

    private Vector2 ToNormalized(IntPtr lParam)
    {
        float x = Win32.LoWord(lParam) / (float)Math.Max(1, Width);
        float y = Win32.HiWord(lParam) / (float)Math.Max(1, Height);
        return new Vector2(x, y);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_graphics.RenderLock)
        {
            _renderTargetView?.Dispose();
            _renderTargetView = null;
            _swapChain?.Dispose();
            _swapChain = null;
        }

        if (_handle != IntPtr.Zero)
        {
            Instances.Remove(_handle);
            Win32.DestroyWindow(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
