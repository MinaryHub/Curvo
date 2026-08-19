using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ProjectorWarp.Capture;
using ProjectorWarp.Geometry;
using ProjectorWarp.Interop;
using ProjectorWarp.Media;
using ProjectorWarp.Presets;
using ProjectorWarp.Rendering;
using ProjectorWarp.Update;

namespace ProjectorWarp.UI;

/// <summary>컨트롤 패널. 소스/출력 선택과 모든 보정 파라미터를 조작한다.</summary>
public partial class MainWindow : Window
{
    private const string PresetFileFilter = "ProjectorWarp 프리셋 (*.json)|*.json|모든 파일 (*.*)|*.*";
    private const byte ThumbnailOpacity = 255;

    private readonly ProjectionEngine _engine;
    private readonly OverlayEditor _editor;

    private List<WindowInfo> _windows = new();
    private List<MonitorInfo> _monitors = new();
    private IntPtr _thumbnail = IntPtr.Zero;
    // 생성자에서 XAML 초기값이 적용되는 동안 이벤트 핸들러가 동작하지 않도록 막는다.
    private bool _suppressSync = true;

    private AppSettings _appSettings = new();
    /// <summary>시작 시 불러온 프리셋. 자동 시작에서 소스/출력을 찾는 근거가 된다.</summary>
    private Preset? _startupPreset;
    private DispatcherTimer? _autoStartTimer;
    private DateTime _autoStartDeadlineUtc;

    /// <summary>현재 선택된 내부 재생 대상(동영상/슬라이드).</summary>
    private PresetMedia _media = new();
    private List<string> _slidePaths = new();
    private DispatcherTimer? _playbackTimer;
    private DispatcherTimer? _slideAdvanceTimer;
    private bool _updatingPosition;

    /// <summary>업데이트 확인으로 찾은 새 릴리스와, 내려받아 둔 exe 경로.</summary>
    private ReleaseInfo? _pendingRelease;
    private string? _stagedUpdatePath;
    private bool _updateBusy;

    public MainWindow()
    {
        InitializeComponent();

        _engine = new ProjectionEngine(Dispatcher);
        _editor = new OverlayEditor(_engine);

        _engine.StatusChanged += OnStatusChanged;
        _engine.SourceLost += UpdateFeedbackWarning;
        _engine.OutputWindowCreated += window =>
        {
            _editor.Attach(window);
            window.SetTopmost(TopmostCheck.IsChecked == true);
        };
        _engine.OutputWindowClosing += _ => _editor.Detach();

        _editor.StateChanged += SyncUiFromState;
        _editor.SavePresetRequested += () => Dispatcher.BeginInvoke(SavePreset);
        _editor.OpenPresetRequested += () => Dispatcher.BeginInvoke(OpenPreset);

        _engine.MediaEnded += () => StatusText.Text = "재생이 끝났습니다.";
        _engine.SlideChanged += UpdatePlaybackUi;

        Title = $"{AppConfig.AppName} {UpdateService.CurrentVersionText} — 프로젝터 곡면 워핑";
        VersionText.Text = $"버전 {UpdateService.CurrentVersionText}";
        UpdateSourceText.Text = $"업데이트 확인 대상: {UpdateService.Repository}";

        PopulateStaticCombos();
        StartPlaybackTimer();
        Loaded += OnLoaded;
        _suppressSync = false;
    }

    private WarpSettings Settings => _engine.Settings;

    private OverlayState Overlay => _engine.Overlay;

    // ---- 초기화 -----------------------------------------------------------
    private void PopulateStaticCombos()
    {
        PatternCombo.ItemsSource = new[]
        {
            PatternListItem.Create(TestPattern.None, "없음"),
            PatternListItem.Create(TestPattern.Grid, "격자"),
            PatternListItem.Create(TestPattern.Checker, "체커보드"),
            PatternListItem.Create(TestPattern.Rings, "원형 링"),
            PatternListItem.Create(TestPattern.ColorBars, "컬러바"),
            PatternListItem.Create(TestPattern.WhiteField, "화이트 풀필드"),
            PatternListItem.Create(TestPattern.BlackField, "블랙 풀필드"),
        };
        PatternCombo.DisplayMemberPath = nameof(PatternListItem.Label);
        PatternCombo.SelectedIndex = 0;

        var gridSizes = new List<GridSizeListItem>();
        for (int size = AppConfig.MinGridSize; size <= AppConfig.MaxGridSize; size++)
            gridSizes.Add(GridSizeListItem.Create(size));
        GridSizeCombo.ItemsSource = gridSizes;
        GridSizeCombo.DisplayMemberPath = nameof(GridSizeListItem.Label);
        GridSizeCombo.SelectedIndex = AppConfig.DefaultGridSize - AppConfig.MinGridSize;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!CaptureEngine.IsSupported)
        {
            MessageBox.Show(this,
                "이 PC 에서는 Windows.Graphics.Capture 를 사용할 수 없습니다.\n" +
                "Windows 10 버전 1903(빌드 18362) 이상이 필요합니다.",
                AppConfig.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        _appSettings = AppSettingsStore.Load();
        // 앱을 옮겼을 때 Run 키의 경로가 어긋나지 않도록 맞춘다.
        AutoStartRegistry.SyncPathIfEnabled();
        _appSettings.LaunchAtLogon = AutoStartRegistry.IsEnabled();

        RefreshSourceLists();
        RestoreStartupState();
        SyncUiFromState();
        SyncAppSettingsUi();

        if (_appSettings.StartMinimized) WindowState = WindowState.Minimized;
        if (_appSettings.AutoStartProjection) BeginAutoStart();
        if (_appSettings.CheckForUpdatesOnStartup) _ = CheckForUpdatesAsync(quiet: true);
    }

    // ---- 목록 -------------------------------------------------------------
    private void RefreshSourceLists()
    {
        IntPtr selfHandle = new WindowInteropHelper(this).Handle;
        var excluded = new List<IntPtr> { selfHandle };
        if (_engine.Window is not null) excluded.Add(_engine.Window.Handle);

        _windows = SourceEnumerator.EnumerateWindows(excluded);
        _monitors = SourceEnumerator.EnumerateMonitors();

        WindowList.ItemsSource = _windows.Select(WindowListItem.From).ToList();

        MonitorInfo? previousOutput = SelectedOutputMonitor();
        OutputMonitorCombo.ItemsSource = _monitors.Select(MonitorListItem.From).ToList();
        OutputMonitorCombo.SelectedIndex = IndexOfMonitor(previousOutput?.DeviceName);
    }

    private int IndexOfMonitor(string? deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return _monitors.Count > 0 ? 0 : -1;
        int index = _monitors.FindIndex(m => m.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : (_monitors.Count > 0 ? 0 : -1);
    }

    private MonitorInfo? SelectedOutputMonitor()
        => (OutputMonitorCombo.SelectedItem as MonitorListItem)?.Monitor;

    private WindowInfo? SelectedWindow() => (WindowList.SelectedItem as WindowListItem)?.Window;

    /// <summary>SourceTabs 의 탭 순서. XAML 의 TabItem 순서와 일치해야 한다.</summary>
    private const int MediaSourceTabIndex = 0;

    private const int WindowSourceTabIndex = 1;

    private bool IsWindowSourceTab => SourceTabs.SelectedIndex == WindowSourceTabIndex;

    private bool IsMediaSourceTab => SourceTabs.SelectedIndex == MediaSourceTabIndex;

    private CaptureTarget? BuildSelectedTarget()
    {
        WindowInfo? window = SelectedWindow();
        return window is null ? null : CaptureTarget.FromWindow(window);
    }

    // ---- 이벤트 핸들러 ----------------------------------------------------
    private void OnStatusChanged(string message) => Dispatcher.BeginInvoke(() =>
    {
        StatusText.Text = message;
        UpdateFeedbackWarning();
    });

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshSourceLists();

    private void OnSourceTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateThumbnail();
        UpdateFeedbackWarning();
    }

    private void OnWindowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateThumbnail();
        UpdateFeedbackWarning();
    }

