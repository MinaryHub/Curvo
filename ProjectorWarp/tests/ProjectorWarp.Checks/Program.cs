using System.Numerics;
using ProjectorWarp;
using ProjectorWarp.Capture;
using ProjectorWarp.Geometry;
using ProjectorWarp.Interop;
using ProjectorWarp.Media;
using ProjectorWarp.Presets;
using ProjectorWarp.Rendering;
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
            renderer.Render(window, settings, overlay);

        Check(window.RenderTargetView is not null && window.Width > 0 && window.Height > 0,
            $"output window swapchain + present ({window.Width}x{window.Height} on {target.DeviceName})");
        Check(window.IsExcludedFromCapture, "output window excluded from capture (WDA_EXCLUDEFROMCAPTURE)");
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
            OutputTopmost = false,
        };
        bool saved = AppSettingsStore.TrySave(written, out string? saveError);
        AppSettings loaded = AppSettingsStore.Load();

        Check(saved && loaded.AutoStartProjection && loaded.StartMinimized && !loaded.OutputTopmost
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
                    for (int i = 0; i < 10; i++) renderer.Render(window, settings, overlay);
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
                    for (int i = 0; i < 10; i++) renderer.Render(window, settings, overlay);
                    slideRendered = true;
                }
            }
        }
        Check(slideRendered, "slide texture warped and presented with edit overlay");
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex.ToString());
        Check(false, "media through warp pipeline");
    }
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
