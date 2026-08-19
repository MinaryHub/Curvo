using System.Diagnostics;
using ProjectorWarp.Interop;

namespace ProjectorWarp.Capture;

/// <summary>캡처 가능한 최상위 창과 연결된 모니터를 열거한다.</summary>
internal static class SourceEnumerator
{
    /// <summary>프리셋 바로가기에서 사용하는 프로세스 이름 힌트.</summary>
    public static readonly string[] PowerPointProcessNames = { "POWERPNT" };

    public static readonly string[] MediaPlayerProcessNames =
        { "PotPlayerMini64", "PotPlayerMini", "PotPlayer64", "vlc", "mpc-hc64", "mpc-hc", "mpc-be64", "wmplayer" };

    /// <summary>
    /// 캡처 가능한 최상위 창 목록.
    /// 보이지 않는 창, 최소화된 창, 클로킹된 UWP 셸 창, 자기 자신은 제외한다.
    /// </summary>
    public static List<WindowInfo> EnumerateWindows(IReadOnlyCollection<IntPtr> excludedHandles)
    {
        var results = new List<WindowInfo>();
        uint selfProcessId = (uint)Environment.ProcessId;

        Win32.EnumWindows((hwnd, _) =>
        {
            if (excludedHandles.Contains(hwnd)) return true;
            if (!Win32.IsWindowVisible(hwnd)) return true;
            if (Win32.IsIconic(hwnd)) return true;
            if (Win32.GetAncestor(hwnd, Win32.GA_ROOT) != hwnd) return true;
            if (Win32.IsCloaked(hwnd)) return true;

            uint exStyle = (uint)(long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
            if ((exStyle & Win32.WS_EX_TOOLWINDOW) != 0) return true;

            string title = Win32.GetWindowText(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            Win32.GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == selfProcessId) return true;

            if (!Win32.GetWindowRect(hwnd, out Win32.RECT rect)) return true;
            if (rect.Width <= 1 || rect.Height <= 1) return true;

            results.Add(new WindowInfo
            {
                Handle = hwnd,
                Title = title,
                ProcessId = processId,
                ProcessName = GetProcessName(processId),
                ExecutablePath = GetProcessImagePath(processId),
            });
            return true;
        }, IntPtr.Zero);

        return results
            .OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>연결된 모든 디스플레이 목록.</summary>
    public static List<MonitorInfo> EnumerateMonitors()
    {
        var results = new List<MonitorInfo>();

        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref Win32.RECT rect, IntPtr data) =>
        {
            var info = new Win32.MONITORINFOEXW { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFOEXW>() };
            if (!Win32.GetMonitorInfoW(hMonitor, ref info)) return true;

            uint dpi = 96;
            if (Win32.GetDpiForMonitor(hMonitor, Win32.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0)
                dpi = dpiX;

            results.Add(new MonitorInfo
            {
                Handle = hMonitor,
                DeviceName = info.szDevice,
                Bounds = info.rcMonitor,
                IsPrimary = (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0,
                Dpi = dpi,
            });
            return true;
        }, IntPtr.Zero);

        return results
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>프리셋에 기록된 제목/프로세스 힌트로 창을 다시 찾는다.</summary>
    public static WindowInfo? FindWindow(IEnumerable<WindowInfo> windows, string? matchTitle, string? matchProcess)
    {
        var list = windows.ToList();

        if (!string.IsNullOrWhiteSpace(matchProcess) && !string.IsNullOrWhiteSpace(matchTitle))
        {
            var exact = list.FirstOrDefault(w =>
                w.ProcessName.Equals(matchProcess, StringComparison.OrdinalIgnoreCase) &&
                w.Title.Equals(matchTitle, StringComparison.CurrentCultureIgnoreCase));
            if (exact is not null) return exact;
        }

        if (!string.IsNullOrWhiteSpace(matchTitle))
        {
            var byTitle = list.FirstOrDefault(w =>
                w.Title.Contains(matchTitle, StringComparison.CurrentCultureIgnoreCase));
            if (byTitle is not null) return byTitle;
        }

        if (!string.IsNullOrWhiteSpace(matchProcess))
        {
            var byProcess = list.FirstOrDefault(w =>
                w.ProcessName.Equals(matchProcess, StringComparison.OrdinalIgnoreCase));
            if (byProcess is not null) return byProcess;
        }

        return null;
    }

    /// <summary>프로세스 이름 후보 중 처음 발견되는 창.</summary>
    public static WindowInfo? FindByProcessNames(IEnumerable<WindowInfo> windows, IEnumerable<string> processNames)
    {
        var candidates = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
        return windows.FirstOrDefault(w => candidates.Contains(w.ProcessName));
    }

    public static MonitorInfo? FindMonitor(IEnumerable<MonitorInfo> monitors, string? deviceName)
        => string.IsNullOrWhiteSpace(deviceName)
            ? null
            : monitors.FirstOrDefault(m => m.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));

    private static string GetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return "(알 수 없음)";
        }
    }

    private static string? GetProcessImagePath(uint processId)
    {
        IntPtr handle = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            uint size = (uint)buffer.Length;
            return Win32.QueryFullProcessImageNameW(handle, 0, buffer, ref size)
                ? new string(buffer, 0, (int)size)
                : null;
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }
}
