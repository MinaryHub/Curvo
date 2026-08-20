using System.Diagnostics;
using System.Numerics;
using System.Windows.Threading;
using ProjectorWarp;
using ProjectorWarp.Capture;
using ProjectorWarp.Geometry;
using ProjectorWarp.Interop;
using ProjectorWarp.Media;
using ProjectorWarp.Presets;
using ProjectorWarp.Rendering;
using ProjectorWarp.Update;
using Vortice.Direct3D11;
using Windows.Graphics;

int failures = 0;

void Check(bool condition, string name)
{
    Console.WriteLine((condition ? "PASS  " : "FAIL  ") + name);
    if (!condition) failures++;
}

// 1. 셰이더 컴파일
try
{
    ShaderLoader.CompileVertexShader("warp.hlsl", "VSMain");
    ShaderLoader.CompilePixelShader("warp.hlsl", "PSMain");
    ShaderLoader.CompileVertexShader("overlay.hlsl", "VSMain");
    ShaderLoader.CompilePixelShader("overlay.hlsl", "PSMain");
    Check(true, "shaders compile");
}
catch (Exception ex)
{
    Console.WriteLine(ex.Message);
    Check(false, "shaders compile");
}

// 2. 균일 격자 베지어는 항등 매핑이어야 한다
{
    bool ok = true;
    foreach (int size in new[] { 3, 4, 5, 6 })
    {
        var grid = new ControlPointGrid(size);
        for (float u = 0; u <= 1.0001f; u += 0.125f)
            for (float v = 0; v <= 1.0001f; v += 0.125f)
            {
                Vector2 p = Bezier.Evaluate(grid.Points, size, u, v);
                if (MathF.Abs(p.X - u) > 1e-4f || MathF.Abs(p.Y - v) > 1e-4f) ok = false;
            }
    }
    Check(ok, "bezier identity on uniform grid (3..6)");
}

// 3. 차수 상승이 곡면 형상을 보존한다
{
    var grid = new ControlPointGrid(4);
    grid[5] = new Vector2(0.45f, 0.20f);   // 임의 왜곡
    grid[10] = new Vector2(0.70f, 0.75f);
    grid[2] = new Vector2(0.65f, -0.05f);

    var before = new List<Vector2>();
    for (float u = 0; u <= 1.0001f; u += 0.1f)
        for (float v = 0; v <= 1.0001f; v += 0.1f)
            before.Add(Bezier.Evaluate(grid.Points, 4, u, v));

    var elevated = grid.Clone();
    elevated.Resize(6);

    int index = 0;
    float maxError = 0;
    for (float u = 0; u <= 1.0001f; u += 0.1f)
        for (float v = 0; v <= 1.0001f; v += 0.1f)
        {
            Vector2 after = Bezier.Evaluate(elevated.Points, 6, u, v);
            maxError = MathF.Max(maxError, Vector2.Distance(before[index++], after));
        }
    Check(maxError < 1e-4f, $"degree elevation preserves surface (max error {maxError:E2})");
}

// 4. 호모그래피 코너 매핑과 역변환 왕복
{
    Vector2 tl = new(0.10f, 0.05f), tr = new(0.95f, 0.15f), br = new(0.90f, 0.98f), bl = new(0.02f, 0.80f);
    Homography h = Homography.FromUnitSquare(tl, tr, br, bl);

    float cornerError = 0;
    cornerError = MathF.Max(cornerError, Vector2.Distance(h.Transform(new Vector2(0, 0)), tl));
    cornerError = MathF.Max(cornerError, Vector2.Distance(h.Transform(new Vector2(1, 0)), tr));
    cornerError = MathF.Max(cornerError, Vector2.Distance(h.Transform(new Vector2(1, 1)), br));
    cornerError = MathF.Max(cornerError, Vector2.Distance(h.Transform(new Vector2(0, 1)), bl));
    Check(cornerError < 1e-5f, $"homography maps unit square corners (error {cornerError:E2})");

    Homography inverse = h.Invert();
    float roundTrip = 0;
    for (float u = 0; u <= 1.0001f; u += 0.25f)
        for (float v = 0; v <= 1.0001f; v += 0.25f)
        {
            Vector2 original = new(u, v);
            roundTrip = MathF.Max(roundTrip, Vector2.Distance(inverse.Transform(h.Transform(original)), original));
        }
    Check(roundTrip < 1e-4f, $"homography inverse round trip (error {roundTrip:E2})");
}

