using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectorWarp.Presets;

/// <summary>프리셋 JSON 저장/불러오기와 마지막 세션 상태 자동 복원.</summary>
internal static class PresetStore
{
    /// <summary>프리셋과 앱 설정이 공유하는 직렬화 옵션.</summary>
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string LastSessionPath => Path.Combine(AppConfig.UserDataDirectory, AppConfig.LastSessionFileName);

    public static void Save(Preset preset, string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(preset, SerializerOptions));
    }

    public static Preset? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            Preset? preset = JsonSerializer.Deserialize<Preset>(File.ReadAllText(path), SerializerOptions);
            if (preset is null) return null;
            if (preset.Version > AppConfig.PresetSchemaVersion)
                throw new InvalidOperationException(
                    $"This preset is from a newer version ({preset.Version}). Please update the app.");
            return preset;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The preset file could not be read: {ex.Message}", ex);
        }
    }

    /// <summary>앱 종료 시 마지막 상태를 저장한다(실패해도 조용히 무시).</summary>
    public static void SaveLastSession(Preset preset)
    {
        try
        {
            Save(preset, LastSessionPath);
        }
        catch (Exception)
        {
            // 사용자 폴더에 쓸 수 없는 환경에서도 종료는 정상적으로 진행한다.
        }
    }

    public static Preset? LoadLastSession()
    {
        try
        {
            return Load(LastSessionPath);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
