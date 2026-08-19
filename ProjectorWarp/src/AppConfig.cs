namespace ProjectorWarp;

/// <summary>
/// 앱 전역 기본값과 한계값. 매직넘버는 모두 이곳에 모은다.
/// </summary>
internal static class AppConfig
{
    public const string AppName = "ProjectorWarp";
    public const string OutputWindowClassName = "ProjectorWarpOutputWindow";
    public const string OutputWindowTitle = "ProjectorWarp 출력";

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

    /// <summary>슬라이드 변환 결과 캐시 폴더.</summary>
    public static string SlideCacheDirectory => Path.Combine(UserDataDirectory, SlideCacheFolderName);

    /// <summary>프리셋/세션 상태를 저장하는 사용자 폴더.</summary>
    public static string UserDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
}
