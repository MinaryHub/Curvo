using System.Runtime.InteropServices;

namespace Curvo.Interop;

/// <summary>
/// Media Foundation Media Engine 상호 운용.
/// 외부 플레이어 없이 앱 안에서 동영상을 하드웨어 디코딩해 D3D11 텍스처로 받기 위해 사용한다.
/// </summary>
internal static class MediaFoundationInterop
{
    /// <summary>MF_SDK_VERSION(0x0002) &lt;&lt; 16 | MF_API_VERSION(0x0070).</summary>
    public const int MF_VERSION = 0x00020070;

    public const int MFSTARTUP_NOSOCKET = 1;

    public static readonly Guid CLSID_MFMediaEngineClassFactory = new("B44392DA-499B-446B-A4CB-005FEAD0E6D5");

    // Media Engine 생성 속성 키
    public static readonly Guid MF_MEDIA_ENGINE_CALLBACK = new("C60381B8-83A4-41F8-A3D0-DE05076849A9");
    public static readonly Guid MF_MEDIA_ENGINE_DXGI_MANAGER = new("065702DA-1094-486D-8617-EE7CC4EE4648");
    public static readonly Guid MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT = new("5066893C-8CF9-42BC-8B8A-472212E52726");

    // IMFAttributes vtable 인덱스 (0~2 = IUnknown)
    private const int SlotSetUInt32 = 21;
    private const int SlotSetUnknown = 27;

    // IMFDXGIDeviceManager vtable 인덱스
    private const int SlotResetDevice = 7;

    // IMFMediaEngineClassFactory vtable 인덱스
    private const int SlotCreateInstance = 3;

    public static readonly Guid IID_IMFMediaEngineClassFactory = new("4D645ACE-26AA-4688-9BE1-DF3516990B93");

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateAttributes(out IntPtr attributes, int initialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateDXGIDeviceManager(out int resetToken, out IntPtr deviceManager);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        in Guid classId, IntPtr outer, int context, in Guid interfaceId, out IntPtr instance);

    private const int CLSCTX_INPROC_SERVER = 1;

    /// <summary>IMFAttributes::SetUINT32 (vtable 직접 호출).</summary>
    public static unsafe int SetAttributeUInt32(IntPtr attributes, Guid key, uint value)
    {
        var vtbl = *(void***)attributes;
        var setUInt32 = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)vtbl[SlotSetUInt32];
        return setUInt32(attributes, &key, value);
    }

    /// <summary>IMFAttributes::SetUnknown (vtable 직접 호출).</summary>
    public static unsafe int SetAttributeUnknown(IntPtr attributes, Guid key, IntPtr unknown)
    {
        var vtbl = *(void***)attributes;
        var setUnknown = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr, int>)vtbl[SlotSetUnknown];
        return setUnknown(attributes, &key, unknown);
    }

    /// <summary>IMFDXGIDeviceManager::ResetDevice (vtable 직접 호출).</summary>
    public static unsafe int ResetDevice(IntPtr deviceManager, IntPtr d3dDevice, int resetToken)
    {
        var vtbl = *(void***)deviceManager;
        var resetDevice = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int>)vtbl[SlotResetDevice];
        return resetDevice(deviceManager, d3dDevice, resetToken);
    }

    /// <summary>
    /// IMFMediaEngineClassFactory 로 Media Engine 을 만든다.
    /// RCW 를 만들지 않고 원시 포인터로 다루므로 어느 스레드에서든 호출할 수 있다.
    /// </summary>
    public static unsafe MediaEngine CreateMediaEngine(IntPtr attributes)
    {
        Guid classId = CLSID_MFMediaEngineClassFactory;
        Guid interfaceId = IID_IMFMediaEngineClassFactory;
        Marshal.ThrowExceptionForHR(
            CoCreateInstance(in classId, IntPtr.Zero, CLSCTX_INPROC_SERVER, in interfaceId, out IntPtr factory));
        try
        {
            var vtbl = *(void***)factory;
            var createInstance = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr*, int>)vtbl[SlotCreateInstance];
            IntPtr engine;
            Marshal.ThrowExceptionForHR(createInstance(factory, 0, attributes, &engine));
            return new MediaEngine(engine);
        }
        finally
        {
            Marshal.Release(factory);
        }
    }
}

/// <summary>Media Engine 이벤트 (MF_MEDIA_ENGINE_EVENT 중 사용하는 것만).</summary>
internal enum MediaEngineEvent : uint
{
    LoadStart = 1,
    Error = 5,
    Stalled = 7,
    Play = 8,
    Pause = 9,
    LoadedMetadata = 10,
    LoadedData = 11,
    CanPlay = 14,
    Seeked = 17,
    TimeUpdate = 18,
    Ended = 19,
    DurationChange = 21,
    FormatChange = 23,
    FirstFrameReady = 32,
}

/// <summary>MF_MEDIA_ENGINE_ERR.</summary>
internal enum MediaEngineError : uint
{
    NoError = 0,
    Aborted = 1,
    Network = 2,
    Decode = 3,
    SrcNotSupported = 4,
    Encrypted = 5,
}

