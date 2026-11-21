using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using W11CleanerTool.Core.Safety;

namespace W11CleanerTool.Core.Actions;

/// <summary>App UWP bloatware nota e sicura da rimuovere.</summary>
public sealed record KnownBloatwareApp(
    string PackageName,
    string FriendlyName,
    string Description);

/// <summary>Pacchetto Appx rilevato sul sistema.</summary>
public sealed record InstalledAppxPackage(
    string Name,
    string PackageFullName,
    string PackageFamilyName,
    string InstallLocation);

/// <summary>
/// Rileva e rimuove in modo conservativo solo app UWP bloatware sicure.
/// Esclude rigorosamente Microsoft Store, Xbox/GamingServices, Sicurezza di Windows, Edge,
/// Calcolatrice, Foto, Blocco note, Terminale e componenti di sistema; niente privacy/telemetria né winget.
/// </summary>
public sealed class DebloatService
{
    private const string CategoryName = "App preinstallate";
    private const string FamilyNameMetaKey = "PackageFamilyName";
    private const string InstallLocationMetaKey = "InstallLocation";
    private const string PackageNameMetaKey = "PackageName";

    private static readonly Regex SafePackageTokenRegex = new(@"^[A-Za-z0-9._~+-]+$", RegexOptions.Compiled);

    /// <summary>
    /// Prefissi o nomi esatti di pacchetti critici protetti che non devono MAI essere proposti o rimossi.
    /// </summary>
    public static IReadOnlyList<string> ProtectedPackagePrefixes { get; } =
    [
        "Microsoft.WindowsStore",
        "Microsoft.StorePurchaseApp",
        "Microsoft.GamingServices",
        "Microsoft.Xbox",
        "Microsoft.SecHealthUI",
        "Microsoft.Windows.SecHealthUI",
        "WindowsDefender",
        "Microsoft.MicrosoftEdge",
        "Microsoft.WindowsCalculator",
        "Microsoft.Windows.Photos",
        "Microsoft.WindowsNotepad",
        "Microsoft.WindowsTerminal",
        "Microsoft.DesktopAppInstaller",
        "Microsoft.Paint",
        "Microsoft.ScreenSketch",
        "Microsoft.WindowsCamera",
        "Microsoft.VCLibs",
        "Microsoft.NET",
        "Microsoft.UI.Xaml",
        "Microsoft.WindowsAppRuntime",
        "Microsoft.Services.Store",
        "Microsoft.WebMediaExtensions",
        "Microsoft.HEIFImageExtension",
        "Microsoft.VP9VideoExtensions",
        "Microsoft.WebpImageExtension",
        "Microsoft.AV1VideoExtension",
        "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.StartMenuExperienceHost",
        "MicrosoftWindows.Client",
        "Microsoft.AAD.BrokerPlugin",
        "Microsoft.AccountsControl",
    ];

