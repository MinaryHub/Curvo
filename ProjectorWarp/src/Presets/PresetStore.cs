using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Curvo.Presets;

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
        WriteAllTextAtomic(path, JsonSerializer.Serialize(preset, SerializerOptions));
    }

    /// <summary>
    /// 임시 파일에 다 쓰고 디스크에 내린 뒤 한 번에 바꿔 넣는다.
    /// 쓰는 도중 전원이 꺼져도 이전 파일이 온전히 남는다(잘린 JSON 은 조용히 기본값으로 대체되어 보정값을 잃는다).
    /// </summary>
    internal static void WriteAllTextAtomic(string path, string contents)
    {
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(contents);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null);
        else File.Move(temporary, path);
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
