using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Curvo.Interop;

/// <summary>
/// WinRT(Windows.Graphics.Capture)와 D3D11 사이의 COM 상호 운용.
/// 마샬링 모호성을 없애기 위해 vtable 직접 호출을 사용한다.
/// </summary>
internal static unsafe class WinRTInterop
{
    // IGraphicsCaptureItemInterop
    private static readonly Guid IID_GraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    // IGraphicsCaptureItem (런타임 클래스의 기본 인터페이스)
    private static readonly Guid IID_GraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    // IDirect3DDxgiInterfaceAccess
    private static readonly Guid IID_Direct3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private const string GraphicsCaptureItemClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";

    // vtable 슬롯: 0~2 = IUnknown, 3 = CreateForWindow, 4 = CreateForMonitor
    private const int SlotCreateForWindow = 3;
    private const int SlotCreateForMonitor = 4;
    // IDirect3DDxgiInterfaceAccess: 0~2 = IUnknown, 3 = GetInterface
    private const int SlotGetInterface = 3;

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    /// <summary>지정한 창(HWND)에 대한 캡처 아이템을 만든다.</summary>
    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
        => CreateItem(hwnd, SlotCreateForWindow);

    /// <summary>지정한 모니터(HMONITOR)에 대한 캡처 아이템을 만든다.</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmonitor)
        => CreateItem(hmonitor, SlotCreateForMonitor);

    private static GraphicsCaptureItem CreateItem(IntPtr handle, int vtableSlot)
    {
        IntPtr factory = GetActivationFactory(GraphicsCaptureItemClassName, IID_GraphicsCaptureItemInterop);
        try
        {
            var vtbl = *(void***)factory;
            var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[vtableSlot];
            Guid iid = IID_GraphicsCaptureItem;
            IntPtr itemPtr;
            int hr = create(factory, handle, &iid, &itemPtr);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    private static IntPtr GetActivationFactory(string className, Guid iid)
    {
        int hr = WindowsCreateString(className, className.Length, out IntPtr hstring);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            hr = RoGetActivationFactory(hstring, ref iid, out IntPtr factory);
            Marshal.ThrowExceptionForHR(hr);
            return factory;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    /// <summary>D3D11 디바이스를 WinRT 캡처가 사용할 IDirect3DDevice 로 변환한다.</summary>
    public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device device)
    {
        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr inspectable);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>캡처 프레임 서피스에서 GPU 상주 상태 그대로 ID3D11Texture2D 를 얻는다.</summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        IntPtr surfacePtr = WinRT.MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
        if (surfacePtr == IntPtr.Zero)
            throw new InvalidOperationException("Could not obtain the native pointer of the capture surface.");

        try
        {
            Guid accessIid = IID_Direct3DDxgiInterfaceAccess;
            int hr = Marshal.QueryInterface(surfacePtr, ref accessIid, out IntPtr accessPtr);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                var vtbl = *(void***)accessPtr;
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[SlotGetInterface];
                Guid textureIid = IID_ID3D11Texture2D;
                IntPtr texturePtr;
                hr = getInterface(accessPtr, &textureIid, &texturePtr);
                Marshal.ThrowExceptionForHR(hr);
                // Vortice 래퍼가 참조를 소유한다.
                return new ID3D11Texture2D(texturePtr);
            }
            finally
            {
                Marshal.Release(accessPtr);
            }
        }
        finally
        {
            Marshal.Release(surfacePtr);
        }
    }
}
