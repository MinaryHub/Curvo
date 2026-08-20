namespace Curvo;

/// <summary>
/// 앱 전역 기본값과 한계값. 매직넘버는 모두 이곳에 모은다.
/// </summary>
internal static class AppConfig
{
    public const string AppName = "Curvo";
    /// <summary>1.2.x 까지 쓰던 이름. 설정 폴더와 자동 실행 항목을 이전하는 데만 쓴다.</summary>
    public const string LegacyAppName = "ProjectorWarp";
    public const string OutputWindowClassName = "CurvoOutputWindow";
    public const string OutputWindowTitle = "Curvo Output";

    // ---- 베지어 제어점 격자 ----------------------------------------------
    public const int MinGridSize = 3;
    public const int MaxGridSize = 6;
    public const int DefaultGridSize = 4;

    // ---- 테셀레이션 -------------------------------------------------------
    public const int MinTessellation = 16;
    public const int MaxTessellation = 128;
    public const int DefaultTessellation = 64;

    // ---- 색상 보정 --------------------------------------------------------
    public const float MinBrightness = 0.0f;
    public const float MaxBrightness = 2.0f;
    public const float MinContrast = 0.0f;
    public const float MaxContrast = 2.0f;
    public const float MinGamma = 0.2f;
    public const float MaxGamma = 3.0f;
    public const float DefaultBrightness = 1.0f;
    public const float DefaultContrast = 1.0f;
    public const float DefaultGamma = 1.0f;

    // ---- 엣지 블렌딩 ------------------------------------------------------
    public const float MaxEdgeBlendWidth = 0.5f;
    public const float DefaultEdgeBlendGamma = 2.2f;

    // ---- 편집 오버레이 ----------------------------------------------------
    /// <summary>제어점 핸들 반경(픽셀).</summary>
    public const float HandleRadiusPixels = 7.0f;
    /// <summary>제어점 잡기 판정 반경(픽셀).</summary>
    public const float HandlePickRadiusPixels = 14.0f;
    /// <summary>오버레이 선 두께(픽셀).</summary>
    public const float OverlayLineWidthPixels = 1.5f;
    /// <summary>참조 그리드 분할 수.</summary>
    public const int ReferenceGridDivisions = 8;
    /// <summary>화살표키 미세 이동량(픽셀).</summary>
    public const float NudgePixels = 1.0f;
    /// <summary>Shift + 화살표키 이동량(픽셀).</summary>
    public const float NudgePixelsFast = 10.0f;

    // ---- 테스트 패턴 ------------------------------------------------------
    public const int TestPatternGridDivisions = 16;
    public const int TestPatternCheckerDivisions = 16;
    public const int TestPatternRingCount = 12;

    // ---- 캡처 -------------------------------------------------------------
    /// <summary>프레임 풀 버퍼 수. 지연 최소화를 위해 2개만 사용한다.</summary>
    public const int FramePoolBufferCount = 2;

    /// <summary>
    /// 동영상 렌더 루프가 새 프레임을 확인하는 간격(ms).
    /// 24~30fps 소스의 프레임 도착을 놓치지 않을 만큼 짧게 둔다.
    /// </summary>
    public const int VideoFramePollIntervalMilliseconds = 2;

    // ---- 출력 창 ----------------------------------------------------------
    public const int WindowedOutputWidth = 1280;
    public const int WindowedOutputHeight = 720;

    // ---- 실행 취소 --------------------------------------------------------
    public const int UndoStackDepth = 64;

    // ---- 프리셋 -----------------------------------------------------------
    public const int PresetSchemaVersion = 1;
    public const string PresetFileExtension = ".json";
    public const string LastSessionFileName = "last-session.json";
    public const string AppSettingsFileName = "app-settings.json";

