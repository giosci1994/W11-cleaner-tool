using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Win32;
using W11CleanerTool.Core;
using W11CleanerTool.Core.Actions;
using W11CleanerTool.Core.Checks;
using W11CleanerTool.Core.Safety;

namespace W11CleanerTool;

public partial class MainWindow : Window
{
    private enum ActiveSection
    {
        None,
        All,
        Gaming,
        Hardware,
        Crash,
        Clean,
        Optimize,
        Debloat,
        Duplicates,
        HistoryQuarantine,
    }

    private sealed record CachedActionState(
        List<PlannedChangeItem> Items,
        string SummaryText,
        ActionExecutionResult? LastResult = null);

    private readonly Settings _settings = Settings.Load();
    private readonly bool _isAdmin = RestorePointService.IsAdministrator();
    private readonly KeepAwakeService _keepAwake = new();
    private readonly ActionLog _actionLog = new();
    private readonly QuarantineService _quarantineService = new();
    private readonly RestorePointService _restorePointService = new();
    private readonly CleanupService _cleanupService;
    private readonly OptimizationService _optimizationService;
    private readonly DebloatService _debloatService;
    private readonly DuplicateFinderService _duplicateFinderService;
    private readonly UndoCoordinator _undoCoordinator;

    private readonly Dictionary<ActiveSection, CachedActionState> _actionCache = new();
    private readonly List<string> _duplicateFolders = new();

    private List<CheckResult> _lastCheckResults = new();
    private string? _report;
    private List<PlannedChangeItem> _currentPlannedItems = new();
    private List<ActionSession> _cachedSessions = new();
    private List<QuarantineItem> _cachedQuarantine = new();
    private List<QuarantineItem> _cachedExpired = new();

    private ActiveSection _activeSection = ActiveSection.None;
    private CancellationTokenSource? _scanCts;
    private bool _busy;

    /// <summary>Vero solo mentre si modifica il sistema (applica, annulla, ripristina, svuota): allora la finestra non si chiude.</summary>
    private bool _modifying;
    private bool _updatingKeepAwakeToggle;
    private readonly bool _initialized;

    public MainWindow()
    {
        _cleanupService = new CleanupService(_actionLog, _restorePointService);
        _optimizationService = new OptimizationService(_actionLog, _restorePointService);
        _debloatService = new DebloatService(_actionLog, _restorePointService);
        _duplicateFinderService = new DuplicateFinderService(_quarantineService, _actionLog, _restorePointService);
        _undoCoordinator = new UndoCoordinator(
            _actionLog,
            _quarantineService,
            _optimizationService,
            _debloatService,
            _cleanupService);

        InitializeComponent();

        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int plus = version.IndexOf('+');
        Title = $"W11-cleaner-tool {(plus > 0 ? version[..plus] : version)}{(_isAdmin ? " \u00B7 amministratore" : "")}";

        string? accountWarning = BuildAccountWarning();
        if (accountWarning is not null)
        {
            // Avviso fisso sotto il titolo, più un messaggio all'apertura.
            Title += " \u00B7 account diverso";
            SubtitleText.Text = accountWarning;
            SubtitleText.Foreground = UiBrushes.Orange;
            SubtitleText.Opacity = 1;
            Loaded += (_, _) => MessageBox.Show(
                this,
                accountWarning,
                "Account diverso \u00B7 W11-cleaner-tool",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        ExpertToggle.IsChecked = _settings.ExpertMode;
        _initialized = true;
        ApplyMode();
        _ = RefreshExpiredHintAsync();
    }

    private bool Expert => ExpertToggle.IsChecked == true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_modifying)
        {
            // Chiudere a metà lascerebbe modifiche applicate senza esito: si aspetta la fine.
            e.Cancel = true;
            MessageBox.Show(
                this,
                "C'è un'operazione in corso: aspetta che finisca prima di chiudere l'app.",
                "W11-cleaner-tool",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Un'analisi in sola lettura si può interrompere: si annulla e si chiude.
        _scanCts?.Cancel();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
        _keepAwake.Dispose();
        base.OnClosed(e);
    }

    private void ApplyMode()
    {
        bool expert = Expert;
        ElevateButton.Visibility = !_isAdmin ? Visibility.Visible : Visibility.Collapsed;
        ReportText.Visibility = expert && _report is not null ? Visibility.Visible : Visibility.Collapsed;

        ShowCheckResults(_lastCheckResults);

        foreach (var item in _currentPlannedItems)
        {
            item.ShowExpertDetails = expert;
        }

        foreach (var cached in _actionCache.Values)
        {
            foreach (var item in cached.Items)
            {
                item.ShowExpertDetails = expert;
            }
        }

        BindHistoryAndQuarantine();
    }

    private void ExpertToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        _settings.ExpertMode = Expert;
        _settings.Save();
        ApplyMode();
    }

    private void KeepAwakeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _updatingKeepAwakeToggle)
        {
            return;
        }

        bool wantActive = KeepAwakeToggle.IsChecked == true;
        bool ok = _keepAwake.SetEnabled(wantActive);
        if (!ok && wantActive)
        {
            _updatingKeepAwakeToggle = true;
            KeepAwakeToggle.IsChecked = false;
            _updatingKeepAwakeToggle = false;
            StatusText.Text = "Non sono riuscito ad attivare «Niente sospensione» su questo sistema.";
            return;
        }