    /// <summary>Elenco curato e conservativo di app preinstallate sicure da rimuovere.</summary>
    public static IReadOnlyList<KnownBloatwareApp> CuratedBloatware { get; } =
    [
        new("Clipchamp.Clipchamp", "Clipchamp", "Editor video preinstallato."),
        new("Microsoft.MicrosoftSolitaireCollection", "Microsoft Solitaire Collection", "Raccolta di giochi di carte Solitario."),
        new("Microsoft.BingNews", "Notizie Microsoft (Bing News)", "App notizie MSN."),
        new("Microsoft.BingWeather", "Meteo Microsoft (Bing Weather)", "App meteo MSN."),
        new("Microsoft.BingFinance", "Finanza Microsoft", "App finanza e mercati MSN."),
        new("Microsoft.BingSports", "Sport Microsoft", "App sport MSN."),
        new("Microsoft.BingSearch", "Ricerca Bing", "Estensione/app di ricerca Bing dallo Store."),
        new("Microsoft.GetHelp", "Richiesta supporto (Get Help)", "App di assistenza guidata Microsoft."),
        new("Microsoft.Getstarted", "Suggerimenti (Get Started)", "App introduttiva e consigli di Windows."),
        new("Microsoft.Todos", "Microsoft To Do", "App elenco attività sincronizzata con account Microsoft."),
        new("Microsoft.YourPhone", "Collegamento al telefono", "App per collegare lo smartphone Android/iOS al PC."),
        new("MicrosoftTeams", "Microsoft Teams (personale)", "Client Teams consumer integrato."),
        new("MSTeams", "Microsoft Teams", "Nuova app Microsoft Teams preinstallata."),
        new("Microsoft.549981C3F5F10", "Cortana", "Assistente vocale Cortana ormai dismesso."),
        new("Microsoft.WindowsFeedbackHub", "Hub di Feedback", "App per inviare segnalazioni a Microsoft."),
        new("Microsoft.ZuneMusic", "Lettore multimediale / Groove", "App musicale preinstallata."),
        new("Microsoft.ZuneVideo", "Film e TV", "App video e noleggio Microsoft."),
        new("Microsoft.PowerAutomateDesktop", "Power Automate Desktop", "Strumento di automazione flussi desktop."),
        new("Microsoft.WindowsMaps", "Mappe di Windows", "App mappe offline."),
        new("Microsoft.People", "Contatti (People)", "Rubrica integrata di Windows."),
        new("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office Hub)", "Collegamento promozionale alle app Office."),
        new("Microsoft.MixedReality.Portal", "Portale Realtà Mista", "App per visori Windows Mixed Reality."),
        new("MicrosoftCorporationII.QuickAssist", "Assistenza rapida", "Strumento di controllo remoto Microsoft."),
        new("Microsoft.OutlookForWindows", "Nuovo Outlook per Windows", "Client di posta preinstallato."),
        new("Microsoft.Copilot", "Microsoft Copilot", "App dedicata Microsoft Copilot."),
    ];

    private readonly ActionLog _actionLog;
    private readonly RestorePointService _restorePointService;
    private readonly Func<string, (int ExitCode, string Output, string Error)> _runPowerShell;

    public DebloatService(ActionLog? actionLog = null, RestorePointService? restorePointService = null)
        : this(actionLog, restorePointService, RunPowerShellScript)
    {
    }

    internal DebloatService(
        ActionLog? actionLog,
        RestorePointService? restorePointService,
        Func<string, (int ExitCode, string Output, string Error)> runPowerShell)
    {
        _actionLog = actionLog ?? new ActionLog();
        _restorePointService = restorePointService ?? new RestorePointService();
        _runPowerShell = runPowerShell ?? throw new ArgumentNullException(nameof(runPowerShell));
    }

    /// <summary>Restituisce true se il pacchetto è protetto e non deve mai essere rimosso.</summary>
    public static bool IsProtectedPackage(string? packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return true;
        }

        string trimmed = packageName.Trim();
        foreach (string prefix in ProtectedPackagePrefixes)
        {
            if (trimmed.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return trimmed.Contains("GamingServices", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Elenca solo le app bloatware dell'elenco curato effettivamente installate.</summary>
    public List<PlannedChange> Scan(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        const string script =
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
            "Get-AppxPackage | Select-Object Name, PackageFullName, PackageFamilyName, InstallLocation | ConvertTo-Json -Compress";

        var (exitCode, output, _) = _runPowerShell(script);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            return new List<PlannedChange>();
        }

        var installed = ParseAppxJson(output);
        var curatedByName = CuratedBloatware.ToDictionary(a => a.PackageName, StringComparer.OrdinalIgnoreCase);
        var results = new List<PlannedChange>();

        foreach (var pkg in installed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsProtectedPackage(pkg.Name) || IsProtectedPackage(pkg.PackageFullName))
            {
                continue;
            }

            if (!curatedByName.TryGetValue(pkg.Name, out var known))
            {
                continue;
            }

            results.Add(new PlannedChange
            {
                Id = $"debloat:{pkg.PackageFullName}",
                Category = CategoryName,
                Title = known.FriendlyName,
                Explanation = $"{known.Description} Rimozione solo per l'utente corrente.",
                TechnicalDetail = $"{pkg.PackageFullName} ({pkg.PackageFamilyName})",
                BeforeValue = "Installata",
                AfterValue = "Rimossa per questo utente",
                EstimatedBytes = 0,
                RequiresAdmin = false,
                IsReversible = true,
                IsSelected = false,
                TargetKey = pkg.PackageFullName,
                ActionKind = ActionKind.DebloatRemoveApp,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [PackageNameMetaKey] = pkg.Name,
                    [FamilyNameMetaKey] = pkg.PackageFamilyName,
                    [InstallLocationMetaKey] = pkg.InstallLocation,
                },
            });
        }

        return results;
    }

