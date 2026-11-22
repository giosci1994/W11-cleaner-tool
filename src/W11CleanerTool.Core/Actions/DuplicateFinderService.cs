using System.Security;
using System.Security.Cryptography;
using W11CleanerTool.Core.Safety;

namespace W11CleanerTool.Core.Actions;

/// <summary>
/// Trova file duplicati esatti (dimensione -> SHA256 dei primi 4 KB -> SHA256 completo).
/// Mantiene il file più vecchio (o con percorso più corto) e sposta gli altri solo in Quarantena, mai cancellazione diretta.
/// </summary>
public sealed class DuplicateFinderService
{
    private const string CategoryName = "Duplicati";
    private const int PartialHashBytes = 4096;

    private static readonly HashSet<string> ExcludedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows",
        "Program Files",
        "Program Files (x86)",
        "ProgramData",
        "AppData",
        "$Recycle.Bin",
        "System Volume Information",
        "Recovery",
        "WinSxS",
        ".git",
        "node_modules",
    };

    private readonly QuarantineService _quarantineService;
    private readonly ActionLog _actionLog;
    private readonly RestorePointService _restorePointService;

    public DuplicateFinderService(
        QuarantineService? quarantineService = null,
        ActionLog? actionLog = null,
        RestorePointService? restorePointService = null)
    {
        _quarantineService = quarantineService ?? new QuarantineService();
        _actionLog = actionLog ?? new ActionLog();
        _restorePointService = restorePointService ?? new RestorePointService();
    }

    /// <summary>Scansiona le cartelle scelte e restituisce le proposte di spostamento in quarantena.</summary>
    public List<PlannedChange> Scan(
        IEnumerable<string> folders,
        CancellationToken cancellationToken = default,
        long minFileSizeBytes = 1)
    {
        ArgumentNullException.ThrowIfNull(folders);

        long effectiveMinSize = Math.Max(1L, minFileSizeBytes);
        var filesBySize = new Dictionary<long, List<FileInfo>>();

        // Un solo elenco di file visti per tutte le cartelle: lo stesso file non deve mai risultare «copia di sé stesso».
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string folder in NormalizeScanRoots(folders))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder) || IsExcludedRoot(folder))
            {
                continue;
            }

            CollectFiles(folder, effectiveMinSize, filesBySize, seenFiles, cancellationToken);
        }

        var partialGroups = new Dictionary<(long Size, string PartialHash), List<FileInfo>>();
        foreach (var (size, group) in filesBySize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Count < 2)
            {
                continue;
            }

            foreach (var file in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? partial = TryComputePartialSha256(file.FullName);
                if (partial is null)
                {
                    continue;
                }

                var key = (size, partial);
                if (!partialGroups.TryGetValue(key, out var list))
                {
                    list = new List<FileInfo>();
                    partialGroups[key] = list;
                }

                list.Add(file);
            }
        }

        var fullGroups = new Dictionary<string, List<FileInfo>>(StringComparer.Ordinal);
        foreach (var ((size, partialHash), group) in partialGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Count < 2)
            {
                continue;
            }

            foreach (var file in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? fullHash = size <= PartialHashBytes
                    ? partialHash
                    : TryComputeFullSha256(file.FullName);

                if (fullHash is null)
                {
                    continue;
                }

                if (!fullGroups.TryGetValue(fullHash, out var list))
                {
                    list = new List<FileInfo>();
                    fullGroups[fullHash] = list;
                }

                list.Add(file);
            }
        }

        var changes = new List<PlannedChange>();
        foreach (var (hash, group) in fullGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Count < 2)
            {
                continue;
            }

            // Mantiene il file più vecchio; a parità di data, il percorso più corto.
            var ordered = group
                .OrderBy(f => f.LastWriteTimeUtc)
                .ThenBy(f => f.FullName.Length)
                .ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var keeper = ordered[0];
            foreach (var dup in ordered.Skip(1))
            {
                string sizeStr = Heuristics.FormatBytes(dup.Length);
                changes.Add(new PlannedChange
                {
                    Id = $"dup:{hash[..12]}:{Guid.NewGuid().ToString("N")[..8]}",
                    Category = CategoryName,
                    Title = dup.Name,
                    Explanation = $"Copia identica ({sizeStr}) di «{keeper.Name}» in {keeper.DirectoryName}. Verrà spostata in quarantena (non cancellata).",
                    TechnicalDetail = $"Duplicato: {dup.FullName} · Originale tenuto: {keeper.FullName} · SHA256: {hash}",
                    BeforeValue = dup.FullName,
                    AfterValue = "Spostato in quarantena (30 giorni)",
                    EstimatedBytes = dup.Length,
                    RequiresAdmin = false,
                    IsReversible = true,
                    IsSelected = true,
                    TargetKey = dup.FullName,
                    ActionKind = ActionKind.QuarantineDuplicate,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["OriginalKeptPath"] = keeper.FullName,
                        ["Sha256"] = hash,
                    },
                });
            }
        }

        return changes;
    }

    /// <summary>Sposta in quarantena i duplicati selezionati (mai eliminazione diretta).</summary>
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
            ? _restorePointService.Create("W11-cleaner-tool: quarantena duplicati")
            : null;
        if (ActionExecutionResult.IsMissing(createRestorePoint, restorePoint))
        {
            return ActionExecutionResult.MissingRestorePoint(restorePoint!);
        }

        var recorder = new SessionRecorder(_actionLog, restorePoint?.Status == RestorePointStatus.Created);

        // Lo SHA256 di un originale con più copie si calcola una volta sola per questa applicazione.
        var keeperHashCache = new Dictionary<KeeperFingerprint, string?>();

        foreach (var change in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (change.ActionKind != ActionKind.QuarantineDuplicate)
            {
                outcomes.Add(new ActionItemOutcome(change.Id, change.Title, Succeeded: false, "Azione non supportata da DuplicateFinderService."));
                continue;
            }

            string? refusal = CheckKeeperBeforeMove(change, keeperHashCache, out string expectedHash);
            if (refusal is not null)
            {
                outcomes.Add(new ActionItemOutcome(change.Id, change.Title, Succeeded: false, refusal));
                continue;
            }

            QuarantineItem item;
            try
            {
                // MoveToQuarantine ricontrolla lo SHA256 della copia e non la sposta se è cambiata.
                item = _quarantineService.MoveToQuarantine(change.TargetKey, recorder.Session.SessionId, expectedHash);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                outcomes.Add(new ActionItemOutcome(
                    change.Id,
                    change.Title,
                    Succeeded: false,
                    $"Impossibile spostare «{change.Title}»: {ex.Message}"));
                continue;
            }

            string sizeText = Heuristics.FormatBytes(item.SizeBytes);
            outcomes.Add(new ActionItemOutcome(
                change.Id,
                change.Title,
                Succeeded: true,
                item.FromOtherDrive
                    ? $"Spostato in quarantena ({sizeText}). Veniva da un altro disco: ora occupa spazio sul disco della quarantena finché non la svuoti."
                    : $"Spostato in quarantena ({sizeText}). Lo spazio si libera quando svuoti la quarantena.",
                BytesFreed: item.SizeBytes));

            recorder.Record(new ActionLogEntry
            {
                ActionKind = ActionKind.QuarantineDuplicate,
                Title = change.Title,
                Target = item.OriginalPath,
                OldValue = item.OriginalPath,
                NewValue = "Quarantined",
                QuarantinePath = item.QuarantinedPath,
                TimestampUtc = DateTime.UtcNow,
            });
        }

        return new ActionExecutionResult(recorder.Complete(), restorePoint, outcomes);
    }

    /// <summary>
    /// Prima di spostare una copia: l'originale tenuto deve esistere ed essere ancora identico all'analisi.
    /// Restituisce il motivo del rifiuto, oppure null se si può procedere.
    /// </summary>
    private static string? CheckKeeperBeforeMove(
        PlannedChange change,
        Dictionary<KeeperFingerprint, string?> keeperHashCache,
        out string expectedHash)
    {
        expectedHash = "";
        if (!change.Metadata.TryGetValue("OriginalKeptPath", out string? keeper) || string.IsNullOrWhiteSpace(keeper) ||
            !change.Metadata.TryGetValue("Sha256", out string? hash) || string.IsNullOrWhiteSpace(hash))
        {
            return "Mancano i dati dell'analisi (originale e SHA256): rifai la ricerca. La copia resta dov'è.";
        }

        expectedHash = hash.Trim();

        if (string.Equals(
                Path.GetFullPath(keeper),
                Path.GetFullPath(change.TargetKey),
                StringComparison.OrdinalIgnoreCase))
        {
            return "L'originale e la copia sono lo stesso file: niente da spostare.";
        }

        if (!File.Exists(keeper))
        {
            return $"L'originale tenuto «{keeper}» non esiste più: per sicurezza la copia resta dov'è. Rifai la ricerca.";
        }

        // La chiave contiene dimensione e data di modifica: se l'originale cambia, l'hash si ricalcola.
        string? keeperHash;
        try
        {
            var info = new FileInfo(keeper);
            var fingerprint = new KeeperFingerprint(Path.GetFullPath(keeper).ToUpperInvariant(), info.Length, info.LastWriteTimeUtc);
            if (!keeperHashCache.TryGetValue(fingerprint, out keeperHash))
            {
                keeperHash = TryComputeFullSha256(keeper);
                keeperHashCache[fingerprint] = keeperHash;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            keeperHash = null;
        }

        if (keeperHash is null)
        {
            return $"Non riesco a leggere l'originale tenuto «{keeper}»: per sicurezza la copia resta dov'è.";
        }

        if (!string.Equals(keeperHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            return "L'originale tenuto è cambiato dopo l'analisi (SHA256 diverso): per sicurezza la copia resta dov'è. Rifai la ricerca.";
        }

        return null;
    }

    /// <summary>Identità di un originale per riusare il suo SHA256: percorso, dimensione e data di modifica.</summary>
    private readonly record struct KeeperFingerprint(string FullPath, long Length, DateTime LastWriteTimeUtc);

    /// <summary>Cartelle da scansionare senza doppioni: una cartella dentro un'altra già scelta si toglie.</summary>
    internal static List<string> NormalizeScanRoots(IEnumerable<string> folders)
    {
        var normalized = folders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f.Length)
            .ToList();

        var roots = new List<string>();
        foreach (string folder in normalized)
        {
            if (!roots.Any(r => IsSameOrInside(folder, r)))
            {
                roots.Add(folder);
            }
        }

        return roots;
    }

    private void CollectFiles(
        string rootFolder,
        long minFileSizeBytes,
        Dictionary<long, List<FileInfo>> filesBySize,
        HashSet<string> seenFiles,
        CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(rootFolder);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string current = stack.Pop();

            try
            {
                var dir = new DirectoryInfo(current);
                if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                foreach (var file in dir.EnumerateFiles())
                {
                    ct.ThrowIfCancellationRequested();
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if (file.Length < minFileSizeBytes)
                    {
                        continue;
                    }

                    if (!seenFiles.Add(file.FullName))
                    {
                        continue;
                    }

                    if (!filesBySize.TryGetValue(file.Length, out var list))
                    {
                        list = new List<FileInfo>();
                        filesBySize[file.Length] = list;
                    }

                    list.Add(file);
                }

                foreach (var sub in dir.EnumerateDirectories())
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if (IsExcludedSubdirectory(sub))
                    {
                        continue;
                    }

                    stack.Push(sub.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Cartelle di sistema o non accessibili si saltano senza bloccare la scansione.
            }
        }
    }

    private bool IsExcludedRoot(string fullPath)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        string quarantineRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_quarantineService.RootDirectory));
        if (IsSameOrInside(normalized, quarantineRoot))
        {
            return true;
        }

        string? folderName = Path.GetFileName(normalized);
        if (!string.IsNullOrEmpty(folderName) && ExcludedFolderNames.Contains(folderName))
        {
            return true;
        }

        return IsSystemSpecialFolder(normalized);
    }

    private bool IsExcludedSubdirectory(DirectoryInfo sub)
    {
        if (ExcludedFolderNames.Contains(sub.Name))
        {
            return true;
        }

        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sub.FullName));
        string quarantineRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_quarantineService.RootDirectory));
        if (IsSameOrInside(normalized, quarantineRoot))
        {
            return true;
        }

        return IsSystemSpecialFolder(normalized);
    }

    private static bool IsSystemSpecialFolder(string normalizedPath)
    {
        string[] systemRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ];

        foreach (string sys in systemRoots)
        {
            if (string.IsNullOrWhiteSpace(sys))
            {
                continue;
            }

            string trimmedSys = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sys));
            if (string.Equals(normalizedPath, trimmedSys, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSameOrInside(string path, string parent)
    {
        if (string.Equals(path, parent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Una radice come «C:\» finisce già con il separatore.
        string parentWithSep = parent.EndsWith(Path.DirectorySeparatorChar) || parent.EndsWith(Path.AltDirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return path.StartsWith(parentWithSep, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryComputePartialSha256(string path)
    {
        try
        {
            byte[] buffer = new byte[PartialHashBytes];
            using var stream = File.OpenRead(path);
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            byte[] hash = SHA256.HashData(buffer.AsSpan(0, totalRead));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    private static string? TryComputeFullSha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            byte[] hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }
}
