using System.Diagnostics;
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
    private const string PresetFileFilter = "ProjectorWarp preset (*.json)|*.json|All files (*.*)|*.*";
    private const byte ThumbnailOpacity = 255;

    private readonly ProjectionEngine _engine;
    private readonly OverlayEditor _editor;

    private List<WindowInfo> _windows = new();
    private List<MonitorInfo> _monitors = new();
    private IntPtr _thumbnail = IntPtr.Zero;
    /// <summary>현재 썸네일이 가리키는 소스 창. 같으면 재등록 없이 사각형만 갱신한다.</summary>
    private IntPtr _thumbnailSource = IntPtr.Zero;
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

        _engine.MediaEnded += () => StatusText.Text = "Playback finished.";
        _engine.SlideChanged += UpdatePlaybackUi;

        Title = $"{AppConfig.AppName} {UpdateService.CurrentVersionText} — Curved Surface Warping";
        VersionText.Text = $"Version {UpdateService.CurrentVersionText}";
        UpdateSourceText.Text = $"Update source: {UpdateService.Repository}";
        SponsorUrlText.Text = AppConfig.SponsorUrl;

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
            PatternListItem.Create(TestPattern.None, "None"),
            PatternListItem.Create(TestPattern.Grid, "Grid"),
            PatternListItem.Create(TestPattern.Checker, "Checkerboard"),
            PatternListItem.Create(TestPattern.Rings, "Concentric rings"),
            PatternListItem.Create(TestPattern.ColorBars, "Colour bars"),
            PatternListItem.Create(TestPattern.WhiteField, "White field"),
            PatternListItem.Create(TestPattern.BlackField, "Black field"),
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
                "Windows.Graphics.Capture is not available on this PC.\n" +
                "Windows 10 version 1903 (build 18362) or later is required.",
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

        UpdatePreviewVisibility();
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

        List<WindowInfo> windows = SourceEnumerator.EnumerateWindows(excluded);
        List<MonitorInfo> monitors = SourceEnumerator.EnumerateMonitors();

        // 자동 시작 재시도는 2초마다 이 메서드를 부른다. 목록이 그대로일 때 ItemsSource 를
        // 다시 만들면 WPF 아이템을 전부 새로 짓고 선택도 풀리므로, 바뀐 경우에만 갱신한다.
        if (!SameWindows(windows, _windows))
        {
            _windows = windows;
            WindowList.ItemsSource = _windows.Select(WindowListItem.From).ToList();
        }

        if (SameMonitors(monitors, _monitors)) return;

        _monitors = monitors;
        MonitorInfo? previousOutput = SelectedOutputMonitor();
        OutputMonitorCombo.ItemsSource = _monitors.Select(MonitorListItem.From).ToList();
        OutputMonitorCombo.SelectedIndex = IndexOfMonitor(previousOutput?.DeviceName);
    }

    private static bool SameWindows(List<WindowInfo> left, List<WindowInfo> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (left[i].Handle != right[i].Handle ||
                !string.Equals(left[i].Title, right[i].Title, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static bool SameMonitors(List<MonitorInfo> left, List<MonitorInfo> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (left[i].Handle != right[i].Handle ||
                !string.Equals(left[i].DeviceName, right[i].DeviceName, StringComparison.OrdinalIgnoreCase) ||
                left[i].Bounds.Width != right[i].Bounds.Width ||
                left[i].Bounds.Height != right[i].Bounds.Height)
                return false;
        }
        return true;
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

    private async void OnSourceTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdatePreviewVisibility();
        UpdateThumbnail();
        UpdateFeedbackWarning();
        // 캡처 시작 버튼이 없으므로, 출력 중이면 탭을 바꾸는 순간 소스도 바뀐다.
        await StartSelectedSourceAsync();
    }

    private async void OnWindowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateThumbnail();
        UpdateFeedbackWarning();
        if (IsWindowSourceTab) await StartSelectedSourceAsync();
    }

    /// <summary>
    /// [출력 시작] 은 출력 창을 열고 <b>현재 선택된 소스까지 함께 시작</b>한다.
    /// 화면 표시 여부는 출력 시작/중지 두 버튼만으로 결정된다.
    /// </summary>
    private async void OnStartOutputClick(object sender, RoutedEventArgs e)
    {
        MonitorInfo? monitor = SelectedOutputMonitor();
        if (monitor is null)
        {
            StatusText.Text = "Choose an output monitor.";
            return;
        }

        CancelAutoStart(null);
        if (!_engine.IsOutputActive) _engine.StartOutput(monitor);
        await StartSelectedSourceAsync();
        UpdateFeedbackWarning();
    }

    private void OnStopOutputClick(object sender, RoutedEventArgs e)
    {
        CancelAutoStart(null);
        StopSlideAdvanceTimer();
        _engine.StopOutput();
        UpdatePlaybackUi();
        UpdateFeedbackWarning();
    }

    /// <summary>현재 탭의 소스를 출력에 연결한다. 출력 창이 없으면 아무것도 하지 않는다.</summary>
    private async Task StartSelectedSourceAsync()
    {
        if (!_engine.IsOutputActive) return;

        if (IsMediaSourceTab)
        {
            if (!_media.IsActive)
            {
                StatusText.Text = "Pick a file with Open video or Open slides.";
                return;
            }
            await StartCurrentMediaAsync();
            return;
        }

        CaptureTarget? target = BuildSelectedTarget();
        if (target is null)
        {
            StatusText.Text = "Select the window to capture from the list.";
            return;
        }
        _engine.StartCapture(target);
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
            StatusText.Text = $"Preset saved: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save the preset.\n{ex.Message}", AppConfig.AppName,
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
                StatusText.Text = "Could not read the preset.";
                return;
            }
            ApplyPreset(preset, restoreSourceAndOutput: true);
            StatusText.Text = $"Preset loaded: {preset.Name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not load the preset.\n{ex.Message}", AppConfig.AppName,
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
            StatusText.Text = "Whole-monitor capture is no longer supported. Choose a source on the Built-in playback or Window tab.";
            return;
        }

        WindowInfo? window = SourceEnumerator.FindWindow(_windows, preset.Source.MatchTitle, preset.Source.MatchProcess);
        if (window is null)
        {
            StatusText.Text = "The window this preset captures was not found. Select one from the list.";
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
            StatusText.Text = $"Could not read the start-up preset: {ex.Message}";
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
                ? $"Start-up preset loaded: {Path.GetFileName(presetPath)}"
                : "Last state restored. Press Start output to begin projecting.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not restore the start-up state: {ex.Message}";
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
            SlideIntervalText.Text = _media.SlideIntervalSeconds <= 0 ? "Manual" : $"{_media.SlideIntervalSeconds:F0} s";
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
            FeedbackWarningArea.Visibility = Visibility.Collapsed;
            return;
        }

        IntPtr sourceMonitor = IntPtr.Zero;
        if (IsWindowSourceTab)
        {
            WindowInfo? window = SelectedWindow();
            if (window is not null && Win32.IsWindow(window.Handle)) sourceMonitor = window.MonitorHandle;
        }

        bool sameMonitor = sourceMonitor != IntPtr.Zero && sourceMonitor == output.Handle;
        FeedbackWarningArea.Visibility = sameMonitor ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- DWM 썸네일 미리보기 ---------------------------------------------
    private void OnWindowLayoutChanged(object sender, EventArgs e) => UpdateThumbnail();

    /// <summary>미리보기는 [창] 탭에서만 쓴다. 다른 탭에서는 자리만 차지한다.</summary>
    private void UpdatePreviewVisibility()
        => PreviewArea.Visibility = IsWindowSourceTab ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateThumbnail()
    {
        IntPtr destination = new WindowInteropHelper(this).Handle;
        if (destination == IntPtr.Zero) return;

        IntPtr sourceHandle = IsWindowSourceTab ? SelectedWindow()?.Handle ?? IntPtr.Zero : IntPtr.Zero;
        if (sourceHandle == IntPtr.Zero || !Win32.IsWindow(sourceHandle))
        {
            UnregisterThumbnail();
            PreviewPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        // 창을 옮기거나 크기를 바꿀 때마다 이 메서드가 불린다. 소스가 그대로면
        // 등록을 다시 하지 않고 목적지 사각형만 갱신한다(DWM 재등록은 비싸다).
        if (_thumbnail == IntPtr.Zero || sourceHandle != _thumbnailSource)
        {
            UnregisterThumbnail();
            if (Win32.DwmRegisterThumbnail(destination, sourceHandle, out _thumbnail) != 0 || _thumbnail == IntPtr.Zero)
            {
                _thumbnail = IntPtr.Zero;
                PreviewPlaceholder.Visibility = Visibility.Visible;
                return;
            }
            _thumbnailSource = sourceHandle;
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
        _thumbnailSource = IntPtr.Zero;
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
            ? $"{slides.CurrentIndex + 1} / {slides.Count}"
            : string.Empty;

        MediaFileText.Text = _media.IsActive
            ? $"{(_media.IsVideo ? "Video" : "Slides")}: {Path.GetFileName(_media.Path)}" +
              (mediaActive ? "  (playing)" : "  (idle)")
            : "No file open.";
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
        return $"Video files ({patterns})|{patterns}|All files (*.*)|*.*";
    }

    private async void OnOpenVideoClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = BuildVideoFilter(), Title = "Choose a video to play" };
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
            Title = "Choose a file to show as slides",
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
        StatusText.Text = "Built-in playback closed.";
    }

    /// <summary>현재 선택된 미디어를 실제로 재생한다(필요하면 출력 창을 먼저 연다).</summary>
    private async Task<bool> StartCurrentMediaAsync()
    {
        if (!_media.IsActive || _media.Path is null) return false;
        if (!File.Exists(_media.Path))
        {
            StatusText.Text = $"File not found: {_media.Path}";
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
        StatusText.Text = "Converting slides…";
        try
        {
            return await Task.Run(() => SlideImporter.Import(path,
                message => Dispatcher.BeginInvoke(() => StatusText.Text = message)));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, AppConfig.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Slide conversion failed.";
            return null;
        }
    }

    private bool EnsureOutputStarted()
    {
        if (_engine.IsOutputActive) return true;

        MonitorInfo? monitor = SelectedOutputMonitor();
        if (monitor is null)
        {
            StatusText.Text = "Choose an output monitor first.";
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
        SlideIntervalText.Text = e.NewValue <= 0 ? "Manual" : $"{e.NewValue:F0} s";
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
                ? "Last state (saved automatically on exit)"
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
            if (!quiet) StatusText.Text = $"Settings saved: {AppSettingsStore.FilePath}";
            return;
        }
        StatusText.Text = $"Could not save the settings: {error}";
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
            StatusText.Text = $"Could not change the launch-at-logon setting: {error}";
            _suppressSync = true;
            LaunchAtLogonCheck.IsChecked = AutoStartRegistry.IsEnabled();
            _suppressSync = false;
            return;
        }

        _appSettings.LaunchAtLogon = enable;
        SaveAppSettings(quiet: true);
        StatusText.Text = enable
            ? $"Launch at logon is on. ({AutoStartRegistry.ExecutablePath})"
            : "Launch at logon is off.";
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
        StatusText.Text = $"Start-up preset set: {Path.GetFileName(dialog.FileName)}";
    }

    private void OnClearStartupPresetClick(object sender, RoutedEventArgs e)
    {
        _appSettings.StartupPresetPath = null;
        SaveAppSettings(quiet: true);
        SyncAppSettingsUi();
        StatusText.Text = "The last state will be used at start-up.";
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
            StatusText.Text = $"Current setup saved: {target}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save the setup. {ex.Message}", AppConfig.AppName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCancelAutoStartClick(object sender, RoutedEventArgs e)
        => CancelAutoStart("Auto-start cancelled.");

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
            ? $"Auto-start waiting… (connecting in {AppConfig.LogonStartDelaySeconds} s)"
            : "Auto-start running…";
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
                FailAutoStartIfExpired("Auto-start: no output monitor found.");
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
                    Dispatcher.BeginInvoke(() => CancelAutoStart($"Auto-start done: {Path.GetFileName(_media.Path)}"));
            }, TaskScheduler.Default);
            FailAutoStartIfExpired("Auto-start: could not open the media file.");
            return;
        }

        CaptureTarget? target = ResolveAutoStartSource();
        if (target is null)
        {
            FailAutoStartIfExpired("Auto-start: capture target not found. Select one from the list.");
            return;
        }

        _engine.StartCapture(target);
        if (!_engine.IsCapturing)
        {
            FailAutoStartIfExpired("Auto-start: could not start capture.");
            return;
        }

        CancelAutoStart($"Auto-start done: {target.DisplayName}");
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
        UpdateStatusText.Text = "Checking for a new version…";
        try
        {
            ReleaseInfo? release = await UpdateService.CheckAsync(CancellationToken.None);
            if (release is null)
            {
                _pendingRelease = null;
                ApplyUpdateButton.Visibility = Visibility.Collapsed;
                UpdateStatusText.Text = $"You are up to date. (version {UpdateService.CurrentVersionText})";
                return;
            }

            _pendingRelease = release;
            _stagedUpdatePath = null;
            ApplyUpdateButton.Visibility = Visibility.Visible;
            UpdateStatusText.Text = FormatReleaseSummary(release);
            StatusText.Text = $"Version {release.Version} is available. Install it under 8. Version · updates.";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = ex is InvalidOperationException
                ? ex.Message
                : $"Could not check for updates: {ex.Message}";
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
            $"Version {release.Version} will be installed and the app restarted.\nProjection stops briefly. Continue?",
            AppConfig.AppName, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        _updateBusy = true;
        ApplyUpdateButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        try
        {
            UpdateStatusText.Text = $"Downloading {release.AssetName}…";
            var progress = new Progress<double>(value => UpdateProgress.Value = value);
            _stagedUpdatePath ??= await UpdateService.DownloadAsync(release, progress, CancellationToken.None);

            UpdateStatusText.Text = "Installing and restarting…";
            UpdateService.StartApply(_stagedUpdatePath);

            // Close 로 끝내야 마지막 세션과 앱 설정이 저장된다.
            Close();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Could not install the update: {ex.Message}";
            UpdateProgress.Visibility = Visibility.Collapsed;
            ApplyUpdateButton.IsEnabled = true;
            CheckUpdateButton.IsEnabled = true;
            _updateBusy = false;
        }
    }

    private static string FormatReleaseSummary(ReleaseInfo release)
    {
        string size = release.Size > 0 ? $" · {release.Size / (1024.0 * 1024.0):F1} MB" : string.Empty;
        return $"Version {release.Version} (tag {release.Tag}){size}{FormatReleaseNotes(release.Notes)}";
    }

    /// <summary>릴리스 본문은 통째로 넣으면 패널을 밀어내므로 첫 줄만 짧게 보여준다.</summary>
    private static string FormatReleaseNotes(string notes)
    {
        string? first = notes
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrEmpty(first)) return string.Empty;

        return first.Length <= AppConfig.UpdateNotesPreviewLength
            ? "\n" + first
            : "\n" + first[..AppConfig.UpdateNotesPreviewLength].TrimEnd() + "…";
    }

    // ---- 후원 -------------------------------------------------------------
    private void OnSponsorClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // 앱은 결제에 관여하지 않고 후원 페이지만 기본 브라우저로 연다.
            Process.Start(new ProcessStartInfo(AppConfig.SponsorUrl) { UseShellExecute = true });
            StatusText.Text = $"Opened the sponsor page: {AppConfig.SponsorUrl}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open the sponsor page: {ex.Message}. Copy the link and paste it into a browser.";
        }
    }

    private void OnCopySponsorLinkClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AppConfig.SponsorUrl);
            StatusText.Text = "Sponsor link copied to the clipboard.";
        }
        catch (Exception ex)
        {
            // 다른 프로그램이 클립보드를 잡고 있으면 실패할 수 있다.
            StatusText.Text = $"Could not copy the link: {ex.Message}";
        }
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
