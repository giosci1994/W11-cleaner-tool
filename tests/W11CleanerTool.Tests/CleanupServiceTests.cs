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

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