// 5. 메시 생성 — 보정 없음이면 격자 그대로, 텍스처 좌표 w=1
{
    var settings = new WarpSettings { Tessellation = 16 };
    var vertices = new WarpVertex[MeshBuilder.VertexCount(16)];
    MeshBuilder.BuildVertices(settings, vertices);

    bool ok = true;
    int side = 17;
    for (int row = 0; row < side; row++)
        for (int column = 0; column < side; column++)
        {
            WarpVertex vertex = vertices[row * side + column];
            float u = column / 16.0f, v = row / 16.0f;
            if (MathF.Abs(vertex.Position.X - u) > 1e-4f || MathF.Abs(vertex.Position.Y - v) > 1e-4f) ok = false;
            if (MathF.Abs(vertex.TexCoord.Z - 1.0f) > 1e-6f) ok = false;
        }
    Check(ok, "mesh identity without correction");

    ushort[] indices = MeshBuilder.BuildIndices(16);
    Check(indices.Length == MeshBuilder.IndexCount(16) && indices.Max() < vertices.Length, "index buffer in range");
}

// 6. 코너 핀이 켜지면 메시 모서리가 코너 점과 일치한다
{
    var settings = new WarpSettings { Tessellation = 16, CornerPinEnabled = true };
    settings.SetCornerPoints(new[]
    {
        new Vector2(0.10f, 0.05f), new Vector2(0.95f, 0.15f),
        new Vector2(0.90f, 0.98f), new Vector2(0.02f, 0.80f),
    });

    var vertices = new WarpVertex[MeshBuilder.VertexCount(16)];
    MeshBuilder.BuildVertices(settings, vertices);
    int side = 17;
    float error = 0;
    error = MathF.Max(error, Vector2.Distance(vertices[0].Position, settings.CornerPoints[0]));
    error = MathF.Max(error, Vector2.Distance(vertices[side - 1].Position, settings.CornerPoints[1]));
    error = MathF.Max(error, Vector2.Distance(vertices[side * side - 1].Position, settings.CornerPoints[2]));
    error = MathF.Max(error, Vector2.Distance(vertices[side * (side - 1)].Position, settings.CornerPoints[3]));
    Check(error < 1e-5f, $"corner pin mesh corners (error {error:E2})");
}

// 7. 프리셋 왕복(제어점 직렬화)
{
    var grid = new ControlPointGrid(5);
    grid[7] = new Vector2(0.33f, 0.44f);
    float[] flat = grid.ToFlatArray();
    ControlPointGrid restored = ControlPointGrid.FromFlatArray(5, flat);
    Check(restored.GridSize == 5 && Vector2.Distance(restored[7], grid[7]) < 1e-6f, "control point serialization round trip");
}

// 8. 실행 취소 / 다시 실행
{
    var settings = new WarpSettings();
    var history = new UndoHistory();
    Vector2 original = settings.Grid[0];
    history.Push(settings);
    settings.Grid[0] = new Vector2(0.25f, 0.25f);
    bool undone = history.Undo(settings) && Vector2.Distance(settings.Grid[0], original) < 1e-6f;
    bool redone = history.Redo(settings) && Vector2.Distance(settings.Grid[0], new Vector2(0.25f, 0.25f)) < 1e-6f;
    Check(undone && redone, "undo / redo restores geometry");
}

// 9. D3D11 디바이스 + WinRT 상호 운용
GraphicsDevice? graphics = null;
try
{
    graphics = GraphicsDevice.Create();
    Check(graphics.Device is not null && graphics.WinRTDevice is not null && graphics.Factory is not null,
        "d3d11 device + winrt interop device");
}
catch (Exception ex)
{
    Console.WriteLine(ex.Message);
    Check(false, "d3d11 device + winrt interop device");
}

// 10. 워핑 렌더러 리소스(셰이더 · 입력 레이아웃 · 상태 객체) 생성
if (graphics is not null)
{
    try
    {
        using var renderer = new WarpRenderer(graphics);
        Check(true, "warp renderer resources (shaders, input layouts, states)");
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex.Message);
        Check(false, "warp renderer resources (shaders, input layouts, states)");
    }
}

