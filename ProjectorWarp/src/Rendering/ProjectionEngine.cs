using System.Windows.Threading;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Curvo.Capture;
using Curvo.Geometry;
using Curvo.Interop;
using Curvo.Media;

namespace Curvo.Rendering;

/// <summary>현재 화면에 내보내는 소스 종류.</summary>
internal enum ProjectionSourceKind
{
    None,
    /// <summary>다른 창/모니터 캡처.</summary>
    Capture,
    /// <summary>앱 내부 동영상 재생.</summary>
    Video,
    /// <summary>앱 내부 슬라이드(PPT/PDF/이미지) 재생.</summary>
    Slides,
}

/// <summary>
/// 캡처 · 렌더링 · 출력 창을 묶는 조정자.
/// 모든 공개 메서드는 UI 스레드에서 호출한다(출력 창 메시지 펌프가 UI 스레드에 있다).
/// <para>
/// <b>그리기와 Present 는 전용 렌더 스레드 한 곳에서만 한다.</b>
/// UI 스레드(편집 · 슬라이더)와 캡처 스레드(새 프레임)는 "다시 그려 달라" 고 알리기만 한다.
/// vsync Present 는 최대 한 화면 주기 동안 블록되므로, UI 스레드에서 하면 슬라이더 한 칸 ·
/// 마우스 이동 한 번마다 UI 가 멈추고, 캡처 스레드와 겹치면 같은 그림을 두 번 그린다.
/// 알림은 모아서 처리되므로 아무리 자주 와도 한 화면 주기에 한 번만 그린다.
/// </para>
/// <para>
/// 렌더러 · 출력 창 · 소스 텍스처 · 설정의 구조 변경은 <see cref="GraphicsDevice.RenderLock"/> 으로 보호한다.
/// Present 는 락 밖에서 한다(출력 창이 자체 락으로 크기 변경 · 해제와 직렬화한다).
/// </para>
/// </summary>
internal sealed class ProjectionEngine : IDisposable
{
    /// <summary>렌더 오류가 이어질 때 다음 시도까지 쉬는 시간(ms).</summary>
    private const int RenderErrorBackoffMilliseconds = 100;

    /// <summary>렌더 스레드를 멈출 때 기다리는 최대 시간.</summary>
    private static readonly TimeSpan RenderThreadStopTimeout = TimeSpan.FromSeconds(5);

    private readonly Dispatcher _dispatcher;
    private readonly AutoResetEvent _renderSignal = new(false);

    private GraphicsDevice? _graphics;
    private CaptureEngine? _capture;
    private WarpRenderer? _renderer;
    private OutputWindow? _window;
    private MonitorInfo? _outputMonitor;
    private CaptureTarget? _pendingSource;
    /// <summary>렌더 스레드가 프레임을 확인하므로 volatile. 바꿀 때는 렌더 락 안에서 바꾼다.</summary>
    private volatile VideoPlayer? _video;
    private SlideDeck? _slides;

    /// <summary>
    /// 캡처 프레임을 복사해 두는 텍스처(렌더 락으로 보호).
    /// 캡처 프레임은 콜백이 끝나면 프레임 풀로 돌아가 다음 프레임이 덮어쓰므로,
    /// 나중에(렌더 스레드에서) 그리려면 GPU 안에서 복사해 둬야 한다.
    /// </summary>
    private ID3D11Texture2D? _captureCopy;
    /// <summary>캡처 프레임을 받아들일지(렌더 락으로 보호). 멈춘 뒤 늦게 도착한 프레임이 다른 소스를 덮지 않게 한다.</summary>
    private bool _acceptCaptureFrames;

    private Thread? _renderThread;
    private volatile bool _renderThreadRunning;
    /// <summary>소스 프레임과 무관하게 다시 그려야 할 변경(설정·오버레이·기하·새 캡처 프레임)이 있는지.</summary>
    private volatile bool _renderRequested = true;
    private volatile bool _recreating;
    private bool _disposed;

