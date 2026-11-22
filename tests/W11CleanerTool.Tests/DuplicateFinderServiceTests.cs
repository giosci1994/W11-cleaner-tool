using W11CleanerTool.Core.Actions;
using W11CleanerTool.Core.Safety;

namespace W11CleanerTool.Tests;

public sealed class DuplicateFinderServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "w11ct-dup-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Scan_FindsExactDuplicatesOnly_IgnoresSameSizeDifferentContent()
    {
        string scanRoot = Path.Combine(_tempDir, "scan");
        string subDir = Path.Combine(scanRoot, "sub");
        Directory.CreateDirectory(subDir);

        // Duplicati più grandi di 4 KB per verificare sia il primo blocco che lo SHA256 completo.
        byte[] payloadA = new byte[6000];
        Array.Fill(payloadA, (byte)'A');
        byte[] payloadB = new byte[6000];
        Array.Fill(payloadB, (byte)'A');
        payloadB[5500] = (byte)'Z'; // Stessa taglia e stessi primi 4 KB, diverso dopo!

        string original = Path.Combine(scanRoot, "orig.dat");
        string copy = Path.Combine(subDir, "copia.dat");
        string samePrefixDifferentTail = Path.Combine(scanRoot, "diverso.dat");
        string differentSize = Path.Combine(scanRoot, "piccolo.dat");

        File.WriteAllBytes(original, payloadA);
        File.WriteAllBytes(copy, payloadA);
        File.WriteAllBytes(samePrefixDifferentTail, payloadB);
        File.WriteAllText(differentSize, "diverso");

        File.SetLastWriteTimeUtc(original, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(copy, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        var quarantine = new QuarantineService(Path.Combine(_tempDir, "quarantine"));
        var log = new ActionLog(Path.Combine(_tempDir, "history.json"));
        var finder = new DuplicateFinderService(quarantine, log);

        var changes = finder.Scan([scanRoot]);

        Assert.Single(changes);
        Assert.Equal(copy, changes[0].TargetKey);
        Assert.Equal(ActionKind.QuarantineDuplicate, changes[0].ActionKind);
        Assert.Equal(original, changes[0].Metadata["OriginalKeptPath"]);

        // Applica in quarantena e poi ripristina tramite UndoCoordinator.
        var apply = finder.Apply(changes);
        Assert.Equal(1, apply.SucceededCount);
        Assert.True(File.Exists(original));
        Assert.False(File.Exists(copy));

        var coordinator = new UndoCoordinator(log, quarantine);
        var undoOutcomes = coordinator.UndoSession(apply.Session!.SessionId);
        Assert.Single(undoOutcomes);
        Assert.True(undoOutcomes[0].Succeeded);
        Assert.True(File.Exists(copy));
    }

    [Fact]
    public void Scan_ExcludesSystemFoldersLikeAppDataAndWindows()
    {
        string scanRoot = Path.Combine(_tempDir, "userdocs");
        string appDataSub = Path.Combine(scanRoot, "AppData");
        string windowsSub = Path.Combine(scanRoot, "Windows");
        Directory.CreateDirectory(appDataSub);
        Directory.CreateDirectory(windowsSub);

        File.WriteAllText(Path.Combine(scanRoot, "doc.txt"), "testo uguale");
        File.WriteAllText(Path.Combine(appDataSub, "doc.txt"), "testo uguale");
        File.WriteAllText(Path.Combine(windowsSub, "doc.txt"), "testo uguale");

        var quarantine = new QuarantineService(Path.Combine(_tempDir, "quarantine"));
        var finder = new DuplicateFinderService(quarantine, new ActionLog(Path.Combine(_tempDir, "history.json")));

        var changes = finder.Scan([scanRoot]);
        Assert.Empty(changes);
    }

    [Fact]
    public void Scan_RespectsCancellationToken()
    {
        string scanRoot = Path.Combine(_tempDir, "cancel");
        Directory.CreateDirectory(scanRoot);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var finder = new DuplicateFinderService(
            new QuarantineService(Path.Combine(_tempDir, "q")),
            new ActionLog(Path.Combine(_tempDir, "h.json")));

        Assert.Throws<OperationCanceledException>(() => finder.Scan([scanRoot], cts.Token));
    }

    private (DuplicateFinderService Finder, ActionLog Log, string Original, string Copy) PrepareDuplicatePair(string name)
    {
        string scanRoot = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(scanRoot);
        string original = Path.Combine(scanRoot, "orig.txt");
        string copy = Path.Combine(scanRoot, "copia.txt");
        File.WriteAllText(original, "contenuto identico");
        File.WriteAllText(copy, "contenuto identico");
        File.SetLastWriteTimeUtc(original, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(copy, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        var log = new ActionLog(Path.Combine(_tempDir, name + "-history.json"));
        var finder = new DuplicateFinderService(new QuarantineService(Path.Combine(_tempDir, name + "-q")), log);
        return (finder, log, original, copy);
    }

    [Fact]
    public void Scan_NestedFolders_DoNotReportFileAsCopyOfItself()
    {
        string scanRoot = Path.Combine(_tempDir, "nested");
        string subDir = Path.Combine(scanRoot, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "unico.txt"), "un solo file");

        var finder = new DuplicateFinderService(
            new QuarantineService(Path.Combine(_tempDir, "q")),
            new ActionLog(Path.Combine(_tempDir, "h.json")));

        Assert.Empty(finder.Scan([scanRoot, subDir, scanRoot + Path.DirectorySeparatorChar]));

        File.WriteAllText(Path.Combine(scanRoot, "copia.txt"), "un solo file");
        Assert.Single(finder.Scan([subDir, scanRoot]));
    }

    [Fact]
    public void NormalizeScanRoots_RemovesFoldersInsideOthers()
    {
        string a = Path.Combine(_tempDir, "a");
        string aSub = Path.Combine(a, "sub");
        string ab = Path.Combine(_tempDir, "ab");

        var roots = DuplicateFinderService.NormalizeScanRoots([aSub, a, ab, a + Path.DirectorySeparatorChar]);
        Assert.Equal(2, roots.Count);
        Assert.Contains(a, roots);
        Assert.Contains(ab, roots);
    }

    [Fact]
    public void Apply_KeeperMissing_LeavesCopyInPlace()
    {
        var (finder, log, original, copy) = PrepareDuplicatePair("keeper-missing");
        var changes = finder.Scan([Path.GetDirectoryName(original)!]);
        Assert.Single(changes);

        File.Delete(original);
        var result = finder.Apply(changes);

        Assert.Equal(1, result.FailedCount);
        Assert.Contains("non esiste più", result.Outcomes[0].Message);
        Assert.True(File.Exists(copy));
        Assert.Empty(log.ReadSessions());
    }

    [Fact]
    public void Apply_KeeperChangedAfterScan_LeavesCopyInPlace()
    {
        var (finder, _, original, copy) = PrepareDuplicatePair("keeper-changed");
        var changes = finder.Scan([Path.GetDirectoryName(original)!]);

        File.WriteAllText(original, "contenuto cambiato");
        var result = finder.Apply(changes);

        Assert.Equal(1, result.FailedCount);
        Assert.True(File.Exists(copy));
    }

    [Fact]
    public void Apply_CopyChangedAfterScan_LeavesCopyInPlace()
    {
        var (finder, _, original, copy) = PrepareDuplicatePair("copy-changed");
        var changes = finder.Scan([Path.GetDirectoryName(original)!]);

        File.WriteAllText(copy, "contenuto CAMBIATO"); // stessa lunghezza, contenuto diverso
        var result = finder.Apply(changes);

        Assert.Equal(1, result.FailedCount);
        Assert.Contains("SHA256", result.Outcomes[0].Message);
        Assert.True(File.Exists(copy));
        Assert.True(File.Exists(original));
    }

    [Fact]
    public void Apply_RecordsEachMoveInHistoryRightAway()
    {
        var (finder, log, original, copy) = PrepareDuplicatePair("history");
        var changes = finder.Scan([Path.GetDirectoryName(original)!]);

        var result = finder.Apply(changes);
        Assert.Equal(1, result.SucceededCount);
        Assert.False(File.Exists(copy));

        var sessions = log.ReadSessions();
        Assert.Single(sessions);
        Assert.Equal(result.Session!.SessionId, sessions[0].SessionId);
        Assert.Single(sessions[0].Entries);
        Assert.False(string.IsNullOrWhiteSpace(sessions[0].Entries[0].QuarantinePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
