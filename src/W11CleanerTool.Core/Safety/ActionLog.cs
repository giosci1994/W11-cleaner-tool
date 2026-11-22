using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace W11CleanerTool.Core.Safety;

/// <summary>Singola modifica registrata nello storico.</summary>
public sealed class ActionLogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ActionKind ActionKind { get; set; }
    public string Title { get; set; } = "";
    public string Target { get; set; } = "";
    public string OldValue { get; set; } = "";
    public string NewValue { get; set; } = "";
    public string? QuarantinePath { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public bool Undone { get; set; }

    /// <summary>True se il file in quarantena di questa voce è stato eliminato definitivamente: non si può più annullare.</summary>
    public bool QuarantinePurged { get; set; }
}

/// <summary>Gruppo di modifiche eseguite insieme in una sessione.</summary>
public sealed class ActionSession
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public bool RestorePointCreated { get; set; }
    public List<ActionLogEntry> Entries { get; set; } = new();
}

/// <summary>
/// Lo storico esiste ma non si riesce a leggere o scrivere (file bloccato, permessi):
/// per sicurezza non si sovrascrive e l'operazione non parte.
/// </summary>
public sealed class ActionLogUnavailableException : InvalidOperationException
{
    public ActionLogUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Registro persistente delle modifiche in JSON (%LOCALAPPDATA%\W11-cleaner-tool\history.json).
/// </summary>
public sealed class ActionLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _sync = new();

