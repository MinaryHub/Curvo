using System.Windows.Threading;
using Vortice.Direct3D11;
using Windows.Graphics;
using ProjectorWarp.Capture;
using ProjectorWarp.Geometry;
using ProjectorWarp.Media;

namespace ProjectorWarp.Rendering;

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
            Report($"출력 모니터를 {monitor.DeviceName} 로 이동했습니다.");
            return;
        }

        _window = new OutputWindow(_graphics!, monitor);
        _window.Closed += OnOutputWindowClosed;
        _window.SizeChanged += RequestRender;
        _renderer = new WarpRenderer(_graphics!);
        OutputWindowCreated?.Invoke(_window);

        if (!_window.IsExcludedFromCapture)
            Report("경고: 출력 창을 캡처 대상에서 제외하지 못했습니다. 모니터 캡처 시 피드백 루프가 발생할 수 있습니다.");
        else
            Report($"{monitor.DeviceName} 에 출력 창을 열었습니다.");

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
        Report("출력을 중지했습니다.");
    }

    /// <summary>캡처 소스를 지정한다. 출력 창이 아직 없으면 열릴 때 시작한다.</summary>
    public void StartCapture(CaptureTarget target)
    {
        if (!CaptureEngine.IsSupported)
        {
            Report("이 PC 에서는 Windows.Graphics.Capture 를 사용할 수 없습니다. (Windows 10 1903 이상 필요)");
            return;
        }

        EnsureDevice();

        if (_window is null)
        {
            _pendingSource = target;
            Report("출력 모니터를 먼저 선택하세요. 선택하면 캡처가 시작됩니다.");
            return;
        }

        try
        {
            StopMedia();
            EnsureCaptureEngine();
            _capture!.Start(target);
            ActiveSourceKind = ProjectionSourceKind.Capture;
            MediaPath = null;
            Report($"캡처 시작: {target.DisplayName}");
        }
        catch (Exception ex)
        {
            Report($"캡처를 시작하지 못했습니다: {ex.Message}");
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
        if (_graphics is null || _renderer is null || _window is null) return;
        // 동영상 재생 중에는 전용 루프가 매 프레임 다시 그리므로 중복 렌더를 피한다.
        if (_videoLoopRunning) return;
        lock (_graphics.RenderLock)
        {
            if (_renderer is null || _window is null) return;
            _renderer.CurrentPattern = Overlay.Pattern;
            _renderer.Render(_window, Settings, Overlay);
        }
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
            Report("출력 모니터를 먼저 선택하세요.");
            return;
        }

        try
        {
            _video = new VideoPlayer(_graphics!);
            _video.Failed += message => _dispatcher.BeginInvoke(() => Report($"동영상 오류: {message}"));
            _video.Ended += () => _dispatcher.BeginInvoke(() => MediaEnded?.Invoke());
            _video.MetadataLoaded += () => _dispatcher.BeginInvoke(() =>
                Report($"동영상 재생: {Path.GetFileName(path)} ({FormatDuration(_video?.Duration ?? 0)})"));

            _video.Open(path, loop, volume);
            ActiveSourceKind = ProjectionSourceKind.Video;
            MediaPath = path;
            StartVideoLoop();
        }
        catch (Exception ex)
        {
            StopMedia();
            Report($"동영상을 열지 못했습니다: {ex.Message}");
        }
    }

    /// <summary>변환된 슬라이드 이미지 목록을 앱 내부에서 재생한다.</summary>
    public void StartSlides(IReadOnlyList<string> slidePaths, string? label, string? originalPath)
    {
        EnsureDevice();
        StopSource();

        if (_window is null)
        {
            Report("출력 모니터를 먼저 선택하세요.");
            return;
        }
        if (slidePaths.Count == 0)
        {
            Report("표시할 슬라이드가 없습니다.");
            return;
        }

        try
        {
            _slides = new SlideDeck(_graphics!);
            _slides.Load(slidePaths, label);
            ActiveSourceKind = ProjectionSourceKind.Slides;
            MediaPath = originalPath;
            PushSlideToRenderer();
            Report($"슬라이드 재생: {label} ({slidePaths.Count}장)");
        }
        catch (Exception ex)
        {
            StopMedia();
            Report($"슬라이드를 열지 못했습니다: {ex.Message}");
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
        _videoThread = new Thread(VideoRenderLoop)
        {
            IsBackground = true,
            Name = "ProjectorWarp.VideoRender",
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

                lock (graphics.RenderLock)
                {
                    if (_renderer is not null && _window is not null)
                    {
                        if (player.TryAcquireFrame() && player.FrameTexture is not null)
                            _renderer.SetSourceTexture(player.FrameTexture, player.FrameSize);

                        _renderer.CurrentPattern = Overlay.Pattern;
                        _renderer.Render(_window, Settings, Overlay);
                        rendered = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _dispatcher.BeginInvoke(() => Report($"동영상 렌더 오류: {ex.Message}"));
                break;
            }

            // 출력 창이 없어 Present 로 대기하지 못하는 동안 CPU 를 태우지 않는다.
            if (!rendered) Thread.Sleep(8);
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

        lock (graphics.RenderLock)
        {
            if (_renderer is null || _window is null) return;
            _renderer.SetSourceTexture(texture, contentSize);
            _renderer.CurrentPattern = Overlay.Pattern;
            _renderer.Render(_window, Settings, Overlay);
        }
        CheckDeviceLost();
    }

    private void OnSourceClosed() => _dispatcher.BeginInvoke(() =>
    {
        Report("캡처 대상 창이 닫혔습니다.");
        StopCapture();
        SourceLost?.Invoke();
    });

    private void OnCaptureFailed(Exception exception) => _dispatcher.BeginInvoke(() =>
    {
        Report($"캡처 오류: {exception.Message}");
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
            Report("그래픽 디바이스가 재설정되어 파이프라인을 다시 만듭니다.");

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
                Report($"디바이스를 복구했습니다. 미디어를 다시 열어주세요: {Path.GetFileName(previousMedia)}");
        }
        catch (Exception ex)
        {
            Report($"디바이스 복구에 실패했습니다: {ex.Message}");
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
