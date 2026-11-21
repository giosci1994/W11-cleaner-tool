using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using W11CleanerTool.Core;
using W11CleanerTool.Core.Actions;
using W11CleanerTool.Core.Safety;

namespace W11CleanerTool;

internal static class UiBrushes
{
    public static readonly Brush Green = Frozen(0x2E, 0x9E, 0x44);
    public static readonly Brush Orange = Frozen(0xD9, 0x82, 0x00);
    public static readonly Brush Red = Frozen(0xD1, 0x34, 0x38);
    public static readonly Brush Blue = Frozen(0x00, 0x78, 0xD4);
    public static readonly Brush Muted = Frozen(0x7A, 0x82, 0x8C);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Riga selezionabile di modifica proposta (Pulisci, Ottimizza, Debloat, Duplicati).</summary>
public sealed class PlannedChangeItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _showExpertDetails;

    public PlannedChangeItem(PlannedChange change, bool expert)
    {
        Change = change;
        _isSelected = change.IsSelected;
        _showExpertDetails = expert;

        SizeText = change.EstimatedBytes > 0 ? Heuristics.FormatBytes(change.EstimatedBytes) : "";
        HasSize = change.EstimatedBytes > 0;

        HasCurrentStateBadge = change.ActionKind is ActionKind.StartupDisable
            or ActionKind.StartupEnable
            or ActionKind.PowerPlanChange;
        CurrentStateBadge = change.ActionKind == ActionKind.PowerPlanChange
            ? $"Attuale: {change.BeforeValue}"
            : change.BeforeValue;

        HasBeforeAfter = !string.IsNullOrWhiteSpace(change.BeforeValue) || !string.IsNullOrWhiteSpace(change.AfterValue);
        BeforeAfterText = $"Prima: {change.BeforeValue}  \u2192  Dopo: {change.AfterValue}";
        HasTechnicalDetail = !string.IsNullOrWhiteSpace(change.TechnicalDetail);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PlannedChange Change { get; }
    public string Id => Change.Id;
    public string Category => Change.Category;
    public string Title => Change.Title;
    public string Explanation => Change.Explanation;
    public string TechnicalDetail => Change.TechnicalDetail;
    public bool RequiresAdmin => Change.RequiresAdmin;
    public bool IsReversible => Change.IsReversible;
    public long EstimatedBytes => Change.EstimatedBytes;
    public ActionKind ActionKind => Change.ActionKind;

    public string SizeText { get; }
    public bool HasSize { get; }
    public string CurrentStateBadge { get; }
    public bool HasCurrentStateBadge { get; }
    public string BeforeAfterText { get; }
    public bool HasBeforeAfter { get; }
    public bool HasTechnicalDetail { get; }
    public bool ShowExpertTechnicalDetail => _showExpertDetails && HasTechnicalDetail;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public bool ShowExpertDetails
    {
        get => _showExpertDetails;
        set
        {
            if (_showExpertDetails == value)
            {
                return;
            }

            _showExpertDetails = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowExpertTechnicalDetail));
        }
    }

    public PlannedChange ToPlannedChange() => Change with { IsSelected = _isSelected };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>Esito visivo di una singola modifica applicata.</summary>
public sealed class OutcomeDisplayItem
{
    public OutcomeDisplayItem(ActionItemOutcome outcome)
    {
        Title = outcome.Title;
        Message = outcome.Message;
        Glyph = outcome.Succeeded ? "\uE73E" : "\uEA39";
        Color = outcome.Succeeded ? UiBrushes.Green : UiBrushes.Red;
        HasBytes = outcome.BytesFreed > 0;
        BytesText = HasBytes ? Heuristics.FormatBytes(outcome.BytesFreed) : "";
    }

    public string Title { get; }
    public string Message { get; }
    public string Glyph { get; }
    public Brush Color { get; }
    public string BytesText { get; }
    public bool HasBytes { get; }
}

/// <summary>Esito di annullamento nello storico (con eventuale link a Microsoft Store).</summary>
public sealed class UndoOutcomeDisplayItem
{
    public UndoOutcomeDisplayItem(UndoOutcome outcome)
    {
        Title = string.IsNullOrWhiteSpace(outcome.Title) ? "Operazione" : outcome.Title;
        Message = outcome.Message;
        Glyph = outcome.Succeeded ? "\uE73E" : (string.IsNullOrWhiteSpace(outcome.StoreUrl) ? "\uEA39" : "\uE946");
        Color = outcome.Succeeded ? UiBrushes.Green : (string.IsNullOrWhiteSpace(outcome.StoreUrl) ? UiBrushes.Red : UiBrushes.Blue);
        StoreUrl = outcome.StoreUrl;
        HasStoreUrl = !string.IsNullOrWhiteSpace(outcome.StoreUrl);
    }

    public string Title { get; }
    public string Message { get; }
    public string Glyph { get; }
    public Brush Color { get; }
    public string? StoreUrl { get; }
    public bool HasStoreUrl { get; }
}

