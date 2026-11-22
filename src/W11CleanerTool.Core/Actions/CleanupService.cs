using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using W11CleanerTool.Core.Safety;

namespace W11CleanerTool.Core.Actions;

/// <summary>Bersaglio di pulizia configurabile (utile anche per i test unitari).</summary>
public sealed record CleanupTarget(
    string Id,
    string Title,
    string Explanation,
    ActionKind ActionKind,
    IReadOnlyList<string> Directories,
    bool RequiresAdmin = false,
    bool IsRecycleBin = false,
    TimeSpan MinFileAge = default);

/// <summary>
/// Cartella che una pulizia non deve mai svuotare. Con <see cref="ProtectInside"/> sono vietate anche le sue sottocartelle.
/// </summary>
internal sealed record ProtectedFolder(string Path, string Label, bool ProtectInside);

/// <summary>
/// Stima e pulizia di temporanei, cache Windows Update, cache browser comuni (solo Cache / Code Cache) e Cestino.
/// I file in uso vengono saltati in modo sicuro senza eccezioni fatali.
/// </summary>
public sealed class CleanupService
{
    private const string CategoryName = "Pulizia";
    private const string DirectoriesJsonKey = "DirectoriesJson";
    private const string IsRecycleBinKey = "IsRecycleBin";
    private const string MinFileAgeHoursKey = "MinFileAgeHours";
    private const string BlockedReasonKey = "BlockedReason";
    private static readonly TimeSpan TempMinFileAge = TimeSpan.FromHours(24);
    private const int RecycleBinEmptyHResult = unchecked((int)0x8000FFFF);

    private readonly ActionLog _actionLog;
    private readonly RestorePointService _restorePointService;
    private readonly Func<IReadOnlyList<CleanupTarget>> _targetsFactory;
    private readonly Func<(long Bytes, long Items)> _queryRecycleBin;
    private readonly Func<int> _emptyRecycleBin;
    private readonly IReadOnlyList<ProtectedFolder> _protectedFolders;
    private readonly Func<string?> _windowsUpdateBusyReason;
    private readonly IReadOnlyList<string> _keepSubfolders;

    public CleanupService(ActionLog? actionLog = null, RestorePointService? restorePointService = null)
        : this(
            actionLog,
            restorePointService,
            BuildDefaultTargets,
            QueryWindowsRecycleBin,
            EmptyWindowsRecycleBin,
            protectedFolders: null,
            windowsUpdateBusyReason: QueryWindowsUpdateBusyReason)
    {
    }

