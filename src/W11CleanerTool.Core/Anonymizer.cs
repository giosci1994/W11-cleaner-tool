using System.Text.RegularExpressions;

namespace W11CleanerTool.Core;

/// <summary>
/// Toglie dal testo i dati che identificano la persona o il PC prima di esportarlo:
/// nome utente, nome del PC, dominio, percorsi della cartella utente.
/// </summary>
public sealed class Anonymizer
{
    private readonly List<(string Value, string Replacement)> _secrets = new();

    public Anonymizer(string? userName, string? machineName, string? userDomain, string? userProfile)
    {
        // Prima i valori più lunghi, così un percorso completo si sostituisce prima del nome che contiene.
        Add(userProfile, @"C:\Users\<utente>");
        Add(machineName, "<nome-pc>");
        if (!string.Equals(userDomain, machineName, StringComparison.OrdinalIgnoreCase))
        {
            Add(userDomain, "<dominio>");
        }

        Add(userName, "<utente>");
        _secrets.Sort((a, b) => b.Value.Length.CompareTo(a.Value.Length));
    }

    public static Anonymizer ForCurrentUser() => new(
        Environment.UserName,
        Environment.MachineName,
        Environment.UserDomainName,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private void Add(string? value, string replacement)
    {
        // Valori troppo corti (es. un nome utente di 2 lettere) cancellerebbero pezzi di parole normali.
        if (!string.IsNullOrWhiteSpace(value) && value.Trim().Length >= 3)
        {
            _secrets.Add((value.Trim(), replacement));
        }
    }

    public string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string result = text;
        foreach (var (value, replacement) in _secrets)
        {
            result = Regex.Replace(result, Regex.Escape(value), replacement.Replace("$", "$$"), RegexOptions.IgnoreCase);
        }

        // Qualunque altro profilo utente rimasto (C:\Users\<qualcuno>\...).
        result = Regex.Replace(
            result,
            @"([A-Za-z]:\\Users\\)(?!<utente>|Public\\|Default\\)[^\\\r\n""]+",
            "$1<utente>",
            RegexOptions.IgnoreCase);

        return result;
    }
}