    /// <summary>Rimuove le app selezionate e le registra nello storico.</summary>
    public ActionExecutionResult Apply(
        IEnumerable<PlannedChange> changes,
        bool createRestorePoint = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var selected = changes.Where(c => c.IsSelected).ToList();
        var outcomes = new List<ActionItemOutcome>();

        if (selected.Count == 0)
        {
            return new ActionExecutionResult(null, null, outcomes);
        }

        RestorePointResult? restorePoint = createRestorePoint
            ? _restorePointService.Create("W11-cleaner-tool: rimozione app")
            : null;

        var session = new ActionSession
        {
            TimestampUtc = DateTime.UtcNow,
            RestorePointCreated = restorePoint?.Status == RestorePointStatus.Created,
        };

        foreach (var change in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (change.ActionKind != ActionKind.DebloatRemoveApp)
            {
                outcomes.Add(new ActionItemOutcome(change.Id, change.Title, Succeeded: false, "Azione non supportata da DebloatService."));
                continue;
            }

            string packageName = change.Metadata.TryGetValue(PackageNameMetaKey, out string? n) ? n : ExtractNameFromFullName(change.TargetKey);
            if (IsProtectedPackage(packageName) || IsProtectedPackage(change.TargetKey))
            {
                outcomes.Add(new ActionItemOutcome(
                    change.Id,
                    change.Title,
                    Succeeded: false,
                    "Pacchetto protetto di sistema: rimozione bloccata."));
                continue;
            }

            if (!SafePackageTokenRegex.IsMatch(change.TargetKey))
            {
                outcomes.Add(new ActionItemOutcome(
                    change.Id,
                    change.Title,
                    Succeeded: false,
                    "Identificativo del pacchetto non valido."));
                continue;
            }

            string script =
                "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
                $"Remove-AppxPackage -Package '{EscapePowerShellSingleQuotes(change.TargetKey)}' -ErrorAction Stop";

            var (exitCode, _, error) = _runPowerShell(script);
            if (exitCode != 0)
            {
                string errText = string.IsNullOrWhiteSpace(error) ? $"codice {exitCode}" : error.Trim();
                outcomes.Add(new ActionItemOutcome(
                    change.Id,
                    change.Title,
                    Succeeded: false,
                    $"Rimozione non riuscita: {errText}"));
                continue;
            }

            string family = change.Metadata.TryGetValue(FamilyNameMetaKey, out string? f) ? f : "";
            string location = change.Metadata.TryGetValue(InstallLocationMetaKey, out string? loc) ? loc : "";

            outcomes.Add(new ActionItemOutcome(
                change.Id,
                change.Title,
                Succeeded: true,
                $"«{change.Title}» rimossa."));

            session.Entries.Add(new ActionLogEntry
            {
                ActionKind = ActionKind.DebloatRemoveApp,
                Title = change.Title,
                Target = change.TargetKey,
                OldValue = $"{family}|{location}",
                NewValue = "Removed",
                TimestampUtc = DateTime.UtcNow,
            });
        }

        if (session.Entries.Count > 0)
        {
            _actionLog.AddSession(session);
            return new ActionExecutionResult(session, restorePoint, outcomes);
        }

        return new ActionExecutionResult(null, restorePoint, outcomes);
    }

