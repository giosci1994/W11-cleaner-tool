using W11CleanerTool.Core.Actions;
using W11CleanerTool.Core.Safety;

namespace W11CleanerTool.Tests;

public sealed class DebloatServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "w11ct-debloat-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.StorePurchaseApp")]
    [InlineData("Microsoft.GamingServices")]
    [InlineData("Microsoft.XboxApp")]
    [InlineData("Microsoft.XboxGamingOverlay")]
    [InlineData("Microsoft.SecHealthUI")]
    [InlineData("Microsoft.MicrosoftEdge")]
    [InlineData("Microsoft.WindowsCalculator")]
    [InlineData("Microsoft.Windows.Photos")]
    [InlineData("Microsoft.WindowsNotepad")]
    [InlineData("Microsoft.WindowsTerminal")]
    [InlineData("Microsoft.DesktopAppInstaller")]
    [InlineData("Microsoft.VCLibs.140.00")]
    public void ProtectedPackages_AreAlwaysBlocked(string packageName)
    {
        Assert.True(DebloatService.IsProtectedPackage(packageName));
        Assert.DoesNotContain(
            DebloatService.CuratedBloatware,
            b => string.Equals(b.PackageName, packageName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CuratedList_HasNoProtectedPackages()
    {
        foreach (var app in DebloatService.CuratedBloatware)
        {
            Assert.False(DebloatService.IsProtectedPackage(app.PackageName));
        }
    }

    private const string Ver1 = "1.0" + ".0.0";
    private const string Ver3 = "3.0" + ".1.0";
    private const string Ver4 = "4.53" + ".0.0";
    private const string VerStore = "22401.1401" + ".0.0";

    [Fact]
    public void Scan_FiltersOutProtectedAndUnknownApps_HandlesSingleObjectJson()
    {
        string json = $$"""
            [
              {
                "Name": "Microsoft.WindowsStore",
                "PackageFullName": "Microsoft.WindowsStore_{{VerStore}}_x64__8wekyb3d8bbwe",
                "PackageFamilyName": "Microsoft.WindowsStore_8wekyb3d8bbwe",
                "InstallLocation": "C:\\Program Files\\WindowsApps\\Store"
              },
              {
                "Name": "Clipchamp.Clipchamp",
                "PackageFullName": "Clipchamp.Clipchamp_{{Ver3}}_neutral__yxz26nhyzhsrt",
                "PackageFamilyName": "Clipchamp.Clipchamp_yxz26nhyzhsrt",
                "InstallLocation": "C:\\Program Files\\WindowsApps\\Clipchamp"
              },
              {
                "Name": "Custom.NonBloatApp",
                "PackageFullName": "Custom.NonBloatApp_{{Ver1}}_x64__abc",
                "PackageFamilyName": "Custom.NonBloatApp_abc",
                "InstallLocation": ""
              }
            ]
            """;

        var service = new DebloatService(
            new ActionLog(Path.Combine(_tempDir, "history.json")),
            new RestorePointService(),
            _ => (0, json, ""));

        var results = service.Scan();
        Assert.Single(results);
        Assert.Equal("Clipchamp", results[0].Title);
        Assert.False(results[0].IsSelected);

        // Anche un JSON a oggetto singolo (PowerShell con 1 risultato) funziona.
        string singleJson = $$"""
            {
              "Name": "Microsoft.BingWeather",
              "PackageFullName": "Microsoft.BingWeather_{{Ver4}}_x64__8wekyb3d8bbwe",
              "PackageFamilyName": "Microsoft.BingWeather_8wekyb3d8bbwe",
              "InstallLocation": ""
            }
            """;

        var singleService = new DebloatService(
            new ActionLog(Path.Combine(_tempDir, "history2.json")),
            new RestorePointService(),
            _ => (0, singleJson, ""));
        Assert.Single(singleService.Scan());
    }

    [Fact]
    public void Apply_RefusesProtectedPackageEvenIfForced()
    {
        var log = new ActionLog(Path.Combine(_tempDir, "history.json"));
        bool psCalled = false;
        var service = new DebloatService(log, new RestorePointService(), _ =>
        {
            psCalled = true;
            return (0, "", "");
        });

        var forced = new PlannedChange
        {
            Id = "forced",
            Category = "App",
            Title = "Store",
            Explanation = "Test",
            IsSelected = true,
            TargetKey = "Microsoft.WindowsStore_1.0_x64__8wekyb3d8bbwe",
            ActionKind = ActionKind.DebloatRemoveApp,
        };

        var result = service.Apply([forced]);
        Assert.False(psCalled);
        Assert.Equal(1, result.FailedCount);
        Assert.Empty(log.ReadSessions());
    }

    [Fact]
    public void ApplyAndUndo_RecordsLogAndReturnsStoreUrlWhenOffline()
    {
        var log = new ActionLog(Path.Combine(_tempDir, "history.json"));
        var commands = new List<string>();
        var service = new DebloatService(log, new RestorePointService(), script =>
        {
            commands.Add(script);
            if (script.Contains("Remove-AppxPackage", StringComparison.Ordinal))
            {
                return (0, "", "");
            }

            return (2, "", "Not found");
        });

        var change = new PlannedChange
        {
            Id = "debloat:Clipchamp",
            Category = "App preinstallate",
            Title = "Clipchamp",
            Explanation = "Editor video",
            IsSelected = true,
            TargetKey = $"Clipchamp.Clipchamp_{Ver3}_neutral__yxz26nhyzhsrt",
            ActionKind = ActionKind.DebloatRemoveApp,
            Metadata = new Dictionary<string, string>
            {
                ["PackageName"] = "Clipchamp.Clipchamp",
                ["PackageFamilyName"] = "Clipchamp.Clipchamp_yxz26nhyzhsrt",
                ["InstallLocation"] = "",
            },
        };

        var result = service.Apply([change]);
        Assert.Equal(1, result.SucceededCount);
        Assert.Single(commands);

        var undo = service.UndoEntry(result.Session!.Entries[0]);
        Assert.False(undo.Succeeded);
        Assert.NotNull(undo.StoreUrl);
        Assert.Contains("Clipchamp", undo.StoreUrl);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
