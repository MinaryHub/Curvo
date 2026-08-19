using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using ProjectorWarp.Interop;
using ProjectorWarp.Rendering;

namespace ProjectorWarp.Media;

/// <summary>
/// Media Foundation Media Engine 기반 내부 동영상 재생기.
/// 외부 플레이어 없이 앱이 직접 하드웨어 디코딩하고, 프레임을 D3D11 텍스처로 받아
/// 캡처 경로와 동일하게 워핑 파이프라인에 넣는다. 오디오는 Media Engine 이 직접 출력한다.
/// </summary>
internal sealed class VideoPlayer : IDisposable
{
    /// <summary>지원 확장자(파일 대화상자 필터와 안내에 사용).</summary>
    public static readonly string[] SupportedExtensions =
        { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".ts", ".mpg", ".mpeg", ".asf", ".3gp" };

    private static readonly object StartupLock = new();
    private static bool _mediaFoundationStarted;

    private readonly GraphicsDevice _graphics;
    private readonly EngineNotify _notify;

    private MediaEngine? _engine;
    private IntPtr _notifyPointer;
    private IntPtr _deviceManager;
    private ID3D11Texture2D? _frameTexture;
    private SizeInt32 _frameSize;
    private bool _disposed;

    public VideoPlayer(GraphicsDevice graphics)
    {
        _graphics = graphics;
        _notify = new EngineNotify(OnEngineEvent);
        EnsureMediaFoundation();
    }

    /// <summary>재생 오류. (사용자에게 보여줄 메시지)</summary>
    public event Action<string>? Failed;

    /// <summary>길이·해상도 등 메타데이터를 읽었을 때.</summary>
    public event Action? MetadataLoaded;

    /// <summary>첫 프레임이 준비되어 화면에 그릴 수 있을 때.</summary>
    public event Action? FirstFrameReady;

    /// <summary>끝까지 재생했을 때(반복이 꺼진 경우에만 발생).</summary>
    public event Action? Ended;

    public string? CurrentPath { get; private set; }

    public bool IsOpen => _engine is not null;

    public SizeInt32 FrameSize => _frameSize;

    public ID3D11Texture2D? FrameTexture => _frameTexture;

    public bool IsPaused => _engine?.IsPaused() ?? true;

    public bool HasVideo => _engine?.HasVideo() ?? false;

    public double Duration
    {
        get
        {
            double duration = _engine?.GetDuration() ?? 0.0;
            return double.IsNaN(duration) || double.IsInfinity(duration) ? 0.0 : duration;
        }
    }

    public double Position
    {
        get => _engine?.GetCurrentTime() ?? 0.0;
        set
        {
            if (_engine is null) return;
            try
            {
                _engine.SetCurrentTime(Math.Max(0.0, value));
            }
            catch (COMException)
            {
                // 아직 시크할 수 없는 상태
            }
        }
    }

    public bool Loop
    {
        get => _engine?.GetLoop() ?? false;
        set => _engine?.SetLoop(value);
    }

    /// <summary>0.0 ~ 1.0</summary>
    public double Volume
    {
        get => _engine?.GetVolume() ?? 1.0;
        set => _engine?.SetVolume(Math.Clamp(value, 0.0, 1.0));
    }

    private static void EnsureMediaFoundation()
    {
        lock (StartupLock)
        {
            if (_mediaFoundationStarted) return;
            int hr = MediaFoundationInterop.MFStartup(
                MediaFoundationInterop.MF_VERSION, MediaFoundationInterop.MFSTARTUP_NOSOCKET);
            Marshal.ThrowExceptionForHR(hr);
            _mediaFoundationStarted = true;
        }
    }

    /// <summary>파일을 열고 재생을 시작한다.</summary>
    public void Open(string path, bool loop, double volume, bool autoPlay = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(path)) throw new FileNotFoundException("동영상 파일을 찾을 수 없습니다.", path);

        Close();

