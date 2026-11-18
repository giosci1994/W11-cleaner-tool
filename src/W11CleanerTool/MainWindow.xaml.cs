using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using W11CleanerTool.Core;
using W11CleanerTool.Core.Checks;

namespace W11CleanerTool;

public partial class MainWindow : Window
{
    private readonly Settings _settings = Settings.Load();
    private readonly bool _isAdmin = IsAdministrator();
    private List<CheckResult> _lastResults = new();
    private string? _report;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int plus = version.IndexOf('+');
        Title = $"W11-cleaner-tool {(plus > 0 ? version[..plus] : version)}{(_isAdmin ? " · amministratore" : "")}";
        ExpertToggle.IsChecked = _settings.ExpertMode;
        ApplyMode();
    }

    private bool Expert => ExpertToggle.IsChecked == true;

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void ApplyMode()
    {
        ElevateButton.Visibility = Expert && !_isAdmin ? Visibility.Visible : Visibility.Collapsed;
        ReportText.Visibility = Expert && _report is not null ? Visibility.Visible : Visibility.Collapsed;
        ShowResults(_lastResults);
    }

    private void ExpertToggle_Changed(object sender, RoutedEventArgs e)
    {
        _settings.ExpertMode = Expert;
        _settings.Save();
        ApplyMode();
    }

    private void ShowResults(List<CheckResult> results)
    {
        bool expert = Expert;
        ResultsList.ItemsSource = results.Select(r => new ResultItem(r, expert)).ToList();
    }

    private async Task RunAsync(string heading, Func<(List<CheckResult> Results, string? Report)> work)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetButtonsEnabled(false);
        ResultsHeading.Text = heading + " · controllo in corso…";
        StatusText.Text = "Sto leggendo le informazioni del PC. Non cambio niente.";
        try
        {
            var (results, report) = await Task.Run(work);
            _lastResults = results;
            _report = report;
            ShowResults(results);
            int warnings = results.Count(r => r.Status == CheckStatus.Warning);
            int problems = results.Count(r => r.Status == CheckStatus.Problem);
            ResultsHeading.Text = problems + warnings == 0
                ? $"{heading} · tutto a posto"
                : $"{heading} · {problems} problemi, {warnings} cose da guardare";
            StatusText.Text = $"Controllo finito alle {DateTime.Now:HH:mm}. Niente è stato cambiato sul PC.";
            ReportButtons.Visibility = report is not null ? Visibility.Visible : Visibility.Collapsed;
            ReportText.Text = report ?? "";
            ReportText.Visibility = Expert && report is not null ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ResultsHeading.Text = heading + " · non riuscito";
            StatusText.Text = "Errore: " + ex.Message;
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        AllButton.IsEnabled = enabled;
        GamingButton.IsEnabled = enabled;
        HardwareButton.IsEnabled = enabled;
        CrashButton.IsEnabled = enabled;
    }

    private async void AllButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Controllo completo", () =>
        {
            var crash = CrashReport.Build();
            var all = new List<CheckResult>();
            all.AddRange(GamingChecks.Run());
            all.AddRange(HardwareChecks.Run());
            all.AddRange(crash.Results);
            return (all, crash.Text);
        });

    private async void GamingButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Gaming", () => (GamingChecks.Run(), null));

    private async void HardwareButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("RAM e driver", () => (HardwareChecks.Run(), null));

    private async void CrashButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Problemi e crash", () =>
        {
            var crash = CrashReport.Build();
            return (crash.Results, crash.Text);
        });

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_report);
            StatusText.Text = "Rapporto copiato: incollalo nella chat della tua IA. Nomi e percorsi personali sono già stati tolti.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = "Gli appunti sono occupati: riprova tra un attimo.";
        }
    }

    private void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = $"rapporto-crash-{DateTime.Now:yyyyMMdd-HHmm}.md",
            Filter = "Markdown (*.md)|*.md|Testo (*.txt)|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                System.IO.File.WriteAllText(dialog.FileName, _report, new UTF8Encoding(false));
                StatusText.Text = "Rapporto salvato.";
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                StatusText.Text = "Non sono riuscito a salvare il rapporto: " + ex.Message;
            }
        }
    }

    private void ElevateButton_Click(object sender, RoutedEventArgs e)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Win32Exception)
        {
            StatusText.Text = "Riavvio come amministratore annullato.";
        }
    }
}