// 11. 캡처 아이템 생성(IGraphicsCaptureItemInterop vtable 호출)
List<MonitorInfo> monitors = SourceEnumerator.EnumerateMonitors();
Check(monitors.Count > 0, $"monitor enumeration ({monitors.Count} found)");

if (graphics is not null && monitors.Count > 0 && CaptureEngine.IsSupported)
{
    try
    {
        var item = WinRTInterop.CreateItemForMonitor(monitors[0].Handle);
        Check(item.Size.Width > 0 && item.Size.Height > 0,
            $"capture item for monitor ({item.Size.Width}x{item.Size.Height})");
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex.Message);
        Check(false, "capture item for monitor");
    }

    // 12. 실제 프레임 수신 + GPU 텍스처 획득
    try
    {
        using var capture = new CaptureEngine(graphics.WinRTDevice!);
        using var received = new ManualResetEventSlim(false);
        int width = 0, height = 0;

        capture.FrameArrived += (ID3D11Texture2D texture, SizeInt32 size) =>
        {
            width = texture.Description.Width > 0 ? (int)texture.Description.Width : 0;
            height = size.Height;
            received.Set();
        };
        capture.Start(CaptureTarget.FromMonitor(monitors[0]));
        bool got = received.Wait(TimeSpan.FromSeconds(5));
        capture.Stop();

        Check(got && width > 0 && height > 0, $"wgc frame received ({width}x{height})");
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex.Message);
        Check(false, "wgc frame received");
    }
}
else if (!CaptureEngine.IsSupported)
{
    Console.WriteLine("SKIP  windows.graphics.capture not supported on this OS");
}

// 13. 출력 창 + 스왑체인 + 실제 프레젠트 (보조 모니터에 약 0.5초간 테스트 패턴 표시)
if (graphics is not null && monitors.Count > 0)
{
    try
    {
        MonitorInfo target = monitors[^1];
        using var window = new OutputWindow(graphics, target);
        using var renderer = new WarpRenderer(graphics) { CurrentPattern = TestPattern.Grid };

        var settings = new WarpSettings();
        var overlay = new OverlayState { EditMode = true, ShowReferenceGrid = true, ShowDiagonals = true };

        for (int i = 0; i < 30; i++)
        {
            renderer.Render(window, settings, overlay);
            window.Present(verticalSync: false);
        }

        Check(window.RenderTargetView is not null && window.Width > 0 && window.Height > 0,
            $"output window swapchain + present ({window.Width}x{window.Height} on {target.DeviceName})");
        Check(window.IsExcludedFromCapture, "output window excluded from capture (WDA_EXCLUDEFROMCAPTURE)");

        // 항상 위는 기본으로 꺼져 있어야 하고, 켜면 WS_EX_TOPMOST 가 붙어야 한다.
        bool topmostStyle() =>
            (Win32.GetWindowLongPtr(window.Handle, Win32.GWL_EXSTYLE).ToInt64() & Win32.WS_EX_TOPMOST) != 0;
        bool topmostOffByDefault = !window.IsTopmost && !topmostStyle();
        window.SetTopmost(true);
        bool topmostApplied = window.IsTopmost && topmostStyle();
        window.SetTopmost(false);
        Check(topmostOffByDefault && topmostApplied && !window.IsTopmost && !topmostStyle(),
            $"output window topmost off by default, toggles on demand (default={topmostOffByDefault} on={topmostApplied})");
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex.Message);
        Check(false, "output window swapchain + present");
    }
}

// 14. 앱 설정 저장/불러오기 왕복 (기존 파일은 보존)
{
    string path = AppSettingsStore.FilePath;
    string? backup = File.Exists(path) ? File.ReadAllText(path) : null;
    try
    {
        var written = new AppSettings
        {
            LaunchAtLogon = false,
            AutoStartProjection = true,
            StartMinimized = true,
            StartupPresetPath = @"C:\temp\example-preset.json",
            AutoStartRetrySeconds = 45,
            // 기본값(꺼짐)과 다른 값이어야 왕복이 실제로 확인된다.
            OutputTopmost = true,
        };
        bool saved = AppSettingsStore.TrySave(written, out string? saveError);
        AppSettings loaded = AppSettingsStore.Load();

        Check(saved && loaded.AutoStartProjection && loaded.StartMinimized && loaded.OutputTopmost
              && loaded.AutoStartRetrySeconds == 45 && loaded.StartupPresetPath == written.StartupPresetPath,
            $"app settings round trip{(saved ? string.Empty : " (save failed: " + saveError + ")")}");
    }
    finally
    {
        if (backup is null) File.Delete(path); else File.WriteAllText(path, backup);
    }
}

