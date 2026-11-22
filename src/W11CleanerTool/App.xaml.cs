using System.Windows;

namespace W11CleanerTool;

public partial class App : Application
{
    /// <summary>Argomento con cui l'istanza normale passa il proprio SID a quella avviata come amministratore.</summary>
    internal const string OriginalUserArgument = "--original-user";

    /// <summary>SID dell'utente che ha chiesto il riavvio come amministratore (null se l'app è partita normalmente).</summary>
    internal static string? OriginalUserSid { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        OriginalUserSid = ParseOriginalUserSid(e.Args);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                "Si è verificato un errore imprevisto.\n\n" + args.Exception.Message +
                "\n\nSe era in corso un'operazione, una parte delle modifiche potrebbe essere già stata applicata: " +
                "controlla «Storico e Quarantena» per vedere che cosa è stato registrato.",
                "W11-cleaner-tool",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }

    private static string? ParseOriginalUserSid(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], OriginalUserArgument, StringComparison.OrdinalIgnoreCase))
            {
                string candidate = args[i + 1].Trim();
                // Un SID ha la forma S-1-5-21-…: niente altro viene accettato.
                return System.Text.RegularExpressions.Regex.IsMatch(candidate, @"^S-1-[0-9]+(-[0-9]+)+$")
                    ? candidate
                    : null;
            }
        }

        return null;
    }
}
