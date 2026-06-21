// App.xaml.cs
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace HachBobAI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Важно: подписываемся ДО base.OnStartup(e), иначе исключение при создании MainWindow
        // может быть проглочено/не показано, и процесс останется висеть без окна.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            LogFatal(ex.ExceptionObject as Exception ?? new Exception(ex.ExceptionObject?.ToString() ?? "Unknown fatal error"));
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            LogFatal(ex.Exception);
            ex.SetObserved();
        };

        ShutdownMode = ShutdownMode.OnMainWindowClose;

        base.OnStartup(e);

        // Если в App.xaml случайно нет StartupUri, окно не создастся, а процесс будет висеть.
        // Проверяем после старта диспетчера и создаём MainWindow вручную только если его нет.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (MainWindow != null) return;

            try
            {
                var window = new MainWindow();
                MainWindow = window;
                window.Show();
            }
            catch (Exception ex)
            {
                ShowFatal(ex);
            }
        }), DispatcherPriority.ApplicationIdle);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Не оставляем приложение висеть без окна. Показываем ошибку и завершаемся.
        e.Handled = true;
        ShowFatal(e.Exception);
    }

    private static void LogFatal(Exception ex)
    {
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_error.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }

    private void ShowFatal(Exception ex)
    {
        LogFatal(ex);
        try
        {
            MessageBox.Show(ex.ToString(), "HachBobAI startup error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
        Shutdown(-1);
    }
}