        IntPtr attributes = IntPtr.Zero;
        try
        {
            _notifyPointer = Marshal.GetComInterfaceForObject(_notify, typeof(IMFMediaEngineNotify));

            int resetToken;
            Marshal.ThrowExceptionForHR(
                MediaFoundationInterop.MFCreateDXGIDeviceManager(out resetToken, out _deviceManager));
            Marshal.ThrowExceptionForHR(
                MediaFoundationInterop.ResetDevice(_deviceManager, _graphics.Device.NativePointer, resetToken));

            Marshal.ThrowExceptionForHR(MediaFoundationInterop.MFCreateAttributes(out attributes, 4));
            Marshal.ThrowExceptionForHR(MediaFoundationInterop.SetAttributeUnknown(
                attributes, MediaFoundationInterop.MF_MEDIA_ENGINE_CALLBACK, _notifyPointer));
            Marshal.ThrowExceptionForHR(MediaFoundationInterop.SetAttributeUnknown(
                attributes, MediaFoundationInterop.MF_MEDIA_ENGINE_DXGI_MANAGER, _deviceManager));
            Marshal.ThrowExceptionForHR(MediaFoundationInterop.SetAttributeUInt32(
                attributes, MediaFoundationInterop.MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT, (uint)Format.B8G8R8A8_UNorm));

            _engine = MediaFoundationInterop.CreateMediaEngine(attributes);

            _engine.SetAutoPlay(autoPlay);
            _engine.SetLoop(loop);
            _engine.SetVolume(Math.Clamp(volume, 0.0, 1.0));
            // file:// URI 를 넘기면 안 된다. Media Engine 은 퍼센트 인코딩을 UTF-8 이 아닌
            // ANSI 코드페이지로 되돌리기 때문에, 한글·일본어가 든 파일명이 0x80070002
            // (ERROR_FILE_NOT_FOUND) 로 실패하고 "지원하지 않는 형식" 으로 보고된다.
            // 로컬 경로를 그대로 주면 MF 소스 리졸버가 비ASCII·# ·% 가 든 이름도 모두 연다.
            _engine.SetSource(Path.GetFullPath(path));

            CurrentPath = path;
        }
        catch
        {
            Close();
            throw;
        }
        finally
        {
            if (attributes != IntPtr.Zero) Marshal.Release(attributes);
        }
    }

    public void Play() => _engine?.Play();

    public void Pause() => _engine?.Pause();

    public void TogglePlayPause()
    {
        if (_engine is null) return;
        if (_engine.IsPaused()) _engine.Play();
        else _engine.Pause();
    }

    /// <summary>처음으로 되돌린다.</summary>
    public void Restart()
    {
        Position = 0.0;
        Play();
    }

    /// <summary>
    /// 새 프레임이 있으면 내부 텍스처로 옮긴다. 렌더 스레드에서 호출한다.
    /// </summary>
    public bool TryAcquireFrame()
    {
        MediaEngine? engine = _engine;
        if (engine is null) return false;

        int tick = engine.OnVideoStreamTick(out long _);
        if (tick != 0) return false; // S_FALSE = 새 프레임 없음

        if (!EnsureFrameTexture(engine)) return false;

        var destination = new Win32.RECT { Left = 0, Top = 0, Right = _frameSize.Width, Bottom = _frameSize.Height };
        int hr = engine.TransferVideoFrame(_frameTexture!.NativePointer, IntPtr.Zero, ref destination, IntPtr.Zero);
        return hr >= 0;
    }

    private bool EnsureFrameTexture(MediaEngine engine)
    {
        uint width, height;
        try
        {
            engine.GetNativeVideoSize(out width, out height);
        }
        catch (COMException)
        {
            return false;
        }
        if (width == 0 || height == 0) return false;

        if (_frameTexture is not null && _frameSize.Width == (int)width && _frameSize.Height == (int)height)
            return true;

        _frameTexture?.Dispose();
        var description = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            // TransferVideoFrame 의 대상은 렌더 타겟이어야 한다.
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _frameTexture = _graphics.Device.CreateTexture2D(description);
        _frameSize = new SizeInt32 { Width = (int)width, Height = (int)height };
        return true;
    }

    private void OnEngineEvent(uint rawEvent, UIntPtr param1, uint param2)
    {
        switch ((MediaEngineEvent)rawEvent)
        {
            case MediaEngineEvent.Error:
                Failed?.Invoke(DescribeError((MediaEngineError)(uint)param1, param2));
                break;

            case MediaEngineEvent.LoadedMetadata:
                MetadataLoaded?.Invoke();
                break;

            case MediaEngineEvent.FirstFrameReady:
                FirstFrameReady?.Invoke();
                break;

            case MediaEngineEvent.Ended:
                Ended?.Invoke();
                break;
        }
    }

    /// <summary>ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND (HRESULT_FROM_WIN32).</summary>
    private const uint FileNotFoundHResult = 0x80070002;
    private const uint PathNotFoundHResult = 0x80070003;
    private const uint AccessDeniedHResult = 0x80070005;

    private static string DescribeError(MediaEngineError error, uint hresult)
    {
        // 코덱과 무관한 파일 접근 실패가 "지원하지 않는 형식" 으로 보이면 원인을 찾기 어렵다.
        if (hresult is FileNotFoundHResult or PathNotFoundHResult)
            return $"파일을 찾을 수 없습니다. (HRESULT 0x{hresult:X8})";
        if (hresult == AccessDeniedHResult)
            return $"파일을 읽을 권한이 없습니다. (HRESULT 0x{hresult:X8})";

        string reason = error switch
        {
            MediaEngineError.Aborted => "재생이 중단되었습니다.",
            MediaEngineError.Network => "파일을 읽는 중 오류가 발생했습니다.",
            MediaEngineError.Decode => "디코딩에 실패했습니다. 코덱이 설치되지 않았을 수 있습니다.",
            MediaEngineError.SrcNotSupported => "지원하지 않는 형식입니다. MP4(H.264) 로 변환해 보세요.",
            MediaEngineError.Encrypted => "DRM 으로 보호된 파일은 재생할 수 없습니다.",
            _ => "알 수 없는 재생 오류입니다.",
        };
        return hresult == 0 ? reason : $"{reason} (HRESULT 0x{hresult:X8})";
    }

    public void Close()
    {
        if (_engine is not null)
        {
            try
            {
                _engine.Shutdown();
            }
            catch (COMException)
            {
                // 이미 종료된 상태
            }
            _engine.Release();
            _engine = null;
        }

        if (_notifyPointer != IntPtr.Zero)
        {
            Marshal.Release(_notifyPointer);
            _notifyPointer = IntPtr.Zero;
        }

        if (_deviceManager != IntPtr.Zero)
        {
            Marshal.Release(_deviceManager);
            _deviceManager = IntPtr.Zero;
        }

        _frameTexture?.Dispose();
        _frameTexture = null;
        _frameSize = default;
        CurrentPath = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    /// <summary>Media Engine 콜백을 받는 CCW 대상 클래스.</summary>
    private sealed class EngineNotify : IMFMediaEngineNotify
    {
        private readonly Action<uint, UIntPtr, uint> _handler;

        public EngineNotify(Action<uint, UIntPtr, uint> handler) => _handler = handler;

        public void EventNotify(uint mediaEngineEvent, UIntPtr param1, uint param2)
        {
            try
            {
                _handler(mediaEngineEvent, param1, param2);
            }
            catch (Exception)
            {
                // 콜백에서 예외가 나가면 MF 파이프라인이 깨지므로 반드시 삼킨다.
            }
        }
    }
}
