using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace W11CleanerTool.Core.Safety;

/// <summary>Voce salvata nel manifest di una cartella di quarantena.</summary>
public sealed class QuarantineItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string QuarantinedPath { get; set; } = "";
    public DateTime QuarantinedAtUtc { get; set; } = DateTime.UtcNow;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";

    /// <summary>
    /// True se il file veniva da un altro disco: è stato copiato (e verificato) sul disco della quarantena,
    /// quindi lo spazio si è liberato sul disco di origine ma è occupato su quello della quarantena.
    /// </summary>
    public bool FromOtherDrive { get; set; }
}

/// <summary>Il file è cambiato rispetto all'impronta SHA256 attesa: non si sposta.</summary>
public sealed class QuarantineHashMismatchException : IOException
{
    public QuarantineHashMismatchException(string message)
        : base(message)
    {
    }
}

public sealed record QuarantineRestoreResult(bool Success, string Message, string? RestoredPath = null);

/// <summary>
/// Gestisce la quarantena dei file utente in %LOCALAPPDATA%\W11-cleaner-tool\quarantine\&lt;sessione&gt;\
/// salvando manifest.json con percorso originale, data UTC, dimensione e SHA256.
/// I file di un altro disco si copiano, si verificano con SHA256 e solo dopo si cancella l'originale;
/// prima si controlla che sul disco della quarantena ci sia spazio.
/// </summary>
public sealed class QuarantineService
{
    private const string ManifestFileName = "manifest.json";

    /// <summary>Spazio che deve restare libero sul disco della quarantena dopo la copia di un file da un altro disco.</summary>
    internal const long FreeSpaceMarginBytes = 1024L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly Func<string, string, bool> _isSameVolume;
    private readonly Func<string, long?> _freeSpace;

    public QuarantineService(string? rootDirectory = null)
        : this(rootDirectory, null, null)
    {
    }