// 15. 로그온 자동 실행 등록/해제 (원래 상태로 복원)
{
    bool wasEnabled = AutoStartRegistry.IsEnabled();
    try
    {
        bool enabled = AutoStartRegistry.TryEnable(out string? enableError);
        bool readsBack = AutoStartRegistry.IsEnabled();
        bool disabled = AutoStartRegistry.TryDisable(out string? disableError);
        bool cleared = !AutoStartRegistry.IsEnabled();

        Check(enabled && readsBack && disabled && cleared,
            $"logon auto start enable/disable (enable={enabled} {enableError} disable={disabled} {disableError})");
    }
    finally
    {
        if (wasEnabled) AutoStartRegistry.TryEnable(out _); else AutoStartRegistry.TryDisable(out _);
    }
}

// 16. 프리셋 파일 왕복 (설정 저장 → 불러오기)
{
    string path = Path.Combine(Path.GetTempPath(), "projectorwarp-check-preset.json");
    try
    {
        var settings = new WarpSettings { CornerPinEnabled = true, Tessellation = 96 };
        settings.Grid.Resize(5);
        settings.Grid[6] = new Vector2(0.4f, 0.6f);
        settings.EdgeBlendEnabled = true;
        settings.EdgeBlendLeft = 0.15f;

        Preset written = Preset.FromState("check", settings, null, @"\\.\DISPLAY1");
        PresetStore.Save(written, path);

        Preset? read = PresetStore.Load(path);
        var restored = new WarpSettings();
        read!.ApplyTo(restored);

        Check(restored.CornerPinEnabled && restored.Tessellation == 96 && restored.Grid.GridSize == 5
              && Vector2.Distance(restored.Grid[6], settings.Grid[6]) < 1e-6f
              && restored.EdgeBlendEnabled && MathF.Abs(restored.EdgeBlendLeft - 0.15f) < 1e-6f
              && read.Output.MonitorDeviceName == @"\\.\DISPLAY1",
            "preset file round trip (geometry + output monitor)");
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex.Message);
        Check(false, "preset file round trip");
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

// 17. 내부 동영상 재생 (Media Foundation) — 샘플 파일 경로를 인자로 받는다
if (graphics is not null)
{
    string? clip = args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal));
    if (clip is null || !File.Exists(clip))
    {
        Console.WriteLine("SKIP  video playback (샘플 파일 인자가 없음)");
    }
    else
    {

        VideoPlayer? player = null;
        try
        {
            player = new VideoPlayer(graphics);
            string? failure = null;
            using var metadata = new ManualResetEventSlim(false);
            player.Failed += message => { failure = message; metadata.Set(); };
            player.MetadataLoaded += () => metadata.Set();

            player.Open(clip, loop: true, volume: 0.0);
            bool loaded = metadata.Wait(TimeSpan.FromSeconds(10));
            Check(loaded && failure is null, $"video opened ({Path.GetFileName(clip)}) {failure}");

            player.Play();

            int frames = 0;
            bool anyContent = false;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && (frames == 0 || !anyContent))
            {
                if (player.TryAcquireFrame())
                {
                    frames++;
                    // 페이드인으로 시작하는 영상이 있으므로 여러 프레임을 확인한다.
                    if (player.FrameTexture is not null && NotBlank(graphics, player.FrameTexture)) anyContent = true;
                }
                else
                {
                    Thread.Sleep(5);
                }
            }
            Check(frames > 0, $"video frames transferred to d3d texture ({frames} frames, {player.FrameSize.Width}x{player.FrameSize.Height})");
            Check(anyContent, "decoded frames contain non-black content");
            Check(player.Duration > 0.0, $"video duration reported ({player.Duration:F2}s)");

            player.Pause();
            Check(player.IsPaused, "pause works");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
            Check(false, "video playback");
        }
        finally
        {
            player?.Dispose();
        }

        // 17-b. 비ASCII 파일명 회귀 검사.
        // file:// URI 로 넘기면 퍼센트 인코딩이 ANSI 로 해석되어 0x80070002 로 실패했다.
        string awkward = Path.Combine(Path.GetTempPath(), "프로젝터워프 테스트 #1" + Path.GetExtension(clip));
        try
        {
            File.Copy(clip, awkward, overwrite: true);
            using var awkwardPlayer = new VideoPlayer(graphics);
            string? awkwardFailure = null;
            awkwardPlayer.Failed += message => awkwardFailure ??= message;
            awkwardPlayer.Open(awkward, loop: false, volume: 0.0);

            int awkwardFrames = 0;
            var awkwardDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < awkwardDeadline && awkwardFrames == 0 && awkwardFailure is null)
            {
                if (awkwardPlayer.TryAcquireFrame() && awkwardPlayer.FrameTexture is not null) awkwardFrames++;
                else Thread.Sleep(5);
            }
            Check(awkwardFrames > 0 && awkwardFailure is null,
                $"video with non-ascii file name plays ({Path.GetFileName(awkward)}) {awkwardFailure}");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            Check(false, "video with non-ascii file name plays");
        }
        finally
        {
            try
            {
                if (File.Exists(awkward)) File.Delete(awkward);
            }
            catch (IOException)
            {
                // 임시 파일이 남아도 무해하다.
            }
        }
    }
}