    // ---- 자동 시작 --------------------------------------------------------
    /// <summary>로그온 자동 실행 시 붙는 명령줄 인자.</summary>
    public const string AutoStartArgument = "--autostart";
    /// <summary>
    /// MSIX 패키지의 로그온 자동 실행 작업 아이디.
    /// <b>Package.appxmanifest 의 uap5:StartupTask/@TaskId 와 반드시 같아야 한다.</b>
    /// </summary>
    public const string StartupTaskId = "CurvoStartup";
    /// <summary>자동 시작에서 캡처 대상을 찾기 위해 재시도하는 기본 최대 시간(초).</summary>
    public const int DefaultAutoStartRetrySeconds = 60;
    /// <summary>자동 시작 재시도 간격(초).</summary>
    public const int AutoStartRetryIntervalSeconds = 2;
    /// <summary>로그온 직후에는 디스플레이/대상 앱이 준비되지 않으므로 이만큼 기다린다(초).</summary>
    public const int LogonStartDelaySeconds = 5;

    // ---- 내장 미디어 ------------------------------------------------------
    /// <summary>PPT/PDF 를 슬라이드 이미지로 변환해 보관하는 폴더 이름.</summary>
    public const string SlideCacheFolderName = "SlideCache";
    /// <summary>PPT/PDF 를 이미지로 변환할 때의 가로 해상도(px).</summary>
    public const int SlideRenderWidth = 1920;
    /// <summary>동시에 GPU 에 올려 두는 슬라이드 텍스처 수.</summary>
    public const int MaxCachedSlideTextures = 4;
    /// <summary>슬라이드 자동 전환 간격 기본값(초). 0 이면 수동 전환.</summary>
    public const double DefaultSlideIntervalSeconds = 0.0;
    /// <summary>슬라이드 자동 전환 간격 최대값(초).</summary>
    public const double MaxSlideIntervalSeconds = 120.0;
    /// <summary>동영상 기본 음량.</summary>
    public const double DefaultVolume = 0.8;

    // ---- 자동 업데이트 ----------------------------------------------------
    /// <summary>
    /// 업데이트를 받아올 GitHub 저장소. 배포처를 옮길 때 <b>이 한 줄만</b> 바꾸면 된다.
    /// (사용자가 앱에서 입력하는 값이 아니다.)
    /// <para>
    /// 소스와 릴리스를 한 저장소에 둔다. <b>공개 저장소여야 한다</b> — 비공개 저장소의 릴리스는
    /// 인증 없이 조회할 수 없어(GitHub 이 릴리스가 없는 것과 똑같이 404 를 준다) 받는 PC 마다
    /// 토큰이 필요해진다.
    /// </para>
    /// </summary>
    public const string UpdateRepository = "MinaryHub/Curvo";
    /// <summary>GitHub 최신 릴리스 조회 주소. {0} 에 "owner/repo" 가 들어간다.</summary>
    public const string UpdateReleaseApiFormat = "https://api.github.com/repos/{0}/releases/latest";
    public const string UpdateApiMediaType = "application/vnd.github+json";
    /// <summary>릴리스 자산을 API 로 내려받을 때의 Accept 값.</summary>
    public const string UpdateAssetMediaType = "application/octet-stream";
    /// <summary>
    /// 비공개 저장소일 때만 필요한 읽기 전용 토큰을 담는 환경 변수 이름.
    /// 실행파일에 토큰을 박아 배포하면 그대로 유출되므로 반드시 환경에서 읽는다.
    /// </summary>
    public const string UpdateTokenEnvironmentVariable = "CURVO_GITHUB_TOKEN";
    /// <summary>릴리스에서 내려받을 자산 이름(없으면 첫 번째 exe 를 쓴다).</summary>
    public const string UpdateAssetName = "Curvo.exe";
    /// <summary>내려받은 exe 를 두는 폴더 이름(%LocalAppData%\Curvo 하위).</summary>
    public const string UpdateStagingFolderName = "Update";
    public const string UpdatePartialSuffix = ".part";
    public const string UpdateBackupSuffix = ".bak";
    /// <summary>실행 중인 exe 를 교체하기 위해 새 프로세스가 붙는 인자.</summary>
    public const string ApplyUpdateArgument = "--apply-update";
    public const int UpdateRequestTimeoutSeconds = 30;
    public const int UpdateDownloadBufferBytes = 128 * 1024;
    /// <summary>시작 시 업데이트 확인을 이만큼 미뤄 첫 화면 표시를 방해하지 않는다(초).</summary>
    public const int UpdateStartupCheckDelaySeconds = 5;
    /// <summary>교체 전에 이전 프로세스 종료를 기다리는 최대 시간(ms).</summary>
    public const int UpdateProcessExitTimeoutMilliseconds = 15000;
    /// <summary>파일 잠금이 풀릴 때까지의 교체 재시도 횟수와 간격(ms).</summary>
    public const int UpdateReplaceAttempts = 20;
    public const int UpdateReplaceRetryMilliseconds = 250;
    /// <summary>상태 표시에 넣는 릴리스 본문 미리보기 길이(글자).</summary>
    public const int UpdateNotesPreviewLength = 90;