    private void OnPowerPointShortcutClick(object sender, RoutedEventArgs e)
        => SelectByProcessNames(SourceEnumerator.PowerPointProcessNames, "PowerPoint 창을 찾지 못했습니다. 슬라이드 쇼를 창 모드로 실행하거나 [내장 재생] 으로 파일을 직접 열어 보세요.");

    private void OnPlayerShortcutClick(object sender, RoutedEventArgs e)
        => SelectByProcessNames(SourceEnumerator.MediaPlayerProcessNames, "지원되는 미디어 플레이어 창을 찾지 못했습니다.");

    private void SelectByProcessNames(IEnumerable<string> processNames, string notFoundMessage)
    {
        RefreshSourceLists();
        WindowInfo? match = SourceEnumerator.FindByProcessNames(_windows, processNames);
        if (match is null)
        {
            StatusText.Text = notFoundMessage;
            return;
        }

        SourceTabs.SelectedIndex = WindowSourceTabIndex;
        int index = _windows.IndexOf(match);
        WindowList.SelectedIndex = index;
        WindowList.ScrollIntoView(WindowList.SelectedItem);
        StatusText.Text = $"선택: {match.DisplayText}";
    }

    private async void OnStartCaptureClick(object sender, RoutedEventArgs e)
    {
        if (IsMediaSourceTab)
        {
            if (!_media.IsActive)
            {
                StatusText.Text = "먼저 [동영상 열기] 또는 [슬라이드 열기] 로 파일을 선택하세요.";
                return;
            }
            await StartCurrentMediaAsync();
            return;
        }

        CaptureTarget? target = BuildSelectedTarget();
        if (target is null)
        {
            StatusText.Text = "캡처할 창 또는 모니터를 먼저 선택하세요.";
            return;
        }

        if (!_engine.IsOutputActive)
        {
            MonitorInfo? monitor = SelectedOutputMonitor();
            if (monitor is null)
            {
                StatusText.Text = "출력 모니터를 먼저 선택하세요.";
                return;
            }
            _engine.StartOutput(monitor);
        }

        CancelAutoStart(null);
        _engine.StartCapture(target);
        UpdateFeedbackWarning();
    }

    private void OnStopCaptureClick(object sender, RoutedEventArgs e)
    {
        CancelAutoStart(null);
        StopSlideAdvanceTimer();
        _engine.StopSource();
        UpdatePlaybackUi();
    }

    private void OnStartOutputClick(object sender, RoutedEventArgs e)
    {
        MonitorInfo? monitor = SelectedOutputMonitor();
        if (monitor is null)
        {
            StatusText.Text = "출력 모니터를 선택하세요.";
            return;
        }

        _engine.StartOutput(monitor);
        UpdateFeedbackWarning();
    }

    private void OnStopOutputClick(object sender, RoutedEventArgs e)
    {
        CancelAutoStart(null);
        _engine.StopOutput();
        UpdateFeedbackWarning();
    }

    private void OnTopmostChanged(object sender, RoutedEventArgs e)
    {
        // XAML 의 IsChecked 초기값이 생성자 도중 이 핸들러를 호출하므로 반드시 가드가 필요하다.
        if (_suppressSync) return;
        _appSettings.OutputTopmost = TopmostCheck.IsChecked == true;
        _engine.Window?.SetTopmost(_appSettings.OutputTopmost);
        SaveAppSettings(quiet: true);
    }