    public ProjectionEngine(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public WarpSettings Settings { get; } = new();

    public OverlayState Overlay { get; } = new();

    public UndoHistory History { get; } = new();

    public OutputWindow? Window => _window;

    public MonitorInfo? OutputMonitor => _outputMonitor;

    public CaptureTarget? Source => _capture?.CurrentTarget ?? _pendingSource;

    public bool IsOutputActive => _window is not null;

    public bool IsCapturing => _capture?.IsCapturing == true;

    /// <summary>현재 사용 중인 소스 종류.</summary>
    public ProjectionSourceKind ActiveSourceKind { get; private set; } = ProjectionSourceKind.None;

    /// <summary>내부 재생 중인 미디어 파일 경로(동영상/슬라이드 원본).</summary>
    public string? MediaPath { get; private set; }

    public VideoPlayer? Video => _video;

    public SlideDeck? Slides => _slides;

    /// <summary>지금까지 출력 창에 그린 프레임 수(검증용).</summary>
    public long RenderCount => _renderer?.RenderCount ?? 0;

    /// <summary>내부 미디어(동영상/슬라이드)가 활성 상태인지.</summary>
    public bool IsMediaActive =>
        ActiveSourceKind is ProjectionSourceKind.Video or ProjectionSourceKind.Slides;

    /// <summary>상태 메시지(컨트롤 패널 표시용).</summary>
    public event Action<string>? StatusChanged;

    /// <summary>캡처 대상이 사라졌을 때.</summary>
    public event Action? SourceLost;

    /// <summary>출력 창이 새로 만들어졌을 때(입력 핸들러 연결용).</summary>
    public event Action<OutputWindow>? OutputWindowCreated;

    /// <summary>출력 창이 닫히기 직전.</summary>
    public event Action<OutputWindow>? OutputWindowClosing;

    /// <summary>동영상 재생이 끝났을 때(반복이 꺼진 경우).</summary>
    public event Action? MediaEnded;

    /// <summary>슬라이드가 바뀌었을 때(UI 표시 갱신용).</summary>
    public event Action? SlideChanged;

    /// <summary>출력 창을 지정 모니터에 만든다(이미 있으면 이동).</summary>
    public void StartOutput(MonitorInfo monitor)
    {
        EnsureDevice();
        _outputMonitor = monitor;

        if (_window is not null)
        {
            _window.MoveToMonitor(monitor);
            RequestRender();
            Report($"Moved the output to {monitor.DeviceName}.");
            return;
        }

        OutputWindow window = new(_graphics!, monitor);
        WarpRenderer renderer = new(_graphics!);
        lock (_graphics!.RenderLock)
        {
            _window = window;
            _renderer = renderer;
        }
        window.Closed += OnOutputWindowClosed;
        window.SizeChanged += RequestRender;
        OutputWindowCreated?.Invoke(window);

        if (!window.IsExcludedFromCapture)
            Report("Warning: could not exclude the output window from capture. Capturing a whole monitor may create a feedback loop.");
        else
            Report($"Opened the output window on {monitor.DeviceName}.");

        StartRenderThread();
        RequestRender();

        if (_pendingSource is not null)
        {
            CaptureTarget target = _pendingSource;
            _pendingSource = null;
            StartCapture(target);
        }
    }

    public void StopOutput()
    {
        StopSource();
        if (_window is null) return;

        OutputWindowClosing?.Invoke(_window);
        StopRenderThread();
        ReleaseOutputResources();
        Report("Output stopped.");
    }

    /// <summary>캡처 소스를 지정한다. 출력 창이 아직 없으면 열릴 때 시작한다.</summary>
    public void StartCapture(CaptureTarget target)
    {
        if (!CaptureEngine.IsSupported)
        {
            Report("Windows.Graphics.Capture is not available on this PC. Windows 10 1903 or later is required.");
            return;
        }

        EnsureDevice();

        if (_window is null)
        {
            _pendingSource = target;
            Report("Choose an output monitor first — capture starts as soon as you do.");
            return;
        }

        try
        {
            StopMedia();
            EnsureCaptureEngine();
            lock (_graphics!.RenderLock)
            {
                _acceptCaptureFrames = true;
            }
            _capture!.Start(target);
            ActiveSourceKind = ProjectionSourceKind.Capture;
            MediaPath = null;
            Report($"Capturing: {target.DisplayName}");
        }
        catch (Exception ex)
        {
            Report($"Could not start capture: {ex.Message}");
        }
    }

    /// <summary>캡처와 내부 미디어를 모두 멈춘다.</summary>
    public void StopSource()
    {
        StopMedia();
        StopCapture();
    }

    public void StopCapture()
    {
        GraphicsDevice? graphics = _graphics;
        if (graphics is not null)
        {
            // 먼저 프레임을 거절해 둬야, 세션을 멈추는 동안 도착한 프레임이 화면을 되살리지 않는다.
            lock (graphics.RenderLock)
            {
                _acceptCaptureFrames = false;
                _renderer?.ClearSource();
                DisposeCaptureCopy();
            }
        }

        _capture?.Stop();
        if (ActiveSourceKind == ProjectionSourceKind.Capture) ActiveSourceKind = ProjectionSourceKind.None;
        RequestRender();
    }

    /// <summary>
    /// 다시 그려 달라고 렌더 스레드에 알린다. 바로 반환하며(블록하지 않음),
    /// 여러 번 불려도 다음 화면 주기에 한 번만 그린다.
    /// </summary>
    public void RequestRender()
    {
        _renderRequested = true;
        _renderSignal.Set();
    }

    // ---- 렌더 스레드 -------------------------------------------------------

    private void StartRenderThread()
    {
        if (_renderThread is not null) return;
        _renderThreadRunning = true;
        _renderThread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "Curvo.Render",
        };
        _renderThread.Start();
    }