    /// <summary>Per i test: permette di simulare un altro disco e lo spazio libero.</summary>
    internal QuarantineService(
        string? rootDirectory,
        Func<string, string, bool>? isSameVolume,
        Func<string, long?>? freeSpace)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? DefaultRootDirectory
            : Path.GetFullPath(rootDirectory);
        _isSameVolume = isSameVolume ?? ((a, b) => IsSameVolume(a, b));
        _freeSpace = freeSpace ?? (d => GetFreeSpace(d));
    }

    public static string DefaultRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "W11-cleaner-tool",
        "quarantine");

    public string RootDirectory { get; }

    /// <summary>
    /// Sposta un file nella quarantena di sessione registrandolo nel manifest.json.
    /// Con <paramref name="expectedSha256"/> il file si sposta solo se la sua impronta è ancora quella attesa.
    /// </summary>
    public QuarantineItem MoveToQuarantine(string sourcePath, string? sessionId = null, string? expectedSha256 = null)
        => MoveToQuarantine(sourcePath, sessionId, DateTime.UtcNow, expectedSha256);

    internal QuarantineItem MoveToQuarantine(string sourcePath, string? sessionId, DateTime timestampUtc, string? expectedSha256 = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("Percorso sorgente vuoto.", nameof(sourcePath));
        }

        string fullSource = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSource))
        {
            throw new FileNotFoundException("Il file da mettere in quarantena non esiste.", fullSource);
        }

        string safeSession = SanitizeSessionId(sessionId);
        string id = Guid.NewGuid().ToString("N");
        var fileInfo = new FileInfo(fullSource);
        long size = fileInfo.Length;
        string sha256 = ComputeSha256(fullSource);
        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            !string.Equals(sha256, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new QuarantineHashMismatchException(
                "Il file è cambiato dopo l'analisi (SHA256 diverso): per sicurezza resta dov'è. Rifai la ricerca.");
        }

        string sessionDir = Path.Combine(RootDirectory, safeSession);
        string destName = $"{id}_{Path.GetFileName(fullSource)}";
        string destPath = Path.Combine(sessionDir, destName);
        bool fromOtherDrive = !_isSameVolume(fullSource, RootDirectory);

        lock (_sync)
        {
            Directory.CreateDirectory(sessionDir);
            if (fromOtherDrive)
            {
                CopyVerifyAndDelete(fullSource, destPath, size, sha256);
            }
            else
            {
                File.Move(fullSource, destPath);
            }

            var item = new QuarantineItem
            {
                Id = id,
                SessionId = safeSession,
                OriginalPath = fullSource,
                QuarantinedPath = destPath,
                QuarantinedAtUtc = timestampUtc,
                SizeBytes = size,
                Sha256 = sha256,
                FromOtherDrive = fromOtherDrive,
            };

            string manifestPath = Path.Combine(sessionDir, ManifestFileName);
            var items = ReadManifestUnsafe(manifestPath);
            items.Add(item);
            WriteManifestUnsafe(manifestPath, items);
            return item;
        }
    }

    /// <summary>Elenca tutti gli elementi attualmente presenti nei manifest di quarantena.</summary>
    public List<QuarantineItem> ListItems()
    {
        lock (_sync)
        {
            if (!Directory.Exists(RootDirectory))
            {
                return new List<QuarantineItem>();
            }

            var all = new List<QuarantineItem>();
            foreach (string manifestPath in Directory.EnumerateFiles(RootDirectory, ManifestFileName, SearchOption.AllDirectories))
            {
                all.AddRange(ReadManifestUnsafe(manifestPath));
            }

            return all.OrderByDescending(i => i.QuarantinedAtUtc).ToList();
        }
    }

    /// <summary>Ripristina un file dalla quarantena al suo percorso originale (accetta Id o QuarantinedPath).</summary>
    public QuarantineRestoreResult RestoreItem(string idOrQuarantinedPath)
    {
        if (string.IsNullOrWhiteSpace(idOrQuarantinedPath))
        {
            return new QuarantineRestoreResult(false, "Identificativo quarantena mancante.");
        }

        lock (_sync)
        {
            if (!Directory.Exists(RootDirectory))
            {
                return new QuarantineRestoreResult(false, "La cartella di quarantena è vuota.");
            }

            foreach (string manifestPath in Directory.EnumerateFiles(RootDirectory, ManifestFileName, SearchOption.AllDirectories))
            {
                var items = ReadManifestUnsafe(manifestPath);
                var item = items.FirstOrDefault(i =>
                    string.Equals(i.Id, idOrQuarantinedPath, StringComparison.Ordinal) ||
                    string.Equals(i.QuarantinedPath, idOrQuarantinedPath, StringComparison.OrdinalIgnoreCase));
                if (item is null)
                {
                    continue;
                }

                if (!File.Exists(item.QuarantinedPath))
                {
                    return new QuarantineRestoreResult(false, "Il file non è più presente nella cartella di quarantena.");
                }

                // Prima di rimetterlo a posto si controlla che sia ancora quello messo in quarantena.
                if (!string.IsNullOrWhiteSpace(item.Sha256))
                {
                    string currentHash = ComputeSha256(item.QuarantinedPath);
                    if (!string.Equals(currentHash, item.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return new QuarantineRestoreResult(
                            false,
                            "Il file in quarantena non è più uguale a quello salvato (SHA256 diverso): non lo ripristino in automatico. " +
                            $"Lo trovi in {item.QuarantinedPath}; controllalo e, se ti serve, copialo tu.");
                    }
                }

                string targetPath = ResolveRestoreTargetPath(item.OriginalPath);
                string? targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                File.Move(item.QuarantinedPath, targetPath);
                items.Remove(item);
                SaveOrCleanupManifestUnsafe(manifestPath, items);

                return new QuarantineRestoreResult(true, $"File ripristinato in {targetPath}.", targetPath);
            }

            return new QuarantineRestoreResult(false, "Elemento non trovato in quarantena.");
        }
    }

    /// <summary>Restituisce gli elementi in quarantena da almeno <paramref name="days"/> giorni.</summary>
    public List<QuarantineItem> GetExpiredItems(int days = 30)
        => GetExpiredItems(days, DateTime.UtcNow);

    public List<QuarantineItem> GetExpiredItems(int days, DateTime asOfUtc)
    {
        if (days < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(days));
        }

        DateTime cutoff = asOfUtc.AddDays(-days);
        return ListItems()
            .Where(i => i.QuarantinedAtUtc <= cutoff)
            .ToList();
    }

    /// <summary>Elimina definitivamente dalla quarantena gli elementi con gli Id indicati.</summary>
    public int PurgeItems(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var idSet = new HashSet<string>(ids.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
        if (idSet.Count == 0)
        {
            return 0;
        }

        int purged = 0;
        lock (_sync)
        {
            if (!Directory.Exists(RootDirectory))
            {
                return 0;
            }

            foreach (string manifestPath in Directory.EnumerateFiles(RootDirectory, ManifestFileName, SearchOption.AllDirectories).ToList())
            {
                var items = ReadManifestUnsafe(manifestPath);
                var toRemove = items.Where(i => idSet.Contains(i.Id)).ToList();
                if (toRemove.Count == 0)
                {
                    continue;
                }

                foreach (var item in toRemove)
                {
                    if (File.Exists(item.QuarantinedPath))
                    {
                        File.Delete(item.QuarantinedPath);
                    }

                    items.Remove(item);
                    purged++;
                }

                SaveOrCleanupManifestUnsafe(manifestPath, items);
            }
        }

        return purged;
    }

    private void CopyVerifyAndDelete(string source, string destination, long size, string expectedSha256)
    {
        long? free = _freeSpace(RootDirectory);
        if (free is long available && available - size < FreeSpaceMarginBytes)
        {
            throw new IOException(
                $"Spazio insufficiente sul disco della quarantena: servono {Heuristics.FormatBytes(size)} più " +
                $"{Heuristics.FormatBytes(FreeSpaceMarginBytes)} di margine, liberi {Heuristics.FormatBytes(available)}. Il file resta dov'è.");
        }

        File.Copy(source, destination, overwrite: false);
        string copiedHash;
        try
        {
            copiedHash = ComputeSha256(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(destination);
            throw;
        }

        if (!string.Equals(copiedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(destination);
            throw new QuarantineHashMismatchException("La copia in quarantena non è uguale all'originale (SHA256 diverso): l'originale resta dov'è.");
        }

        try
        {
            File.Delete(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(destination);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Resta una copia in più nella quarantena: l'originale è comunque al suo posto.
        }
    }

    internal static bool IsSameVolume(string pathA, string pathB)
    {
        string? rootA = Path.GetPathRoot(Path.GetFullPath(pathA));
        string? rootB = Path.GetPathRoot(Path.GetFullPath(pathB));
        return !string.IsNullOrEmpty(rootA) && string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
    }

    private static long? GetFreeSpace(string directory)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(directory));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string ResolveRestoreTargetPath(string originalPath)
    {
        if (!File.Exists(originalPath))
        {
            return originalPath;
        }

        string dir = Path.GetDirectoryName(originalPath) ?? "";
        string stem = Path.GetFileNameWithoutExtension(originalPath);
        string ext = Path.GetExtension(originalPath);

        for (int index = 1; index < 10_000; index++)
        {
            string suffix = index == 1 ? " (ripristinato)" : $" (ripristinato {index})";
            string candidate = Path.Combine(dir, stem + suffix + ext);
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(dir, $"{stem}_{Guid.NewGuid():N}{ext}");
    }

    private static string SanitizeSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(sessionId.Length);
        foreach (char ch in sessionId.Trim())
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        string cleaned = sb.ToString();
        return string.IsNullOrWhiteSpace(cleaned) ? Guid.NewGuid().ToString("N") : cleaned;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static List<QuarantineItem> ReadManifestUnsafe(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return new List<QuarantineItem>();
        }

        try
        {
            string json = File.ReadAllText(manifestPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<QuarantineItem>();
            }

            return JsonSerializer.Deserialize<List<QuarantineItem>>(json, JsonOptions) ?? new List<QuarantineItem>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new List<QuarantineItem>();
        }
    }

    private static void WriteManifestUnsafe(string manifestPath, List<QuarantineItem> items)
    {
        string tempPath = manifestPath + ".tmp";
        string json = JsonSerializer.Serialize(items, JsonOptions);
        File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, manifestPath, overwrite: true);
    }

    private static void SaveOrCleanupManifestUnsafe(string manifestPath, List<QuarantineItem> items)
    {
        if (items.Count > 0)
        {
            WriteManifestUnsafe(manifestPath, items);
            return;
        }

        if (File.Exists(manifestPath))
        {
            File.Delete(manifestPath);
        }

        string? sessionDir = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrEmpty(sessionDir) && Directory.Exists(sessionDir) && !Directory.EnumerateFileSystemEntries(sessionDir).Any())
        {
            Directory.Delete(sessionDir);
        }
    }
}