// 18. 슬라이드 가져오기 + GPU 업로드 (이미지 / PDF / PPT)
if (graphics is not null)
{
    string? sampleFolder = args.Skip(1).FirstOrDefault(Directory.Exists);
    if (sampleFolder is null)
    {
        Console.WriteLine("SKIP  slide import (샘플 폴더 인자가 없음)");
    }
    else
    {
        // 이미지 여러 장을 슬라이드로
        try
        {
            List<string> images = SlideImporter.ImportImages(Directory.GetFiles(sampleFolder, "slide-*.png"));
            using var deck = new SlideDeck(graphics);
            deck.Load(images, "images");
            bool first = deck.CurrentTexture is not null && NotBlank(graphics, deck.CurrentTexture);
            bool advanced = deck.Next() && deck.CurrentIndex == 1 && deck.CurrentTexture is not null;
            bool wrapped = deck.Next() && deck.CurrentIndex == 0;
            Check(images.Count >= 2 && first && advanced && wrapped,
                $"image slides load and advance ({images.Count} slides, {deck.CurrentSize.Width}x{deck.CurrentSize.Height})");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            Check(false, "image slides load and advance");
        }

        // PDF → 페이지별 이미지 (Windows 내장 렌더러)
        string pdf = Path.Combine(sampleFolder, "testdeck.pdf");
        if (File.Exists(pdf))
        {
            try
            {
                List<string> pages = SlideImporter.Import(pdf);
                using var deck = new SlideDeck(graphics);
                deck.Load(pages, Path.GetFileName(pdf));
                bool rendered = deck.CurrentTexture is not null && NotBlank(graphics, deck.CurrentTexture);
                Check(pages.Count == 3 && rendered,
                    $"pdf import ({pages.Count} pages, {deck.CurrentSize.Width}x{deck.CurrentSize.Height})");

                // 두 번째 호출은 캐시에서 즉시 반환되어야 한다
                List<string> again = SlideImporter.Import(pdf);
                Check(again.Count == pages.Count && again[0] == pages[0], "pdf import uses cache on reopen");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                Check(false, "pdf import");
            }
        }

        // PPTX → PowerPoint COM 내보내기
        string pptx = Path.Combine(sampleFolder, "testdeck.pptx");
        if (File.Exists(pptx))
        {
            try
            {
                List<string> slides = SlideImporter.Import(pptx, message => Console.WriteLine("      " + message));
                using var deck = new SlideDeck(graphics);
                deck.Load(slides, Path.GetFileName(pptx));
                bool rendered = deck.CurrentTexture is not null && NotBlank(graphics, deck.CurrentTexture);
                Check(slides.Count == 3 && rendered,
                    $"pptx import via powerpoint ({slides.Count} slides, {deck.CurrentSize.Width}x{deck.CurrentSize.Height})");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                Check(false, "pptx import via powerpoint");
            }
        }
    }
}

