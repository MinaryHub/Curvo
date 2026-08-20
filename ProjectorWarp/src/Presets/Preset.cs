using System.Numerics;
using System.Text.Json.Serialization;
using Curvo.Capture;
using Curvo.Geometry;

namespace Curvo.Presets;

/// <summary>프리셋 파일 스키마(JSON, camelCase).</summary>
internal sealed class Preset
{
    public int Version { get; set; } = AppConfig.PresetSchemaVersion;

    public string Name { get; set; } = string.Empty;

    public PresetSource Source { get; set; } = new();

    public PresetOutput Output { get; set; } = new();

    public PresetCornerPin CornerPin { get; set; } = new();

    public PresetBezier Bezier { get; set; } = new();

    public PresetColor Color { get; set; } = new();

    public PresetEdgeBlend EdgeBlend { get; set; } = new();


    /// <summary>앱 내부 재생(동영상/슬라이드) 설정.</summary>
    public PresetMedia Media { get; set; } = new();

    /// <summary>현재 상태로부터 프리셋을 만든다.</summary>
    public static Preset FromState(string name, WarpSettings settings, CaptureTarget? source, string? outputMonitor,
        PresetMedia? media = null)
    {
        var preset = new Preset
        {
            Name = name,
            Source = new PresetSource
            {
                Type = source?.Kind == CaptureSourceKind.Monitor ? "monitor" : "window",
                MatchTitle = source?.MatchTitle,
                MatchProcess = source?.MatchProcess,
                MonitorDeviceName = source?.MonitorDeviceName,
            },
            Output = new PresetOutput { MonitorDeviceName = outputMonitor },
            CornerPin = new PresetCornerPin
            {
                Enabled = settings.CornerPinEnabled,
                Points = settings.CornerPoints.Select(ToPair).ToList(),
            },
            Bezier = new PresetBezier
            {
                Enabled = settings.BezierEnabled,
                GridSize = settings.Grid.GridSize,
                Tessellation = settings.Tessellation,
                ControlPoints = settings.Grid.Points.ToArray().Select(ToPair).ToList(),
            },
            Color = new PresetColor
            {
                Enabled = settings.ColorEnabled,
                Brightness = settings.Brightness,
                Contrast = settings.Contrast,
                Gamma = settings.Gamma,
            },
            EdgeBlend = new PresetEdgeBlend
            {
                Enabled = settings.EdgeBlendEnabled,
                Left = settings.EdgeBlendLeft,
                Right = settings.EdgeBlendRight,
                Top = settings.EdgeBlendTop,
                Bottom = settings.EdgeBlendBottom,
                Gamma = settings.EdgeBlendGamma,
            },
        };
        if (media is not null) preset.Media = media;
        return preset;
    }

    /// <summary>프리셋 값을 기하/색상 설정에 적용한다.</summary>
    public void ApplyTo(WarpSettings settings)
    {
        settings.CornerPinEnabled = CornerPin.Enabled;
        if (CornerPin.Points.Count == 4)
            settings.SetCornerPoints(CornerPin.Points.Select(ToVector).ToList());

        settings.BezierEnabled = Bezier.Enabled;
        settings.Tessellation = Bezier.Tessellation;
        var flat = new List<float>();
        foreach (float[] pair in Bezier.ControlPoints)
        {
            if (pair.Length < 2) continue;
            flat.Add(pair[0]);
            flat.Add(pair[1]);
        }
        settings.ReplaceGrid(ControlPointGrid.FromFlatArray(Bezier.GridSize, flat));

        settings.ColorEnabled = Color.Enabled;
        settings.Brightness = Color.Brightness;
        settings.Contrast = Color.Contrast;
        settings.Gamma = Color.Gamma;

        settings.EdgeBlendEnabled = EdgeBlend.Enabled;
        settings.EdgeBlendLeft = EdgeBlend.Left;
        settings.EdgeBlendRight = EdgeBlend.Right;
        settings.EdgeBlendTop = EdgeBlend.Top;
        settings.EdgeBlendBottom = EdgeBlend.Bottom;
        settings.EdgeBlendGamma = EdgeBlend.Gamma;
    }

    private static float[] ToPair(Vector2 point) => new[] { point.X, point.Y };

    private static Vector2 ToVector(float[] pair) =>
        pair.Length >= 2 ? new Vector2(pair[0], pair[1]) : Vector2.Zero;
}

internal sealed class PresetSource
{
    /// <summary>"window" 또는 "monitor".</summary>
    public string Type { get; set; } = "window";

    public string? MatchTitle { get; set; }

    public string? MatchProcess { get; set; }

    public string? MonitorDeviceName { get; set; }

    [JsonIgnore]
    public CaptureSourceKind Kind =>
        Type.Equals("monitor", StringComparison.OrdinalIgnoreCase) ? CaptureSourceKind.Monitor : CaptureSourceKind.Window;
}

internal sealed class PresetOutput
{
    public string? MonitorDeviceName { get; set; }
}

internal sealed class PresetCornerPin
{
    public bool Enabled { get; set; }

    public List<float[]> Points { get; set; } = new();
}

internal sealed class PresetBezier
{
    public bool Enabled { get; set; } = true;

    public int GridSize { get; set; } = AppConfig.DefaultGridSize;

    public int Tessellation { get; set; } = AppConfig.DefaultTessellation;

    public List<float[]> ControlPoints { get; set; } = new();
}

internal sealed class PresetColor
{
    public bool Enabled { get; set; }

    public float Brightness { get; set; } = AppConfig.DefaultBrightness;

    public float Contrast { get; set; } = AppConfig.DefaultContrast;

    public float Gamma { get; set; } = AppConfig.DefaultGamma;
}

internal sealed class PresetEdgeBlend
{
    public bool Enabled { get; set; }

    public float Left { get; set; }

    public float Right { get; set; }

    public float Top { get; set; }

    public float Bottom { get; set; }

    public float Gamma { get; set; } = AppConfig.DefaultEdgeBlendGamma;
}

/// <summary>내부 재생 설정. Kind 가 none 이 아니면 캡처 대신 이 미디어를 사용한다.</summary>
internal sealed class PresetMedia
{
    public const string KindNone = "none";
    public const string KindVideo = "video";
    public const string KindSlides = "slides";

    /// <summary>none | video | slides</summary>
    public string Kind { get; set; } = KindNone;

    public string? Path { get; set; }

    public bool Loop { get; set; } = true;

    public double Volume { get; set; } = AppConfig.DefaultVolume;

    /// <summary>슬라이드 자동 전환 간격(초). 0 이면 수동.</summary>
    public double SlideIntervalSeconds { get; set; }

    [JsonIgnore]
    public bool IsVideo => Kind.Equals(KindVideo, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsSlides => Kind.Equals(KindSlides, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsActive => (IsVideo || IsSlides) && !string.IsNullOrWhiteSpace(Path);
}
