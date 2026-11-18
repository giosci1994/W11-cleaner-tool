namespace W11CleanerTool.Core;

/// <summary>Esito di un controllo.</summary>
public enum CheckStatus
{
    Ok,
    Warning,
    Problem,
    Info,
}

/// <summary>
/// Il risultato di un controllo in sola lettura.
/// <see cref="Explanation"/> è per la modalità Base (parole semplici);
/// <see cref="Detail"/> è per la modalità Esperto (valori tecnici).
/// </summary>
public sealed record CheckResult(
    string Area,
    string Title,
    CheckStatus Status,
    string Explanation,
    string Detail);
