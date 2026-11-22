using System.Security;
using System.Windows;
using Microsoft.Win32;
using W11CleanerTool.Core;
using W11CleanerTool.Core.Safety;

namespace W11CleanerTool;

public partial class PreviewDialog : Window
{
    public PreviewDialog(
        string actionTitle,
        IReadOnlyList<PlannedChange> selectedChanges,
        bool defaultCreateRestorePoint,
        bool expert,
        string confirmLabel = "Conferma e applica")
    {
        InitializeComponent();

        ActionTitleText.Text = $"Anteprima · {actionTitle}";
        ConfirmButton.Content = confirmLabel;

        long totalBytes = selectedChanges.Sum(c => Math.Max(0L, c.EstimatedBytes));
        int count = selectedChanges.Count;
        string countLabel = count == 1 ? "1 modifica selezionata" : $"{count} modifiche selezionate";
        SummaryText.Text = totalBytes > 0
            ? $"{countLabel} · spazio stimato: {Heuristics.FormatBytes(totalBytes)}. Controlla cosa cambia prima di confermare."
            : $"{countLabel}. Controlla cosa cambia prima di confermare.";

        PreviewItemsList.ItemsSource = selectedChanges
            .Select(c => new PlannedChangeItem(c, expert))
            .ToList();

        bool isAdmin = RestorePointService.IsAdministrator();
        bool anyRequiresAdmin = selectedChanges.Any(c => c.RequiresAdmin);
        if (anyRequiresAdmin && !isAdmin)
        {
            AdminWarningBanner.Visibility = Visibility.Visible;
        }

        ConfigureRestorePointSection(isAdmin, defaultCreateRestorePoint);
    }

    public bool CreateRestorePoint => RestorePointCheckBox.IsChecked == true;

    private void ConfigureRestorePointSection(bool isAdmin, bool defaultCreateRestorePoint)
    {
        if (!isAdmin)
        {
            RestorePointCheckBox.IsChecked = false;
            RestorePointCheckBox.IsEnabled = false;
            RestorePointWarningBanner.Visibility = Visibility.Visible;
            RestorePointWarningText.Text =
                "L'app non è avviata come amministratore: non può creare un punto di ripristino. " +
                "Puoi continuare comunque oppure annullare e riavviare come amministratore.";
            return;
        }

        if (IsSystemRestorePolicyDisabled())
        {
            RestorePointCheckBox.IsChecked = false;
            RestorePointCheckBox.IsEnabled = false;
            RestorePointWarningBanner.Visibility = Visibility.Visible;
            RestorePointWarningText.Text =
                "La Protezione sistema è disattivata dalle impostazioni di sistema. " +
                "Il programma non la attiva da solo: puoi attivarla manualmente da «Crea un punto di ripristino» in Windows.";
            return;
        }

        RestorePointCheckBox.IsEnabled = true;
        RestorePointCheckBox.IsChecked = defaultCreateRestorePoint;

        if (IsSystemRestoreLikelyOff())
        {
            RestorePointWarningBanner.Visibility = Visibility.Visible;
            RestorePointWarningText.Text =
                "La Protezione sistema potrebbe essere spenta su questo PC. " +
                "Il programma non la attiva da solo: se il punto di ripristino non si crea, prima di applicare ti chiedo se continuare senza.";
        }
        else if (!defaultCreateRestorePoint)
        {
            RestorePointWarningBanner.Visibility = Visibility.Visible;
            RestorePointWarningText.Text =
                "I file selezionati vengono spostati in quarantena (non cancellati): il punto di ripristino è facoltativo.";
        }
    }

    private static bool IsSystemRestorePolicyDisabled()
        => ReadHklmInt(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore", "DisableSR") == 1;

    private static bool IsSystemRestoreLikelyOff()
        => ReadHklmInt(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "RPSessionInterval") == 0;

    private static int? ReadHklmInt(string subKeyPath, string valueName)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(subKeyPath, writable: false);
            return key?.GetValue(valueName) is int value ? value : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