    public ActionLog(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath) ? DefaultFilePath : Path.GetFullPath(filePath);
    }

    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "W11-cleaner-tool",
        "history.json");

    public string FilePath { get; }

    /// <summary>Legge tutte le sessioni registrate, dalla più recente alla più vecchia.</summary>
    public List<ActionSession> ReadSessions()
    {
        lock (_sync)
        {
            return LoadUnsafe()
                .OrderByDescending(s => s.TimestampUtc)
                .ToList();
        }
    }

    public ActionSession? GetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        lock (_sync)
        {
            return LoadUnsafe().FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
        }
    }

    public ActionLogEntry? FindEntry(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId))
        {
            return null;
        }

        lock (_sync)
        {
            foreach (var session in LoadUnsafe())
            {
                var entry = session.Entries.FirstOrDefault(e => string.Equals(e.Id, entryId, StringComparison.Ordinal));
                if (entry is not null)
                {
                    return entry;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Controlla che lo storico si possa leggere. Se il file c'è ma è bloccato o senza permessi,
    /// lancia <see cref="ActionLogUnavailableException"/>: chi applica modifiche deve fermarsi prima di cominciare.
    /// </summary>
    public void EnsureAccessible()
    {
        lock (_sync)
        {
            _ = LoadUnsafe();
        }
    }

    /// <summary>Aggiunge subito una voce a una sessione già registrata.</summary>
    public void AppendEntry(string sessionId, ActionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_sync)
        {
            var sessions = LoadUnsafe();
            var session = sessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
            if (session is null)
            {
                throw new ActionLogUnavailableException($"La sessione {sessionId} non è più nello storico.");
            }

            session.Entries.Add(entry);
            SaveUnsafe(sessions);
        }
    }

    /// <summary>
    /// Segna come non più annullabili le voci di quarantena il cui file è stato eliminato definitivamente.
    /// </summary>
    public int MarkQuarantinePurged(IEnumerable<string> quarantinedPaths)
    {
        ArgumentNullException.ThrowIfNull(quarantinedPaths);
        var paths = new HashSet<string>(
            quarantinedPaths.Where(p => !string.IsNullOrWhiteSpace(p)),
            StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
        {
            return 0;
        }

        lock (_sync)
        {
            var sessions = LoadUnsafe();
            int updated = 0;
            foreach (var entry in sessions.SelectMany(s => s.Entries))
            {
                if (!entry.QuarantinePurged &&
                    !entry.Undone &&
                    entry.QuarantinePath is not null &&
                    paths.Contains(entry.QuarantinePath))
                {
                    entry.QuarantinePurged = true;
                    updated++;
                }
            }

            if (updated > 0)
            {
                SaveUnsafe(sessions);
            }

            return updated;
        }
    }

    /// <summary>Aggiunge una sessione allo storico e la salva su disco.</summary>
    public void AddSession(ActionSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_sync)
        {
            var sessions = LoadUnsafe();
            sessions.Add(session);
            SaveUnsafe(sessions);
        }
    }

    /// <summary>Segna una singola voce come annullata.</summary>
    public bool MarkEntryUndone(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId))
        {
            return false;
        }

        lock (_sync)
        {
            var sessions = LoadUnsafe();
            foreach (var session in sessions)
            {
                var entry = session.Entries.FirstOrDefault(e => string.Equals(e.Id, entryId, StringComparison.Ordinal));
                if (entry is not null)
                {
                    if (entry.Undone)
                    {
                        return true;
                    }

                    entry.Undone = true;
                    SaveUnsafe(sessions);
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Segna tutte le voci di una sessione come annullate.</summary>
    public int MarkSessionUndone(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return 0;
        }

        lock (_sync)
        {
            var sessions = LoadUnsafe();
            var session = sessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
            if (session is null)
            {
                return 0;
            }

            int updated = 0;
            foreach (var entry in session.Entries)
            {
                if (!entry.Undone)
                {
                    entry.Undone = true;
                    updated++;
                }
            }

            if (updated > 0)
            {
                SaveUnsafe(sessions);
            }

            return updated;
        }
    }

    private List<ActionSession> LoadUnsafe()
    {
        if (!File.Exists(FilePath))
        {
            return new List<ActionSession>();
        }

        try
        {
            string json = File.ReadAllText(FilePath, Encoding.UTF8);
            _currentFileCorrupt = false;
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<ActionSession>();
            }

            return JsonSerializer.Deserialize<List<ActionSession>>(json, JsonOptions) ?? new List<ActionSession>();
        }
        catch (JsonException)
        {
            // File corrotto: lo conserviamo a parte e, al salvataggio, non lo copiamo sopra
            // l'ultima copia buona (history.json.bak), da cui proviamo invece a recuperare.
            _currentFileCorrupt = true;
            try
            {
                string backup = $"{FilePath}.corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Copy(FilePath, backup, overwrite: true);
            }
            catch (IOException)
            {
                // Ignora errore di backup.
            }

            return TryLoadBackup();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Mai restituire un elenco vuoto qui: il salvataggio successivo cancellerebbe lo storico vero.
            throw new ActionLogUnavailableException(
                $"Non riesco a leggere lo storico ({FilePath}): {ex.Message} " +
                "Per sicurezza non lo sovrascrivo e non applico modifiche. Chiudi gli altri programmi che lo usano e riprova.",
                ex);
        }
    }

    private void SaveUnsafe(List<ActionSession> sessions)
    {
        string? dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = FilePath + ".tmp";
        string json = JsonSerializer.Serialize(sessions, JsonOptions);
        try
        {
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // Prima di riscrivere un file esistente se ne tiene una copia (history.json.bak).
            // Un file corrotto non sostituisce la copia buona.
            if (File.Exists(FilePath) && !_currentFileCorrupt)
            {
                File.Copy(FilePath, BackupFilePath, overwrite: true);
            }

            File.Move(tempPath, FilePath, overwrite: true);
            _currentFileCorrupt = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ActionLogUnavailableException(
                $"Non riesco a scrivere lo storico ({FilePath}): {ex.Message}",
                ex);
        }
    }

    /// <summary>Copia dello storico prima dell'ultimo salvataggio.</summary>
    public string BackupFilePath => FilePath + ".bak";

    /// <summary>Vero se l'ultima lettura ha trovato history.json corrotto (protetto da _sync).</summary>
    private bool _currentFileCorrupt;

    /// <summary>Recupera lo storico dalla copia history.json.bak; se manca o è rovinata, elenco vuoto.</summary>
    private List<ActionSession> TryLoadBackup()
    {
        try
        {
            if (!File.Exists(BackupFilePath))
            {
                return new List<ActionSession>();
            }

            string json = File.ReadAllText(BackupFilePath, Encoding.UTF8);
            return string.IsNullOrWhiteSpace(json)
                ? new List<ActionSession>()
                : JsonSerializer.Deserialize<List<ActionSession>>(json, JsonOptions) ?? new List<ActionSession>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new List<ActionSession>();
        }
    }
}