    /// <summary>Prova a ri-registrare l'app rimossa o fornisce il link a Microsoft Store.</summary>
    public UndoOutcome UndoEntry(ActionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string storeUrl = BuildStoreSearchUrl(entry.Title);
        if (IsProtectedPackage(entry.Target))
        {
            return new UndoOutcome(entry.Id, entry.Title, false, "Pacchetto non valido.", storeUrl);
        }

        ParseOldValue(entry.OldValue, out string familyName, out string installLocation);

        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            string manifest = Path.Combine(installLocation, "AppxManifest.xml");
            if (File.Exists(manifest))
            {
                string regScript =
                    "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
                    $"Add-AppxPackage -DisableDevelopmentMode -Register '{EscapePowerShellSingleQuotes(manifest)}' -ErrorAction Stop";
                var (exitCode, _, _) = _runPowerShell(regScript);
                if (exitCode == 0)
                {
                    return new UndoOutcome(entry.Id, entry.Title, true, $"«{entry.Title}» ripristinata dal pacchetto locale.");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(familyName) && SafePackageTokenRegex.IsMatch(familyName))
        {
            string lookupScript =
                "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
                $"$p = Get-AppxPackage -AllUsers | Where-Object {{ $_.PackageFamilyName -eq '{EscapePowerShellSingleQuotes(familyName)}' }} | Select-Object -First 1; " +
                "if ($null -eq $p) { exit 2 }; " +
                "Add-AppxPackage -DisableDevelopmentMode -Register (Join-Path $p.InstallLocation 'AppxManifest.xml') -ErrorAction Stop";

            var (exitCode, _, _) = _runPowerShell(lookupScript);
            if (exitCode == 0)
            {
                return new UndoOutcome(entry.Id, entry.Title, true, $"«{entry.Title}» ri-registrata su questo utente.");
            }
        }

        return new UndoOutcome(
            entry.Id,
            entry.Title,
            Succeeded: false,
            $"Il pacchetto locale di «{entry.Title}» non è più sul disco: puoi reinstallarla da Microsoft Store.",
            StoreUrl: storeUrl);
    }

    public static string BuildStoreSearchUrl(string appName)
        => $"https://apps.microsoft.com/search?query={Uri.EscapeDataString(appName ?? "")}";

    internal static List<InstalledAppxPackage> ParseAppxJson(string json)
    {
        var list = new List<InstalledAppxPackage>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return list;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in doc.RootElement.EnumerateArray())
                {
                    AddAppxElement(elem, list);
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                AddAppxElement(doc.RootElement, list);
            }
        }
        catch (JsonException)
        {
            // Output non JSON: restituiamo elenco vuoto.
        }

        return list;
    }

    private static void AddAppxElement(JsonElement elem, List<InstalledAppxPackage> list)
    {
        if (elem.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string name = GetString(elem, "Name");
        string full = GetString(elem, "PackageFullName");
        string family = GetString(elem, "PackageFamilyName");
        string loc = GetString(elem, "InstallLocation");

        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(full))
        {
            list.Add(new InstalledAppxPackage(name, full, family, loc));
        }
    }

    private static string GetString(JsonElement elem, string prop)
        => elem.TryGetProperty(prop, out JsonElement val) && val.ValueKind == JsonValueKind.String
            ? val.GetString() ?? ""
            : "";

    private static void ParseOldValue(string oldValue, out string familyName, out string installLocation)
    {
        int sep = oldValue.IndexOf('|');
        if (sep < 0)
        {
            familyName = oldValue;
            installLocation = "";
            return;
        }

        familyName = oldValue[..sep];
        installLocation = oldValue[(sep + 1)..];
    }

    private static string ExtractNameFromFullName(string fullName)
    {
        int underscore = fullName.IndexOf('_');
        return underscore > 0 ? fullName[..underscore] : fullName;
    }

    private static string EscapePowerShellSingleQuotes(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static (int ExitCode, string Output, string Error) RunPowerShellScript(string script)
    {
        try
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(
                    "powershell.exe",
                    $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
            };

            process.Start();
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);
            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}
