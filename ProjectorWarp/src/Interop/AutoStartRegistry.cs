using Microsoft.Win32;

namespace ProjectorWarp.Interop;

/// <summary>
/// Windows 로그온 시 자동 실행 등록 (HKCU Run 키). 관리자 권한이 필요하지 않다.
/// </summary>
internal static class AutoStartRegistry
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = AppConfig.AppName;

    /// <summary>현재 실행 파일 경로. 단일 파일 배포에서도 올바른 exe 경로를 반환한다.</summary>
    public static string? ExecutablePath => Environment.ProcessPath;

    /// <summary>Run 키에 기록될 명령줄.</summary>
    private static string? BuildCommand()
    {
        string? path = ExecutablePath;
        return string.IsNullOrEmpty(path) ? null : $"\"{path}\" {AppConfig.AutoStartArgument}";
    }

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryEnable(out string? error)
    {
        error = null;
        string? command = BuildCommand();
        if (command is null)
        {
            error = "The executable path could not be determined.";
            return false;
        }

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("The Run registry key could not be opened.");
            key.SetValue(ValueName, command, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryDisable(out string? error)
    {
        error = null;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 자동 실행이 켜져 있는데 등록된 경로가 현재 실행 파일과 다르면(앱을 옮긴 경우) 갱신한다.
    /// </summary>
    public static void SyncPathIfEnabled()
    {
        if (!IsEnabled()) return;

        string? expected = BuildCommand();
        if (expected is null) return;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not string current) return;
            if (string.Equals(current, expected, StringComparison.OrdinalIgnoreCase)) return;
            key.SetValue(ValueName, expected, RegistryValueKind.String);
        }
        catch (Exception)
        {
            // 경로 갱신 실패는 치명적이지 않다.
        }
    }

    /// <summary>이번 실행이 로그온 자동 실행으로 시작되었는지.</summary>
    public static bool LaunchedByLogon() => Environment.GetCommandLineArgs()
        .Skip(1)
        .Any(argument => argument.Equals(AppConfig.AutoStartArgument, StringComparison.OrdinalIgnoreCase));
}