        StatusText.Text = wantActive
            ? "Modalità «Niente sospensione» attiva finché la finestra resta aperta (impostazioni di Windows invariate)."
            : "Sospensione normale di Windows ripristinata.";
    }

    private void SwitchSection(ActiveSection section)
    {
        _activeSection = section;
        HighlightActiveNavButton(section);

        bool isChecks = section is ActiveSection.None or ActiveSection.All or ActiveSection.Gaming
            or ActiveSection.Hardware or ActiveSection.Crash;
        bool isActions = section is ActiveSection.Clean or ActiveSection.Optimize
            or ActiveSection.Debloat or ActiveSection.Duplicates;
        bool isHistory = section == ActiveSection.HistoryQuarantine;

        ChecksPanel.Visibility = isChecks ? Visibility.Visible : Visibility.Collapsed;
        ActionsPanel.Visibility = isActions ? Visibility.Visible : Visibility.Collapsed;
        HistoryTabControl.Visibility = isHistory ? Visibility.Visible : Visibility.Collapsed;

        if (isActions)
        {
            ConfigureActionsSection(section);
        }
        else if (isHistory)
        {
            ResultsHeading.Text = "Storico e Quarantena";
            ResultsSubheading.Text = "Annulla le modifiche reversibili o ripristina i file messi in quarantena.";
            ResultsSubheading.Visibility = Visibility.Visible;
        }
    }

    private void HighlightActiveNavButton(ActiveSection section)
    {
        Button[] allNav =
        [
            AllButton,
            GamingButton,
            HardwareButton,
            CrashButton,
            CleanButton,
            OptimizeButton,
            DebloatButton,
            DuplicatesButton,
            HistoryButton,
        ];

        // Lo stile NavItem (MainWindow.xaml) evidenzia la voce con Tag="active".
        foreach (var btn in allNav)
        {
            btn.Tag = null;
        }

        Button? active = section switch
        {
            ActiveSection.All => AllButton,
            ActiveSection.Gaming => GamingButton,
            ActiveSection.Hardware => HardwareButton,
            ActiveSection.Crash => CrashButton,
            ActiveSection.Clean => CleanButton,
            ActiveSection.Optimize => OptimizeButton,
            ActiveSection.Debloat => DebloatButton,
            ActiveSection.Duplicates => DuplicatesButton,
            ActiveSection.HistoryQuarantine => HistoryButton,
            _ => null,
        };

        if (active is not null)
        {
            active.Tag = "active";
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        AllButton.IsEnabled = enabled;
        GamingButton.IsEnabled = enabled;
        HardwareButton.IsEnabled = enabled;
        CrashButton.IsEnabled = enabled;
        CleanButton.IsEnabled = enabled;
        OptimizeButton.IsEnabled = enabled;
        DebloatButton.IsEnabled = enabled;
        DuplicatesButton.IsEnabled = enabled;
        HistoryButton.IsEnabled = enabled;

        ScanActionButton.IsEnabled = enabled;
        SelectFolderButton.IsEnabled = enabled;
        SelectAllButton.IsEnabled = enabled;
        DeselectAllButton.IsEnabled = enabled;
        RefreshHistoryButton.IsEnabled = enabled;
        RefreshQuarantineButton.IsEnabled = enabled;
        PurgeExpiredButton.IsEnabled = enabled;
        ElevateButton.IsEnabled = enabled;
        UpdatePreviewButtonState(enabled);
    }

    // -------------------------------------------------------------------------
    // Controlli in sola lettura (v0.1)
    // -------------------------------------------------------------------------

    private void ShowCheckResults(List<CheckResult> results)
    {
        bool expert = Expert;
        ResultsList.ItemsSource = results.Select(r => new ResultItem(r, expert)).ToList();
    }

    private async Task RunChecksAsync(ActiveSection section, string heading, Func<(List<CheckResult> Results, string? Report)> work)
    {
        if (_busy)
        {
            return;
        }

        SwitchSection(section);
        _busy = true;
        SetControlsEnabled(false);
        ResultsHeading.Text = heading + " \u00B7 controllo in corso\u2026";
        ResultsSubheading.Visibility = Visibility.Collapsed;
        StatusText.Text = "Sto leggendo le informazioni del PC. Non cambio niente.";
        try
        {
            var (results, report) = await Task.Run(work);
            _lastCheckResults = results;
            _report = report;
            ShowCheckResults(results);

            int warnings = results.Count(r => r.Status == CheckStatus.Warning);
            int problems = results.Count(r => r.Status == CheckStatus.Problem);
            ResultsHeading.Text = problems + warnings == 0
                ? $"{heading} \u00B7 tutto a posto"
                : $"{heading} \u00B7 {problems} problemi, {warnings} cose da guardare";
            StatusText.Text = $"Controllo finito alle {DateTime.Now:HH:mm}. Niente è stato cambiato sul PC.";
            ReportButtons.Visibility = report is not null ? Visibility.Visible : Visibility.Collapsed;
            ReportText.Text = report ?? "";
            ReportText.Visibility = Expert && report is not null ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ResultsHeading.Text = heading + " \u00B7 non riuscito";
            StatusText.Text = "Errore: " + ex.Message;
        }
        finally
        {
            _busy = false;
            SetControlsEnabled(true);
        }
    }

    private async void AllButton_Click(object sender, RoutedEventArgs e) =>
        await RunChecksAsync(ActiveSection.All, "Controllo completo", () =>
        {
            var crash = CrashReport.Build();
            var all = new List<CheckResult>();
            all.AddRange(GamingChecks.Run());
            all.AddRange(HardwareChecks.Run());
            all.AddRange(crash.Results);
            return (all, crash.Text);
        });

    private async void GamingButton_Click(object sender, RoutedEventArgs e) =>
        await RunChecksAsync(ActiveSection.Gaming, "Gaming", () => (GamingChecks.Run(), null));

    private async void HardwareButton_Click(object sender, RoutedEventArgs e) =>
        await RunChecksAsync(ActiveSection.Hardware, "RAM e driver", () => (HardwareChecks.Run(), null));

    private async void CrashButton_Click(object sender, RoutedEventArgs e) =>
        await RunChecksAsync(ActiveSection.Crash, "Problemi e crash", () =>
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
                File.WriteAllText(dialog.FileName, _report, new UTF8Encoding(false));
                StatusText.Text = "Rapporto salvato.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusText.Text = "Non sono riuscito a salvare il rapporto: " + ex.Message;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Azioni (Pulisci, Ottimizza, Debloat, Duplicati)
    // -------------------------------------------------------------------------

    private void CleanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SwitchSection(ActiveSection.Clean);
    }

    private void OptimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SwitchSection(ActiveSection.Optimize);
    }

    private void DebloatButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SwitchSection(ActiveSection.Debloat);
    }

    private void DuplicatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SwitchSection(ActiveSection.Duplicates);
    }

    private void ConfigureActionsSection(ActiveSection section)
    {
        DuplicatesFolderBar.Visibility = section == ActiveSection.Duplicates ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectedFoldersDisplay();

        (ResultsHeading.Text, ResultsSubheading.Text, ScanActionButton.Content, PreviewApplyButton.Content) = section switch
        {
            ActiveSection.Clean => (
                "Pulisci",
                "Analizza file temporanei, cache dei browser, Windows Update e Cestino. Nessun file viene eliminato prima della tua conferma.",
                "Analizza spazio liberabile",
                "Anteprima e applica"),
            ActiveSection.Optimize => (
                "Ottimizza",
                "Controlla i programmi all'avvio e il piano energetico attuale. Ogni modifica è reversibile nello Storico.",
                "Analizza avvio e energia",
                "Anteprima e applica"),
            ActiveSection.Debloat => (
                "Debloat",
                "Elenca solo le app preinstallate superflue e sicure da rimuovere (Store, Xbox, Sicurezza e app essenziali sono protette).",
                "Analizza app rimovibili",
                "Anteprima e applica"),
            ActiveSection.Duplicates => (
                "Duplicati",
                "Trova file identici per contenuto e sposta solo le copie extra in Quarantena (mai cancellazione diretta).",
                "Cerca duplicati",
                "Sposta in quarantena"),
            _ => ("Azioni", "", "Analizza", "Anteprima e applica"),
        };
        ResultsSubheading.Visibility = Visibility.Visible;

        if (_actionCache.TryGetValue(section, out var cached))
        {
            _currentPlannedItems = cached.Items;
            PlannedChangesList.ItemsSource = _currentPlannedItems;
            ActionSummaryText.Text = cached.SummaryText;
            ActionSummaryBanner.Visibility = string.IsNullOrWhiteSpace(cached.SummaryText)
                ? Visibility.Collapsed
                : Visibility.Visible;

            if (cached.LastResult is not null)
            {
                ShowExecutionOutcomes(cached.LastResult);
            }
            else
            {
                OutcomesBanner.Visibility = Visibility.Collapsed;
            }

            ActionsEmptyText.Visibility = _currentPlannedItems.Count == 0 && cached.LastResult is null
                ? Visibility.Visible
                : Visibility.Collapsed;
            ActionsEmptyText.Text = _currentPlannedItems.Count == 0 ? EmptyMessageForSection(section) : "";
        }
        else
        {
            _currentPlannedItems = new List<PlannedChangeItem>();
            PlannedChangesList.ItemsSource = null;
            ActionSummaryBanner.Visibility = Visibility.Collapsed;
            OutcomesBanner.Visibility = Visibility.Collapsed;
            ActionsEmptyText.Text = InitialPromptForSection(section);
            ActionsEmptyText.Visibility = Visibility.Visible;
        }

        UpdateSelectionButtonsVisibility();
        UpdateActionSelectionSummary();
    }

    private static string InitialPromptForSection(ActiveSection section) => section switch
    {
        ActiveSection.Clean => "Premi «Analizza spazio liberabile» per calcolare quanto spazio puoi recuperare.",
        ActiveSection.Optimize => "Premi «Analizza avvio e energia» per vedere i programmi all'avvio e i piani energetici disponibili.",
        ActiveSection.Debloat => "Premi «Analizza app rimovibili» per controllare quali app preinstallate sono presenti sul PC.",
        ActiveSection.Duplicates => "Scegli una o più cartelle e premi «Cerca duplicati». Le copie trovate vengono spostate solo in quarantena.",
        _ => "",
    };

    private static string EmptyMessageForSection(ActiveSection section) => section switch
    {
        ActiveSection.Clean => "Nessun file temporaneo o cache significativa trovata.",
        ActiveSection.Optimize => "Nessuna voce di avvio o piano energetico alternativo rilevato.",
        ActiveSection.Debloat => "Nessuna app preinstallata superflua rilevata tra quelle supportate.",
        ActiveSection.Duplicates => "Nessun file duplicato trovato nelle cartelle selezionate.",
        _ => "Nessun elemento trovato.",
    };

    private void SelectFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        PickDuplicateFolders();
    }

    private bool PickDuplicateFolders()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Scegli una o più cartelle in cui cercare file duplicati",
            Multiselect = true,
            InitialDirectory = _duplicateFolders.FirstOrDefault(Directory.Exists)
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        string[] chosen = dialog.FolderNames.Length > 0
            ? dialog.FolderNames
            : (string.IsNullOrWhiteSpace(dialog.FolderName) ? [] : [dialog.FolderName]);

        var valid = chosen
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (valid.Count == 0)
        {
            return false;
        }

        _duplicateFolders.Clear();
        _duplicateFolders.AddRange(valid);
        UpdateSelectedFoldersDisplay();
        StatusText.Text = _duplicateFolders.Count == 1
            ? $"Cartella selezionata: {_duplicateFolders[0]}"
            : $"Selezionate {_duplicateFolders.Count} cartelle per la ricerca duplicati.";
        return true;
    }

    private void UpdateSelectedFoldersDisplay()
    {
        if (_duplicateFolders.Count == 0)
        {
            SelectedFoldersText.Text = "Nessuna cartella selezionata";
            return;
        }

        SelectedFoldersText.Text = string.Join(" ; ", _duplicateFolders);
    }

    private async void ScanActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        ActiveSection section = _activeSection;
        if (section is not (ActiveSection.Clean or ActiveSection.Optimize or ActiveSection.Debloat or ActiveSection.Duplicates))
        {
            return;
        }

        if (section == ActiveSection.Duplicates && _duplicateFolders.Count == 0)
        {
            if (!PickDuplicateFolders())
            {
                return;
            }
        }

        _busy = true;
        SetControlsEnabled(false);
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        CancellationToken ct = _scanCts.Token;

        CancelScanButton.Visibility = Visibility.Visible;
        CancelScanButton.IsEnabled = true;
        ActionProgressBar.Visibility = Visibility.Visible;
        OutcomesBanner.Visibility = Visibility.Collapsed;
        ActionsEmptyText.Visibility = Visibility.Collapsed;
        StatusText.Text = section == ActiveSection.Duplicates
            ? "Ricerca duplicati in corso (dimensione e confronto SHA256). Non modifico nessun file\u2026"
            : "Analisi in sola lettura in corso\u2026";

        try
        {
            List<string> foldersSnapshot = _duplicateFolders.ToList();
            var (changes, summary) = await Task.Run(() => ScanForSection(section, foldersSnapshot, ct), ct);

            bool expert = Expert;
            _currentPlannedItems = changes.Select(c => new PlannedChangeItem(c, expert)).ToList();
            _actionCache[section] = new CachedActionState(_currentPlannedItems, summary);

            PlannedChangesList.ItemsSource = _currentPlannedItems;
            ActionSummaryText.Text = summary;
            ActionSummaryBanner.Visibility = Visibility.Visible;
            ActionsEmptyText.Text = _currentPlannedItems.Count == 0 ? EmptyMessageForSection(section) : "";
            ActionsEmptyText.Visibility = _currentPlannedItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            StatusText.Text = $"Analisi completata alle {DateTime.Now:HH:mm}. Niente è stato modificato sul PC.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Ricerca annullata. Niente è stato modificato.";
        }
        catch (Exception ex)
        {
            ActionSummaryBanner.Visibility = Visibility.Collapsed;
            ActionsEmptyText.Text = "Analisi non riuscita: " + ex.Message;
            ActionsEmptyText.Visibility = Visibility.Visible;
            StatusText.Text = "Errore durante l'analisi: " + ex.Message;
        }
        finally
        {
            CancelScanButton.Visibility = Visibility.Collapsed;
            ActionProgressBar.Visibility = Visibility.Collapsed;
            _scanCts?.Dispose();
            _scanCts = null;
            _busy = false;
            SetControlsEnabled(true);
            UpdateSelectionButtonsVisibility();
            UpdateActionSelectionSummary();
        }
    }

    private (List<PlannedChange> Changes, string Summary) ScanForSection(
        ActiveSection section,
        List<string> foldersSnapshot,
        CancellationToken ct)
    {
        switch (section)
        {
            case ActiveSection.Clean:
            {
                var changes = _cleanupService.Scan(ct);
                long total = changes.Sum(c => Math.Max(0L, c.EstimatedBytes));
                string summary = changes.Count == 0
                    ? "Nessuno spazio significativo da liberare in questo momento."
                    : $"Spazio totale liberabile stimato: {Heuristics.FormatBytes(total)} ({changes.Count} {(changes.Count == 1 ? "voce" : "voci")}).";
                return (changes, summary);
            }
            case ActiveSection.Optimize:
            {
                var plans = _optimizationService.GetPowerPlans();
                var activePlan = plans.FirstOrDefault(p => p.IsActive);
                var changes = _optimizationService.Scan(ct);
                int startupCount = changes.Count(c => c.ActionKind is ActionKind.StartupDisable or ActionKind.StartupEnable);
                int planCount = changes.Count(c => c.ActionKind == ActionKind.PowerPlanChange);
                string planLabel = activePlan is not null ? activePlan.Name : "non rilevato";
                string summary = $"Piano energetico attivo: {planLabel} \u00B7 {startupCount} programmi all'avvio e {planCount} piani alternativi.";
                return (changes, summary);
            }
            case ActiveSection.Debloat:
            {
                var changes = _debloatService.Scan(ct);
                string summary = changes.Count == 0
                    ? "Nessuna app preinstallata superflua rilevata."
                    : $"Rilevate {changes.Count} {(changes.Count == 1 ? "app preinstallata rimovibile" : "app preinstallate rimovibili")} per l'utente corrente.";
                return (changes, summary);
            }
            case ActiveSection.Duplicates:
            {
                var changes = _duplicateFinderService.Scan(foldersSnapshot, ct);
                long total = changes.Sum(c => Math.Max(0L, c.EstimatedBytes));
                string summary = changes.Count == 0
                    ? "Nessun file duplicato trovato nelle cartelle scelte."
                    : $"Trovati {changes.Count} {(changes.Count == 1 ? "duplicato" : "duplicati")} per un totale di {Heuristics.FormatBytes(total)}. L'originale più vecchio resta dov'è.";
                return (changes, summary);
            }
            default:
                return ([], "");
        }
    }

    private void CancelScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCts is not null && !_scanCts.IsCancellationRequested)
        {
            CancelScanButton.IsEnabled = false;
            _scanCts.Cancel();
            StatusText.Text = "Annullamento della ricerca in corso\u2026";
        }
    }

    private void PlannedChangeCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: PlannedChangeItem clicked }
            && _activeSection == ActiveSection.Optimize
            && clicked.ActionKind == ActionKind.PowerPlanChange
            && clicked.IsSelected)
        {
            foreach (var item in _currentPlannedItems)
            {
                if (!ReferenceEquals(item, clicked) && item.ActionKind == ActionKind.PowerPlanChange && item.IsSelected)
                {
                    item.IsSelected = false;
                }
            }
        }

        UpdateActionSelectionSummary();
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _currentPlannedItems)
        {
            if (_activeSection == ActiveSection.Optimize && item.ActionKind == ActionKind.PowerPlanChange)
            {
                continue;
            }

            item.IsSelected = true;
        }

        UpdateActionSelectionSummary();
    }

    private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _currentPlannedItems)
        {
            item.IsSelected = false;
        }

        UpdateActionSelectionSummary();
    }

    private void UpdateSelectionButtonsVisibility()
    {
        bool hasItems = _currentPlannedItems.Count > 0;
        SelectAllButton.Visibility = hasItems && _activeSection != ActiveSection.Optimize
            ? Visibility.Visible
            : Visibility.Collapsed;
        DeselectAllButton.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateActionSelectionSummary()
    {
        if (_currentPlannedItems.Count == 0)
        {
            ActionsBottomBar.Visibility = Visibility.Collapsed;
            SelectedSummaryText.Text = "";
            UpdatePreviewButtonState(!_busy);
            return;
        }

        ActionsBottomBar.Visibility = Visibility.Visible;
        var selected = _currentPlannedItems.Where(i => i.IsSelected).ToList();
        long bytes = selected.Sum(i => Math.Max(0L, i.EstimatedBytes));

        if (selected.Count == 0)
        {
            SelectedSummaryText.Text = "Nessuna voce selezionata.";
        }
        else
        {
            string countText = selected.Count == 1 ? "1 voce selezionata" : $"{selected.Count} voci selezionate";
            SelectedSummaryText.Text = bytes > 0
                ? $"{countText} \u00B7 {Heuristics.FormatBytes(bytes)}"
                : countText;
        }

        UpdatePreviewButtonState(!_busy);
    }

    private void UpdatePreviewButtonState(bool controlsEnabled)
    {
        bool anySelected = _currentPlannedItems.Any(i => i.IsSelected);
        PreviewApplyButton.IsEnabled = controlsEnabled && anySelected;
    }

    private async void PreviewApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        ActiveSection section = _activeSection;
        var selected = _currentPlannedItems
            .Where(i => i.IsSelected)
            .Select(i => i.ToPlannedChange())
            .ToList();

        if (selected.Count == 0)
        {
            return;
        }

        string actionTitle = section switch
        {
            ActiveSection.Clean => "Pulizia",
            ActiveSection.Optimize => "Ottimizzazione",
            ActiveSection.Debloat => "Rimozione app preinstallate",
            ActiveSection.Duplicates => "Spostamento duplicati in quarantena",
            _ => "Modifiche",
        };
        bool defaultRestorePoint = section != ActiveSection.Duplicates;
        string confirmLabel = section == ActiveSection.Duplicates ? "Sposta in quarantena" : "Conferma e applica";

        var preview = new PreviewDialog(actionTitle, selected, defaultRestorePoint, Expert, confirmLabel)
        {
            Owner = this,
        };
        if (preview.ShowDialog() != true)
        {
            return;
        }

        bool createRestorePoint = preview.CreateRestorePoint;

        _busy = true;
        _modifying = true;
        SetControlsEnabled(false);
        ActionProgressBar.Visibility = Visibility.Visible;
        StatusText.Text = createRestorePoint
            ? "Creazione del punto di ripristino e applicazione delle modifiche in corso\u2026"
            : "Applicazione delle modifiche in corso\u2026";

        try
        {
            Func<bool, ActionExecutionResult> apply = withRestorePoint => section switch
            {
                ActiveSection.Clean => _cleanupService.Apply(selected, withRestorePoint),
                ActiveSection.Optimize => _optimizationService.Apply(selected, withRestorePoint),
                ActiveSection.Debloat => _debloatService.Apply(selected, withRestorePoint),
                ActiveSection.Duplicates => _duplicateFinderService.Apply(selected, withRestorePoint),
                _ => new ActionExecutionResult(null, null, []),
            };

            ActionExecutionResult result = await Task.Run(() => apply(createRestorePoint));

            if (result.StoppedForMissingRestorePoint)
            {
                // Il punto di ripristino era richiesto e non c'è: non è stato applicato niente, decide l'utente.
                RestorePointResult? missing = result.RestorePoint;
                string reason = missing?.Message ?? "motivo sconosciuto.";
                MessageBoxResult answer = MessageBox.Show(
                    this,
                    $"Il punto di ripristino non è stato creato:\n{reason}\n\nNon ho ancora applicato niente.\n\nContinuare senza punto di ripristino?",
                    "Punto di ripristino \u00B7 W11-cleaner-tool",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);

                if (answer != MessageBoxResult.Yes)
                {
                    StatusText.Text = "Nessuna modifica applicata: il punto di ripristino non è stato creato.";
                    return;
                }

                StatusText.Text = "Applicazione delle modifiche senza punto di ripristino in corso\u2026";
                result = await Task.Run(() => apply(false));
                result = result with { RestorePoint = missing };
            }

            _currentPlannedItems = new List<PlannedChangeItem>();
            PlannedChangesList.ItemsSource = null;
            ActionsEmptyText.Visibility = Visibility.Collapsed;

            string postSummary = BuildPostApplySummary(section, result);
            ActionSummaryText.Text = postSummary;
            ActionSummaryBanner.Visibility = Visibility.Visible;

            _actionCache[section] = new CachedActionState(_currentPlannedItems, postSummary, result);
            ShowExecutionOutcomes(result);

            try
            {
                await LoadHistoryAndQuarantineAsync();
            }
            catch (Exception ex) when (ex is ActionLogUnavailableException or IOException or UnauthorizedAccessException)
            {
                // Le modifiche sono applicate; si segnala solo che l'elenco dello storico non si è aggiornato.
                ActionSummaryText.Text = postSummary + " Lo storico non si è potuto rileggere: " + ex.Message;
            }

            StatusText.Text = result.FailedCount == 0
                ? $"Operazione completata ({result.SucceededCount} applicate). Storico aggiornato."
                : $"Operazione completata: {result.SucceededCount} riuscite, {result.FailedCount} non riuscite.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Errore durante l'applicazione: " + ex.Message;
            MessageBox.Show(
                this,
                "Si è verificato un errore durante l'applicazione:\n\n" + ex.Message,
                "W11-cleaner-tool",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            ActionProgressBar.Visibility = Visibility.Collapsed;
            _busy = false;
            _modifying = false;
            SetControlsEnabled(true);
            UpdateSelectionButtonsVisibility();
            UpdateActionSelectionSummary();
        }
    }

    private static string BuildPostApplySummary(ActiveSection section, ActionExecutionResult result)
    {
        if (section == ActiveSection.Duplicates)
        {
            // Spostare in quarantena non libera spazio: si libera solo quando la quarantena si svuota.
            return $"Spostati in quarantena {result.SucceededCount} file ({Heuristics.FormatBytes(result.TotalBytesFreed)}). " +
                   "Lo spazio si libera quando svuoti la quarantena (proposto dopo 30 giorni); i file presi da un altro disco " +
                   "intanto occupano spazio sul disco della quarantena. Puoi ripristinarli da «Storico e Quarantena».";
        }

        if (section == ActiveSection.Clean)
        {
            return $"Pulizia completata: {result.SucceededCount} voci applicate, spazio liberato circa {Heuristics.FormatBytes(result.TotalBytesFreed)}.";
        }

        return $"Operazione completata: {result.SucceededCount} modifiche applicate" +
               (result.FailedCount > 0 ? $", {result.FailedCount} non riuscite." : ". Puoi annullarle dalla scheda «Storico e Quarantena».");
    }

    private void ShowExecutionOutcomes(ActionExecutionResult result)
    {
        OutcomesBanner.Visibility = Visibility.Visible;
        OutcomesBanner.BorderBrush = result.FailedCount == 0
            ? UiBrushes.Green
            : (result.SucceededCount > 0 ? UiBrushes.Orange : UiBrushes.Red);

        OutcomesHeadingText.Text = result.FailedCount == 0
            ? $"Esito: {result.SucceededCount} {(result.SucceededCount == 1 ? "modifica applicata" : "modifiche applicate")}"
            : $"Esito: {result.SucceededCount} riuscite, {result.FailedCount} non riuscite";

        if (result.RestorePoint is not null)
        {
            RestorePointOutcomeText.Text = "Punto di ripristino: " + result.RestorePoint.Message;
            RestorePointOutcomeText.Visibility = Visibility.Visible;
        }
        else
        {
            RestorePointOutcomeText.Visibility = Visibility.Collapsed;
        }

        OutcomesList.ItemsSource = result.Outcomes.Select(o => new OutcomeDisplayItem(o)).ToList();
    }

    // -------------------------------------------------------------------------
    // Storico e Quarantena
    // -------------------------------------------------------------------------

    private async void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SwitchSection(ActiveSection.HistoryQuarantine);
        await RefreshHistoryAndQuarantineSafeAsync();
    }

    private async void HistoryTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, HistoryTabControl) || !_initialized || _busy)
        {
            return;
        }

        if (_activeSection == ActiveSection.HistoryQuarantine)
        {
            await RefreshHistoryAndQuarantineSafeAsync();
        }
    }

    private async void RefreshHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        await RefreshHistoryAndQuarantineSafeAsync();
    }

    private async void RefreshQuarantine_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        await RefreshHistoryAndQuarantineSafeAsync();
    }

    private async Task RefreshExpiredHintAsync()
    {
        try
        {
            var expired = await Task.Run(() => _quarantineService.GetExpiredItems(30));
            _cachedExpired = expired;
            UpdateExpiredHintBanner();
        }
        catch (Exception)
        {
            // Se la lettura iniziale fallisce non blocchiamo l'avvio dell'app.
        }
    }

    private async Task RefreshHistoryAndQuarantineSafeAsync()
    {
        _busy = true;
        SetControlsEnabled(false);
        try
        {
            await LoadHistoryAndQuarantineAsync();
            StatusText.Text = $"Storico e quarantena aggiornati alle {DateTime.Now:HH:mm}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Errore nel caricamento di storico/quarantena: " + ex.Message;
        }
        finally
        {
            _busy = false;
            SetControlsEnabled(true);
        }
    }

    private async Task LoadHistoryAndQuarantineAsync()
    {
        var (sessions, items, expired) = await Task.Run(() =>
        {
            var s = _actionLog.ReadSessions();
            var q = _quarantineService.ListItems();
            var exp = _quarantineService.GetExpiredItems(30);
            return (s, q, exp);
        });

        _cachedSessions = sessions;
        _cachedQuarantine = items;
        _cachedExpired = expired;

        BindHistoryAndQuarantine();
    }

    private void BindHistoryAndQuarantine()
    {
        bool expert = Expert;
        DateTime nowUtc = DateTime.UtcNow;

        var sessionModels = _cachedSessions.Select(s => new HistorySessionItem(s, expert)).ToList();
        HistorySessionsList.ItemsSource = sessionModels;
        HistoryEmptyText.Visibility = sessionModels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var quarantineModels = _cachedQuarantine.Select(i => new QuarantineDisplayItem(i, expert, nowUtc)).ToList();
        QuarantineItemsList.ItemsSource = quarantineModels;
        QuarantineEmptyText.Visibility = quarantineModels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateExpiredHintBanner();
    }

    private void UpdateExpiredHintBanner()
    {
        if (_cachedExpired.Count > 0)
        {
            long expiredBytes = _cachedExpired.Sum(i => Math.Max(0L, i.SizeBytes));
            string label = _cachedExpired.Count == 1
                ? "1 elemento in quarantena da oltre 30 giorni"
                : $"{_cachedExpired.Count} elementi in quarantena da oltre 30 giorni";

            HistoryExpiredHint.Text = label;
            HistoryExpiredHint.Visibility = Visibility.Visible;

            ExpiredQuarantineText.Text =
                $"Ci sono {_cachedExpired.Count} file in quarantena da più di 30 giorni (totale {Heuristics.FormatBytes(expiredBytes)}). " +
                "Se non ti servono più, puoi svuotarli definitivamente.";
            ExpiredQuarantineBanner.Visibility = Visibility.Visible;
        }
        else
        {
            HistoryExpiredHint.Visibility = Visibility.Collapsed;
            ExpiredQuarantineBanner.Visibility = Visibility.Collapsed;
        }
    }

    private async void UndoEntry_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not FrameworkElement { Tag: string entryId } || string.IsNullOrWhiteSpace(entryId))
        {
            return;
        }

        _busy = true;
        _modifying = true;
        SetControlsEnabled(false);
        StatusText.Text = "Annullamento della modifica in corso\u2026";

        try
        {
            UndoOutcome outcome = await Task.Run(() => _undoCoordinator.UndoEntry(entryId));
            ShowUndoFeedback([outcome]);
            await LoadHistoryAndQuarantineAsync();
            StatusText.Text = outcome.Succeeded
                ? $"Annullato: {outcome.Title}"
                : outcome.Message;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Errore durante l'annullamento: " + ex.Message;
        }
        finally
        {
            _busy = false;
            _modifying = false;
            SetControlsEnabled(true);
        }
    }

    private async void UndoSession_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not FrameworkElement { Tag: string sessionId } || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _busy = true;
        _modifying = true;
        SetControlsEnabled(false);
        StatusText.Text = "Annullamento dell'intera sessione in corso\u2026";

        try
        {
            List<UndoOutcome> outcomes = await Task.Run(() => _undoCoordinator.UndoSession(sessionId));
            ShowUndoFeedback(outcomes);
            await LoadHistoryAndQuarantineAsync();

            int ok = outcomes.Count(o => o.Succeeded);
            int ko = outcomes.Count - ok;
            StatusText.Text = outcomes.Count == 0
                ? "Nessuna voce da annullare in questa sessione."
                : $"Sessione elaborata: {ok} annullate, {ko} non annullate.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Errore durante l'annullamento della sessione: " + ex.Message;
        }
        finally
        {
            _busy = false;
            _modifying = false;
            SetControlsEnabled(true);
        }
    }

    private void ShowUndoFeedback(IReadOnlyList<UndoOutcome> outcomes)
    {
        if (outcomes.Count == 0)
        {
            UndoFeedbackBanner.Visibility = Visibility.Collapsed;
            return;
        }

        bool allOk = outcomes.All(o => o.Succeeded);
        UndoFeedbackBanner.BorderBrush = allOk ? UiBrushes.Green : UiBrushes.Blue;
        UndoOutcomesList.ItemsSource = outcomes.Select(o => new UndoOutcomeDisplayItem(o)).ToList();
        UndoFeedbackBanner.Visibility = Visibility.Visible;
    }

    private void DismissUndoFeedback_Click(object sender, RoutedEventArgs e)
    {
        UndoFeedbackBanner.Visibility = Visibility.Collapsed;
    }

    private void OpenStoreUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink { Tag: string url }
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            StatusText.Text = "Impossibile aprire il browser: " + ex.Message;
        }
    }

    private async void RestoreQuarantineItem_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not FrameworkElement { Tag: string itemId } || string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        string? quarantinedPath = _cachedQuarantine
            .FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.Ordinal))
            ?.QuarantinedPath;

        _busy = true;
        _modifying = true;
        SetControlsEnabled(false);
        StatusText.Text = "Ripristino del file dalla quarantena in corso\u2026";

        try
        {
            QuarantineRestoreResult result = await Task.Run(() =>
            {
                var restore = _quarantineService.RestoreItem(itemId);
                if (restore.Success && !string.IsNullOrWhiteSpace(quarantinedPath))
                {
                    foreach (var session in _actionLog.ReadSessions())
                    {
                        foreach (var entry in session.Entries)
                        {
                            if (!entry.Undone &&
                                string.Equals(entry.QuarantinePath, quarantinedPath, StringComparison.OrdinalIgnoreCase))
                            {
                                _actionLog.MarkEntryUndone(entry.Id);
                            }
                        }
                    }
                }

                return restore;
            });

            await LoadHistoryAndQuarantineAsync();
            StatusText.Text = result.Message;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Errore nel ripristino: " + ex.Message;
        }
        finally
        {
            _busy = false;
            _modifying = false;
            SetControlsEnabled(true);
        }
    }

    private async void PurgeExpired_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _cachedExpired.Count == 0)
        {
            return;
        }

        long totalBytes = _cachedExpired.Sum(i => Math.Max(0L, i.SizeBytes));
        var confirm = MessageBox.Show(
            this,
            $"Vuoi eliminare definitivamente {_cachedExpired.Count} file in quarantena da oltre 30 giorni ({Heuristics.FormatBytes(totalBytes)})?\n\nQuesta operazione non può essere annullata.",
            "Svuota quarantena · W11-cleaner-tool",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var ids = _cachedExpired.Select(i => i.Id).ToList();
        var expiredPaths = _cachedExpired.Select(i => i.QuarantinedPath).ToList();
        _busy = true;
        _modifying = true;
        SetControlsEnabled(false);
        StatusText.Text = "Eliminazione degli elementi scaduti in corso\u2026";

        try
        {
            int purged = await Task.Run(() =>
            {
                int count = _quarantineService.PurgeItems(ids);

                // Le voci di storico dei file eliminati davvero non sono più annullabili.
                var gone = expiredPaths.Where(p => !string.IsNullOrWhiteSpace(p) && !File.Exists(p)).ToList();
                _actionLog.MarkQuarantinePurged(gone);
                return count;
            });
            await LoadHistoryAndQuarantineAsync();
            StatusText.Text = purged == 1
                ? "1 file oltre 30 giorni eliminato definitivamente dalla quarantena."
                : $"{purged} file oltre 30 giorni eliminati definitivamente dalla quarantena.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Errore durante lo svuotamento: " + ex.Message;
        }
        finally
        {
            _busy = false;
            _modifying = false;
            SetControlsEnabled(true);
        }
    }

    // -------------------------------------------------------------------------
    // Elevazione UAC
    // -------------------------------------------------------------------------

    private void ElevateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            StatusText.Text = "Aspetta che finisca l'operazione in corso prima di riavviare come amministratore.";
            return;
        }

        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }

        try
        {
            // L'istanza elevata riceve il SID di chi l'ha chiesta: se gira con un altro account lo dice subito.
            var startInfo = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            string? sid = GetCurrentUserSid();
            if (!string.IsNullOrWhiteSpace(sid))
            {
                startInfo.Arguments = $"{App.OriginalUserArgument} {sid}";
            }

            Process.Start(startInfo);
            Application.Current.Shutdown();
        }
        catch (Win32Exception)
        {
            StatusText.Text = "Riavvio come amministratore annullato.";
        }
    }

    private static string? GetCurrentUserSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Se l'istanza elevata gira con un account diverso da quello che ha chiesto il riavvio,
    /// restituisce l'avviso da mostrare (storico, quarantena, TEMP e app sono quelli dell'altro account).
    /// </summary>
    private static string? BuildAccountWarning()
    {
        string? originalSid = App.OriginalUserSid;
        if (string.IsNullOrWhiteSpace(originalSid))
        {
            return null;
        }

        string? currentSid = GetCurrentUserSid();
        if (currentSid is not null && string.Equals(currentSid, originalSid, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string originalName = TranslateSid(originalSid);
        string currentName = Environment.UserName;
        return $"Attenzione: ora l'app gira come amministratore con l'account «{currentName}», non con quello di prima ({originalName}). " +
               $"Storico, quarantena, cartella TEMP, cache dei browser, programmi all'avvio e app preinstallate sono quelli di «{currentName}»: " +
               "le modifiche fatte prima non si vedono qui e quelle di adesso non toccano l'altro account.";
    }

    private static string TranslateSid(string sid)
    {
        try
        {
            return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
        }
        catch (SystemException)
        {
            return sid;
        }
    }
}