/// <summary>Singola voce dello storico di una sessione.</summary>
public sealed class HistoryEntryItem
{
    public HistoryEntryItem(ActionLogEntry entry, bool expert)
    {
        Id = entry.Id;
        Title = entry.Title;
        ActionKindLabel = FormatActionKind(entry.ActionKind);
        Target = entry.Target;
        BeforeAfterText = FormatBeforeAfter(entry);
        TimestampText = entry.TimestampUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        Undone = entry.Undone;

        bool reversible = IsReversibleKind(entry.ActionKind);
        CanUndo = !entry.Undone && reversible;
        ShowNonReversibleNote = !entry.Undone && !reversible;
        ShowExpertDetails = expert;

        if (entry.Undone)
        {
            StatusText = "Annullata";
            StatusColor = UiBrushes.Green;
        }
        else if (!reversible)
        {
            StatusText = "Non reversibile";
            StatusColor = UiBrushes.Muted;
        }
        else
        {
            StatusText = "Applicata";
            StatusColor = UiBrushes.Blue;
        }
    }

    public string Id { get; }
    public string Title { get; }
    public string ActionKindLabel { get; }
    public string Target { get; }
    public string BeforeAfterText { get; }
    public string TimestampText { get; }
    public bool Undone { get; }
    public bool CanUndo { get; }
    public bool ShowNonReversibleNote { get; }
    public bool ShowExpertDetails { get; }
    public string StatusText { get; }
    public Brush StatusColor { get; }

    public static bool IsReversibleKind(ActionKind kind) => kind switch
    {
        ActionKind.StartupDisable => true,
        ActionKind.StartupEnable => true,
        ActionKind.PowerPlanChange => true,
        ActionKind.DebloatRemoveApp => true,
        ActionKind.QuarantineDuplicate => true,
        _ => false,
    };

    private static string FormatActionKind(ActionKind kind) => kind switch
    {
        ActionKind.CleanTempFiles => "Pulizia temporanei",
        ActionKind.CleanBrowserCache => "Pulizia cache browser",
        ActionKind.CleanWindowsUpdateCache => "Pulizia cache Windows Update",
        ActionKind.EmptyRecycleBin => "Svuotamento Cestino",
        ActionKind.StartupDisable => "Avvio disabilitato",
        ActionKind.StartupEnable => "Avvio riabilitato",
        ActionKind.PowerPlanChange => "Piano energetico",
        ActionKind.DebloatRemoveApp => "App preinstallata rimossa",
        ActionKind.QuarantineDuplicate => "Duplicato in quarantena",
        _ => kind.ToString(),
    };

    private static string FormatBeforeAfter(ActionLogEntry entry)
    {
        if (entry.ActionKind is ActionKind.StartupDisable or ActionKind.StartupEnable)
        {
            return $"Target: {entry.Target} (stato registro salvato per ripristino)";
        }

        return string.IsNullOrWhiteSpace(entry.OldValue)
            ? entry.NewValue
            : $"{entry.OldValue}  \u2192  {entry.NewValue}";
    }
}

/// <summary>Sessione registrata in ActionLog.</summary>
public sealed class HistorySessionItem
{
    public HistorySessionItem(ActionSession session, bool expert)
    {
        SessionId = session.SessionId;
        TimestampText = session.TimestampUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        RestorePointCreated = session.RestorePointCreated;
        Entries = session.Entries.Select(e => new HistoryEntryItem(e, expert)).ToList();
        HasUndoableEntries = Entries.Any(e => e.CanUndo);

        int total = Entries.Count;
        int undone = Entries.Count(e => e.Undone);
        SummaryText = undone > 0
            ? $"{total} {(total == 1 ? "voce" : "voci")} \u00B7 {undone} annullate"
            : $"{total} {(total == 1 ? "voce" : "voci")}";
    }

    public string SessionId { get; }
    public string TimestampText { get; }
    public string SummaryText { get; }
    public bool RestorePointCreated { get; }
    public bool HasUndoableEntries { get; }
    public List<HistoryEntryItem> Entries { get; }
}

/// <summary>Elemento visivo nella sezione Quarantena.</summary>
public sealed class QuarantineDisplayItem
{
    public QuarantineDisplayItem(QuarantineItem item, bool expert, DateTime nowUtc)
    {
        Id = item.Id;
        FileName = Path.GetFileName(item.OriginalPath);
        OriginalPath = item.OriginalPath;
        QuarantinedPath = item.QuarantinedPath;
        SizeText = Heuristics.FormatBytes(item.SizeBytes);
        DateText = item.QuarantinedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        int days = Math.Max(0, (int)(nowUtc - item.QuarantinedAtUtc).TotalDays);
        AgeText = days == 0 ? "Oggi" : (days == 1 ? "1 giorno fa" : $"{days} giorni fa");
        IsExpired = days >= 30;
        Sha256 = item.Sha256;
        ShowExpertDetails = expert;
    }

    public string Id { get; }
    public string FileName { get; }
    public string OriginalPath { get; }
    public string QuarantinedPath { get; }
    public string SizeText { get; }
    public string DateText { get; }
    public string AgeText { get; }
    public bool IsExpired { get; }
    public string Sha256 { get; }
    public bool ShowExpertDetails { get; }
}
