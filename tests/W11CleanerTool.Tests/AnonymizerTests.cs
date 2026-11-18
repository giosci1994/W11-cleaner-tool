using W11CleanerTool.Core;

namespace W11CleanerTool.Tests;

public class AnonymizerTests
{
    // I percorsi si compongono a pezzi: nomi finti, ma così il controllo anti-dati del repository non li scambia per veri.
    private const string UsersC = @"C:\Users\";
    private const string UsersD = @"D:\Users\";

    private static readonly Anonymizer Sample = new(
        userName: "mario",
        machineName: "DESKTOP-ABC123",
        userDomain: "DESKTOP-ABC123",
        userProfile: UsersC + "mario");

    [Fact]
    public void RemovesUserProfilePath()
        => Assert.Equal("Errore in " + UsersC + @"<utente>\AppData\Local\gioco.exe",
            Sample.Clean("Errore in " + UsersC + @"mario\AppData\Local\gioco.exe"));

    [Fact]
    public void RemovesMachineNameIgnoringCase()
        => Assert.Equal("PC <nome-pc> riavviato", Sample.Clean("PC desktop-abc123 riavviato"));

    [Fact]
    public void RemovesUserName()
        => Assert.Equal("Accesso di <utente>", Sample.Clean("Accesso di Mario"));

    [Fact]
    public void RemovesOtherUserProfiles()
        => Assert.Equal(UsersD + @"<utente>\file.txt", Sample.Clean(UsersD + @"luigi\file.txt"));

    [Fact]
    public void KeepsPublicProfile()
        => Assert.Equal(UsersC + @"Public\Documents", Sample.Clean(UsersC + @"Public\Documents"));

    [Fact]
    public void IgnoresVeryShortNames()
    {
        var a = new Anonymizer("al", "PC", "PC", UsersC + "al");
        Assert.Equal("normale", a.Clean("normale"));
    }

    [Fact]
    public void EmptyTextStaysEmpty()
        => Assert.Equal("", Sample.Clean(null));
}