// 19. 내부 미디어 → 워핑 파이프라인 → 출력 창 (동영상 프레임과 슬라이드 텍스처를 실제로 그린다)
if (graphics is not null && monitors.Count > 0)
{
    string? clipPath = args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal) && File.Exists(a));
    string? folder = args.Skip(1).FirstOrDefault(Directory.Exists);

    try
    {
        MonitorInfo target = monitors[^1];
        using var window = new OutputWindow(graphics, target);
        using var renderer = new WarpRenderer(graphics);
        var settings = new WarpSettings { CornerPinEnabled = true };
        settings.Grid[5] = new Vector2(0.40f, 0.28f); // 곡면 왜곡을 실제로 적용
        var overlay = new OverlayState();

        bool videoRendered = false;
        if (clipPath is not null)
        {
            using var player = new VideoPlayer(graphics);
            player.Open(clipPath, loop: true, volume: 0.0);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && !videoRendered)
            {
                if (player.TryAcquireFrame() && player.FrameTexture is not null)
                {
                    renderer.SetSourceTexture(player.FrameTexture, player.FrameSize);
                    for (int i = 0; i < 10; i++)
                    {
                        renderer.Render(window, settings, overlay);
                        window.Present(verticalSync: false);
                    }
                    videoRendered = true;
                }
                else
                {
                    Thread.Sleep(5);
                }
            }
        }
        Check(videoRendered, "video frame warped and presented to output window");

        bool slideRendered = false;
        if (folder is not null)
        {
            List<string> images = SlideImporter.ImportImages(Directory.GetFiles(folder, "slide-*.png"));
            if (images.Count > 0)
            {
                using var deck = new SlideDeck(graphics);
                deck.Load(images, "images");
                if (deck.CurrentTexture is not null)
                {
                    renderer.SetSourceTexture(deck.CurrentTexture, deck.CurrentSize);
                    overlay.EditMode = true;
                    for (int i = 0; i < 10; i++)
                    {
                        renderer.Render(window, settings, overlay);
                        window.Present(verticalSync: false);
                    }
                    slideRendered = true;
                }
            }
        }
        if (folder is null)
            Console.WriteLine("SKIP  slide texture warped and presented (샘플 폴더 인자가 없음)");
        else
            Check(slideRendered, "slide texture warped and presented with edit overlay");
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex.ToString());
        Check(false, "media through warp pipeline");
    }
}

// 21-b. 동영상 렌더 루프가 소스 프레임 속도만큼만 그리는지 (60Hz 로 같은 그림을 되그리지 않는지).
//       마지막 모니터에 약 4초간 동영상이 표시된다.
if (graphics is not null && monitors.Count > 0 && args.FirstOrDefault(File.Exists) is string loopClip)
{
    ProjectionEngine? engine = null;
    try
    {
        engine = new ProjectionEngine(Dispatcher.CurrentDispatcher);
        engine.StartOutput(monitors[^1]);
        engine.StartVideo(loopClip, loop: true, volume: 0.0);

        // 첫 프레임이 나올 때까지 기다린다.
        var ready = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < ready && engine.RenderCount == 0) Thread.Sleep(20);

        long before = engine.RenderCount;
        var window = Stopwatch.StartNew();
        Thread.Sleep(2000);
        window.Stop();
        double rendersPerSecond = (engine.RenderCount - before) / window.Elapsed.TotalSeconds;

        // 60Hz vsync 로 헛돌면 약 60이 나온다. 24~30fps 소스라면 그 근처여야 한다.
        Check(rendersPerSecond > 5 && rendersPerSecond < 45,
            $"video loop renders at source rate, not vsync rate ({rendersPerSecond:F1}/s)");

        // 일시정지 상태에서도 설정 변경(RequestRender)은 반드시 화면에 반영되어야 한다.
        engine.Video!.Pause();
        Thread.Sleep(300);
        long paused = engine.RenderCount;
        Thread.Sleep(400);
        Check(engine.RenderCount == paused, $"paused video stops redrawing ({engine.RenderCount - paused} renders)");

        engine.RequestRender();
        Thread.Sleep(300);
        Check(engine.RenderCount > paused, $"paused video still repaints on request ({engine.RenderCount - paused} renders)");
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex.ToString());
        Check(false, "video loop render rate");
    }
    finally
    {
        engine?.Dispose();
    }
}