    private void OnGeometryToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSync) return;
        Settings.CornerPinEnabled = CornerPinCheck.IsChecked == true;
        Settings.BezierEnabled = BezierCheck.IsChecked == true;
        Settings.MaskEnabled = MaskEnableCheck.IsChecked == true;
        _engine.InvalidateGeometry();
    }

    private void OnOverlayToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSync) return;
        Overlay.EditMode = EditModeCheck.IsChecked == true;
        Overlay.ShowControlGrid = ControlGridCheck.IsChecked == true;
        Overlay.ShowReferenceGrid = ReferenceGridCheck.IsChecked == true;
        Overlay.ShowDiagonals = DiagonalCheck.IsChecked == true;
        _engine.RequestRender();
    }

    private void OnPatternChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSync) return;
        if (PatternCombo.SelectedItem is not PatternListItem item) return;
        Overlay.Pattern = item.Pattern;
        _engine.RequestRender();
    }

    private void OnGridSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSync) return;
        if (GridSizeCombo.SelectedItem is not GridSizeListItem item) return;
        if (item.Size == Settings.Grid.GridSize) return;

        _engine.History.Push(Settings);
        Settings.Grid.Resize(item.Size);
        Overlay.ClearSelection();
        _engine.InvalidateGeometry();
    }

    private void OnTessellationChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSync) return;
        Settings.Tessellation = (int)Math.Round(e.NewValue);
        _engine.InvalidateGeometry();
    }

    private void OnColorChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSync) return;
        Settings.ColorEnabled = ColorEnableCheck.IsChecked == true;
        Settings.Brightness = (float)BrightnessSlider.Value;
        Settings.Contrast = (float)ContrastSlider.Value;
        Settings.Gamma = (float)GammaSlider.Value;
        _engine.RequestRender();
    }

    private void OnColorChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => OnColorChanged(sender, (RoutedEventArgs)e);

    private void OnEdgeBlendChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSync) return;
        Settings.EdgeBlendEnabled = EdgeBlendCheck.IsChecked == true;
        Settings.EdgeBlendLeft = (float)BlendLeftSlider.Value;
        Settings.EdgeBlendRight = (float)BlendRightSlider.Value;
        Settings.EdgeBlendTop = (float)BlendTopSlider.Value;
        Settings.EdgeBlendBottom = (float)BlendBottomSlider.Value;
        Settings.EdgeBlendGamma = (float)BlendGammaSlider.Value;
        _engine.RequestRender();
    }

    private void OnEdgeBlendChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => OnEdgeBlendChanged(sender, (RoutedEventArgs)e);

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _engine.History.Push(Settings);
        Settings.ResetGeometry();
        Overlay.ClearSelection();
        _engine.InvalidateGeometry();
        SyncUiFromState();
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (!_engine.History.Undo(Settings)) return;
        Overlay.ClearSelection();
        _engine.InvalidateGeometry();
        SyncUiFromState();
    }

    private void OnRedoClick(object sender, RoutedEventArgs e)
    {
        if (!_engine.History.Redo(Settings)) return;
        Overlay.ClearSelection();
        _engine.InvalidateGeometry();
        SyncUiFromState();
    }

    private void OnAddMaskClick(object sender, RoutedEventArgs e)
    {
        _editor.AddMask();
        SyncUiFromState();
    }

    private void OnDeleteMaskClick(object sender, RoutedEventArgs e)
    {
        _editor.DeleteSelectedMask();
        SyncUiFromState();
    }

    // ---- 프리셋 -----------------------------------------------------------
    private void OnSavePresetClick(object sender, RoutedEventArgs e) => SavePreset();

    private void OnOpenPresetClick(object sender, RoutedEventArgs e) => OpenPreset();

    private void SavePreset()
    {
        var dialog = new SaveFileDialog
        {
            Filter = PresetFileFilter,
            DefaultExt = AppConfig.PresetFileExtension,
            FileName = "warp-preset.json",
            InitialDirectory = EnsureUserDirectory(),
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            PresetStore.Save(BuildPreset(Path.GetFileNameWithoutExtension(dialog.FileName)), dialog.FileName);
            StatusText.Text = $"프리셋을 저장했습니다: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"프리셋 저장에 실패했습니다.\n{ex.Message}", AppConfig.AppName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenPreset()
    {
        var dialog = new OpenFileDialog
        {
            Filter = PresetFileFilter,
            InitialDirectory = EnsureUserDirectory(),
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            Preset? preset = PresetStore.Load(dialog.FileName);
            if (preset is null)
            {
                StatusText.Text = "프리셋을 읽지 못했습니다.";
                return;
            }
            ApplyPreset(preset, restoreSourceAndOutput: true);
            StatusText.Text = $"프리셋을 불러왔습니다: {preset.Name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"프리셋을 불러오지 못했습니다.\n{ex.Message}", AppConfig.AppName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string EnsureUserDirectory()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.UserDataDirectory);
            return AppConfig.UserDataDirectory;
        }
        catch
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }

    private Preset BuildPreset(string name)
        => Preset.FromState(name, Settings, _engine.Source ?? BuildSelectedTarget(),
            _engine.OutputMonitor?.DeviceName ?? SelectedOutputMonitor()?.DeviceName,
            _media.IsActive ? CurrentMediaState() : null);

    private PresetMedia CurrentMediaState() => new()
    {
        Kind = _media.Kind,
        Path = _media.Path,
        Loop = LoopCheck.IsChecked == true,
        Volume = VolumeSlider.Value,
        SlideIntervalSeconds = SlideIntervalSlider.Value,
    };

    private void ApplyPreset(Preset preset, bool restoreSourceAndOutput)
    {
        _engine.History.Push(Settings);
        preset.ApplyTo(Settings);
        Overlay.ClearSelection();
        _engine.InvalidateGeometry();

        if (restoreSourceAndOutput)
        {
            RefreshSourceLists();
            RestoreOutput(preset);
            RestoreSource(preset);
        }

        SyncUiFromState();
        UpdateFeedbackWarning();
    }

    private void RestoreOutput(Preset preset)
    {
        MonitorInfo? monitor = SourceEnumerator.FindMonitor(_monitors, preset.Output.MonitorDeviceName);
        if (monitor is null) return;

        OutputMonitorCombo.SelectedIndex = IndexOfMonitor(monitor.DeviceName);
        _engine.StartOutput(monitor);
    }

    private void RestoreSource(Preset preset)
    {
        if (preset.Source.Kind == CaptureSourceKind.Monitor)
        {
            StatusText.Text = "모니터 전체 캡처는 더 이상 지원하지 않습니다. [내장 재생] 또는 [창] 에서 소스를 선택하세요.";
            return;
        }

        WindowInfo? window = SourceEnumerator.FindWindow(_windows, preset.Source.MatchTitle, preset.Source.MatchProcess);
        if (window is null)
        {
            StatusText.Text = "프리셋의 캡처 대상 창을 찾지 못했습니다. 목록에서 직접 선택하세요.";
            return;
        }

        SourceTabs.SelectedIndex = WindowSourceTabIndex;
        WindowList.SelectedIndex = _windows.IndexOf(window);
        _engine.StartCapture(CaptureTarget.FromWindow(window));
    }

    /// <summary>시작 시 지정된 프리셋(없으면 마지막 세션)을 복원하고 소스/출력을 미리 선택한다.</summary>
    private void RestoreStartupState()
    {
        string? presetPath = _appSettings.StartupPresetPath;
        bool fromFile = !string.IsNullOrWhiteSpace(presetPath) && File.Exists(presetPath);

        Preset? preset;
        try
        {
            preset = fromFile ? PresetStore.Load(presetPath!) : PresetStore.LoadLastSession();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"시작 프리셋을 읽지 못했습니다: {ex.Message}";
            return;
        }
        if (preset is null) return;

        try
        {
            preset.ApplyTo(Settings);
            _engine.History.Clear();
            _startupPreset = preset;

            OutputMonitorCombo.SelectedIndex = IndexOfMonitor(preset.Output.MonitorDeviceName);
            RestoreMediaSelection(preset);
            PreselectSource(preset);

            StatusText.Text = fromFile
                ? $"시작 프리셋을 불러왔습니다: {Path.GetFileName(presetPath)}"
                : "마지막 상태를 복원했습니다. [출력 시작] 을 눌러 투사를 시작하세요.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"시작 상태를 복원하지 못했습니다: {ex.Message}";
        }
    }

    /// <summary>프리셋의 내부 재생 설정을 UI 에 반영한다(재생은 시작하지 않는다).</summary>
    private void RestoreMediaSelection(Preset preset)
    {
        if (!preset.Media.IsActive) return;

        _media = new PresetMedia
        {
            Kind = preset.Media.Kind,
            Path = preset.Media.Path,
            Loop = preset.Media.Loop,
            Volume = preset.Media.Volume,
            SlideIntervalSeconds = preset.Media.SlideIntervalSeconds,
        };
        _slidePaths.Clear();

        _suppressSync = true;
        try
        {
            LoopCheck.IsChecked = _media.Loop;
            VolumeSlider.Value = Math.Clamp(_media.Volume, 0.0, 1.0);
            SlideIntervalSlider.Value = Math.Clamp(_media.SlideIntervalSeconds, 0.0, AppConfig.MaxSlideIntervalSeconds);
            SlideIntervalText.Text = _media.SlideIntervalSeconds <= 0 ? "수동" : $"{_media.SlideIntervalSeconds:F0}초";
        }
        finally
        {
            _suppressSync = false;
        }

        SourceTabs.SelectedIndex = MediaSourceTabIndex;
        UpdatePlaybackUi();
    }

    /// <summary>프리셋에 기록된 소스를 목록에서 선택만 해 둔다(캡처는 시작하지 않는다).</summary>
    private void PreselectSource(Preset preset)
    {
        if (preset.Media.IsActive) return;

        if (preset.Source.Kind == CaptureSourceKind.Monitor) return;

        WindowInfo? window = SourceEnumerator.FindWindow(_windows, preset.Source.MatchTitle, preset.Source.MatchProcess);
        if (window is null) return;
        SourceTabs.SelectedIndex = WindowSourceTabIndex;
        WindowList.SelectedIndex = _windows.IndexOf(window);
    }

    // ---- UI 동기화 --------------------------------------------------------
    private void SyncUiFromState()
    {
        _suppressSync = true;
        try
        {
            CornerPinCheck.IsChecked = Settings.CornerPinEnabled;
            BezierCheck.IsChecked = Settings.BezierEnabled;
            MaskEnableCheck.IsChecked = Settings.MaskEnabled;
            TessellationSlider.Value = Settings.Tessellation;
            GridSizeCombo.SelectedIndex = Settings.Grid.GridSize - AppConfig.MinGridSize;

            EditModeCheck.IsChecked = Overlay.EditMode;
            ControlGridCheck.IsChecked = Overlay.ShowControlGrid;
            ReferenceGridCheck.IsChecked = Overlay.ShowReferenceGrid;
            DiagonalCheck.IsChecked = Overlay.ShowDiagonals;
            PatternCombo.SelectedIndex = (int)Overlay.Pattern;

            ColorEnableCheck.IsChecked = Settings.ColorEnabled;
            BrightnessSlider.Value = Settings.Brightness;
            ContrastSlider.Value = Settings.Contrast;
            GammaSlider.Value = Settings.Gamma;

            EdgeBlendCheck.IsChecked = Settings.EdgeBlendEnabled;
            BlendLeftSlider.Value = Settings.EdgeBlendLeft;
            BlendRightSlider.Value = Settings.EdgeBlendRight;
            BlendTopSlider.Value = Settings.EdgeBlendTop;
            BlendBottomSlider.Value = Settings.EdgeBlendBottom;
            BlendGammaSlider.Value = Settings.EdgeBlendGamma;

            UndoButton.IsEnabled = _engine.History.CanUndo;
            RedoButton.IsEnabled = _engine.History.CanRedo;
        }
        finally
        {
            _suppressSync = false;
        }
    }

    /// <summary>소스와 출력이 같은 모니터이면 피드백 루프 경고를 표시한다.</summary>
    private void UpdateFeedbackWarning()
    {
        MonitorInfo? output = _engine.OutputMonitor ?? SelectedOutputMonitor();
        if (output is null)
        {
            FeedbackWarning.Visibility = Visibility.Collapsed;
            return;
        }

        IntPtr sourceMonitor = IntPtr.Zero;
        if (IsWindowSourceTab)
        {
            WindowInfo? window = SelectedWindow();
            if (window is not null && Win32.IsWindow(window.Handle)) sourceMonitor = window.MonitorHandle;
        }

        bool sameMonitor = sourceMonitor != IntPtr.Zero && sourceMonitor == output.Handle;
        FeedbackWarning.Visibility = sameMonitor ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- DWM 썸네일 미리보기 ---------------------------------------------
    private void OnWindowLayoutChanged(object sender, EventArgs e) => UpdateThumbnail();

    private void UpdateThumbnail()
    {
        UnregisterThumbnail();

        IntPtr destination = new WindowInteropHelper(this).Handle;
        if (destination == IntPtr.Zero) return;

        IntPtr sourceHandle = IsWindowSourceTab ? SelectedWindow()?.Handle ?? IntPtr.Zero : IntPtr.Zero;
        if (sourceHandle == IntPtr.Zero || !Win32.IsWindow(sourceHandle))
        {
            PreviewPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        if (Win32.DwmRegisterThumbnail(destination, sourceHandle, out _thumbnail) != 0 || _thumbnail == IntPtr.Zero)
        {
            PreviewPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Point topLeft = PreviewArea.TranslatePoint(new Point(0, 0), this);

        var properties = new Win32.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Win32.DWM_TNP_RECTDESTINATION | Win32.DWM_TNP_VISIBLE |
                      Win32.DWM_TNP_OPACITY | Win32.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new Win32.RECT
            {
                Left = (int)(topLeft.X * dpi.DpiScaleX),
                Top = (int)(topLeft.Y * dpi.DpiScaleY),
                Right = (int)((topLeft.X + PreviewArea.ActualWidth) * dpi.DpiScaleX),
                Bottom = (int)((topLeft.Y + PreviewArea.ActualHeight) * dpi.DpiScaleY),
            },
            opacity = ThumbnailOpacity,
            fVisible = true,
            fSourceClientAreaOnly = true,
        };

        Win32.DwmUpdateThumbnailProperties(_thumbnail, ref properties);
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void UnregisterThumbnail()
    {
        if (_thumbnail == IntPtr.Zero) return;
        Win32.DwmUnregisterThumbnail(_thumbnail);
        _thumbnail = IntPtr.Zero;
    }

    // ---- 내장 재생 (동영상 · 슬라이드) -----------------------------------
    private void StartPlaybackTimer()
    {
        _playbackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _playbackTimer.Tick += (_, _) => UpdatePlaybackUi();
        _playbackTimer.Start();
    }

    private void UpdatePlaybackUi()
    {
        bool mediaActive = _engine.IsMediaActive;
        PlaybackGroup.IsEnabled = mediaActive;

        VideoPlayer? video = _engine.Video;
        SlideDeck? slides = _engine.Slides;

        bool hasVideo = video is not null && video.IsOpen;
        PositionSlider.IsEnabled = hasVideo;
        RestartMediaButton.IsEnabled = hasVideo;
        VolumeSlider.IsEnabled = hasVideo;
        PreviousSlideButton.IsEnabled = slides is not null && slides.Count > 1;
        NextSlideButton.IsEnabled = slides is not null && slides.Count > 1;
        SlideIntervalSlider.IsEnabled = slides is not null;

        if (hasVideo)
        {
            double duration = video!.Duration;
            double position = video.Position;
            _updatingPosition = true;
            try
            {
                PositionSlider.Maximum = duration > 0 ? duration : 1.0;
                PositionSlider.Value = Math.Clamp(position, 0.0, PositionSlider.Maximum);
            }
            finally
            {
                _updatingPosition = false;
            }
            PositionText.Text = $"{FormatTime(position)} / {FormatTime(duration)}";
        }
        else
        {
            PositionText.Text = "0:00 / 0:00";
        }

        SlideIndexText.Text = slides is not null && slides.Count > 0
            ? $"{slides.CurrentIndex + 1} / {slides.Count} 장"
            : string.Empty;

        MediaFileText.Text = _media.IsActive
            ? $"{(_media.IsVideo ? "동영상" : "슬라이드")}: {Path.GetFileName(_media.Path)}" +
              (mediaActive ? "  (재생 중)" : "  (대기)")
            : "열린 파일이 없습니다.";
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes}:{span.Seconds:D2}";
    }

    private static string BuildVideoFilter()
    {
        string patterns = string.Join(";", VideoPlayer.SupportedExtensions.Select(extension => "*" + extension));
        return $"동영상 파일 ({patterns})|{patterns}|모든 파일 (*.*)|*.*";
    }

    private async void OnOpenVideoClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = BuildVideoFilter(), Title = "재생할 동영상 선택" };
        if (dialog.ShowDialog(this) != true) return;

        _slidePaths.Clear();
        _media = new PresetMedia
        {
            Kind = PresetMedia.KindVideo,
            Path = dialog.FileName,
            Loop = LoopCheck.IsChecked == true,
            Volume = VolumeSlider.Value,
        };
        SourceTabs.SelectedIndex = MediaSourceTabIndex;
        await StartCurrentMediaAsync();
    }

    private async void OnOpenSlidesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = SlideImporter.BuildFileFilter(),
            Title = "슬라이드로 재생할 파일 선택",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        string primary = dialog.FileNames[0];
        if (dialog.FileNames.Length > 1 && dialog.FileNames.All(SlideImporter.IsImage))
        {
            // 이미지 여러 장을 그대로 슬라이드로 사용한다.
            _slidePaths = SlideImporter.ImportImages(dialog.FileNames);
        }
        else
        {
            _slidePaths.Clear();
        }

        _media = new PresetMedia
        {
            Kind = PresetMedia.KindSlides,
            Path = primary,
            SlideIntervalSeconds = SlideIntervalSlider.Value,
        };
        SourceTabs.SelectedIndex = MediaSourceTabIndex;
        await StartCurrentMediaAsync();
    }

    private void OnCloseMediaClick(object sender, RoutedEventArgs e)
    {
        StopSlideAdvanceTimer();
        _engine.StopSource();
        _media = new PresetMedia();
        _slidePaths.Clear();
        UpdatePlaybackUi();
        StatusText.Text = "내장 재생을 닫았습니다.";
    }

    /// <summary>현재 선택된 미디어를 실제로 재생한다(필요하면 출력 창을 먼저 연다).</summary>
    private async Task<bool> StartCurrentMediaAsync()
    {
        if (!_media.IsActive || _media.Path is null) return false;
        if (!File.Exists(_media.Path))
        {
            StatusText.Text = $"파일을 찾을 수 없습니다: {_media.Path}";
            return false;
        }
        if (!EnsureOutputStarted()) return false;

        CancelAutoStart(null);
        StopSlideAdvanceTimer();

        if (_media.IsVideo)
        {
            _engine.StartVideo(_media.Path, _media.Loop, _media.Volume);
            UpdatePlaybackUi();
            return _engine.ActiveSourceKind == ProjectionSourceKind.Video;
        }

        if (_slidePaths.Count == 0)
        {
            List<string>? imported = await ImportSlidesAsync(_media.Path);
            if (imported is null || imported.Count == 0) return false;
            _slidePaths = imported;
        }

        _engine.StartSlides(_slidePaths, Path.GetFileName(_media.Path), _media.Path);
        RestartSlideAdvanceTimer();
        UpdatePlaybackUi();
        return _engine.ActiveSourceKind == ProjectionSourceKind.Slides;
    }

    private async Task<List<string>?> ImportSlidesAsync(string path)
    {
        StatusText.Text = "슬라이드를 변환하는 중…";
        try
        {
            return await Task.Run(() => SlideImporter.Import(path,
                message => Dispatcher.BeginInvoke(() => StatusText.Text = message)));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, AppConfig.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "슬라이드 변환에 실패했습니다.";
            return null;
        }
    }

    private bool EnsureOutputStarted()
    {
        if (_engine.IsOutputActive) return true;

        MonitorInfo? monitor = SelectedOutputMonitor();
        if (monitor is null)
        {
            StatusText.Text = "출력 모니터를 먼저 선택하세요.";
            return false;
        }
        _engine.StartOutput(monitor);
        return _engine.IsOutputActive;
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        _engine.ToggleMediaPlayback();
        UpdatePlaybackUi();
    }

    private void OnRestartMediaClick(object sender, RoutedEventArgs e)
    {
        _engine.Video?.Restart();
        _engine.GoToSlide(0);
        UpdatePlaybackUi();
    }

    private void OnLoopChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSync) return;
        _media.Loop = LoopCheck.IsChecked == true;
        if (_engine.Video is not null) _engine.Video.Loop = _media.Loop;
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSync) return;
        _media.Volume = e.NewValue;
        if (_engine.Video is not null) _engine.Video.Volume = e.NewValue;
    }

    private void OnPositionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSync || _updatingPosition) return;
        if (_engine.Video is not null) _engine.Video.Position = e.NewValue;
    }

    private void OnPreviousSlideClick(object sender, RoutedEventArgs e)
    {
        _engine.PreviousSlide();
        UpdatePlaybackUi();
    }

    private void OnNextSlideClick(object sender, RoutedEventArgs e)
    {
        _engine.NextSlide();
        UpdatePlaybackUi();
    }

    private void OnSlideIntervalChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSync) return;
        _media.SlideIntervalSeconds = e.NewValue;
        SlideIntervalText.Text = e.NewValue <= 0 ? "수동" : $"{e.NewValue:F0}초";
        RestartSlideAdvanceTimer();
    }

    private void RestartSlideAdvanceTimer()
    {
        StopSlideAdvanceTimer();
        if (_media.SlideIntervalSeconds <= 0) return;
        if (_engine.ActiveSourceKind != ProjectionSourceKind.Slides) return;

        _slideAdvanceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(
                _media.SlideIntervalSeconds, 1.0, AppConfig.MaxSlideIntervalSeconds)),
        };
        _slideAdvanceTimer.Tick += (_, _) => _engine.NextSlide();
        _slideAdvanceTimer.Start();
    }

    private void StopSlideAdvanceTimer()
    {
        if (_slideAdvanceTimer is null) return;
        _slideAdvanceTimer.Stop();
        _slideAdvanceTimer = null;
    }

    // ---- 앱 설정 · 자동 시작 ---------------------------------------------
    private void SyncAppSettingsUi()
    {
        _suppressSync = true;
        try
        {
            LaunchAtLogonCheck.IsChecked = _appSettings.LaunchAtLogon;
            AutoStartProjectionCheck.IsChecked = _appSettings.AutoStartProjection;
            StartMinimizedCheck.IsChecked = _appSettings.StartMinimized;
            AutoStartRetrySlider.Value = _appSettings.AutoStartRetrySeconds;
            TopmostCheck.IsChecked = _appSettings.OutputTopmost;
            StartupPresetText.Text = string.IsNullOrWhiteSpace(_appSettings.StartupPresetPath)
                ? "마지막 상태 (앱 종료 시 자동 저장됨)"
                : _appSettings.StartupPresetPath;
            CancelAutoStartButton.IsEnabled = _autoStartTimer is not null;
            CheckUpdateOnStartupCheck.IsChecked = _appSettings.CheckForUpdatesOnStartup;
        }
        finally
        {
            _suppressSync = false;
        }
    }

    private void SaveAppSettings(bool quiet)
    {
        if (AppSettingsStore.TrySave(_appSettings, out string? error))
        {
            if (!quiet) StatusText.Text = $"설정을 저장했습니다: {AppSettingsStore.FilePath}";
            return;
        }
        StatusText.Text = $"설정을 저장하지 못했습니다: {error}";
    }

    private void OnLaunchAtLogonChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSync) return;

        bool enable = LaunchAtLogonCheck.IsChecked == true;
        bool succeeded = enable
            ? AutoStartRegistry.TryEnable(out string? error)
            : AutoStartRegistry.TryDisable(out error);

        if (!succeeded)
        {
            StatusText.Text = $"자동 실행 설정을 변경하지 못했습니다: {error}";
            _suppressSync = true;
            LaunchAtLogonCheck.IsChecked = AutoStartRegistry.IsEnabled();
            _suppressSync = false;
            return;
        }

        _appSettings.LaunchAtLogon = enable;
        SaveAppSettings(quiet: true);
        StatusText.Text = enable
            ? $"로그온 시 자동 실행을 등록했습니다. ({AutoStartRegistry.ExecutablePath})"
            : "로그온 시 자동 실행을 해제했습니다.";
    }

    private void OnAutoStartOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSync) return;
        _appSettings.AutoStartProjection = AutoStartProjectionCheck.IsChecked == true;
        _appSettings.StartMinimized = StartMinimizedCheck.IsChecked == true;
        _appSettings.AutoStartRetrySeconds = (int)Math.Round(AutoStartRetrySlider.Value);
        SaveAppSettings(quiet: true);
    }

    private void OnAutoStartOptionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => OnAutoStartOptionChanged(sender, (RoutedEventArgs)e);

    private void OnChooseStartupPresetClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = PresetFileFilter, InitialDirectory = EnsureUserDirectory() };
        if (dialog.ShowDialog(this) != true) return;

        _appSettings.StartupPresetPath = dialog.FileName;
        SaveAppSettings(quiet: true);
        SyncAppSettingsUi();
        StatusText.Text = $"시작 프리셋을 지정했습니다: {Path.GetFileName(dialog.FileName)}";
    }

    private void OnClearStartupPresetClick(object sender, RoutedEventArgs e)
    {
        _appSettings.StartupPresetPath = null;
        SaveAppSettings(quiet: true);
        SyncAppSettingsUi();
        StatusText.Text = "시작 시 마지막 상태를 사용합니다.";
    }

    /// <summary>보정값과 앱 설정을 한 번에 저장한다.</summary>
    private void OnSaveConfigurationClick(object sender, RoutedEventArgs e)
    {
        string target = string.IsNullOrWhiteSpace(_appSettings.StartupPresetPath)
            ? PresetStore.LastSessionPath
            : _appSettings.StartupPresetPath!;

        try
        {
            PresetStore.Save(BuildPreset(Path.GetFileNameWithoutExtension(target)), target);
            SaveAppSettings(quiet: true);
            StatusText.Text = $"현재 설정을 저장했습니다: {target}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"설정 저장에 실패했습니다. {ex.Message}", AppConfig.AppName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCancelAutoStartClick(object sender, RoutedEventArgs e)
        => CancelAutoStart("자동 시작을 취소했습니다.");

    /// <summary>저장된 출력 모니터와 캡처 소스로 자동 연결을 시도한다(대상이 준비될 때까지 재시도).</summary>
    private void BeginAutoStart()
    {
        CancelAutoStart(null);

        bool fromLogon = AutoStartRegistry.LaunchedByLogon();
        TimeSpan firstDelay = fromLogon
            ? TimeSpan.FromSeconds(AppConfig.LogonStartDelaySeconds)
            : TimeSpan.FromMilliseconds(1);

        _autoStartDeadlineUtc = DateTime.UtcNow
            + TimeSpan.FromSeconds(Math.Max(0, _appSettings.AutoStartRetrySeconds))
            + firstDelay;

        _autoStartTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = firstDelay };
        _autoStartTimer.Tick += OnAutoStartTick;
        _autoStartTimer.Start();

        CancelAutoStartButton.IsEnabled = true;
        StatusText.Text = fromLogon
            ? $"자동 시작 대기 중... ({AppConfig.LogonStartDelaySeconds}초 후 연결)"
            : "자동 시작을 진행합니다...";
    }

    private void OnAutoStartTick(object? sender, EventArgs e)
    {
        if (_autoStartTimer is null) return;
        // 첫 틱 이후에는 재시도 간격으로 전환한다.
        _autoStartTimer.Interval = TimeSpan.FromSeconds(AppConfig.AutoStartRetryIntervalSeconds);

        RefreshSourceLists();

        if (!_engine.IsOutputActive)
        {
            MonitorInfo? monitor = SourceEnumerator.FindMonitor(_monitors, _startupPreset?.Output.MonitorDeviceName)
                ?? SelectedOutputMonitor();
            if (monitor is null)
            {
                FailAutoStartIfExpired("자동 시작: 출력 모니터를 찾지 못했습니다.");
                return;
            }
            _engine.StartOutput(monitor);
        }

        // 내부 재생이 지정되어 있으면 캡처 대신 미디어를 시작한다.
        if (_media.IsActive)
        {
            _ = StartCurrentMediaAsync().ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully && task.Result)
                    Dispatcher.BeginInvoke(() => CancelAutoStart($"자동 시작 완료: {Path.GetFileName(_media.Path)}"));
            }, TaskScheduler.Default);
            FailAutoStartIfExpired("자동 시작: 미디어 파일을 열지 못했습니다.");
            return;
        }

        CaptureTarget? target = ResolveAutoStartSource();
        if (target is null)
        {
            FailAutoStartIfExpired("자동 시작: 캡처 대상을 찾지 못했습니다. 목록에서 직접 선택하세요.");
            return;
        }

        _engine.StartCapture(target);
        if (!_engine.IsCapturing)
        {
            FailAutoStartIfExpired("자동 시작: 캡처를 시작하지 못했습니다.");
            return;
        }

        CancelAutoStart($"자동 시작 완료: {target.DisplayName}");
        UpdateFeedbackWarning();
    }

    private CaptureTarget? ResolveAutoStartSource()
    {
        PresetSource? source = _startupPreset?.Source;
        if (source is null) return BuildSelectedTarget();

        if (source.Kind == CaptureSourceKind.Monitor) return null;

        WindowInfo? window = SourceEnumerator.FindWindow(_windows, source.MatchTitle, source.MatchProcess);
        if (window is null) return null;
        SourceTabs.SelectedIndex = WindowSourceTabIndex;
        WindowList.SelectedIndex = _windows.IndexOf(window);
        return CaptureTarget.FromWindow(window);
    }

    private void FailAutoStartIfExpired(string message)
    {
        if (DateTime.UtcNow < _autoStartDeadlineUtc) return;
        CancelAutoStart(message);
    }

    private void CancelAutoStart(string? message)
    {
        if (_autoStartTimer is not null)
        {
            _autoStartTimer.Stop();
            _autoStartTimer.Tick -= OnAutoStartTick;
            _autoStartTimer = null;
        }
        CancelAutoStartButton.IsEnabled = false;
        if (message is not null) StatusText.Text = message;
    }

    // ---- 종료 -------------------------------------------------------------
    // ---- 버전 · 자동 업데이트 ---------------------------------------------
    private void OnUpdateOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSync) return;
        _appSettings.CheckForUpdatesOnStartup = CheckUpdateOnStartupCheck.IsChecked == true;
        SaveAppSettings(quiet: true);
    }

    private void OnCheckUpdateClick(object sender, RoutedEventArgs e) => _ = CheckForUpdatesAsync(quiet: false);

    /// <summary>최신 릴리스를 조회한다. quiet 이면 시작 직후 조용히 확인하는 경로다.</summary>
    private async Task CheckForUpdatesAsync(bool quiet)
    {
        if (_updateBusy) return;

        // 첫 화면 표시와 자동 시작을 방해하지 않도록 시작 직후 확인은 잠깐 미룬다.
        if (quiet) await Task.Delay(TimeSpan.FromSeconds(AppConfig.UpdateStartupCheckDelaySeconds));

        _updateBusy = true;
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "새 버전을 확인하는 중…";
        try
        {
            ReleaseInfo? release = await UpdateService.CheckAsync(CancellationToken.None);
            if (release is null)
            {
                _pendingRelease = null;
                ApplyUpdateButton.Visibility = Visibility.Collapsed;
                UpdateStatusText.Text = $"최신 버전입니다. (버전 {UpdateService.CurrentVersionText})";
                return;
            }

            _pendingRelease = release;
            _stagedUpdatePath = null;
            ApplyUpdateButton.Visibility = Visibility.Visible;
            UpdateStatusText.Text = FormatReleaseSummary(release);
            StatusText.Text = $"새 버전 {release.Version} 이 있습니다. [9. 버전 · 업데이트] 에서 설치할 수 있습니다.";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = ex is InvalidOperationException
                ? ex.Message
                : $"업데이트 확인에 실패했습니다: {ex.Message}";
        }
        finally
        {
            _updateBusy = false;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void OnApplyUpdateClick(object sender, RoutedEventArgs e)
    {
        ReleaseInfo? release = _pendingRelease;
        if (release is null || _updateBusy) return;

        MessageBoxResult answer = MessageBox.Show(this,
            $"버전 {release.Version} 을 설치하고 앱을 재시작합니다.\n투사가 잠시 중단됩니다. 계속할까요?",
            AppConfig.AppName, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        _updateBusy = true;
        ApplyUpdateButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        try
        {
            UpdateStatusText.Text = $"{release.AssetName} 을 내려받는 중…";
            var progress = new Progress<double>(value => UpdateProgress.Value = value);
            _stagedUpdatePath ??= await UpdateService.DownloadAsync(release, progress, CancellationToken.None);

            UpdateStatusText.Text = "설치하고 재시작합니다…";
            UpdateService.StartApply(_stagedUpdatePath);

            // Close 로 끝내야 마지막 세션과 앱 설정이 저장된다.
            Close();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"업데이트를 설치하지 못했습니다: {ex.Message}";
            UpdateProgress.Visibility = Visibility.Collapsed;
            ApplyUpdateButton.IsEnabled = true;
            CheckUpdateButton.IsEnabled = true;
            _updateBusy = false;
        }
    }

    private static string FormatReleaseSummary(ReleaseInfo release)
    {
        string size = release.Size > 0 ? $" · {release.Size / (1024.0 * 1024.0):F1} MB" : string.Empty;
        string notes = release.Notes.Length == 0
            ? string.Empty
            : "\n" + release.Notes.ReplaceLineEndings(" ").Trim();
        return $"새 버전 {release.Version} (태그 {release.Tag}){size}{notes}";
    }

    private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        CancelAutoStart(null);
        StopSlideAdvanceTimer();
        _playbackTimer?.Stop();
        PresetStore.SaveLastSession(BuildPreset("last-session"));
        AppSettingsStore.TrySave(_appSettings, out _);
        UnregisterThumbnail();
        _editor.Detach();
        _engine.Dispose();
    }
}
