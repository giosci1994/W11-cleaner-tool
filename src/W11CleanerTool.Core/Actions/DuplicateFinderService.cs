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

        foreach (string folder in folders
                     .Where(f => !string.IsNullOrWhiteSpace(f))
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder) || IsExcludedRoot(folder))
            {
                continue;
            }

            CollectFiles(folder, effectiveMinSize, filesBySize, cancellationToken);
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

        RestorePointResult? restorePoint = createRestorePoint
            ? _restorePointService.Create("W11-cleaner-tool: quarantena duplicati")
            : null;

        var session = new ActionSession
        {
            TimestampUtc = DateTime.UtcNow,
            RestorePointCreated = restorePoint?.Status == RestorePointStatus.Created,
        };

        foreach (var change in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (change.ActionKind != ActionKind.QuarantineDuplicate)
            {
                outcomes.Add(new ActionItemOutcome(change.Id, change.Title, Succeeded: false, "Azione non supportata da DuplicateFinderService."));
                continue;
            }

            try
            {
                var item = _quarantineService.MoveToQuarantine(change.TargetKey, session.SessionId);
                outcomes.Add(new ActionItemOutcome(
                    change.Id,
                    change.Title,
                    Succeeded: true,
                    $"Spostato in quarantena ({Heuristics.FormatBytes(item.SizeBytes)}).",
                    BytesFreed: item.SizeBytes));

                session.Entries.Add(new ActionLogEntry
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
            catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException or SecurityException)
            {
                outcomes.Add(new ActionItemOutcome(
                    change.Id,
                    change.Title,
                    Succeeded: false,
                    $"Impossibile spostare «{change.Title}»: {ex.Message}"));
            }
        }

        if (session.Entries.Count > 0)
        {
            _actionLog.AddSession(session);
            return new ActionExecutionResult(session, restorePoint, outcomes);
        }

        return new ActionExecutionResult(null, restorePoint, outcomes);
    }

    private void CollectFiles(
        string rootFolder,
        long minFileSizeBytes,
        Dictionary<long, List<FileInfo>> filesBySize,
        CancellationToken ct)
    {
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

        return path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(parent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
