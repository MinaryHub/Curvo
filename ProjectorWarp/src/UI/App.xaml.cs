using System.Windows;
using System.Windows.Threading;
using ProjectorWarp.Update;

namespace ProjectorWarp.UI;

public partial class App : Application
{
    private bool _mainWindowShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 처리되지 않은 예외가 창도 없이 프로세스만 남기는 상황을 막고 원인을 보여준다.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        base.OnStartup(e);

        // 업데이트 교체 모드로 실행되었으면 창을 띄우지 않고 교체만 하고 끝낸다.
        if (UpdateService.TryApplyPendingUpdate(e.Args, out string? updateError))
        {
            if (updateError is not null)
            {
                MessageBox.Show(updateError, AppConfig.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Shutdown(updateError is null ? 0 : 1);
            return;
        }

        UpdateService.CleanStagingDirectory();

        MainWindow = new MainWindow();
        MainWindow.Show();
        _mainWindowShown = true;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ReportFatal(e.Exception);

        // 창이 뜨기 전에 실패했다면 복구할 상태가 없으므로 종료한다.
        if (!_mainWindowShown) Shutdown(1);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception) ReportFatal(exception);
    }

    private static void ReportFatal(Exception exception)
    {
        MessageBox.Show(
            $"An unexpected error occurred.\n\n{exception.GetType().Name}: {exception.Message}\n\n{exception.StackTrace}",
            AppConfig.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
