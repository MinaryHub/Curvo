using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace ProjectorWarp.Update;

/// <summary>GitHub 릴리스 하나의 정보(업데이트 판단과 다운로드에 필요한 것만).</summary>
internal sealed record ReleaseInfo(
    Version Version, string Tag, string Notes, string AssetName, string DownloadUrl, string AssetApiUrl, long Size);

/// <summary>
/// GitHub Releases 기반 자동 업데이트.
/// <para>
/// 단일 실행파일은 실행 중 자기 자신을 덮어쓸 수 없으므로 <b>새 exe 가 교체를 수행한다</b>:
/// 새 버전을 임시 폴더에 내려받아 <c>--apply-update &lt;대상 exe&gt; &lt;이전 PID&gt;</c> 로 실행하고,
/// 새 프로세스가 이전 프로세스의 종료를 기다린 뒤 자신을 대상 경로로 복사하고 다시 띄운다.
/// </para>
/// </summary>
internal static class UpdateService
{
    private static readonly Lazy<HttpClient> Client = new(CreateClient);

    /// <summary>비교에 사용하는 현재 버전(어셈블리 버전).</summary>
    public static Version CurrentVersion { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>화면에 보여줄 현재 버전 문자열.</summary>
    public static string CurrentVersionText { get; } = ReadInformationalVersion();

    /// <summary>내려받은 새 exe 를 보관하는 폴더.</summary>
    public static string StagingDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppConfig.AppName, AppConfig.UpdateStagingFolderName);

    /// <summary>업데이트를 받아올 저장소("owner/repo"). 빌드에 고정되어 있다.</summary>
    public static string Repository => AppConfig.UpdateRepository;

    /// <summary>비공개 저장소용 토큰(환경 변수). 없으면 인증 없이 조회한다.</summary>
    private static string? Token
    {
        get
        {
            string? token = Environment.GetEnvironmentVariable(AppConfig.UpdateTokenEnvironmentVariable);
            return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }
    }

    /// <summary>"owner/repo" 형식을 검사해 정규화한다.</summary>
    public static bool TryParseRepository(string? value, out string repository)
    {
        repository = (value ?? string.Empty).Trim().Trim('/');

        // 저장소 주소를 그대로 붙여넣는 경우가 많아 URL 형태도 받아준다.
        const string prefix = "github.com/";
        int marker = repository.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (marker >= 0) repository = repository[(marker + prefix.Length)..].Trim('/');
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repository = repository[..^4];

        string[] parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts.Any(part => part.Contains(' ')))
        {
            repository = string.Empty;
            return false;
        }

