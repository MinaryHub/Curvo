using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Curvo.Interop;

/// <summary>
/// Windows 로그온 시 자동 실행 등록 (HKCU Run 키). 관리자 권한이 필요하지 않다.
/// </summary>
internal static class AutoStartRegistry
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = AppConfig.AppName;
    private const string LegacyValueName = AppConfig.LegacyAppName;

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
        // 패키지에서는 Run 키가 아니라 매니페스트의 startupTask 가 자동 실행을 관리한다.
        if (PackageContext.IsPackaged) return PackagedIsEnabled();

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
        if (PackageContext.IsPackaged) return PackagedSetEnabled(true, out error);

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
        if (PackageContext.IsPackaged) return PackagedSetEnabled(false, out error);

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
        // 패키지는 경로가 고정이고 Run 키를 쓰지 않으므로 맞출 것이 없다.
        if (PackageContext.IsPackaged) return;

        MigrateLegacyValue();
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

    /// <summary>
    /// 예전 이름(ProjectorWarp)으로 등록된 Run 항목을 새 이름으로 옮긴다.
    /// 그냥 두면 지워지지 않은 옛 항목이 이제 존재하지 않는 exe 를 로그온마다 실행하려 든다.
    /// </summary>
    private static void MigrateLegacyValue()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(LegacyValueName) is not string legacy || string.IsNullOrWhiteSpace(legacy)) return;

            // 새 이름이 이미 있으면 옛 항목만 치운다.
            if (key.GetValue(ValueName) is not string) key.SetValue(ValueName, legacy, RegistryValueKind.String);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch (Exception)
        {
            // 이전 실패는 치명적이지 않다. 사용자가 체크박스로 다시 켜면 된다.
        }
    }

    /// <summary>이번 실행이 로그온 자동 실행으로 시작되었는지.</summary>
    public static bool LaunchedByLogon() => Environment.GetCommandLineArgs()
        .Skip(1)
        .Any(argument => argument.Equals(AppConfig.AutoStartArgument, StringComparison.OrdinalIgnoreCase));

    // ---- MSIX StartupTask 경로 --------------------------------------------
    // WinRT 호출이 비동기인데 호출자는 UI 스레드의 동기 핸들러다. STA 메시지 루프에서 대기하면
    // 교착될 수 있으므로 스레드 풀에서 돌리고 짧게 기다린다.

    private static bool PackagedIsEnabled()
    {
        try
        {
            StartupTask task = Task.Run(async () => await StartupTask.GetAsync(AppConfig.StartupTaskId))
                .GetAwaiter().GetResult();
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool PackagedSetEnabled(bool enable, out string? error)
    {
        error = null;
        try
        {
            return Task.Run(async () =>
            {
                StartupTask task = await StartupTask.GetAsync(AppConfig.StartupTaskId);
                if (!enable)
                {
                    task.Disable();
                    return true;
                }

                StartupTaskState state = await task.RequestEnableAsync();
                return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