// 22. 자동 업데이트: 릴리스 응답 해석과 저장소 표기 정규화 (네트워크 없이 확인)
{
    const string releaseJson = """
    {
      "tag_name": "v1.2.3",
      "body": "동영상 재생 오류 수정",
      "assets": [
        { "name": "notes.txt", "size": 12, "browser_download_url": "https://example.invalid/notes.txt",
          "url": "https://api.github.com/repos/o/r/releases/assets/1" },
        { "name": "ProjectorWarp.exe", "size": 73400320,
          "browser_download_url": "https://example.invalid/ProjectorWarp.exe",
          "url": "https://api.github.com/repos/o/r/releases/assets/2" }
      ]
    }
    """;

    bool parsed = UpdateService.TryParseRelease(releaseJson, out ReleaseInfo? release, out string? error);
    Check(parsed && release is not null, $"release json parsed {error}");
    Check(release?.Version == new Version(1, 2, 3), $"release version from tag ({release?.Version})");
    Check(release?.AssetName == AppConfig.UpdateAssetName, $"release asset picked ({release?.AssetName})");
    Check(release?.Size == 73400320 && release?.Notes == "동영상 재생 오류 수정", "release asset size and notes");

    Check(!UpdateService.TryParseRelease("""{ "tag_name": "v9.9.9", "assets": [] }""", out _, out _),
        "release without downloadable asset rejected");
    Check(!UpdateService.TryParseRelease("""{ "tag_name": "nightly" }""", out _, out _),
        "release with unparsable tag rejected");

    bool tags =
        UpdateService.TryParseVersion("v1.0.1", out Version? a) && a == new Version(1, 0, 1) &&
        UpdateService.TryParseVersion("2.1", out Version? b) && b == new Version(2, 1) &&
        UpdateService.TryParseVersion("v3.0.0-beta.2", out Version? c) && c == new Version(3, 0, 0) &&
        !UpdateService.TryParseVersion("", out _);
    Check(tags, "release tag variants parsed");

    bool repositories =
        UpdateService.TryParseRepository("smic/ProjectorWarp", out string r1) && r1 == "smic/ProjectorWarp" &&
        UpdateService.TryParseRepository("https://github.com/smic/ProjectorWarp.git", out string r2) &&
        r2 == "smic/ProjectorWarp" &&
        !UpdateService.TryParseRepository("ProjectorWarp", out _) &&
        !UpdateService.TryParseRepository("", out _);
    Check(repositories, "repository text normalized");

    // 배포처는 빌드에 고정되어 있다. 오타가 나면 업데이트가 조용히 죽으므로 형식을 검사한다.
    Check(UpdateService.TryParseRepository(UpdateService.Repository, out string configured) &&
        configured == UpdateService.Repository,
        $"built-in update repository is well formed ({UpdateService.Repository})");

    Check(release?.AssetApiUrl == "https://api.github.com/repos/o/r/releases/assets/2",
        $"release asset api url read ({release?.AssetApiUrl})");
}

graphics?.Dispose();

Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
return failures == 0 ? 0 : 1;


/// <summary>스테이징 텍스처로 복사해 실제 픽셀이 검정만은 아닌지 확인한다.</summary>
static bool NotBlank(GraphicsDevice graphics, ID3D11Texture2D source)
{
    Texture2DDescription description = source.Description;
    description.Usage = ResourceUsage.Staging;
    description.BindFlags = BindFlags.None;
    description.CPUAccessFlags = CpuAccessFlags.Read;
    description.MiscFlags = ResourceOptionFlags.None;

    using ID3D11Texture2D staging = graphics.Device.CreateTexture2D(description);
    lock (graphics.RenderLock)
    {
        graphics.Context.CopyResource(staging, source);
        graphics.Context.Flush();

        MappedSubresource mapped = graphics.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            unsafe
            {
                byte* rows = (byte*)mapped.DataPointer;
                long total = 0;
                int sampled = 0;
                for (int y = 0; y < description.Height; y += 8)
                {
                    byte* row = rows + y * mapped.RowPitch;
                    for (int x = 0; x < description.Width; x += 8)
                    {
                        total += row[x * 4] + row[x * 4 + 1] + row[x * 4 + 2];
                        sampled++;
                    }
                }
                return sampled > 0 && total / sampled > 8;
            }
        }
        finally
        {
            graphics.Context.Unmap(staging, 0);
        }
    }
}
