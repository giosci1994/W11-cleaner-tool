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
    public int SucceededCount => Outcomes.Count(o => o.Succeeded);
    public int FailedCount => Outcomes.Count(o => !o.Succeeded);
    public long TotalBytesFreed => Outcomes.Sum(o => o.BytesFreed);
}

/// <summary>Esito dell'annullamento di una voce dello storico.</summary>
public sealed record UndoOutcome(
    string EntryId,
    string Title,
    bool Succeeded,
    string Message,
    string? StoreUrl = null);