/// <summary>
/// Media Engine 이 재생 상태 변화를 알려주는 콜백. 관리 객체를 CCW 로 넘긴다.
/// MF 워커 스레드에서 호출되므로 구현은 짧고 스레드 안전해야 한다.
/// </summary>
[ComImport, Guid("FEE7C112-E776-42B5-9BBF-0048524E2BD5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEngineNotify
{
    void EventNotify(uint mediaEngineEvent, UIntPtr param1, uint param2);
}

/// <summary>
/// IMFMediaEngine 원시 포인터 래퍼.
/// <para>
/// RCW(<c>ComImport</c> 인터페이스)로 다루면 안 된다. Media Engine 은 WPF UI 스레드(STA)에서 만들어지는데
/// 이 COM 개체에는 아파트먼트 간 마샬러가 등록되어 있지 않아, 동영상 렌더 스레드에서 RCW 를 호출하는 순간
/// CLR 이 프록시를 만들려다 <c>E_NOINTERFACE</c> 로 실패한다(프레임이 한 장도 오지 않는다).
/// vtable 을 직접 호출하면 CLR 의 아파트먼트 마샬링을 거치지 않으므로 어느 스레드에서든 그대로 동작한다.
/// </para>
/// <para>vtable 인덱스는 mfmediaengine.h 의 선언 순서(0~2 = IUnknown)를 그대로 따른다.</para>
/// </summary>
internal sealed unsafe class MediaEngine
{
    private const int SlotSetSource = 6;
    private const int SlotGetCurrentTime = 16;
    private const int SlotSetCurrentTime = 17;
    private const int SlotGetDuration = 19;
    private const int SlotIsPaused = 20;
    private const int SlotSetAutoPlay = 29;
    private const int SlotGetLoop = 30;
    private const int SlotSetLoop = 31;
    private const int SlotPlay = 32;
    private const int SlotPause = 33;
    private const int SlotGetVolume = 36;
    private const int SlotSetVolume = 37;
    private const int SlotHasVideo = 38;
    private const int SlotGetNativeVideoSize = 40;
    private const int SlotShutdown = 42;
    private const int SlotTransferVideoFrame = 43;
    private const int SlotOnVideoStreamTick = 44;

    private IntPtr _pointer;

    public MediaEngine(IntPtr pointer) => _pointer = pointer;

    private void** Vtbl => *(void***)_pointer;

    /// <summary>URL 을 지정하면 Media Engine 이 로드를 시작한다(BSTR 로 넘겨야 한다).</summary>
    public void SetSource(string url)
    {
        IntPtr bstr = Marshal.StringToBSTR(url);
        try
        {
            var setSource = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Vtbl[SlotSetSource];
            Marshal.ThrowExceptionForHR(setSource(_pointer, bstr));
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    public double GetCurrentTime()
        => ((delegate* unmanaged[Stdcall]<IntPtr, double>)Vtbl[SlotGetCurrentTime])(_pointer);

    public void SetCurrentTime(double seekTime)
        => Marshal.ThrowExceptionForHR(
            ((delegate* unmanaged[Stdcall]<IntPtr, double, int>)Vtbl[SlotSetCurrentTime])(_pointer, seekTime));

    public double GetDuration()
        => ((delegate* unmanaged[Stdcall]<IntPtr, double>)Vtbl[SlotGetDuration])(_pointer);

    public bool IsPaused()
        => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl[SlotIsPaused])(_pointer) != 0;

    public void SetAutoPlay(bool autoPlay)
        => Marshal.ThrowExceptionForHR(
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Vtbl[SlotSetAutoPlay])(_pointer, autoPlay ? 1 : 0));

    public bool GetLoop()
        => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl[SlotGetLoop])(_pointer) != 0;

    public void SetLoop(bool loop)
        => Marshal.ThrowExceptionForHR(
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Vtbl[SlotSetLoop])(_pointer, loop ? 1 : 0));

    public void Play()
        => Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl[SlotPlay])(_pointer));

    public void Pause()
        => Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl[SlotPause])(_pointer));

    public double GetVolume()
        => ((delegate* unmanaged[Stdcall]<IntPtr, double>)Vtbl[SlotGetVolume])(_pointer);

    public void SetVolume(double volume)
        => Marshal.ThrowExceptionForHR(
            ((delegate* unmanaged[Stdcall]<IntPtr, double, int>)Vtbl[SlotSetVolume])(_pointer, volume));

    public bool HasVideo()
        => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl[SlotHasVideo])(_pointer) != 0;

    public void GetNativeVideoSize(out uint width, out uint height)
    {
        uint w, h;
        var get = (delegate* unmanaged[Stdcall]<IntPtr, uint*, uint*, int>)Vtbl[SlotGetNativeVideoSize];
        Marshal.ThrowExceptionForHR(get(_pointer, &w, &h));
        width = w;
        height = h;
    }

    public void Shutdown()
        => Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl[SlotShutdown])(_pointer));

    /// <summary>대상은 ID3D11Texture2D 의 원시 포인터여야 한다. HRESULT 를 그대로 돌려준다.</summary>
    public int TransferVideoFrame(IntPtr destinationSurface, IntPtr sourceRectangle,
        ref Win32.RECT destinationRectangle, IntPtr borderColor)
    {
        fixed (Win32.RECT* destination = &destinationRectangle)
        {
            var transfer =
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, Win32.RECT*, IntPtr, int>)Vtbl[SlotTransferVideoFrame];
            return transfer(_pointer, destinationSurface, sourceRectangle, destination, borderColor);
        }
    }

    /// <summary>새 프레임이 준비되면 S_OK(0), 없으면 S_FALSE(1) 를 반환한다.</summary>
    public int OnVideoStreamTick(out long presentationTime)
    {
        long time;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, long*, int>)Vtbl[SlotOnVideoStreamTick])(_pointer, &time);
        presentationTime = time;
        return hr;
    }

    /// <summary>참조를 놓는다(호출 후 이 인스턴스는 더 이상 쓸 수 없다).</summary>
    public void Release()
    {
        if (_pointer == IntPtr.Zero) return;
        Marshal.Release(_pointer);
        _pointer = IntPtr.Zero;
    }
}
