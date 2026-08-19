using System.Windows;
using System.Windows.Threading;

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
            $"예기치 않은 오류가 발생했습니다.\n\n{exception.GetType().Name}: {exception.Message}\n\n{exception.StackTrace}",
            AppConfig.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
