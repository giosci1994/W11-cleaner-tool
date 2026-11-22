using W11CleanerTool.Core.Safety;

namespace W11CleanerTool.Core.Actions;

/// <summary>
/// Registra una sessione nello storico man mano: la sessione nasce sul disco alla prima voce riuscita
/// e ogni voce successiva si aggiunge subito, così se l'app si chiude a metà la traccia resta.
/// Se lo storico non si può scrivere, l'errore risale e chi applica si ferma.
/// </summary>
internal sealed class SessionRecorder
{
    private readonly ActionLog _actionLog;
    private bool _saved;

    public SessionRecorder(ActionLog actionLog, bool restorePointCreated)
    {
        _actionLog = actionLog ?? throw new ArgumentNullException(nameof(actionLog));
        Session = new ActionSession
        {
            TimestampUtc = DateTime.UtcNow,
            RestorePointCreated = restorePointCreated,
        };
    }

    public ActionSession Session { get; }

    /// <summary>Scrive subito la voce nello storico (la modifica è già stata applicata).</summary>
    public void Record(ActionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            if (!_saved)
            {
                var first = new ActionSession
                {
                    SessionId = Session.SessionId,
                    TimestampUtc = Session.TimestampUtc,
                    RestorePointCreated = Session.RestorePointCreated,
                    Entries = [entry],
                };
                _actionLog.AddSession(first);
                _saved = true;
            }
            else
            {
                _actionLog.AppendEntry(Session.SessionId, entry);
            }
        }
        catch (Exception ex) when (ex is ActionLogUnavailableException or IOException or UnauthorizedAccessException)
        {
            throw new ActionLogUnavailableException(
                $"«{entry.Title}» è stata applicata, ma non sono riuscito a scriverla nello storico ({ex.Message}). " +
                "Mi fermo qui: le voci successive non sono state applicate.",
                ex);
        }

        Session.Entries.Add(entry);
    }

    /// <summary>La sessione registrata, oppure null se non è riuscita nessuna voce.</summary>
    public ActionSession? Complete() => Session.Entries.Count > 0 ? Session : null;
}
