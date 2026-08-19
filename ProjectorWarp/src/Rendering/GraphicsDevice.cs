using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using ProjectorWarp.Interop;

namespace ProjectorWarp.Rendering;

/// <summary>
/// 캡처와 렌더링이 공유하는 단일 D3D11 디바이스.
/// 디바이스 로스트 시 이 객체를 버리고 새로 만든다.
/// </summary>
internal sealed class GraphicsDevice : IDisposable
{
    private static readonly FeatureLevel[] RequestedFeatureLevels =
    {
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    };

    private bool _disposed;

    private GraphicsDevice(ID3D11Device device, ID3D11DeviceContext context, IDXGIFactory2 factory, IDirect3DDevice winrtDevice)
    {
        Device = device;
        Context = context;
        Factory = factory;
        WinRTDevice = winrtDevice;
    }

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    public IDXGIFactory2 Factory { get; }

    /// <summary>WGC 프레임 풀에 넘길 WinRT 디바이스 래퍼.</summary>
    public IDirect3DDevice WinRTDevice { get; }

    /// <summary>즉시 컨텍스트는 스레드 안전하지 않으므로 모든 사용을 이 락으로 감싼다.</summary>
    public object RenderLock { get; } = new();

    /// <summary>디바이스가 VideoSupport 로 만들어졌는지(내부 동영상 재생 가능 여부).</summary>
    public static bool VideoSupportEnabled { get; private set; }

    public static GraphicsDevice Create()
    {
        // BgraSupport: WGC 프레임 포맷(BGRA)에 필요.
        // VideoSupport: Media Foundation 의 IMFDXGIDeviceManager.ResetDevice 에 필요(내부 동영상 재생).
        DeviceCreationFlags preferredFlags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        DeviceCreationFlags fallbackFlags = DeviceCreationFlags.BgraSupport;

        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        Result result = default;

        // (플래그, 드라이버) 조합을 순서대로 시도한다.
        (DeviceCreationFlags Flags, DriverType Driver)[] attempts =
        {
            (preferredFlags, DriverType.Hardware),
            (fallbackFlags, DriverType.Hardware),
            (preferredFlags, DriverType.Warp),
            (fallbackFlags, DriverType.Warp),
        };

        foreach ((DeviceCreationFlags flags, DriverType driver) in attempts)
        {
            result = D3D11.D3D11CreateDevice(
                IntPtr.Zero, driver, flags, RequestedFeatureLevels,
                out device, out FeatureLevel _, out context);
            if (result.Success && device is not null && context is not null)
            {
                VideoSupportEnabled = flags.HasFlag(DeviceCreationFlags.VideoSupport);
                break;
            }
            device?.Dispose();
            context?.Dispose();
            device = null;
            context = null;
        }

        if (device is null || context is null)
            throw new InvalidOperationException($"D3D11 디바이스를 만들지 못했습니다. (HRESULT 0x{result.Code:X8})");

        // 캡처 스레드와 UI 스레드가 같은 컨텍스트를 사용하므로 멀티스레드 보호를 켠다.
        using (ID3D11Multithread? multithread = device!.QueryInterfaceOrNull<ID3D11Multithread>())
        {
            multithread?.SetMultithreadProtected(true);
        }

        IDXGIFactory2 factory = CreateFactory(device!);
        IDirect3DDevice winrtDevice = WinRTInterop.CreateDirect3DDevice(device!);
        return new GraphicsDevice(device!, context!, factory, winrtDevice);
    }

    private static IDXGIFactory2 CreateFactory(ID3D11Device device)
    {
        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        return adapter.GetParent<IDXGIFactory2>();
    }

    /// <summary>디바이스가 제거/재설정되었는지 확인한다.</summary>
    public bool IsDeviceLost()
    {
        Result reason = Device.DeviceRemovedReason;
        return reason.Failure;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        (WinRTDevice as IDisposable)?.Dispose();
        Factory.Dispose();
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
    }
}