        repository = $"{parts[0]}/{parts[1]}";
        return true;
    }

    /// <summary>최신 릴리스를 조회한다. 현재 버전보다 새 것이 아니면 null.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken cancellation)
    {
        if (!TryParseRepository(Repository, out string normalized))
            throw new InvalidOperationException($"빌드에 지정된 저장소 값이 올바르지 않습니다: \"{Repository}\"");

        string url = string.Format(AppConfig.UpdateReleaseApiFormat, normalized);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(AppConfig.UpdateApiMediaType));
        Authorize(request);

        using HttpResponseMessage response = await Client.Value.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(DescribeHttpFailure(response, normalized));

        string json = await response.Content.ReadAsStringAsync(cancellation);
        if (!TryParseRelease(json, out ReleaseInfo? release, out string? error))
            throw new InvalidOperationException(error);

        return release!.Version > CurrentVersion ? release : null;
    }

    /// <summary>
    /// GitHub 릴리스 JSON 에서 버전과 내려받을 자산을 뽑는다.
    /// (네트워크 없이 검증할 수 있도록 파싱만 따로 뒀다.)
    /// </summary>
    public static bool TryParseRelease(string json, out ReleaseInfo? release, out string? error)
    {
        release = null;
        error = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            string tag = root.TryGetProperty("tag_name", out JsonElement tagElement)
                ? tagElement.GetString() ?? string.Empty
                : string.Empty;
            if (!TryParseVersion(tag, out Version? version))
            {
                error = $"릴리스 태그에서 버전을 읽지 못했습니다: \"{tag}\"";
                return false;
            }

            string notes = root.TryGetProperty("body", out JsonElement bodyElement)
                ? (bodyElement.GetString() ?? string.Empty).Trim()
                : string.Empty;

            if (!TryPickAsset(root, out string assetName, out string downloadUrl, out string assetApiUrl, out long size))
            {
                error = $"릴리스 {tag} 에 내려받을 {AppConfig.UpdateAssetName} 자산이 없습니다.";
                return false;
            }

            release = new ReleaseInfo(version!, tag, notes, assetName, downloadUrl, assetApiUrl, size);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"릴리스 응답을 해석하지 못했습니다: {ex.Message}";
            return false;
        }
    }

    /// <summary>"v1.2.3" · "1.2" 같은 태그를 Version 으로 바꾼다.</summary>
    public static bool TryParseVersion(string? tag, out Version? version)
    {
        version = null;
        string text = (tag ?? string.Empty).Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];

        // 사전 배포 접미사(-beta 등)는 떼고 숫자 부분만 본다.
        int suffix = text.IndexOfAny(new[] { '-', '+', ' ' });
        if (suffix > 0) text = text[..suffix];
        if (text.Length == 0) return false;

        return Version.TryParse(text.Contains('.') ? text : text + ".0", out version);
    }

    /// <summary>새 exe 를 임시 폴더로 내려받고 그 경로를 돌려준다.</summary>
    public static async Task<string> DownloadAsync(
        ReleaseInfo release, IProgress<double>? progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(StagingDirectory);
        string target = Path.Combine(StagingDirectory, $"{AppConfig.AppName}-{release.Version}.exe");
        string partial = target + AppConfig.UpdatePartialSuffix;

        // 비공개 저장소는 browser_download_url 로 내려받을 수 없어 자산 API 를 쓴다.
        bool useAssetApi = Token is not null && release.AssetApiUrl.Length > 0;
        using var request = new HttpRequestMessage(
            HttpMethod.Get, useAssetApi ? release.AssetApiUrl : release.DownloadUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(AppConfig.UpdateAssetMediaType));
        if (useAssetApi) Authorize(request);

        using (HttpResponseMessage response = await Client.Value.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellation))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? release.Size;

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellation);
            await using var destination = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);

            byte[] buffer = new byte[AppConfig.UpdateDownloadBufferBytes];
            long copied = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellation);
                copied += read;
                if (total > 0) progress?.Report(Math.Min(1.0, (double)copied / total));
            }
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>내려받은 exe 에 교체를 맡긴다. 호출 후에는 현재 프로세스를 종료해야 한다.</summary>
    public static void StartApply(string stagedExePath)
    {
        string? current = Environment.ProcessPath;
        if (string.IsNullOrEmpty(current))
            throw new InvalidOperationException("실행 파일 경로를 확인하지 못해 업데이트를 적용할 수 없습니다.");

        var startInfo = new ProcessStartInfo(stagedExePath) { UseShellExecute = false };
        startInfo.ArgumentList.Add(AppConfig.ApplyUpdateArgument);
        startInfo.ArgumentList.Add(current);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(startInfo);
    }

    /// <summary>
    /// <c>--apply-update</c> 로 실행되었는지 확인하고, 그렇다면 교체 후 대상 exe 를 다시 띄운다.
    /// 교체 모드였으면 true 를 돌려주며 호출자는 창을 띄우지 말고 종료해야 한다.
    /// </summary>
    public static bool TryApplyPendingUpdate(string[] arguments, out string? error)
    {
        error = null;
        if (arguments.Length < 2 ||
            !arguments[0].Equals(AppConfig.ApplyUpdateArgument, StringComparison.OrdinalIgnoreCase))
            return false;

        string target = arguments[1];
        if (arguments.Length >= 3 && int.TryParse(arguments[2], out int previousPid))
            WaitForExit(previousPid);

        try
        {
            ReplaceFile(Environment.ProcessPath!, target);
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            error = $"업데이트 적용에 실패했습니다. {ex.Message}\n\n대상: {target}";
        }
        return true;
    }

    /// <summary>이전에 내려받아 둔 파일을 정리한다.</summary>
    public static void CleanStagingDirectory()
    {
        try
        {
            if (!Directory.Exists(StagingDirectory)) return;
            string? self = Environment.ProcessPath;
            foreach (string file in Directory.EnumerateFiles(StagingDirectory))
            {
                if (string.Equals(file, self, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // 아직 사용 중이면 다음 실행에서 지운다.
                }
            }
        }
        catch (Exception)
        {
            // 정리 실패는 앱 동작에 영향이 없다.
        }
    }

    private static void WaitForExit(int processId)
    {
        try
        {
            using Process previous = Process.GetProcessById(processId);
            previous.WaitForExit(AppConfig.UpdateProcessExitTimeoutMilliseconds);
        }
        catch (ArgumentException)
        {
            // 이미 종료되었다.
        }
    }

    /// <summary>대상 exe 를 백업해 두고 덮어쓴다. 실패하면 백업을 되돌린다.</summary>
    private static void ReplaceFile(string source, string target)
    {
        string backup = target + AppConfig.UpdateBackupSuffix;
        if (File.Exists(target)) File.Copy(target, backup, overwrite: true);

        try
        {
            // 방금 종료한 프로세스가 파일 핸들을 놓을 때까지 잠깐 재시도한다.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(source, target, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < AppConfig.UpdateReplaceAttempts)
                {
                    Thread.Sleep(AppConfig.UpdateReplaceRetryMilliseconds);
                }
            }
        }
        catch
        {
            if (File.Exists(backup)) File.Copy(backup, target, overwrite: true);
            throw;
        }

        try
        {
            if (File.Exists(backup)) File.Delete(backup);
        }
        catch (IOException)
        {
            // 백업이 남아도 무해하다.
        }
    }

    private static bool TryPickAsset(
        JsonElement root, out string name, out string url, out string apiUrl, out long size)
    {
        name = string.Empty;
        url = string.Empty;
        apiUrl = string.Empty;
        size = 0;
        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            return false;

        // 정확히 일치하는 자산이 우선, 없으면 첫 번째 exe 를 쓴다.
        JsonElement? candidate = null;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string assetName = asset.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;

            if (assetName.Equals(AppConfig.UpdateAssetName, StringComparison.OrdinalIgnoreCase))
            {
                candidate = asset;
                break;
            }
            if (candidate is null && assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                candidate = asset;
        }

        if (candidate is null) return false;
        JsonElement chosen = candidate.Value;

        name = chosen.TryGetProperty("name", out JsonElement chosenName)
            ? chosenName.GetString() ?? AppConfig.UpdateAssetName
            : AppConfig.UpdateAssetName;
        url = chosen.TryGetProperty("browser_download_url", out JsonElement urlElement)
            ? urlElement.GetString() ?? string.Empty
            : string.Empty;
        apiUrl = chosen.TryGetProperty("url", out JsonElement apiElement)
            ? apiElement.GetString() ?? string.Empty
            : string.Empty;
        size = chosen.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long value)
            ? value
            : 0;
        return url.Length > 0 || apiUrl.Length > 0;
    }

    private static string DescribeHttpFailure(HttpResponseMessage response, string repository) =>
        (int)response.StatusCode switch
        {
            // 404 는 "릴리스가 없다" 와 "볼 권한이 없다" 를 구분해 주지 않는다.
            // 비공개 저장소에 릴리스가 있어도 인증 없이는 똑같이 404 이므로 단정하지 않는다.
            404 => Token is null
                ? $"{repository} 의 릴리스를 볼 수 없습니다. 비공개 저장소이면 " +
                  $"{AppConfig.UpdateTokenEnvironmentVariable} 환경 변수에 읽기 권한 토큰이 필요합니다. " +
                  "공개 저장소인데도 이 메시지가 보이면 아직 릴리스가 없는 것입니다."
                : $"{repository} 에 릴리스가 없거나, 토큰에 이 저장소 읽기 권한이 없습니다.",
            401 or 403 => $"GitHub 가 요청을 거부했습니다. {AppConfig.UpdateTokenEnvironmentVariable} 토큰이 " +
                          "만료되었거나 요청 한도를 넘었을 수 있습니다.",
            _ => $"업데이트 확인에 실패했습니다. (HTTP {(int)response.StatusCode} {response.ReasonPhrase})",
        };

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(AppConfig.UpdateRequestTimeoutSeconds) };
        // GitHub API 는 User-Agent 가 없는 요청을 거부한다.
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(AppConfig.AppName, CurrentVersion.ToString()));
        return client;
    }

    /// <summary>비공개 저장소용 토큰이 있으면 요청에 붙인다.</summary>
    private static void Authorize(HttpRequestMessage request)
    {
        string? token = Token;
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static string ReadInformationalVersion()
    {
        string? text = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(text)) return CurrentVersion.ToString(3);

        // "1.0.0+<커밋해시>" 형태에서 빌드 메타데이터는 떼고 보여준다.
        int plus = text.IndexOf('+');
        return plus > 0 ? text[..plus] : text;
    }
}
