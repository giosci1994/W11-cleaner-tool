namespace W11CleanerTool.Core.Safety;

/// <summary>Tipo di azione proposta o eseguita sul sistema.</summary>
public enum ActionKind
{
    CleanTempFiles,
    CleanBrowserCache,
    CleanWindowsUpdateCache,
    EmptyRecycleBin,
    StartupDisable,
    StartupEnable,
    PowerPlanChange,
    DebloatRemoveApp,
    QuarantineDuplicate,
}

/// <summary>
/// Anteprima immutabile «prima → dopo» di una singola modifica proposta.
/// <see cref="Explanation"/> è per la modalità Base; <see cref="TechnicalDetail"/> è per la modalità Esperto.
/// </summary>
public sealed record PlannedChange
{
    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string Explanation { get; init; }
    public string TechnicalDetail { get; init; } = "";
    public string BeforeValue { get; init; } = "";
    public string AfterValue { get; init; } = "";
    public long EstimatedBytes { get; init; }
    public bool RequiresAdmin { get; init; }
    public bool IsReversible { get; init; }
    public bool IsSelected { get; init; } = true;
    public required string TargetKey { get; init; }
    public required ActionKind ActionKind { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = EmptyMetadata;
}
