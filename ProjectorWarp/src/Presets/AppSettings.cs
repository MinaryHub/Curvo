using System.Text.Json;

namespace ProjectorWarp.Presets;

/// <summary>
/// 워핑 프리셋과 별개인 앱 동작 설정. `%AppData%\ProjectorWarp\app-settings.json` 에 저장한다.
/// </summary>
internal sealed class AppSettings
{
    /// <summary>Windows 로그온 시 자동 실행(레지스트리 상태와 동기화된다).</summary>
    public bool LaunchAtLogon { get; set; }

    /// <summary>앱이 시작되면 저장된 출력 모니터와 캡처 소스로 자동 연결한다.</summary>
    public bool AutoStartProjection { get; set; }

    /// <summary>컨트롤 패널을 최소화 상태로 시작한다.</summary>
    public bool StartMinimized { get; set; }

    /// <summary>시작 시 불러올 프리셋 경로. 비어 있으면 마지막 세션 상태를 사용한다.</summary>
    public string? StartupPresetPath { get; set; }

    /// <summary>자동 시작 시 캡처 대상을 찾기 위해 재시도할 최대 시간(초).</summary>
    public int AutoStartRetrySeconds { get; set; } = AppConfig.DefaultAutoStartRetrySeconds;

    /// <summary>항상 위 표시 유지.</summary>
    public bool OutputTopmost { get; set; } = true;

    /// <summary>앱을 시작할 때 새 버전이 있는지 조용히 확인한다.</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    public AppSettings Clone() => new()
    {
        LaunchAtLogon = LaunchAtLogon,
        AutoStartProjection = AutoStartProjection,
        StartMinimized = StartMinimized,
        StartupPresetPath = StartupPresetPath,
        AutoStartRetrySeconds = AutoStartRetrySeconds,
        OutputTopmost = OutputTopmost,
        CheckForUpdatesOnStartup = CheckForUpdatesOnStartup,
    };
}

/// <summary>앱 설정 파일 입출력. 실패해도 앱 실행을 막지 않는다.</summary>
internal static class AppSettingsStore
{
    public static string FilePath => Path.Combine(AppConfig.UserDataDirectory, AppConfig.AppSettingsFileName);

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(FilePath), PresetStore.SerializerOptions);
            return settings ?? new AppSettings();
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    public static bool TrySave(AppSettings settings, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(AppConfig.UserDataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, PresetStore.SerializerOptions));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
