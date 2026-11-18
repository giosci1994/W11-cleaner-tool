using System.Windows.Media;
using W11CleanerTool.Core;

namespace W11CleanerTool;

/// <summary>Una riga dei risultati, pronta per l'interfaccia.</summary>
public sealed class ResultItem
{
    private static readonly Brush Green = Frozen(0x2E, 0x9E, 0x44);
    private static readonly Brush Orange = Frozen(0xD9, 0x82, 0x00);
    private static readonly Brush Red = Frozen(0xD1, 0x34, 0x38);
    private static readonly Brush Blue = Frozen(0x00, 0x78, 0xD4);

    public ResultItem(CheckResult result, bool expert)
    {
        Title = result.Title;
        Explanation = result.Explanation;
        Detail = result.Detail;
        ShowDetail = expert && !string.IsNullOrWhiteSpace(result.Detail);
        var look = result.Status switch
        {
            CheckStatus.Ok => (Glyph: "\uE73E", Brush: Green, Text: "OK"),
            CheckStatus.Warning => (Glyph: "\uE7BA", Brush: Orange, Text: "Da guardare"),
            CheckStatus.Problem => (Glyph: "\uEA39", Brush: Red, Text: "Problema"),
            _ => (Glyph: "\uE946", Brush: Blue, Text: "Informazione"),
        };
        Glyph = look.Glyph;
        Color = look.Brush;
        StatusText = look.Text;
    }

    public string Title { get; }
    public string Explanation { get; }
    public string Detail { get; }
    public bool ShowDetail { get; }
    public string Glyph { get; }
    public Brush Color { get; }
    public string StatusText { get; }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
