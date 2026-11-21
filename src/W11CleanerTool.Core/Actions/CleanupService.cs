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
    bool IsRecycleBin = false);

/// <summary>
/// Stima e pulizia di temporanei, cache Windows Update, cache browser comuni (solo Cache / Code Cache) e Cestino.
/// I file in uso vengono saltati in modo sicuro senza eccezioni fatali.
/// </summary>
public sealed class CleanupService
{
    private const string CategoryName = "Pulizia";
    private const string DirectoriesJsonKey = "DirectoriesJson";
    private const string IsRecycleBinKey = "IsRecycleBin";
    private const int RecycleBinEmptyHResult = unchecked((int)0x8000FFFF);

    private readonly ActionLog _actionLog;
    private readonly RestorePointService _restorePointService;
    private readonly Func<IReadOnlyList<CleanupTarget>> _targetsFactory;
    private readonly Func<(long Bytes, long Items)> _queryRecycleBin;
    private readonly Func<int> _emptyRecycleBin;

    public CleanupService(ActionLog? actionLog = null, RestorePointService? restorePointService = null)
        : this(
            actionLog,
            restorePointService,
            BuildDefaultTargets,
            QueryWindowsRecycleBin,
            EmptyWindowsRecycleBin)
    {
    }

    internal CleanupService(
        ActionLog? actionLog,
        RestorePointService? restorePointService,
        Func<IReadOnlyList<CleanupTarget>> targetsFactory,
        Func<(long Bytes, long Items)>? queryRecycleBin = null,
        Func<int>? emptyRecycleBin = null)
    {
        _actionLog = actionLog ?? new ActionLog();
        _restorePointService = restorePointService ?? new RestorePointService();
        _targetsFactory = targetsFactory ?? throw new ArgumentNullException(nameof(targetsFactory));
        _queryRecycleBin = queryRecycleBin ?? (() => (0L, 0L));
        _emptyRecycleBin = emptyRecycleBin ?? (() => 0);
    }

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

            long totalBytes = 0;
            int fileCount = 0;
            foreach (string dir in existingDirs)
            {
                var (bytes, count) = MeasureDirectory(dir, cancellationToken);
                totalBytes += bytes;
                fileCount += count;
            }

            if (fileCount == 0)
            {
                continue;
            }

            string formattedSize = Heuristics.FormatBytes(totalBytes);
            results.Add(new PlannedChange
            {
                Id = target.Id,
                Category = CategoryName,
                Title = target.Title,
                Explanation = $"{target.Explanation} ({fileCount} file, circa {formattedSize}).",
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

        RestorePointResult? restorePoint = createRestorePoint
            ? _restorePointService.Create("W11-cleaner-tool: pulizia")
            : null;

        var session = new ActionSession
        {
            TimestampUtc = DateTime.UtcNow,
            RestorePointCreated = restorePoint?.Status == RestorePointStatus.Created,
        };

        foreach (var change in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ActionItemOutcome outcome;
            if (change.ActionKind == ActionKind.EmptyRecycleBin ||
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
                session.Entries.Add(new ActionLogEntry
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

        if (session.Entries.Count > 0)
        {
            _actionLog.AddSession(session);
            return new ActionExecutionResult(session, restorePoint, outcomes);
        }

        return new ActionExecutionResult(null, restorePoint, outcomes);
    }

    public UndoOutcome UndoEntry(ActionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new UndoOutcome(
            entry.Id,
            entry.Title,
            Succeeded: false,
            "La pulizia di file temporanei e cache non si annulla: Windows e i browser li ricreano automaticamente quando servono.");
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

    private static ActionItemOutcome ApplyDirectoryCleanup(PlannedChange change, CancellationToken ct)
    {
        List<string> dirs = ResolveDirectories(change);
        if (dirs.Count == 0)
        {
            return new ActionItemOutcome(change.Id, change.Title, Succeeded: false, "Nessuna cartella valida specificata.");
        }

        long bytesFreed = 0;
        int deletedFiles = 0;
        int skippedFiles = 0;

        foreach (string dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(dir))
            {
                continue;
            }

            var (freed, deleted, skipped) = CleanDirectoryContents(dir, ct);
            bytesFreed += freed;
            deletedFiles += deleted;
            skippedFiles += skipped;
        }

        if (deletedFiles == 0 && skippedFiles > 0)
        {
            string msg = change.RequiresAdmin && !RestorePointService.IsAdministrator()
                ? "Servono i diritti di amministratore per eliminare questi file."
                : $"Tutti i file trovati ({skippedFiles}) erano in uso o protetti.";
            return new ActionItemOutcome(change.Id, change.Title, Succeeded: false, msg);
        }

        string detail = skippedFiles > 0
            ? $"Eliminati {deletedFiles} file ({Heuristics.FormatBytes(bytesFreed)}), {skippedFiles} in uso saltati."
            : $"Eliminati {deletedFiles} file ({Heuristics.FormatBytes(bytesFreed)}).";
        return new ActionItemOutcome(change.Id, change.Title, Succeeded: true, detail, bytesFreed);
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
                RequiresAdmin: false),
            new(
                Id: "cleanup-windows-temp",
                Title: "File temporanei di Windows",
                Explanation: "File temporanei di sistema in Windows\\Temp.",
                ActionKind: ActionKind.CleanTempFiles,
                Directories: [Path.Combine(windowsDir, "Temp")],
                RequiresAdmin: true),
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

    private static (long Bytes, int Files) MeasureDirectory(string rootDir, CancellationToken ct)
    {
        long bytes = 0;
        int files = 0;
        string appDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        string netExtractDir = Path.TrimEndingDirectorySeparator(Path.Combine(Path.GetTempPath(), ".net"));

        var stack = new Stack<string>();
        stack.Push(rootDir);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string current = stack.Pop();

            if (IsProtectedTempSubfolder(current, appDir, netExtractDir))
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

    private static (long Freed, int Deleted, int Skipped) CleanDirectoryContents(string rootDir, CancellationToken ct)
    {
        long freed = 0;
        int deleted = 0;
        int skipped = 0;
        string appDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        string netExtractDir = Path.TrimEndingDirectorySeparator(Path.Combine(Path.GetTempPath(), ".net"));

        CleanRecursive(rootDir, isRoot: true, appDir, netExtractDir, ref freed, ref deleted, ref skipped, ct);
        return (freed, deleted, skipped);
    }

    private static void CleanRecursive(
        string currentDir,
        bool isRoot,
        string appDir,
        string netExtractDir,
        ref long freed,
        ref int deleted,
        ref int skipped,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!isRoot && IsProtectedTempSubfolder(currentDir, appDir, netExtractDir))
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
                CleanRecursive(sub.FullName, isRoot: false, appDir, netExtractDir, ref freed, ref deleted, ref skipped, ct);
            }

            foreach (var file in dirInfo.EnumerateFiles())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    long len = file.Length;
                    if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                    {
                        file.Attributes &= ~FileAttributes.ReadOnly;
                    }

                    file.Delete();
                    freed += len;
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                {
                    skipped++;
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
            skipped++;
        }
    }

    private static bool IsProtectedTempSubfolder(string path, string appDir, string netExtractDir)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(normalized, appDir, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, netExtractDir, StringComparison.OrdinalIgnoreCase);
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
