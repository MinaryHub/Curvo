using System.Windows.Media.Imaging;
using ProjectorWarp.Capture;
using ProjectorWarp.Geometry;

namespace ProjectorWarp.UI;

/// <summary>창 목록 항목(제목 / 프로세스명 / 아이콘).</summary>
internal sealed class WindowListItem
{
    public required WindowInfo Window { get; init; }

    public string Title => Window.Title;

    public string ProcessName => Window.ProcessName;

    public BitmapSource? Icon => IconLoader.Load(Window.ExecutablePath);

    public static WindowListItem From(WindowInfo window) => new() { Window = window };
}

/// <summary>모니터 목록 항목.</summary>
internal sealed class MonitorListItem
{
    public required MonitorInfo Monitor { get; init; }

    public string Description => Monitor.DisplayText;

    public static MonitorListItem From(MonitorInfo monitor) => new() { Monitor = monitor };

    public override string ToString() => Description;
}

/// <summary>테스트 패턴 콤보 항목.</summary>
internal sealed class PatternListItem
{
    public required TestPattern Pattern { get; init; }

    public required string Label { get; init; }

    public static PatternListItem Create(TestPattern pattern, string label) => new() { Pattern = pattern, Label = label };

    public override string ToString() => Label;
}

/// <summary>제어점 격자 크기 콤보 항목.</summary>
internal sealed class GridSizeListItem
{
    public required int Size { get; init; }

    public string Label => $"{Size} x {Size} ({Size * Size} control points)";

    public static GridSizeListItem Create(int size) => new() { Size = size };

    public override string ToString() => Label;
}
