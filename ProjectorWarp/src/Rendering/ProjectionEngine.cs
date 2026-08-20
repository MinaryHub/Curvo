using System.Windows.Threading;
using Vortice.Direct3D11;
using Windows.Graphics;
using Curvo.Capture;
using Curvo.Geometry;
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
/// </summary>
internal sealed class ProjectionEngine : IDisposable
{
    private readonly Dispatcher _dispatcher;

    private GraphicsDevice? _graphics;
    private CaptureEngine? _capture;
    private WarpRenderer? _renderer;
    private OutputWindow? _window;
    private MonitorInfo? _outputMonitor;
    private CaptureTarget? _pendingSource;
    private VideoPlayer? _video;
    private SlideDeck? _slides;
    private Thread? _videoThread;
    private volatile bool _videoLoopRunning;
    /// <summary>소스 프레임과 무관하게 다시 그려야 할 변경(설정·오버레이·기하)이 있는지.</summary>
    private volatile bool _renderRequested = true;
    private bool _recreating;
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

        _window = new OutputWindow(_graphics!, monitor);
        _window.Closed += OnOutputWindowClosed;
        _window.SizeChanged += RequestRender;
        _renderer = new WarpRenderer(_graphics!);
        OutputWindowCreated?.Invoke(_window);

        if (!_window.IsExcludedFromCapture)
            Report("Warning: could not exclude the output window from capture. Capturing a whole monitor may create a feedback loop.");
        else
            Report($"Opened the output window on {monitor.DeviceName}.");

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
        lock (_graphics!.RenderLock)
        {
            _renderer?.Dispose();
            _renderer = null;
            _window.Dispose();
            _window = null;
        }
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
        _capture?.Stop();
        if (ActiveSourceKind == ProjectionSourceKind.Capture) ActiveSourceKind = ProjectionSourceKind.None;
        GraphicsDevice? graphics = _graphics;
        if (graphics is not null)
        {
            lock (graphics.RenderLock)
            {
                _renderer?.ClearSource();
            }
        }
        RequestRender();
    }

    /// <summary>편집 중 등 프레임이 오지 않을 때 즉시 다시 그린다.</summary>
    public void RequestRender()
    {
        // 동영상 루프는 이 플래그를 보고 그리므로, 루프가 도는 중에도 반드시 표시해 둔다.
        _renderRequested = true;
        if (_graphics is null || _renderer is null || _window is null) return;
        if (_videoLoopRunning) return;
        RenderOnce();
    }

    /// <summary>
    /// 한 프레임을 그리고 화면에 내보낸다.
    /// vsync Present 는 최대 한 프레임 동안 블록되므로 <b>렌더 락을 놓은 뒤에</b> 호출한다.
    /// 락 안에서 하면 캡처 스레드와 UI 스레드가 그만큼 함께 멈춘다.
    /// </summary>
    private void RenderOnce()
    {
        GraphicsDevice? graphics = _graphics;
        if (graphics is null) return;

        OutputWindow? window;
        lock (graphics.RenderLock)
        {
            window = _window;
            if (_renderer is null || window is null) return;
            _renderRequested = false;
            _renderer.CurrentPattern = Overlay.Pattern;
            _renderer.Render(window, Settings, Overlay);
        }

        window.Present(verticalSync: true);
        CheckDeviceLost();
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

        try
        {
            _video = new VideoPlayer(_graphics!);
            _video.Failed += message => _dispatcher.BeginInvoke(() => Report($"Video error: {message}"));
            _video.Ended += () => _dispatcher.BeginInvoke(() => MediaEnded?.Invoke());
            _video.MetadataLoaded += () => _dispatcher.BeginInvoke(() =>
                Report($"Playing {Path.GetFileName(path)} ({FormatDuration(_video?.Duration ?? 0)})"));

            _video.Open(path, loop, volume);
            ActiveSourceKind = ProjectionSourceKind.Video;
            MediaPath = path;
            StartVideoLoop();
        }
        catch (Exception ex)
        {
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
        if (_video is not null) _video.TogglePlayPause();
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

    /// <summary>동영상은 프레임이 계속 들어오므로 전용 렌더 루프를 돈다(Present 의 vsync 로 속도 조절).</summary>
    private void StartVideoLoop()
    {
        _videoLoopRunning = true;
        // 첫 프레임이 오기 전에도 한 번은 그려 화면을 갱신한다.
        _renderRequested = true;
        _videoThread = new Thread(VideoRenderLoop)
        {
            IsBackground = true,
            Name = "Curvo.VideoRender",
        };
        _videoThread.Start();
    }

    private void StopVideoLoop()
    {
        _videoLoopRunning = false;
        Thread? thread = _videoThread;
        _videoThread = null;
        thread?.Join(TimeSpan.FromSeconds(1));
    }

    private void VideoRenderLoop()
    {
        while (_videoLoopRunning)
        {
            bool rendered = false;
            try
            {
                GraphicsDevice? graphics = _graphics;
                VideoPlayer? player = _video;
                if (graphics is null || player is null) break;

                OutputWindow? window = null;
                lock (graphics.RenderLock)
                {
                    window = _window;
                    if (_renderer is not null && window is not null)
                    {
                        bool newFrame = player.TryAcquireFrame() && player.FrameTexture is not null;
                        if (newFrame) _renderer.SetSourceTexture(player.FrameTexture!, player.FrameSize);

                        // 동영상은 보통 24~30fps 다. 새 프레임도 없고 바뀐 설정도 없으면
                        // 같은 그림을 vsync 속도로 다시 그리는 셈이므로 건너뛴다.
                        if (newFrame || _renderRequested)
                        {
                            _renderRequested = false;
                            _renderer.CurrentPattern = Overlay.Pattern;
                            _renderer.Render(window, Settings, Overlay);
                            rendered = true;
                        }
                    }
                }

                // vsync 대기는 렌더 락 밖에서 한다.
                if (rendered) window!.Present(verticalSync: true);
            }
            catch (Exception ex)
            {
                _dispatcher.BeginInvoke(() => Report($"Video render error: {ex.Message}"));
                break;
            }

            // 그리지 않은 바퀴에서는 다음 프레임을 놓치지 않을 만큼만 쉰다.
            if (!rendered) Thread.Sleep(AppConfig.VideoFramePollIntervalMilliseconds);
        }
    }

    private void StopMedia()
    {
        StopVideoLoop();

        GraphicsDevice? graphics = _graphics;
        if (graphics is not null)
        {
            lock (graphics.RenderLock)
            {
                _renderer?.ClearSource();
            }
        }

        _video?.Dispose();
        _video = null;
        _slides?.Dispose();
        _slides = null;

        if (IsMediaActive) ActiveSourceKind = ProjectionSourceKind.None;
        MediaPath = null;
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
        _renderer?.InvalidateMesh();
        RequestRender();
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
        _capture.FramePoolRecreated += OnFramePoolRecreated;
    }

    /// <summary>프레임 풀이 새로 만들어지면 이전 텍스처의 셰이더 뷰 캐시를 버린다.</summary>
    private void OnFramePoolRecreated()
    {
        GraphicsDevice? graphics = _graphics;
        if (graphics is null) return;
        lock (graphics.RenderLock)
        {
            _renderer?.ClearSource();
        }
    }

    private void OnFrameArrived(ID3D11Texture2D texture, SizeInt32 contentSize)
    {
        GraphicsDevice? graphics = _graphics;
        if (graphics is null) return;

        OutputWindow? window;
        lock (graphics.RenderLock)
        {
            window = _window;
            if (_renderer is null || window is null) return;
            _renderRequested = false;
            _renderer.SetSourceTexture(texture, contentSize);
            _renderer.CurrentPattern = Overlay.Pattern;
            _renderer.Render(window, Settings, Overlay);
        }

        // vsync 대기를 락 밖에서 해야 캡처 스레드가 락을 오래 붙잡지 않는다.
        window.Present(verticalSync: true);
        CheckDeviceLost();
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

    /// <summary>디바이스 로스트를 감지하면 전체 파이프라인을 다시 만든다.</summary>
    private void CheckDeviceLost()
    {
        if (_graphics is null || _recreating || !_graphics.IsDeviceLost()) return;
        _recreating = true;
        _dispatcher.BeginInvoke(RecreateDevice);
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
            _renderer?.Dispose();
            _renderer = null;
            _window?.Dispose();
            _window = null;

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
        _renderer?.Dispose();
        _renderer = null;
        _window?.Dispose();
        _window = null;
        _graphics?.Dispose();
        _graphics = null;
    }
}
