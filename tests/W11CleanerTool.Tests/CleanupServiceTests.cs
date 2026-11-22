using System.Diagnostics;
using W11CleanerTool.Core.Actions;
using W11CleanerTool.Core.Safety;

namespace W11CleanerTool.Tests;

public sealed class CleanupServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "w11ct-clean-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ScanAndApply_CleansTempFolderAndLogsSession()
    {
        string targetFolder = Path.Combine(_tempDir, "fake-temp");
        string subFolder = Path.Combine(targetFolder, "nested");
        Directory.CreateDirectory(subFolder);

        string f1 = Path.Combine(targetFolder, "a.tmp");
        string f2 = Path.Combine(subFolder, "b.tmp");
        File.WriteAllBytes(f1, new byte[1024]);
        File.WriteAllBytes(f2, new byte[2048]);

        var log = new ActionLog(Path.Combine(_tempDir, "history.json"));
        var service = new CleanupService(
            log,
            new RestorePointService(),
            () =>
            [
                new CleanupTarget(
                    "test-temp",
                    "Temp di prova",
                    "File temporanei di prova",
                    ActionKind.CleanTempFiles,
                    [targetFolder]),
            ]);

        var changes = service.Scan();
        Assert.Single(changes);
        Assert.Equal(3072, changes[0].EstimatedBytes);
        Assert.True(changes[0].IsSelected);

        var result = service.Apply(changes);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(3072, result.TotalBytesFreed);
        Assert.False(File.Exists(f1));
        Assert.False(File.Exists(f2));
        Assert.True(Directory.Exists(targetFolder));
        Assert.Single(log.ReadSessions());
    }

    [Fact]
    public void ScanAndApply_SkipsLockedFileWithoutCrashing()
    {
        string targetFolder = Path.Combine(_tempDir, "locked-temp");
        Directory.CreateDirectory(targetFolder);

        string freeFile = Path.Combine(targetFolder, "free.tmp");
        string lockedFile = Path.Combine(targetFolder, "in-use.tmp");
        File.WriteAllBytes(freeFile, new byte[512]);
        File.WriteAllBytes(lockedFile, new byte[512]);

        var service = new CleanupService(
            new ActionLog(Path.Combine(_tempDir, "history.json")),
            new RestorePointService(),
            () =>
            [
                new CleanupTarget(
                    "locked-test",
                    "Temp bloccato",
                    "Test file in uso",
                    ActionKind.CleanTempFiles,
                    [targetFolder]),
            ]);

        var changes = service.Scan();
        using var lockHandle = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = service.Apply(changes);
        Assert.Single(result.Outcomes);
        Assert.Equal(1, result.SucceededCount);
        Assert.False(File.Exists(freeFile));
        Assert.True(File.Exists(lockedFile));
    }

    [Fact]
    public void BrowserCacheDiscovery_OnlyReturnsCacheFoldersNeverCookiesOrPasswords()
    {
        string chromiumRoot = Path.Combine(_tempDir, "Edge", "User Data", "Default");
        string cacheDir = Path.Combine(chromiumRoot, "Cache");
        string codeCacheDir = Path.Combine(chromiumRoot, "Code Cache");
        string cookiesFile = Path.Combine(chromiumRoot, "Cookies");
        string loginDataFile = Path.Combine(chromiumRoot, "Login Data");
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(codeCacheDir);
        File.WriteAllText(cookiesFile, "keep-cookies");
        File.WriteAllText(loginDataFile, "keep-passwords");

        var edgeDirs = CleanupService.FindChromiumCacheDirectories(Path.Combine(_tempDir, "Edge", "User Data"));
        Assert.Equal(2, edgeDirs.Count);
        Assert.Contains(cacheDir, edgeDirs);
        Assert.Contains(codeCacheDir, edgeDirs);

        string firefoxProfile = Path.Combine(_tempDir, "Firefox", "Profiles", "abc.default-release");
        string ffCache2 = Path.Combine(firefoxProfile, "cache2");
        string ffLogins = Path.Combine(firefoxProfile, "logins.json");
        Directory.CreateDirectory(ffCache2);
        File.WriteAllText(ffLogins, "keep-logins");

        var ffDirs = CleanupService.FindFirefoxCacheDirectories(Path.Combine(_tempDir, "Firefox", "Profiles"));
        Assert.Single(ffDirs);
        Assert.Equal(ffCache2, ffDirs[0]);
    }

    [Fact]
    public void RecycleBin_AlreadyEmptyHResult_CountsAsSuccess()
    {
        var service = new CleanupService(
            new ActionLog(Path.Combine(_tempDir, "history.json")),
            new RestorePointService(),
            () =>
            [
                new CleanupTarget(
                    "bin",
                    "Cestino",
                    "Svuota cestino",
                    ActionKind.EmptyRecycleBin,
                    [],
                    IsRecycleBin: true),
            ],
            queryRecycleBin: () => (100L, 1L),
            emptyRecycleBin: () => unchecked((int)0x8000FFFF));

        var changes = service.Scan();
        var result = service.Apply(changes);
        Assert.Equal(1, result.SucceededCount);
    }

    private CleanupService CreateService(
        string historyName,
        CleanupTarget target,
        RestorePointService? restorePoints = null,
        IReadOnlyList<ProtectedFolder>? protectedFolders = null,
        Func<string?>? windowsUpdateBusyReason = null,
        IReadOnlyList<string>? keepSubfolders = null)
        => new(
            new ActionLog(Path.Combine(_tempDir, historyName)),
            restorePoints ?? new RestorePointService(),
            () => [target],
            protectedFolders: protectedFolders,
            windowsUpdateBusyReason: windowsUpdateBusyReason,
            keepSubfolders: keepSubfolders);

    private static CleanupTarget Target(string folder, ActionKind kind = ActionKind.CleanTempFiles, TimeSpan minAge = default)
        => new("test-target", "Cartella di prova", "File di prova", kind, [folder], MinFileAge: minAge);

    [Fact]
    public void GetUnsafeDirectoryReason_BlocksDiskRootProfileAndPersonalFolders()
    {
        string fakeProfile = Path.Combine(_tempDir, "profilo");
        string fakeDocs = Path.Combine(fakeProfile, "Documenti");
        string fakeWindows = Path.Combine(_tempDir, "win");
        var protectedFolders = new List<ProtectedFolder>
        {
            new(fakeProfile, "il profilo utente", false),
            new(fakeDocs, "la cartella Documenti", true),
            new(fakeWindows, "la cartella di Windows", false),
        };

        Assert.NotNull(CleanupService.GetUnsafeDirectoryReason(Path.GetPathRoot(_tempDir)!, protectedFolders));
        Assert.NotNull(CleanupService.GetUnsafeDirectoryReason(fakeProfile, protectedFolders));
        Assert.NotNull(CleanupService.GetUnsafeDirectoryReason(_tempDir, protectedFolders)); // contiene il profilo
        Assert.NotNull(CleanupService.GetUnsafeDirectoryReason(fakeDocs, protectedFolders));
        Assert.NotNull(CleanupService.GetUnsafeDirectoryReason(Path.Combine(fakeDocs, "sotto"), protectedFolders));
        Assert.NotNull(CleanupService.GetUnsafeDirectoryReason(fakeWindows, protectedFolders));

        Assert.Null(CleanupService.GetUnsafeDirectoryReason(Path.Combine(fakeWindows, "Temp"), protectedFolders));
        Assert.Null(CleanupService.GetUnsafeDirectoryReason(Path.Combine(fakeProfile, "AppData", "Local", "Temp"), protectedFolders));
    }

    [Fact]
    public void DefaultProtectedFolders_AllowRealTempButBlockProfileAndWindows()
    {
        var defaults = CleanupService.BuildDefaultProtectedFolders();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.Null(CleanupService.GetUnsafeDirectoryReason(Path.GetTempPath(), defaults));
        Assert.Null(CleanupService.GetUnsafeDirectoryReason(Path.Combine(windows, "Temp"), defaults));
        Assert.NotNull(CleanupService.GetUnsafeDirectoryReason(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), defaults));
        Assert.NotNull(CleanupService.GetUnsafeDirectoryReason(windows, defaults));
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents))
        {
            Assert.NotNull(CleanupService.GetUnsafeDirectoryReason(documents, defaults));
        }
    }

    [Fact]
    public void ScanAndApply_TempPointingToProtectedFolder_IsSkippedWithReason()
    {
        string danger = Path.Combine(_tempDir, "documenti-finti");
        Directory.CreateDirectory(danger);
        string personal = Path.Combine(danger, "tesi.docx");
        File.WriteAllText(personal, "non cancellare");

        var log = new ActionLog(Path.Combine(_tempDir, "history.json"));
        var service = new CleanupService(
            log,
            new RestorePointService(),
            () => [Target(danger)],
            protectedFolders: [new ProtectedFolder(danger, "la cartella Documenti", true)]);

        var changes = service.Scan();
        Assert.Single(changes);
        Assert.False(changes[0].IsSelected);
        Assert.Equal(0, changes[0].EstimatedBytes);
        Assert.Contains("saltata per sicurezza", changes[0].Explanation);

        // Anche selezionata a forza, non si svuota.
        var result = service.Apply([changes[0] with { IsSelected = true }]);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains("sicurezza", result.Outcomes[0].Message);
        Assert.True(File.Exists(personal));
        Assert.Empty(log.ReadSessions());
    }

    [Fact]
    public void Apply_SkipsFilesTouchedInLast24Hours()
    {
        string folder = Path.Combine(_tempDir, "temp-recenti");
        Directory.CreateDirectory(folder);
        string oldFile = Path.Combine(folder, "vecchio.tmp");
        string newFile = Path.Combine(folder, "nuovo.tmp");
        File.WriteAllBytes(oldFile, new byte[700]);
        File.WriteAllBytes(newFile, new byte[300]);
        DateTime threeDaysAgo = DateTime.UtcNow.AddDays(-3);
        File.SetCreationTimeUtc(oldFile, threeDaysAgo);
        File.SetLastWriteTimeUtc(oldFile, threeDaysAgo);

        var service = CreateService("history.json", Target(folder, minAge: TimeSpan.FromHours(24)));
        var changes = service.Scan();
        Assert.Single(changes);
        Assert.Equal(700, changes[0].EstimatedBytes);

        var result = service.Apply(changes);
        Assert.Equal(1, result.SucceededCount);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
        Assert.Contains("recenti", result.Outcomes[0].Message);
    }

    [Fact]
    public void Apply_KeepsProtectedTempSubfolders()
    {
        string folder = Path.Combine(_tempDir, "temp-con-net");
        string keep = Path.Combine(folder, ".net");
        Directory.CreateDirectory(keep);
        string keptFile = Path.Combine(keep, "runtime.dll");
        string freeFile = Path.Combine(folder, "a.tmp");
        File.WriteAllText(keptFile, "serve all'app");
        File.WriteAllText(freeFile, "via");

        var service = CreateService("history.json", Target(folder), keepSubfolders: [keep]);
        var result = service.Apply(service.Scan());

        Assert.Equal(1, result.SucceededCount);
        Assert.True(File.Exists(keptFile));
        Assert.False(File.Exists(freeFile));
    }

    [Fact]
    public void Apply_DoesNotFollowJunctionsOrSymlinks()
    {
        string folder = Path.Combine(_tempDir, "temp-con-link");
        string outside = Path.Combine(_tempDir, "fuori");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(outside);
        string precious = Path.Combine(outside, "importante.txt");
        File.WriteAllText(precious, "non toccare");
        File.WriteAllText(Path.Combine(folder, "a.tmp"), "via");

        bool anyLink = false;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(folder, "symlink"), outside);
            anyLink = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Servono privilegi per i collegamenti simbolici: si prova con la junction.
        }

        anyLink |= TryCreateJunction(Path.Combine(folder, "junction"), outside);
        if (!anyLink)
        {
            return; // Il runner non permette di creare collegamenti: niente da provare.
        }

        var service = CreateService("history.json", Target(folder));
        service.Apply(service.Scan());

        Assert.True(File.Exists(precious));
        Assert.False(File.Exists(Path.Combine(folder, "a.tmp")));
    }

    [Fact]
    public void Apply_WindowsUpdateBusy_SkipsCacheWithReason()
    {
        string folder = Path.Combine(_tempDir, "wu-download");
        Directory.CreateDirectory(folder);
        string package = Path.Combine(folder, "update.cab");
        File.WriteAllText(package, "pacchetto");

        var service = CreateService(
            "history.json",
            Target(folder, ActionKind.CleanWindowsUpdateCache),
            windowsUpdateBusyReason: () => "Windows Update è attivo: riprova più tardi.");

        var result = service.Apply(service.Scan());
        Assert.Equal(1, result.FailedCount);
        Assert.Contains("riprova più tardi", result.Outcomes[0].Message);
        Assert.True(File.Exists(package));
    }

    [Fact]
    public void Apply_RestorePointRequestedButNotCreated_AppliesNothing()
    {
        string folder = Path.Combine(_tempDir, "temp-senza-punto");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "a.tmp");
        File.WriteAllText(file, "via");

        var log = new ActionLog(Path.Combine(_tempDir, "history.json"));
        var noPoint = new RestorePointService(_ => new RestorePointResult(
            RestorePointStatus.RecentPointExists,
            "Windows non ha creato un punto nuovo."));
        var service = new CleanupService(log, noPoint, () => [Target(folder)]);

        var changes = service.Scan();
        var stopped = service.Apply(changes, createRestorePoint: true);
        Assert.True(stopped.StoppedForMissingRestorePoint);
        Assert.Empty(stopped.Outcomes);
        Assert.True(File.Exists(file));
        Assert.Empty(log.ReadSessions());

        // Se l'utente risponde «Sì, continua senza», si riapplica senza punto.
        var applied = service.Apply(changes, createRestorePoint: false);
        Assert.False(applied.StoppedForMissingRestorePoint);
        Assert.False(File.Exists(file));
        Assert.False(log.ReadSessions()[0].RestorePointCreated);
    }

    [Fact]
    public void Apply_HistoryLocked_DoesNotStartAndDoesNotOverwrite()
    {
        string folder = Path.Combine(_tempDir, "temp-storico-bloccato");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "a.tmp");
        File.WriteAllText(file, "via");

        string historyPath = Path.Combine(_tempDir, "history.json");
        var log = new ActionLog(historyPath);
        log.AddSession(new ActionSession { SessionId = "vecchia", Entries = [new ActionLogEntry { Id = "e0", Title = "Prima" }] });
        string before = File.ReadAllText(historyPath);

        var service = new CleanupService(log, new RestorePointService(), () => [Target(folder)]);
        var changes = service.Scan();

        using (new FileStream(historyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<ActionLogUnavailableException>(() => service.Apply(changes));
        }

        Assert.True(File.Exists(file));
        Assert.Equal(before, File.ReadAllText(historyPath));
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return Directory.Exists(link);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        // Pulizia della cartella di prova: junction e collegamenti si tolgono come link
        // (senza entrarci), e un errore qui non deve far fallire il test.
        try
        {
            if (Directory.Exists(_tempDir))
            {
                DeleteTreeWithoutFollowingLinks(_tempDir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cartella temporanea del runner: si lascia lì.
        }
    }

    private static void DeleteTreeWithoutFollowingLinks(string dir)
    {
        foreach (string sub in Directory.GetDirectories(dir))
        {
            if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(sub, recursive: false);
            }
            else
            {
                DeleteTreeWithoutFollowingLinks(sub);
            }
        }

        foreach (string file in Directory.GetFiles(dir))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }

        Directory.Delete(dir, recursive: false);
    }
}