    private void StopRenderThread()
    {
        Thread? thread = _renderThread;
        if (thread is null) return;

        _renderThreadRunning = false;
        _renderSignal.Set();
        // 렌더 스레드는 UI 스레드를 동기로 기다리지 않으므로(BeginInvoke 만 쓴다) 길어야 한 화면 주기다.
        if (!thread.Join(RenderThreadStopTimeout))
            Report("The render thread did not stop in time.");
        _renderThread = null;
    }

    private void RenderLoop()
    {
        bool timerRaised = false;
        bool faulted = false;
        try
        {
            while (_renderThreadRunning)
            {
                bool videoActive = _video is not null;
                UpdateTimerResolution(videoActive, ref timerRaised);

                try
                {
                    if (TryRenderFrame(out OutputWindow? window))
                    {
                        // vsync 대기는 렌더 락 밖에서 한다. 이 대기가 그리기 속도를 화면 주사율로 제한한다.
                        window!.Present(verticalSync: true);
                        CheckDeviceLost();
                        faulted = false;
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    // 같은 오류가 매 프레임 반복될 수 있으므로 한 번만 알리고 잠시 쉰다.
                    if (!faulted)
                    {
                        string message = ex.Message;
                        _dispatcher.BeginInvoke(() => Report($"Render error: {message}"));
                    }
                    faulted = true;
                    CheckDeviceLost();
                    _renderSignal.WaitOne(RenderErrorBackoffMilliseconds);
                    continue;
                }

                // 그릴 것이 없다. 동영상은 새 프레임을 놓치지 않을 만큼 짧게, 그 밖에는 알림이 올 때까지 쉰다.
                _renderSignal.WaitOne(videoActive ? AppConfig.VideoFramePollIntervalMilliseconds : Timeout.Infinite);
            }
        }
        finally
        {
            if (timerRaised) Win32.TimeEndPeriod(1);
        }
    }

    /// <summary>
    /// 동영상을 확인하는 짧은 대기(2ms)는 기본 타이머 해상도(15.6ms)에서는 실제로 약 16ms 가 되어
    /// 60fps 영상의 프레임을 놓친다. 동영상이 있는 동안만 해상도를 올린다(전력 소모 때문).
    /// </summary>
    private static void UpdateTimerResolution(bool wanted, ref bool raised)
    {
        if (wanted && !raised)
        {
            raised = Win32.TimeBeginPeriod(1) == 0;
        }
        else if (!wanted && raised)
        {
            Win32.TimeEndPeriod(1);
            raised = false;
        }
    }

    /// <summary>새 동영상 프레임이나 다시 그리기 요청이 있으면 그린다(Present 는 하지 않는다).</summary>
    private bool TryRenderFrame(out OutputWindow? window)
    {
        window = null;
        GraphicsDevice? graphics = _graphics;
        if (graphics is null) return false;

        lock (graphics.RenderLock)
        {
            window = _window;
            if (_renderer is null || window is null) return false;

            // 동영상은 보통 24~30fps 다. 새 프레임도 없고 바뀐 설정도 없으면
            // 같은 그림을 vsync 속도로 다시 그리는 셈이므로 건너뛴다.
            bool newFrame = false;
            VideoPlayer? player = _video;
            if (player is not null && player.TryAcquireFrame() && player.FrameTexture is not null)
            {
                _renderer.SetSourceTexture(player.FrameTexture, player.FrameSize);
                newFrame = true;
            }

            if (!newFrame && !_renderRequested) return false;

            _renderRequested = false;
            _renderer.CurrentPattern = Overlay.Pattern;
            _renderer.Render(window, Settings, Overlay);
            return true;
        }
    }

    // ---- 내부 미디어 (외부 프로그램 없이 재생) ---------------------------

    /// <summary>동영상 파일을 앱 내부에서 재생한다.</summary>
    public void StartVideo(string path, bool loop, double volume)
    {
        EnsureDevice();
        StopSource();

        if (_window is null)
        {
            Report("Choose an output monitor first.");
            return;
        }

        VideoPlayer? player = null;
        try
        {
            player = new VideoPlayer(_graphics!);
            VideoPlayer opened = player;
            player.Failed += message => _dispatcher.BeginInvoke(() => Report($"Video error: {message}"));
            player.Ended += () => _dispatcher.BeginInvoke(() => MediaEnded?.Invoke());
            player.MetadataLoaded += () => _dispatcher.BeginInvoke(() =>
                Report($"Playing {Path.GetFileName(path)} ({FormatDuration(opened.Duration)})"));

            player.Open(path, loop, volume);

            // 다 연 뒤에 렌더 스레드에 넘긴다(여는 중에 프레임을 요청하지 않게).
            lock (_graphics!.RenderLock)
            {
                _video = player;
            }
            player = null;
            ActiveSourceKind = ProjectionSourceKind.Video;
            MediaPath = path;
            // 첫 프레임이 오기 전에도 한 번은 그려 화면을 갱신한다.
            RequestRender();
        }
        catch (Exception ex)
        {
            player?.Dispose();
            StopMedia();
            Report($"Could not open the video: {ex.Message}");
        }
    }

    /// <summary>변환된 슬라이드 이미지 목록을 앱 내부에서 재생한다.</summary>
    public void StartSlides(IReadOnlyList<string> slidePaths, string? label, string? originalPath)
    {
        EnsureDevice();
        StopSource();

        if (_window is null)
        {
            Report("Choose an output monitor first.");
            return;
        }
        if (slidePaths.Count == 0)
        {
            Report("There are no slides to show.");
            return;
        }

        try
        {
            _slides = new SlideDeck(_graphics!);
            _slides.Load(slidePaths, label);
            ActiveSourceKind = ProjectionSourceKind.Slides;
            MediaPath = originalPath;
            PushSlideToRenderer();
            Report($"Showing slides: {label} ({slidePaths.Count} pages)");
        }
        catch (Exception ex)
        {
            StopMedia();
            Report($"Could not open the slides: {ex.Message}");
        }
    }

    public bool NextSlide()
    {
        if (_slides is null || !_slides.Next()) return false;
        PushSlideToRenderer();
        SlideChanged?.Invoke();
        return true;
    }

    public bool PreviousSlide()
    {
        if (_slides is null || !_slides.Previous()) return false;
        PushSlideToRenderer();
        SlideChanged?.Invoke();
        return true;
    }

    public bool GoToSlide(int index)
    {
        if (_slides is null || !_slides.GoTo(index)) return false;
        PushSlideToRenderer();
        SlideChanged?.Invoke();
        return true;
    }

    /// <summary>동영상 재생/일시정지 토글(슬라이드는 다음 장으로 넘어간다).</summary>
    public void ToggleMediaPlayback()
    {
        if (_video is { } video) video.TogglePlayPause();
        else if (_slides is not null) NextSlide();
    }

    private void PushSlideToRenderer()
    {
        GraphicsDevice? graphics = _graphics;
        SlideDeck? deck = _slides;
        if (graphics is null || deck?.CurrentTexture is null) return;

        lock (graphics.RenderLock)
        {
            _renderer?.SetSourceTexture(deck.CurrentTexture, deck.CurrentSize);
        }
        RequestRender();
    }

    private void StopMedia()
    {
        VideoPlayer? video;
        SlideDeck? slides = _slides;
        _slides = null;

        GraphicsDevice? graphics = _graphics;
        if (graphics is not null)
        {
            // 렌더 스레드는 락 안에서만 동영상과 소스 텍스처를 쓰므로,
            // 락 안에서 떼어 낸 뒤에는 락 밖에서 해제해도 안전하다.
            lock (graphics.RenderLock)
            {
                video = _video;
                _video = null;
                _renderer?.ClearSource();
            }
        }
        else
        {
            video = _video;
            _video = null;
        }

        video?.Dispose();
        slides?.Dispose();

        if (IsMediaActive) ActiveSourceKind = ProjectionSourceKind.None;
        MediaPath = null;
        RequestRender();
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds)) return "0:00";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes}:{span.Seconds:D2}";
    }

    /// <summary>제어점이나 테셀레이션이 바뀌었을 때 메시를 다시 만들고 그린다.</summary>
    public void InvalidateGeometry()
    {
        // 렌더 스레드는 락 안에서 메시를 다시 만든 뒤 더티 표시를 지운다.
        // 락 밖에서 표시하면 그 사이에 들어온 표시가 지워져 마지막 드래그 위치가 반영되지 않는다.
        GraphicsDevice? graphics = _graphics;
        if (graphics is null)
        {
            _renderer?.InvalidateMesh();
        }
        else
        {
            lock (graphics.RenderLock)
            {
                _renderer?.InvalidateMesh();
            }
        }
        RequestRender();
    }

    /// <summary>
    /// 격자 크기 · 테셀레이션 · 실행 취소 복원처럼 <b>구조가 바뀌는</b> 설정 변경을 렌더 락 안에서 적용한다.
    /// 렌더 스레드는 같은 설정을 읽어 메시와 오버레이를 만들므로, 락 없이 바꾸면
    /// 점 배열과 격자 크기가 어긋난 순간을 읽어 IndexOutOfRangeException 이 난다.
    /// 적용 후 메시를 다시 만들고 그린다.
    /// </summary>
    public void EditSettings(Action<WarpSettings> edit)
    {
        GraphicsDevice? graphics = _graphics;
        if (graphics is null)
        {
            edit(Settings);
        }
        else
        {
            lock (graphics.RenderLock)
            {
                edit(Settings);
            }
        }
        InvalidateGeometry();
    }

    private void EnsureDevice()
    {
        if (_graphics is not null) return;
        _graphics = GraphicsDevice.Create();
    }

    private void EnsureCaptureEngine()
    {
        if (_capture is not null) return;
        _capture = new CaptureEngine(_graphics!.WinRTDevice);
        _capture.FrameArrived += OnFrameArrived;
        _capture.SourceClosed += OnSourceClosed;
        _capture.CaptureFailed += OnCaptureFailed;
        // 프레임 풀 재생성(FramePoolRecreated)은 신경 쓰지 않아도 된다.
        // 프레임을 엔진 소유 텍스처로 복사해 쓰므로 풀 텍스처를 가리키는 뷰를 갖고 있지 않다.
    }

    /// <summary>
    /// 캡처 스레드에서 호출된다. 프레임을 엔진 소유 텍스처로 복사하고 렌더 스레드에 알리기만 한다.
    /// 복사는 GPU 명령을 쌓을 뿐이라 짧고, vsync 대기는 렌더 스레드가 한다.
    /// </summary>
    private void OnFrameArrived(ID3D11Texture2D texture, SizeInt32 contentSize)
    {
        GraphicsDevice? graphics = _graphics;
        if (graphics is null) return;

        lock (graphics.RenderLock)
        {
            if (!_acceptCaptureFrames || _renderer is null || _window is null) return;

            ID3D11Texture2D copy = EnsureCaptureCopy(graphics, texture);
            graphics.Context.CopyResource(copy, texture);
            _renderer.SetSourceTexture(copy, contentSize);
            _renderRequested = true;
        }
        _renderSignal.Set();
    }

    /// <summary>캡처 프레임과 같은 크기 · 포맷의 복사 대상 텍스처를 준비한다. 호출자는 렌더 락을 잡고 있어야 한다.</summary>
    private ID3D11Texture2D EnsureCaptureCopy(GraphicsDevice graphics, ID3D11Texture2D source)
    {
        Texture2DDescription sourceDescription = source.Description;
        if (_captureCopy is not null)
        {
            Texture2DDescription current = _captureCopy.Description;
            if (current.Width == sourceDescription.Width &&
                current.Height == sourceDescription.Height &&
                current.Format == sourceDescription.Format)
                return _captureCopy;

            // 크기가 바뀌었다(소스 창 크기 변경). 이전 텍스처를 가리키는 셰이더 뷰부터 버린다.
            _renderer?.ClearSource();
            DisposeCaptureCopy();
        }

        _captureCopy = graphics.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = sourceDescription.Width,
            Height = sourceDescription.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = sourceDescription.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });
        return _captureCopy;
    }

    /// <summary>호출자는 렌더 락을 잡고 있어야 하며, 먼저 렌더러의 소스를 비워야 한다.</summary>
    private void DisposeCaptureCopy()
    {
        _captureCopy?.Dispose();
        _captureCopy = null;
    }

    private void OnSourceClosed() => _dispatcher.BeginInvoke(() =>
    {
        Report("The captured window was closed.");
        StopCapture();
        SourceLost?.Invoke();
    });

    private void OnCaptureFailed(Exception exception) => _dispatcher.BeginInvoke(() =>
    {
        Report($"Capture error: {exception.Message}");
        CheckDeviceLost();
    });

    private void OnOutputWindowClosed() => _dispatcher.BeginInvoke(StopOutput);

    /// <summary>디바이스 로스트를 감지하면 전체 파이프라인을 다시 만든다(어느 스레드에서 불러도 된다).</summary>
    private void CheckDeviceLost()
    {
        GraphicsDevice? graphics = _graphics;
        if (graphics is null || _recreating || !graphics.IsDeviceLost()) return;
        _recreating = true;
        _dispatcher.BeginInvoke(RecreateDevice);
    }

    /// <summary>렌더러 · 출력 창 · 캡처 복사본을 해제한다. 렌더 스레드를 먼저 멈춰야 한다.</summary>
    private void ReleaseOutputResources()
    {
        GraphicsDevice? graphics = _graphics;
        if (graphics is null) return;

        // 이미 들어와 있던 캡처 콜백이 쓰는 중일 수 있으므로 락 안에서 해제한다.
        lock (graphics.RenderLock)
        {
            _acceptCaptureFrames = false;
            _renderer?.ClearSource();
            DisposeCaptureCopy();
            _renderer?.Dispose();
            _renderer = null;
            _window?.Dispose();
            _window = null;
        }
    }

    private void RecreateDevice()
    {
        try
        {
            Report("The graphics device was reset — rebuilding the pipeline.");

            CaptureTarget? source = _capture?.CurrentTarget;
            MonitorInfo? monitor = _outputMonitor;
            ProjectionSourceKind previousKind = ActiveSourceKind;
            string? previousMedia = MediaPath;

            StopMedia();
            _capture?.Dispose();
            _capture = null;

            if (_window is not null) OutputWindowClosing?.Invoke(_window);
            StopRenderThread();
            ReleaseOutputResources();

            _graphics?.Dispose();
            _graphics = null;

            EnsureDevice();
            if (monitor is not null) StartOutput(monitor);
            if (source is not null && previousKind == ProjectionSourceKind.Capture) StartCapture(source);
            else if (previousMedia is not null)
                Report($"Device recovered. Please open the media again: {Path.GetFileName(previousMedia)}");
        }
        catch (Exception ex)
        {
            Report($"Could not recover the device: {ex.Message}");
        }
        finally
        {
            _recreating = false;
        }
    }

    private void Report(string message) => StatusChanged?.Invoke(message);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopMedia();
        _capture?.Dispose();
        _capture = null;
        StopRenderThread();
        ReleaseOutputResources();
        _graphics?.Dispose();
        _graphics = null;
        // _renderSignal 은 해제하지 않는다. 늦게 도착한 캡처 콜백이나 UI 이벤트가 Set 을 불러도 안전해야 한다.
    }
}
