using W11CleanerTool.Core.Safety;

namespace W11CleanerTool.Core.Actions;

/// <summary>Esito di una singola modifica applicata.</summary>
public sealed record ActionItemOutcome(
    string ChangeId,
    string Title,
    bool Succeeded,
    string Message,
    long BytesFreed = 0);

/// <summary>Esito complessivo di una sessione di modifiche.</summary>
public sealed record ActionExecutionResult(
    ActionSession? Session,
    RestorePointResult? RestorePoint,
    IReadOnlyList<ActionItemOutcome> Outcomes)
{
    /// <summary>
    /// True se il punto di ripristino era richiesto ma non è stato creato: non è stato applicato niente
    /// e l'interfaccia deve chiedere se continuare senza punto di ripristino.
    /// </summary>
    public bool StoppedForMissingRestorePoint { get; init; }

    public int SucceededCount => Outcomes.Count(o => o.Succeeded);
    public int FailedCount => Outcomes.Count(o => !o.Succeeded);
    public long TotalBytesFreed => Outcomes.Sum(o => o.BytesFreed);

    /// <summary>Esito «fermato prima di cominciare» perché il punto di ripristino richiesto non c'è.</summary>
    public static ActionExecutionResult MissingRestorePoint(RestorePointResult restorePoint)
        => new(null, restorePoint, Array.Empty<ActionItemOutcome>()) { StoppedForMissingRestorePoint = true };

    /// <summary>True se il punto di ripristino era richiesto e non risulta creato.</summary>
    internal static bool IsMissing(bool requested, RestorePointResult? restorePoint)
        => requested && restorePoint?.Status != RestorePointStatus.Created;
}

/// <summary>Esito dell'annullamento di una voce dello storico.</summary>
public sealed record UndoOutcome(
    string EntryId,
    string Title,
    bool Succeeded,
    string Message,
    string? StoreUrl = null);
