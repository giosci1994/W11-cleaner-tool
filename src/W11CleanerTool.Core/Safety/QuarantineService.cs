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
}

public sealed record QuarantineRestoreResult(bool Success, string Message, string? RestoredPath = null);

/// <summary>
/// Gestisce la quarantena dei file utente in %LOCALAPPDATA%\W11-cleaner-tool\quarantine\&lt;sessione&gt;\
/// salvando manifest.json con percorso originale, data UTC, dimensione e SHA256.
/// </summary>
public sealed class QuarantineService
{
    private const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly object _sync = new();

    public QuarantineService(string? rootDirectory = null)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? DefaultRootDirectory
            : Path.GetFullPath(rootDirectory);
    }

    public static string DefaultRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "W11-cleaner-tool",
        "quarantine");

    public string RootDirectory { get; }

    /// <summary>Sposta un file nella quarantena di sessione registrandolo nel manifest.json.</summary>
    public QuarantineItem MoveToQuarantine(string sourcePath, string? sessionId = null)
        => MoveToQuarantine(sourcePath, sessionId, DateTime.UtcNow);

    internal QuarantineItem MoveToQuarantine(string sourcePath, string? sessionId, DateTime timestampUtc)
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

        string sessionDir = Path.Combine(RootDirectory, safeSession);
        string destName = $"{id}_{Path.GetFileName(fullSource)}";
        string destPath = Path.Combine(sessionDir, destName);

        lock (_sync)
        {
            Directory.CreateDirectory(sessionDir);
            File.Move(fullSource, destPath);

            var item = new QuarantineItem
            {
                Id = id,
                SessionId = safeSession,
                OriginalPath = fullSource,
                QuarantinedPath = destPath,
                QuarantinedAtUtc = timestampUtc,
                SizeBytes = size,
                Sha256 = sha256,
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