    internal CleanupService(
        ActionLog? actionLog,
        RestorePointService? restorePointService,
        Func<IReadOnlyList<CleanupTarget>> targetsFactory,
        Func<(long Bytes, long Items)>? queryRecycleBin = null,
        Func<int>? emptyRecycleBin = null,
        IReadOnlyList<ProtectedFolder>? protectedFolders = null,
        Func<string?>? windowsUpdateBusyReason = null,
        IReadOnlyList<string>? keepSubfolders = null)
    {
        _actionLog = actionLog ?? new ActionLog();
        _restorePointService = restorePointService ?? new RestorePointService();
        _targetsFactory = targetsFactory ?? throw new ArgumentNullException(nameof(targetsFactory));
        _queryRecycleBin = queryRecycleBin ?? (() => (0L, 0L));
        _emptyRecycleBin = emptyRecycleBin ?? (() => 0);
        _protectedFolders = protectedFolders ?? BuildDefaultProtectedFolders();
        _windowsUpdateBusyReason = windowsUpdateBusyReason ?? (() => null);
        _keepSubfolders = (keepSubfolders ?? BuildDefaultKeepSubfolders())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => Path.TrimEndingDirectorySeparator(Path.GetFullPath(k)))
            .ToList();
    }

    /// <summary>
    /// Sottocartelle che la pulizia salta sempre: la cartella dell'app (se gira da %TEMP%)
    /// e %TEMP%\.net, dove .NET estrae i file degli exe in file singolo.
    /// </summary>
    internal static IReadOnlyList<string> BuildDefaultKeepSubfolders() =>
    [
        Path.GetFullPath(AppContext.BaseDirectory),
        Path.Combine(Path.GetTempPath(), ".net"),
    ];

    /// <summary>Analizza in sola lettura quanto spazio è possibile liberare.</summary>
    public List<PlannedChange> Scan(CancellationToken cancellationToken = default)
    {
        bool isAdmin = RestorePointService.IsAdministrator();
        var results = new List<PlannedChange>();

        foreach (var target in _targetsFactory())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (target.IsRecycleBin)
            {
                var (bytes, items) = _queryRecycleBin();
                if (items <= 0 && bytes <= 0)
                {
                    continue;
                }

                string readable = Heuristics.FormatBytes(bytes);
                results.Add(new PlannedChange
                {
                    Id = target.Id,
                    Category = CategoryName,
                    Title = target.Title,
                    Explanation = $"{target.Explanation} ({items} elementi, circa {readable}).",
                    TechnicalDetail = $"SHEmptyRecycleBin · {items} elementi · {bytes:N0} byte",
                    BeforeValue = $"{items} elementi ({readable})",
                    AfterValue = "Cestino vuoto",
                    EstimatedBytes = bytes,
                    RequiresAdmin = target.RequiresAdmin,
                    IsReversible = false,
                    IsSelected = true,
                    TargetKey = target.Id,
                    ActionKind = target.ActionKind,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [IsRecycleBinKey] = "true",
                    },
                });
                continue;
            }

            var existingDirs = target.Directories
                .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (existingDirs.Count == 0)
            {
                continue;
            }

            string? blockedReason = existingDirs
                .Select(d => GetUnsafeDirectoryReason(d, _protectedFolders))
                .FirstOrDefault(r => r is not null);
            if (blockedReason is not null)
            {
                // La voce si mostra, non selezionata, con il motivo: non si misura e non si svuota.
                results.Add(new PlannedChange
                {
                    Id = target.Id,
                    Category = CategoryName,
                    Title = target.Title,
                    Explanation = "Voce saltata per sicurezza: " + blockedReason,
                    TechnicalDetail = string.Join(" ; ", existingDirs),
                    BeforeValue = "Non analizzata",
                    AfterValue = "Nessuna modifica",
                    EstimatedBytes = 0,
                    RequiresAdmin = target.RequiresAdmin,
                    IsReversible = false,
                    IsSelected = false,
                    TargetKey = target.Id,
                    ActionKind = target.ActionKind,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [DirectoriesJsonKey] = JsonSerializer.Serialize(existingDirs),
                        [BlockedReasonKey] = blockedReason,
                    },
                });
                continue;
            }

            DateTime cutoffUtc = ComputeCutoffUtc(target.MinFileAge);
            long totalBytes = 0;
            int fileCount = 0;
            foreach (string dir in existingDirs)
            {
                var (bytes, count) = MeasureDirectory(dir, cutoffUtc, _keepSubfolders, cancellationToken);
                totalBytes += bytes;
                fileCount += count;
            }

            if (fileCount == 0)
            {
                continue;
            }

            string formattedSize = Heuristics.FormatBytes(totalBytes);
            string ageNote = target.MinFileAge > TimeSpan.Zero
                ? $" I file toccati nelle ultime {target.MinFileAge.TotalHours:0} ore restano."
                : "";
            results.Add(new PlannedChange
            {
                Id = target.Id,
                Category = CategoryName,
                Title = target.Title,
                Explanation = $"{target.Explanation} ({fileCount} file, circa {formattedSize}).{ageNote}",
                TechnicalDetail = string.Join(" ; ", existingDirs) + $" · {fileCount} file · {totalBytes:N0} byte",
                BeforeValue = $"{fileCount} file ({formattedSize})",
                AfterValue = "Cartella pulita (i file in uso restano intatti)",
                EstimatedBytes = totalBytes,
                RequiresAdmin = target.RequiresAdmin,
                IsReversible = false,
                IsSelected = !target.RequiresAdmin || isAdmin,
                TargetKey = target.Id,
                ActionKind = target.ActionKind,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [DirectoriesJsonKey] = JsonSerializer.Serialize(existingDirs),
                    [MinFileAgeHoursKey] = target.MinFileAge.TotalHours.ToString(CultureInfo.InvariantCulture),
                },
            });
        }

        return results;
    }

    /// <summary>Esegue le pulizie selezionate e registra la sessione nello storico.</summary>
    public ActionExecutionResult Apply(
        IEnumerable<PlannedChange> changes,
        bool createRestorePoint = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var selected = changes.Where(c => c.IsSelected).ToList();
        var outcomes = new List<ActionItemOutcome>();

        if (selected.Count == 0)
        {
            return new ActionExecutionResult(null, null, outcomes);
        }

        // Se lo storico non si legge (file bloccato, permessi) non si comincia nemmeno.
        _actionLog.EnsureAccessible();

        RestorePointResult? restorePoint = createRestorePoint
            ? _restorePointService.Create("W11-cleaner-tool: pulizia")
            : null;
        if (ActionExecutionResult.IsMissing(createRestorePoint, restorePoint))
        {
            return ActionExecutionResult.MissingRestorePoint(restorePoint!);
        }

        var recorder = new SessionRecorder(_actionLog, restorePoint?.Status == RestorePointStatus.Created);

        foreach (var change in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ActionItemOutcome outcome;
            string? updateBusy = change.ActionKind == ActionKind.CleanWindowsUpdateCache
                ? _windowsUpdateBusyReason()
                : null;
            if (updateBusy is not null)
            {
                outcome = new ActionItemOutcome(change.Id, change.Title, Succeeded: false, updateBusy);
            }
            else if (change.ActionKind == ActionKind.EmptyRecycleBin ||
                (change.Metadata.TryGetValue(IsRecycleBinKey, out string? isBin) && isBin == "true"))
            {
                outcome = ApplyRecycleBin(change);
            }
            else
            {
                outcome = ApplyDirectoryCleanup(change, cancellationToken);
            }

            outcomes.Add(outcome);
            if (outcome.Succeeded)
            {
                recorder.Record(new ActionLogEntry
                {
                    ActionKind = change.ActionKind,
                    Title = change.Title,
                    Target = change.TargetKey,
                    OldValue = change.BeforeValue,
                    NewValue = $"Liberati {Heuristics.FormatBytes(outcome.BytesFreed)}",
                    TimestampUtc = DateTime.UtcNow,
                    Undone = false,
                });
            }
        }

        return new ActionExecutionResult(recorder.Complete(), restorePoint, outcomes);
    }

    public UndoOutcome UndoEntry(ActionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new UndoOutcome(
            entry.Id,
            entry.Title,
            Succeeded: false,
            entry.ActionKind == ActionKind.EmptyRecycleBin
                ? "Lo svuotamento del Cestino non si annulla: i file sono stati eliminati definitivamente."
                : "La pulizia di file temporanei e cache non si annulla: i file sono stati eliminati (Windows e i browser ricreano quelli che servono).");
    }

    private ActionItemOutcome ApplyRecycleBin(PlannedChange change)
    {
        try
        {
            int hr = _emptyRecycleBin();
            if (hr == 0 || hr == 1 || hr == RecycleBinEmptyHResult)
            {
                return new ActionItemOutcome(
                    change.Id,
                    change.Title,
                    Succeeded: true,
                    hr == RecycleBinEmptyHResult || hr == 1 ? "Cestino già vuoto." : "Cestino svuotato.",
                    BytesFreed: change.EstimatedBytes);
            }

            var ex = Marshal.GetExceptionForHR(hr);
            return new ActionItemOutcome(
                change.Id,
                change.Title,
                Succeeded: false,
                $"Impossibile svuotare il Cestino: {ex?.Message ?? $"codice 0x{hr:X8}"}");
        }
        catch (Exception ex)
        {
            return new ActionItemOutcome(change.Id, change.Title, Succeeded: false, "Errore Cestino: " + ex.Message);
        }
    }

    private ActionItemOutcome ApplyDirectoryCleanup(PlannedChange change, CancellationToken ct)
    {
        List<string> dirs = ResolveDirectories(change);
        if (dirs.Count == 0)
        {
            return new ActionItemOutcome(change.Id, change.Title, Succeeded: false, "Nessuna cartella valida specificata.");
        }

        // Il controllo si rifà qui, sul percorso vero: l'anteprima non basta.
        foreach (string dir in dirs)
        {
            string? reason = GetUnsafeDirectoryReason(dir, _protectedFolders);
            if (reason is not null)
            {
                return new ActionItemOutcome(change.Id, change.Title, Succeeded: false, "Voce saltata per sicurezza: " + reason);
            }
        }

        DateTime cutoffUtc = ComputeCutoffUtc(ReadMinFileAge(change));
        var counters = new CleanCounters();

        foreach (string dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(dir))
            {
                continue;
            }

            CleanRecursive(dir, isRoot: true, _keepSubfolders, cutoffUtc, counters, ct);
        }

        if (counters.Deleted == 0 && counters.Skipped > 0)
        {
            string msg = change.RequiresAdmin && !RestorePointService.IsAdministrator()
                ? "Servono i diritti di amministratore per eliminare questi file."
                : $"Tutti i file trovati ({counters.Skipped}) erano in uso o protetti.";
            return new ActionItemOutcome(change.Id, change.Title, Succeeded: false, msg);
        }

        var parts = new List<string>
        {
            $"Eliminati {counters.Deleted} file ({Heuristics.FormatBytes(counters.Freed)})",
        };
        if (counters.Skipped > 0)
        {
            parts.Add($"{counters.Skipped} in uso saltati");
        }

        if (counters.Recent > 0)
        {
            parts.Add($"{counters.Recent} recenti (ultime 24 ore) lasciati");
        }

        return new ActionItemOutcome(change.Id, change.Title, Succeeded: true, string.Join(", ", parts) + ".", counters.Freed);
    }

    private static TimeSpan ReadMinFileAge(PlannedChange change)
    {
        if (change.Metadata.TryGetValue(MinFileAgeHoursKey, out string? raw) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double hours) &&
            hours > 0)
        {
            return TimeSpan.FromHours(hours);
        }

        return TimeSpan.Zero;
    }

    private static DateTime ComputeCutoffUtc(TimeSpan minFileAge)
        => minFileAge > TimeSpan.Zero ? DateTime.UtcNow - minFileAge : DateTime.MaxValue;

    private static bool IsRecent(FileInfo file, DateTime cutoffUtc)
    {
        if (cutoffUtc == DateTime.MaxValue)
        {
            return false;
        }

        // Si guarda anche la data di creazione: un file estratto da un archivio può avere una data di modifica vecchia.
        DateTime lastTouch = file.LastWriteTimeUtc > file.CreationTimeUtc ? file.LastWriteTimeUtc : file.CreationTimeUtc;
        return lastTouch > cutoffUtc;
    }

    /// <summary>
    /// Restituisce il motivo per cui una cartella non va mai svuotata (radice di un disco, profilo utente,
    /// cartelle personali, Windows, Programmi), oppure null se si può pulire.
    /// </summary>
    internal static string? GetUnsafeDirectoryReason(string directory, IReadOnlyList<ProtectedFolder> protectedFolders)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return $"il percorso «{directory}» non è valido.";
        }

        string? root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
        {
            return $"«{full}» è la radice di un disco: svuotarla cancellerebbe tutto il disco.";
        }

        foreach (var folder in protectedFolders)
        {
            if (string.IsNullOrWhiteSpace(folder.Path))
            {
                continue;
            }

            string protectedPath;
            try
            {
                protectedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
            {
                continue;
            }

            if (IsSameOrInside(protectedPath, full))
            {
                return $"«{full}» è {folder.Label} o la contiene: svuotarla cancellerebbe file personali o di sistema.";
            }

            if (folder.ProtectInside && IsSameOrInside(full, protectedPath))
            {
                return $"«{full}» è dentro {folder.Label}: svuotarla cancellerebbe file personali o di sistema.";
            }
        }

        return null;
    }

    internal static IReadOnlyList<ProtectedFolder> BuildDefaultProtectedFolders()
    {
        var list = new List<ProtectedFolder>();
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string trimmedProfile = string.IsNullOrWhiteSpace(profile) ? "" : Path.TrimEndingDirectorySeparator(profile);

        void Add(string path, string label, bool protectInside)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            // Se una cartella personale coincide con il profilo (configurazioni insolite), non si blocca tutto il profilo:
            // %TEMP% sta dentro il profilo e deve restare pulibile.
            bool sameAsProfile = trimmedProfile.Length > 0 &&
                string.Equals(Path.TrimEndingDirectorySeparator(path), trimmedProfile, StringComparison.OrdinalIgnoreCase);
            list.Add(new ProtectedFolder(path, label, protectInside && !sameAsProfile));
        }

        Add(profile, "il profilo utente", protectInside: false);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "il Desktop", protectInside: true);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "la cartella Documenti", protectInside: true);
        Add(string.IsNullOrWhiteSpace(profile) ? "" : Path.Combine(profile, "Downloads"), "la cartella Download", protectInside: true);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "la cartella Immagini", protectInside: true);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "la cartella Video", protectInside: true);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "la cartella Musica", protectInside: true);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "la cartella di Windows", protectInside: false);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.System), "la cartella di sistema di Windows", protectInside: true);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "la cartella di sistema di Windows", protectInside: true);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "la cartella Programmi", protectInside: true);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "la cartella Programmi (x86)", protectInside: true);
        return list;
    }

    private static bool IsSameOrInside(string path, string parent)
    {
        if (string.Equals(path, parent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string parentWithSep = parent.EndsWith(Path.DirectorySeparatorChar) || parent.EndsWith(Path.AltDirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return path.StartsWith(parentWithSep, StringComparison.OrdinalIgnoreCase);
    }

    private static string? QueryWindowsUpdateBusyReason()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var running = Wmi.Query("SELECT Name, State FROM Win32_Service WHERE Name = 'wuauserv' OR Name = 'BITS'")
                .Where(r => string.Equals(r.Str("State"), "Running", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Str("Name"))
                .ToList();

            return running.Count == 0
                ? null
                : $"Windows Update è attivo (servizio {string.Join(" e ", running)} in esecuzione): riprova più tardi. Il programma non ferma i servizi da solo.";
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            return "Non riesco a verificare se Windows Update è attivo: per sicurezza la cache non si tocca. Riprova più tardi.";
        }
    }

    private static List<string> ResolveDirectories(PlannedChange change)
    {
        if (change.Metadata.TryGetValue(DirectoriesJsonKey, out string? json) && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch (JsonException)
            {
                // Fallback su TargetKey se il path esiste.
            }
        }

        return Directory.Exists(change.TargetKey) ? [change.TargetKey] : [];
    }

    internal static IReadOnlyList<CleanupTarget> BuildDefaultTargets()
    {
        string userTemp = Path.GetTempPath();
        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var targets = new List<CleanupTarget>
        {
            new(
                Id: "cleanup-user-temp",
                Title: "File temporanei utente",
                Explanation: "File temporanei nella cartella %TEMP% dell'utente.",
                ActionKind: ActionKind.CleanTempFiles,
                Directories: [userTemp],
                RequiresAdmin: false,
                MinFileAge: TempMinFileAge),
            new(
                Id: "cleanup-windows-temp",
                Title: "File temporanei di Windows",
                Explanation: "File temporanei di sistema in Windows\\Temp.",
                ActionKind: ActionKind.CleanTempFiles,
                Directories: [Path.Combine(windowsDir, "Temp")],
                RequiresAdmin: true,
                MinFileAge: TempMinFileAge),
            new(
                Id: "cleanup-windows-update",
                Title: "Cache di Windows Update",
                Explanation: "Pacchetti scaricati di aggiornamenti passati (Windows li riscarica se servono).",
                ActionKind: ActionKind.CleanWindowsUpdateCache,
                Directories: [Path.Combine(windowsDir, "SoftwareDistribution", "Download")],
                RequiresAdmin: true),
        };

        var edgeCaches = FindChromiumCacheDirectories(Path.Combine(localAppData, "Microsoft", "Edge", "User Data"));
        if (edgeCaches.Count > 0)
        {
            targets.Add(new CleanupTarget(
                Id: "cleanup-edge-cache",
                Title: "Cache di Microsoft Edge",
                Explanation: "Solo file temporanei del browser (Cache e Code Cache). Password, cookie e cronologia restano intatti.",
                ActionKind: ActionKind.CleanBrowserCache,
                Directories: edgeCaches));
        }

        var chromeCaches = FindChromiumCacheDirectories(Path.Combine(localAppData, "Google", "Chrome", "User Data"));
        if (chromeCaches.Count > 0)
        {
            targets.Add(new CleanupTarget(
                Id: "cleanup-chrome-cache",
                Title: "Cache di Google Chrome",
                Explanation: "Solo file temporanei del browser (Cache e Code Cache). Password, cookie e cronologia restano intatti.",
                ActionKind: ActionKind.CleanBrowserCache,
                Directories: chromeCaches));
        }

        var firefoxCaches = FindFirefoxCacheDirectories(Path.Combine(localAppData, "Mozilla", "Firefox", "Profiles"));
        if (firefoxCaches.Count > 0)
        {
            targets.Add(new CleanupTarget(
                Id: "cleanup-firefox-cache",
                Title: "Cache di Mozilla Firefox",
                Explanation: "Solo cache temporanea (cache2). Segnalibri, password e cookie restano intatti.",
                ActionKind: ActionKind.CleanBrowserCache,
                Directories: firefoxCaches));
        }

        targets.Add(new CleanupTarget(
            Id: "cleanup-recycle-bin",
            Title: "Cestino di Windows",
            Explanation: "Svuota definitivamente i file già spostati nel Cestino",
            ActionKind: ActionKind.EmptyRecycleBin,
            Directories: [],
            RequiresAdmin: false,
            IsRecycleBin: true));

        return targets;
    }

    internal static List<string> FindChromiumCacheDirectories(string userDataRoot)
    {
        var dirs = new List<string>();
        if (!Directory.Exists(userDataRoot))
        {
            return dirs;
        }

        try
        {
            foreach (var profileDir in new DirectoryInfo(userDataRoot).EnumerateDirectories())
            {
                if ((profileDir.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                bool isProfile =
                    string.Equals(profileDir.Name, "Default", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(profileDir.Name, "Guest Profile", StringComparison.OrdinalIgnoreCase) ||
                    profileDir.Name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);

                if (!isProfile)
                {
                    continue;
                }

                string cache = Path.Combine(profileDir.FullName, "Cache");
                if (Directory.Exists(cache))
                {
                    dirs.Add(cache);
                }

                string codeCache = Path.Combine(profileDir.FullName, "Code Cache");
                if (Directory.Exists(codeCache))
                {
                    dirs.Add(codeCache);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Se la cartella non è leggibile, restituiamo quello che abbiamo trovato.
        }

        return dirs;
    }

    internal static List<string> FindFirefoxCacheDirectories(string profilesRoot)
    {
        var dirs = new List<string>();
        if (!Directory.Exists(profilesRoot))
        {
            return dirs;
        }

        try
        {
            foreach (var profileDir in new DirectoryInfo(profilesRoot).EnumerateDirectories())
            {
                if ((profileDir.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                string cache2 = Path.Combine(profileDir.FullName, "cache2");
                if (Directory.Exists(cache2))
                {
                    dirs.Add(cache2);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Ignora errori di accesso.
        }

        return dirs;
    }

    private static (long Bytes, int Files) MeasureDirectory(
        string rootDir,
        DateTime cutoffUtc,
        IReadOnlyList<string> keepSubfolders,
        CancellationToken ct)
    {
        long bytes = 0;
        int files = 0;

        var stack = new Stack<string>();
        stack.Push(rootDir);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string current = stack.Pop();

            if (IsProtectedTempSubfolder(current, keepSubfolders))
            {
                continue;
            }

            try
            {
                var dirInfo = new DirectoryInfo(current);
                if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                foreach (var file in dirInfo.EnumerateFiles())
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsRecent(file, cutoffUtc))
                    {
                        continue;
                    }

                    bytes += file.Length;
                    files++;
                }

                foreach (var sub in dirInfo.EnumerateDirectories())
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        stack.Push(sub.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Salta cartelle protette o rimosse nel frattempo.
            }
        }

        return (bytes, files);
    }

    private sealed class CleanCounters
    {
        public long Freed;
        public int Deleted;
        public int Skipped;
        public int Recent;
    }

    private static void CleanRecursive(
        string currentDir,
        bool isRoot,
        IReadOnlyList<string> keepSubfolders,
        DateTime cutoffUtc,
        CleanCounters counters,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!isRoot && IsProtectedTempSubfolder(currentDir, keepSubfolders))
        {
            return;
        }

        DirectoryInfo dirInfo;
        try
        {
            dirInfo = new DirectoryInfo(currentDir);
            if (!dirInfo.Exists || (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            foreach (var sub in dirInfo.EnumerateDirectories())
            {
                // Junction e collegamenti simbolici non si seguono: CleanRecursive si ferma sui reparse point.
                CleanRecursive(sub.FullName, isRoot: false, keepSubfolders, cutoffUtc, counters, ct);
            }

            foreach (var file in dirInfo.EnumerateFiles())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (IsRecent(file, cutoffUtc))
                    {
                        counters.Recent++;
                        continue;
                    }

                    long len = file.Length;
                    if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                    {
                        file.Attributes &= ~FileAttributes.ReadOnly;
                    }

                    file.Delete();
                    counters.Freed += len;
                    counters.Deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                {
                    counters.Skipped++;
                }
            }

            if (!isRoot && !Directory.EnumerateFileSystemEntries(currentDir).Any())
            {
                try
                {
                    dirInfo.Delete(recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                {
                    // Se la cartella è in uso, la lasciamo dov'è.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            counters.Skipped++;
        }
    }

    private static bool IsProtectedTempSubfolder(string path, IReadOnlyList<string> keepSubfolders)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return keepSubfolders.Any(k => string.Equals(normalized, k, StringComparison.OrdinalIgnoreCase));
    }

    private static (long Bytes, long Items) QueryWindowsRecycleBin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (0, 0);
        }

        try
        {
            var info = new NativeMethods.SHQUERYRBINFO
            {
                cbSize = Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>(),
            };
            int hr = NativeMethods.SHQueryRecycleBin(null, ref info);
            return hr == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    private static int EmptyWindowsRecycleBin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        return NativeMethods.SHEmptyRecycleBin(
            IntPtr.Zero,
            null,
            NativeMethods.RecycleFlags.SHERB_NOCONFIRMATION |
            NativeMethods.RecycleFlags.SHERB_NOPROGRESSUI |
            NativeMethods.RecycleFlags.SHERB_NOSOUND);
    }
}
