using W11CleanerTool.Core.Safety;

namespace W11CleanerTool.Core.Actions;

/// <summary>
/// Coordina l'annullamento di una singola voce o di un'intera sessione dello storico,
/// delegando al servizio competente (Quarantena, Ottimizzazione, Debloat, Pulizia).
/// </summary>
public sealed class UndoCoordinator
{
    private readonly ActionLog _actionLog;
    private readonly QuarantineService _quarantineService;
    private readonly OptimizationService _optimizationService;
    private readonly DebloatService _debloatService;
    private readonly CleanupService _cleanupService;

    public UndoCoordinator(
        ActionLog? actionLog = null,
        QuarantineService? quarantineService = null,
        OptimizationService? optimizationService = null,
        DebloatService? debloatService = null,
        CleanupService? cleanupService = null)
    {
        _actionLog = actionLog ?? new ActionLog();
        _quarantineService = quarantineService ?? new QuarantineService();
        _optimizationService = optimizationService ?? new OptimizationService(_actionLog);
        _debloatService = debloatService ?? new DebloatService(_actionLog);
        _cleanupService = cleanupService ?? new CleanupService(_actionLog);
    }

    /// <summary>Annulla una singola voce tramite il suo Id.</summary>
    public UndoOutcome UndoEntry(string entryId)
    {
        var entry = _actionLog.FindEntry(entryId);
        if (entry is null)
        {
            return new UndoOutcome(entryId, "", Succeeded: false, "Voce dello storico non trovata.");
        }

        return UndoEntry(entry);
    }

    /// <summary>Annulla una singola voce dello storico.</summary>
    public UndoOutcome UndoEntry(ActionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Undone)
        {
            return new UndoOutcome(entry.Id, entry.Title, Succeeded: true, "Modifica già annullata.");
        }

        UndoOutcome outcome = entry.ActionKind switch
        {
            ActionKind.StartupDisable or ActionKind.StartupEnable or ActionKind.PowerPlanChange =>
                _optimizationService.UndoEntry(entry),

            ActionKind.DebloatRemoveApp =>
                _debloatService.UndoEntry(entry),

            ActionKind.QuarantineDuplicate =>
                RestoreFromQuarantine(entry),

            ActionKind.CleanTempFiles or ActionKind.CleanBrowserCache or ActionKind.CleanWindowsUpdateCache or ActionKind.EmptyRecycleBin =>
                _cleanupService.UndoEntry(entry),

            _ => new UndoOutcome(entry.Id, entry.Title, Succeeded: false, "Tipo di azione non riconosciuto."),
        };

        if (outcome.Succeeded)
        {
            _actionLog.MarkEntryUndone(entry.Id);
        }

        return outcome;
    }

    /// <summary>Annulla tutte le voci non ancora annullate di una sessione (in ordine inverso).</summary>
    public List<UndoOutcome> UndoSession(string sessionId)
    {
        var session = _actionLog.GetSession(sessionId);
        var outcomes = new List<UndoOutcome>();
        if (session is null)
        {
            return outcomes;
        }

        foreach (var entry in session.Entries.AsEnumerable().Reverse())
        {
            if (entry.Undone)
            {
                continue;
            }

            outcomes.Add(UndoEntry(entry));
        }

        return outcomes;
    }

    private UndoOutcome RestoreFromQuarantine(ActionLogEntry entry)
    {
        string key = !string.IsNullOrWhiteSpace(entry.QuarantinePath)
            ? entry.QuarantinePath
            : entry.Target;

        var result = _quarantineService.RestoreItem(key);
        return new UndoOutcome(entry.Id, entry.Title, result.Success, result.Message);
    }
}
