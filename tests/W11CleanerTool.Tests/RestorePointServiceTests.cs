using System.Globalization;
using W11CleanerTool.Core.Safety;

namespace W11CleanerTool.Tests;

public sealed class RestorePointServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Local);

    [Fact]
    public void InterpretReturnCode_MapsKnownCodes()
    {
        Assert.Equal(RestorePointStatus.Created, RestorePointService.InterpretReturnCode(0).Status);
        Assert.Equal(RestorePointStatus.AccessDenied, RestorePointService.InterpretReturnCode(5).Status);
        Assert.Equal(RestorePointStatus.Disabled, RestorePointService.InterpretReturnCode(1058).Status);
        Assert.Equal(RestorePointStatus.Disabled, RestorePointService.InterpretReturnCode(1060).Status);
        Assert.Equal(RestorePointStatus.Disabled, RestorePointService.InterpretReturnCode(16389).Status);

        var unknown = RestorePointService.InterpretReturnCode(1234);
        Assert.Equal(RestorePointStatus.Error, unknown.Status);
        Assert.Equal(1234u, unknown.ErrorCode);
    }

    [Fact]
    public void EvaluateCreation_NewSequenceNumber_IsCreated()
    {
        var before = new RestorePointSnapshot(true, new RestorePointInfo(10, Now.AddDays(-3), "vecchio"));
        var after = new RestorePointSnapshot(true, new RestorePointInfo(11, Now, "nuovo"));

        var result = RestorePointService.EvaluateCreation(before, after, Now);
        Assert.Equal(RestorePointStatus.Created, result.Status);
    }

    [Fact]
    public void EvaluateCreation_SameSequenceNumber_IsRecentPointExists()
    {
        // Windows risponde 0 ma non crea niente se esiste un punto di meno di 24 ore.
        var existing = new RestorePointInfo(10, Now.AddHours(-5), "di stamattina");
        var before = new RestorePointSnapshot(true, existing);
        var after = new RestorePointSnapshot(true, existing);

        var result = RestorePointService.EvaluateCreation(before, after, Now);
        Assert.Equal(RestorePointStatus.RecentPointExists, result.Status);
        Assert.Contains("non ha creato un punto nuovo", result.Message);
        Assert.Contains(Now.AddHours(-5).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture), result.Message);
    }

    [Fact]
    public void EvaluateCreation_FirstPointEver_IsCreated()
    {
        var before = new RestorePointSnapshot(true, null);
        var after = new RestorePointSnapshot(true, new RestorePointInfo(1, Now, "primo"));

        Assert.Equal(RestorePointStatus.Created, RestorePointService.EvaluateCreation(before, after, Now).Status);
    }

    [Fact]
    public void EvaluateCreation_CannotReadListAfter_IsNotCreated()
    {
        var before = new RestorePointSnapshot(true, new RestorePointInfo(10, Now.AddDays(-3), "vecchio"));

        Assert.Equal(RestorePointStatus.Error, RestorePointService.EvaluateCreation(before, new RestorePointSnapshot(false, null), Now).Status);
        Assert.Equal(RestorePointStatus.Error, RestorePointService.EvaluateCreation(before, new RestorePointSnapshot(true, null), Now).Status);
    }

    [Fact]
    public void EvaluateCreation_WithoutListBefore_UsesCreationTime()
    {
        var unknownBefore = new RestorePointSnapshot(false, null);

        var fresh = new RestorePointSnapshot(true, new RestorePointInfo(7, Now.AddMinutes(-1), "appena fatto"));
        Assert.Equal(RestorePointStatus.Created, RestorePointService.EvaluateCreation(unknownBefore, fresh, Now).Status);

        var old = new RestorePointSnapshot(true, new RestorePointInfo(7, Now.AddHours(-6), "di stamattina"));
        Assert.Equal(RestorePointStatus.RecentPointExists, RestorePointService.EvaluateCreation(unknownBefore, old, Now).Status);
    }
}