    // ---- 후원 -------------------------------------------------------------
    /// <summary>GitHub Sponsors 계정(조직).</summary>
    public const string SponsorAccount = "MinaryHub";
    /// <summary>
    /// 후원 페이지. 브라우저로 열기만 하며 앱은 결제에 관여하지 않는다.
    /// frequency=one-time 은 후원자가 정기 결제 대신 금액 입력 화면으로 바로 가게 한다.
    /// </summary>
    public const string SponsorUrl = "https://github.com/sponsors/minaryhub?frequency=one-time";

    /// <summary>슬라이드 변환 결과 캐시 폴더.</summary>
    public static string SlideCacheDirectory => Path.Combine(UserDataDirectory, SlideCacheFolderName);

    /// <summary>프리셋/세션 상태를 저장하는 사용자 폴더.</summary>
    public static string UserDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    /// <summary>이름을 바꾸기 전에 쓰던 사용자 폴더.</summary>
    private static string LegacyUserDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyAppName);

    /// <summary>
    /// 앱 이름이 ProjectorWarp 에서 Curvo 로 바뀌면서 사용자 폴더 위치도 함께 바뀌었다.
    /// 예전 폴더의 내용을 새 폴더로 옮겨 보정값·프리셋·자동 시작 설정을 살린다.
    /// <para>
    /// "새 폴더가 없을 때만" 으로 판단하면 안 된다. 설정을 한 번이라도 저장하면 폴더가 비어 있어도
    /// 먼저 만들어지므로, <b>설정 파일이 있는지</b>를 기준으로 이미 옮겼는지 판단한다.
    /// </para>
    /// 실패해도 앱 실행을 막지 않는다(기본값으로 시작할 뿐이다).
    /// </summary>
    public static void MigrateLegacyUserData()
    {
        try
        {
            if (!Directory.Exists(LegacyUserDataDirectory)) return;
            if (File.Exists(Path.Combine(UserDataDirectory, AppSettingsFileName)) ||
                File.Exists(Path.Combine(UserDataDirectory, LastSessionFileName)))
                return;   // 이미 옮겼거나 새로 만든 설정이 있다.

            Directory.CreateDirectory(UserDataDirectory);

            foreach (string file in Directory.GetFiles(LegacyUserDataDirectory))
            {
                string target = Path.Combine(UserDataDirectory, Path.GetFileName(file));
                if (!File.Exists(target)) File.Move(file, target);
            }

            foreach (string directory in Directory.GetDirectories(LegacyUserDataDirectory))
            {
                string target = Path.Combine(UserDataDirectory, Path.GetFileName(directory));
                if (!Directory.Exists(target)) Directory.Move(directory, target);
            }
        }
        catch (Exception)
        {
            // 옮기지 못하면 새 폴더에 기본값으로 시작한다.
        }
    }
}
