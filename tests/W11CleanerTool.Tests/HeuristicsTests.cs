using W11CleanerTool.Core;

namespace W11CleanerTool.Tests;

public class HeuristicsTests
{
    [Theory]
    [InlineData(3200, 2133, 26, CheckStatus.Warning)] // DDR4 sotto la velocità dichiarata
    [InlineData(2666, 2666, 26, CheckStatus.Warning)] // DDR4 alla velocità base
    [InlineData(3600, 3600, 26, CheckStatus.Ok)]      // DDR4 con XMP attivo
    [InlineData(4800, 4800, 34, CheckStatus.Warning)] // DDR5 alla velocità base
    [InlineData(6000, 6000, 34, CheckStatus.Ok)]      // DDR5 con EXPO attivo
    [InlineData(0, 0, 0, CheckStatus.Info)]           // dati mancanti
    public void RamSpeed(int rated, int configured, int type, CheckStatus expected)
        => Assert.Equal(expected, Heuristics.RamSpeed(rated, configured, type));

    [Fact]
    public void DriverAge_OldDriverIsWarning()
        => Assert.Equal(CheckStatus.Warning, Heuristics.DriverAge(new DateTime(2024, 1, 1), new DateTime(2026, 11, 18)));

    [Fact]
    public void DriverAge_RecentDriverIsOk()
        => Assert.Equal(CheckStatus.Ok, Heuristics.DriverAge(new DateTime(2026, 6, 1), new DateTime(2026, 11, 18)));

    [Fact]
    public void DriverAge_UnknownDateIsInfo()
        => Assert.Equal(CheckStatus.Info, Heuristics.DriverAge(null, new DateTime(2026, 11, 18)));

    [Theory]
    [InlineData(5L, 100L, CheckStatus.Problem)]
    [InlineData(12L, 100L, CheckStatus.Warning)]
    [InlineData(40L, 100L, CheckStatus.Ok)]
    [InlineData(0L, 0L, CheckStatus.Info)]
    public void FreeSpace(long free, long total, CheckStatus expected)
        => Assert.Equal(expected, Heuristics.FreeSpace(free, total));

    [Theory]
    [InlineData(60, 144, CheckStatus.Warning)]
    [InlineData(144, 144, CheckStatus.Ok)]
    [InlineData(1, 60, CheckStatus.Info)]
    public void RefreshRate(int current, int max, CheckStatus expected)
        => Assert.Equal(expected, Heuristics.RefreshRate(current, max));

    [Fact]
    public void ParseWmiDate_ReadsCimDatetime()
        => Assert.Equal(new DateTime(2024, 1, 15), Heuristics.ParseWmiDate("20240115000000.000000-000"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("20241399000000.000000-000")]
    public void ParseWmiDate_BadValuesAreNull(string? value)
        => Assert.Null(Heuristics.ParseWmiDate(value));
}
