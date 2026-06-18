// App.xaml.cs
using System.Windows;

namespace HachBobAI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            var err = ex.Exception;
            Console.WriteLine($"[app] Unhandled: {err.Message}");
            Console.WriteLine($"[app] Type: {err.GetType().FullName}");
            Console.WriteLine($"[app] Stack:\n{err.StackTrace}");
            if (err.InnerException != null)
            {
                Console.WriteLine($"[app] Inner: {err.InnerException.Message}");
                Console.WriteLine($"[app] Inner stack:\n{err.InnerException.StackTrace}");
            }
            ex.Handled = true;
        };
    }
}
