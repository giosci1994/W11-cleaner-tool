using W11CleanerTool.Core.Actions;
using W11CleanerTool.Core.Safety;

namespace W11CleanerTool.Tests;

public sealed class UndoCoordinatorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "w11ct-undo-" + Guid.NewGuid().ToString("N"));

    private (ActionLog Log, UndoCoordinator Coordinator, QuarantineService Quarantine) Create()
    {
        var log = new ActionLog(Path.Combine(_tempDir, "history.json"));
        var quarantine = new QuarantineService(Path.Combine(_tempDir, "q"));
        return (log, new UndoCoordinator(log, quarantine), quarantine);
    }

    [Fact]
    public void UndoEntry_UnknownId_ReturnsFailure()
    {
        var (_, coordinator, _) = Create();
        var outcome = coordinator.UndoEntry("non-esiste");
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void UndoEntry_CleanupAndRecycleBin_AreNeverReported_AsUndone()
    {
        var (log, coordinator, _) = Create();
        log.AddSession(new ActionSession
        {
            SessionId = "s1",
            Entries =
            [
                new ActionLogEntry { Id = "temp", ActionKind = ActionKind.CleanTempFiles, Title = "Temp" },
                new ActionLogEntry { Id = "bin", ActionKind = ActionKind.EmptyRecycleBin, Title = "Cestino" },
            ],
        });

        var outcomes = coordinator.UndoSession("s1");
        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.False(o.Succeeded));
        Assert.False(log.FindEntry("temp")!.Undone);
        Assert.False(log.FindEntry("bin")!.Undone);
    }

    [Fact]
    public void UndoEntry_AlreadyUndone_IsNotRepeated()
    {
        var (log, coordinator, _) = Create();
        log.AddSession(new ActionSession
        {
            SessionId = "s1",
            Entries = [new ActionLogEntry { Id = "e1", ActionKind = ActionKind.QuarantineDuplicate, Title = "File", Undone = true }],
        });

        Assert.True(coordinator.UndoEntry("e1").Succeeded);
        Assert.Empty(coordinator.UndoSession("s1"));
    }

    [Fact]
    public void UndoEntry_QuarantineRestoresFile_AndPurgedEntryIsRefused()
    {
        var (log, coordinator, quarantine) = Create();
        string workDir = Path.Combine(_tempDir, "work");
        Directory.CreateDirectory(workDir);
        string fileA = Path.Combine(workDir, "a.txt");
        string fileB = Path.Combine(workDir, "b.txt");
        File.WriteAllText(fileA, "A");
        File.WriteAllText(fileB, "B");

        var itemA = quarantine.MoveToQuarantine(fileA, "s1");
        var itemB = quarantine.MoveToQuarantine(fileB, "s1");
        log.AddSession(new ActionSession
        {
            SessionId = "s1",
            Entries =
            [
                new ActionLogEntry { Id = "a", ActionKind = ActionKind.QuarantineDuplicate, Title = "a.txt", Target = fileA, QuarantinePath = itemA.QuarantinedPath },
                new ActionLogEntry { Id = "b", ActionKind = ActionKind.QuarantineDuplicate, Title = "b.txt", Target = fileB, QuarantinePath = itemB.QuarantinedPath },
            ],
        });

        // Il primo si ripristina.
        var restored = coordinator.UndoEntry("a");
        Assert.True(restored.Succeeded);
        Assert.True(File.Exists(fileA));
        Assert.True(log.FindEntry("a")!.Undone);

        // Il secondo viene eliminato dalla quarantena: la sua voce non si può più annullare.
        Assert.Equal(1, quarantine.PurgeItems([itemB.Id]));
        Assert.Equal(1, log.MarkQuarantinePurged([itemB.QuarantinedPath]));

        var refused = coordinator.UndoEntry("b");
        Assert.False(refused.Succeeded);
        Assert.Contains("eliminato definitivamente", refused.Message);
        Assert.False(log.FindEntry("b")!.Undone);
        Assert.True(log.FindEntry("b")!.QuarantinePurged);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
