using Curvo.Interop;

namespace Curvo.Capture;

internal enum CaptureSourceKind
{
    Window,
    Monitor,
}

/// <summary>캡처 대상 창 정보.</summary>
internal sealed class WindowInfo
{
    public required IntPtr Handle { get; init; }

    public required string Title { get; init; }

    public required string ProcessName { get; init; }

    public string? ExecutablePath { get; init; }

    public required uint ProcessId { get; init; }

    /// <summary>이 창이 걸쳐 있는 모니터 핸들.</summary>
    public IntPtr MonitorHandle => Win32.MonitorFromWindow(Handle, Win32.MONITOR_DEFAULTTONEAREST);

    public string DisplayText => $"{Title}  —  {ProcessName}";

    public override string ToString() => DisplayText;
}

/// <summary>캡처/출력 대상 모니터 정보.</summary>
internal sealed class MonitorInfo
{
    public required IntPtr Handle { get; init; }

    /// <summary>\\.\DISPLAY1 형태의 장치 이름.</summary>
    public required string DeviceName { get; init; }

    public required Win32.RECT Bounds { get; init; }

    public required bool IsPrimary { get; init; }

    public required uint Dpi { get; init; }

    public int Width => Bounds.Width;

    public int Height => Bounds.Height;

    public string DisplayText =>
        $"{DeviceName}  {Width}x{Height} @ ({Bounds.Left},{Bounds.Top}){(IsPrimary ? "  [primary]" : string.Empty)}";

    public override string ToString() => DisplayText;
}

/// <summary>실제 캡처를 시작할 대상.</summary>
internal sealed class CaptureTarget
{
    public required CaptureSourceKind Kind { get; init; }

    public required IntPtr Handle { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>프리셋 재현용 창 제목 힌트.</summary>
    public string? MatchTitle { get; init; }

    /// <summary>프리셋 재현용 프로세스 이름 힌트.</summary>
    public string? MatchProcess { get; init; }

    /// <summary>프리셋 재현용 모니터 장치 이름.</summary>
    public string? MonitorDeviceName { get; init; }

    public static CaptureTarget FromWindow(WindowInfo window) => new()
    {
        Kind = CaptureSourceKind.Window,
        Handle = window.Handle,
        DisplayName = window.DisplayText,
        MatchTitle = window.Title,
        MatchProcess = window.ProcessName,
    };

    public static CaptureTarget FromMonitor(MonitorInfo monitor) => new()
    {
        Kind = CaptureSourceKind.Monitor,
        Handle = monitor.Handle,
        DisplayName = monitor.DisplayText,
        MonitorDeviceName = monitor.DeviceName,
    };
}
