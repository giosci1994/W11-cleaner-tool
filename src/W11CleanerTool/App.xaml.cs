using System.Windows;

namespace W11CleanerTool;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                "Si è verificato un errore imprevisto. L'app non ha cambiato niente sul PC.\n\n" + args.Exception.Message,
                "W11-cleaner-tool",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
