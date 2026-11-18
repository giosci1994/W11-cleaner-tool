using System.Diagnostics.Eventing.Reader;
using System.Text;

namespace W11CleanerTool.Core.Checks;

/// <summary>Un evento trovato nei registri di Windows.</summary>
public sealed record CrashEvent(DateTime When, string Kind, string Source, int EventId, string Message);

/// <summary>Il rapporto sui crash: gli eventi trovati e il testo da dare a un'IA (già anonimizzato).</summary>
public sealed record CrashReportResult(List<CheckResult> Results, List<CrashEvent> Events, string Text);

/// <summary>
/// Raccoglie gli ultimi crash dai registri di Windows: legge e basta, non cambia niente.
/// </summary>
public static class CrashReport
{
    private const string Area = "Problemi e crash";
    private const int MaxMessageLength = 600;
    private const int MaxEventsPerKind = 25;

    public static CrashReportResult Build(int days = 30)
    {
        var anonymizer = Anonymizer.ForCurrentUser();
        var events = new List<CrashEvent>();
        var notes = new List<string>();
        long ms = (long)TimeSpan.FromDays(days).TotalMilliseconds;
        string time = $"TimeCreated[timediff(@SystemTime) <= {ms}]";

        Read(events, notes, "System", $"*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001 and {time}]]", "Schermata blu");
        Read(events, notes, "System", $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and {time}]]", "Spegnimento improvviso");
        Read(events, notes, "System", $"*[System[Provider[@Name='EventLog'] and EventID=6008 and {time}]]", "Arresto imprevisto");
        Read(events, notes, "System", $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and {time}]]", "Errore hardware (WHEA)");
        Read(events, notes, "Application", $"*[System[Provider[@Name='Application Error'] and EventID=1000 and {time}]]", "App chiusa per errore");
        Read(events, notes, "Application", $"*[System[Provider[@Name='Application Hang'] and EventID=1002 and {time}]]", "App bloccata");

        var dumps = ListMinidumps(notes);

        events = events
            .Select(e => e with { Message = anonymizer.Clean(e.Message) })
            .OrderByDescending(e => e.When)
            .ToList();

        var results = Summarize(events, dumps, days);
        string text = anonymizer.Clean(ComposeText(events, dumps, notes, days));
        return new CrashReportResult(results, events, text);
    }

    private static void Read(List<CrashEvent> events, List<string> notes, string log, string xpath, string kind)
    {
        try
        {
            var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            int count = 0;
            for (EventRecord? record = reader.ReadEvent(); record is not null && count < MaxEventsPerKind; record = reader.ReadEvent())
            {
                using (record)
                {
                    string message;
                    try
                    {
                        message = record.FormatDescription() ?? "";
                    }
                    catch (EventLogException)
                    {
                        message = "";
                    }

                    if (string.IsNullOrWhiteSpace(message) && record.Properties.Count > 0)
                    {
                        message = string.Join(" · ", record.Properties.Select(p => p.Value?.ToString() ?? ""));
                    }

                    message = message.Replace("\r", " ").Replace("\n", " ").Trim();
                    if (message.Length > MaxMessageLength)
                    {
                        message = message[..MaxMessageLength] + "…";
                    }

                    events.Add(new CrashEvent(record.TimeCreated ?? DateTime.MinValue, kind, record.ProviderName ?? "", record.Id, message));
                    count++;
                }
            }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            notes.Add($"Registro {log} ({kind}) non leggibile: {ex.GetType().Name}");
        }
    }

    private static List<(string Name, DateTime When, long Size)> ListMinidumps(List<string> notes)
    {
        var dumps = new List<(string, DateTime, long)>();
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
        try
        {
            if (Directory.Exists(folder))
            {
                foreach (var file in new DirectoryInfo(folder).GetFiles("*.dmp").OrderByDescending(f => f.LastWriteTime).Take(20))
                {
                    dumps.Add((file.Name, file.LastWriteTime, file.Length));
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            notes.Add("La cartella dei minidump si legge solo come amministratore.");
        }
        catch (IOException ex)
        {
            notes.Add("Cartella dei minidump non leggibile: " + ex.GetType().Name);
        }

        return dumps;
    }

    private static List<CheckResult> Summarize(List<CrashEvent> events, List<(string Name, DateTime When, long Size)> dumps, int days)
    {
        var results = new List<CheckResult>();
        int bsod = events.Count(e => e.Kind == "Schermata blu");
        int power = events.Count(e => e.Kind is "Spegnimento improvviso" or "Arresto imprevisto");
        int whea = events.Count(e => e.Kind == "Errore hardware (WHEA)");
        int apps = events.Count(e => e.Kind is "App chiusa per errore" or "App bloccata");

        results.Add(bsod > 0
            ? new CheckResult(Area, "Schermate blu", CheckStatus.Problem, $"{bsod} schermate blu negli ultimi {days} giorni. Esporta il rapporto e dallo a un'IA per capire la causa.", Last(events, "Schermata blu"))
            : new CheckResult(Area, "Schermate blu", CheckStatus.Ok, $"Nessuna schermata blu negli ultimi {days} giorni.", ""));
        results.Add(power > 0
            ? new CheckResult(Area, "Spegnimenti improvvisi", CheckStatus.Warning, $"{power} spegnimenti o riavvii improvvisi negli ultimi {days} giorni (anche un blackout o il tasto di accensione tenuto premuto contano).", Last(events, "Spegnimento improvviso"))
            : new CheckResult(Area, "Spegnimenti improvvisi", CheckStatus.Ok, $"Nessuno spegnimento improvviso negli ultimi {days} giorni.", ""));
        results.Add(whea > 0
            ? new CheckResult(Area, "Errori hardware", CheckStatus.Warning, $"{whea} errori hardware segnalati da Windows (WHEA) negli ultimi {days} giorni. Spesso dipendono da RAM, overclock o alimentazione.", Last(events, "Errore hardware (WHEA)"))
            : new CheckResult(Area, "Errori hardware", CheckStatus.Ok, $"Nessun errore hardware negli ultimi {days} giorni.", ""));
        results.Add(apps > 0
            ? new CheckResult(Area, "App che si chiudono o si bloccano", CheckStatus.Info, $"{apps} app chiuse per errore o bloccate negli ultimi {days} giorni (mostrate al massimo {MaxEventsPerKind} per tipo).", Last(events, "App chiusa per errore"))
            : new CheckResult(Area, "App che si chiudono o si bloccano", CheckStatus.Ok, $"Nessuna app chiusa per errore negli ultimi {days} giorni.", ""));
        results.Add(new CheckResult(Area, "Minidump", dumps.Count > 0 ? CheckStatus.Info : CheckStatus.Ok,
            dumps.Count > 0 ? $"{dumps.Count} file di crash (minidump) nella cartella di Windows." : "Nessun minidump trovato (oppure servono i diritti di amministratore per vederli).",
            string.Join(" | ", dumps.Select(d => $"{d.Name} {d.When:dd/MM/yyyy HH:mm} {d.Size / 1024} KB"))));
        return results;
    }

    private static string Last(List<CrashEvent> events, string kind)
    {
        var e = events.FirstOrDefault(x => x.Kind == kind);
        return e is null ? "" : $"Ultimo: {e.When:dd/MM/yyyy HH:mm} · {e.Source} {e.EventId} · {e.Message}";
    }

    private static string ComposeText(List<CrashEvent> events, List<(string Name, DateTime When, long Size)> dumps, List<string> notes, int days)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Rapporto crash di Windows (W11-cleaner-tool)");
        sb.AppendLine();
        sb.AppendLine("Ciao! Il mio PC con Windows 11 ha avuto i problemi qui sotto. Spiegami in parole semplici");
        sb.AppendLine("qual è la causa più probabile e cosa posso fare, partendo dalle soluzioni più semplici e sicure.");
        sb.AppendLine("Se ti servono altre informazioni, chiedimele.");
        sb.AppendLine();
        sb.AppendLine($"Creato il {DateTime.Now:dd/MM/yyyy HH:mm} · periodo: ultimi {days} giorni · nomi e percorsi personali tolti.");
        sb.AppendLine();
        sb.AppendLine("## Il PC");
        foreach (string line in SystemSummary.Lines())
        {
            sb.AppendLine("- " + line);
        }

        sb.AppendLine();
        sb.AppendLine("## Gli eventi (dal più recente)");
        if (events.Count == 0)
        {
            sb.AppendLine("- Nessun crash trovato nei registri.");
        }

        foreach (var e in events)
        {
            sb.AppendLine($"- {e.When:dd/MM/yyyy HH:mm:ss} · {e.Kind} · {e.Source} (ID {e.EventId}) · {e.Message}");
        }

        sb.AppendLine();
        sb.AppendLine("## Minidump");
        if (dumps.Count == 0)
        {
            sb.AppendLine("- Nessuno trovato (oppure servono i diritti di amministratore).");
        }

        foreach (var d in dumps)
        {
            sb.AppendLine($"- {d.Name} · {d.When:dd/MM/yyyy HH:mm} · {d.Size / 1024} KB");
        }

        if (notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Note");
            foreach (string n in notes)
            {
                sb.AppendLine("- " + n);
            }
        }

        return sb.ToString();
    }
}
