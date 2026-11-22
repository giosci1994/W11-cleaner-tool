# Piano · prima tappa

Proposto il 18/11/2026 dall'ingegnere. Ogni punto si approva con «fallo tu» o si scarta con «ignora». Stato: **approvato il 18/11/2026, tutti i punti «fallo tu»**; nome nel copyright e autore dei commit: `giosci1994`.

## 1 · Struttura del codice
- **C# su .NET 10**, l'ultima LTS (10.0.15 del 10/11/2026, supporto fino al 14/11/2028; la 11 è STS). Fonte: `releases-index.json` di Microsoft, controllato il 18/11/2026.
- **WPF, non WinUI 3**:
  - WPF fa un **exe singolo self-contained** senza problemi (`PublishSingleFile`); WinUI 3 non impacchettato si porta dietro il Windows App SDK e in file singolo dà più grane.
  - Da .NET 9 WPF ha il **tema Fluent** di Windows 11 (chiaro/scuro automatico): l'aspetto è quello di PC Manager.
  - Supporta **x64 e ARM64**, si costruisce su un runner Windows di Actions.
- **Exe self-contained e compresso** (non serve installare .NET sul PC): circa 60–80 MB (stima, da verificare con la prima build). L'alternativa piccola (pochi MB) obbliga a installare il .NET Desktop Runtime.
- **Parte senza diritti di amministratore**; chiede l'elevazione (UAC) solo per le funzioni che la richiedono.
- Cartelle: `src/W11CleanerTool/` (app), `src/W11CleanerTool.Core/` (controlli e modifiche, senza interfaccia), `tests/`, `.github/workflows/`, `docs/`.

## 2 · Modalità Base ed Esperto
- **Base**: poche schede grandi con parole semplici («Controlla il PC», «Gaming», «Problemi e crash», poi «Pulisci»). Ogni risultato: ✅ / ⚠️ / ❌ e una frase su cosa vuol dire e cosa fare. Nessun termine tecnico in primo piano.
- **Esperto**: le stesse funzioni, con ogni voce singola, i dettagli tecnici (chiave di registro, servizio, comando, valore attuale), caselle da spuntare e lo storico.
- Un interruttore in alto passa da una all'altra; la scelta si ricorda.

## 3 · Funzioni, in ordine
**Prima build: solo lettura, rischio nullo** (non cambia niente sul PC):
1. **Checklist gaming**: Modalità gioco, pianificazione GPU accelerata (HAGS), piano energetico, frequenza del monitor rispetto al massimo, integrità della memoria (VBS, solo segnalata), driver della scheda video (versione e data), spazio libero sul disco di sistema, riavvio in sospeso.
2. **XMP e driver**: velocità della RAM configurata rispetto a quella dichiarata (se è più bassa, XMP — EXPO o DOCP sulle schede AMD — probabilmente è spento: si spiega come attivarlo nel BIOS, da Windows non si può); dispositivi con errori; driver principali (video, chipset, rete, audio) con versione e data. Il dato sulla RAM è **indicativo**: da verificare su PC veri.
3. **Log dei crash per un'IA**: ultimi 30 giorni di schermate blu (BugCheck), spegnimenti improvvisi (Kernel-Power 41), errori hardware (WHEA), app che si chiudono o si bloccano, elenco dei minidump. Il monitoraggio affidabilità è rimandato a una build successiva (ripete in gran parte gli eventi delle app). Esporta un file di testo con il riepilogo del PC e una domanda pronta per l'IA. **Anonimizzato**: niente nome utente, nome del PC, percorsi personali.

**Dopo, con le sicurezze del punto 4** (una tappa per volta, ognuna provata sul PC):
4. Niente sospensione durante i download (solo mentre l'app è aperta, senza toccare le impostazioni).
5. Pulizia: file temporanei, cache, cestino; prima solo la stima di quanto si libera.
6. Ottimizzazione: programmi all'avvio, piano energetico.
7. Debloat.
8. Duplicati in quarantena.
- Newsletter: dopo, a parte (già deciso).

## 4 · Sicurezze (per ogni funzione che modifica)
- **Punto di ripristino** prima di ogni gruppo di modifiche. Se la Protezione sistema è spenta, lo si dice e si chiede; non la si accende da soli.
- **Anteprima**: l'elenco di cosa cambia (prima → dopo) e un «Conferma».
- **Registro delle modifiche**: un file JSON in `%LOCALAPPDATA%\W11-cleaner-tool\` con data, cosa, valore vecchio, valore nuovo.
- **Annulla con storico**: una pagina «Storico» con «Annulla» per ogni modifica o per tutta la sessione.
- **Quarantena**: i file dell'utente (duplicati, file non temporanei) si **spostano** in una cartella di quarantena, non si cancellano; dopo 30 giorni si propone di svuotarla. Temporanei e cache (che Windows ricrea) si cancellano.

## 5 · Build con GitHub Actions
- Si parte dallo schema già provato in un altro progetto Windows dello stesso proprietario (`build.yml` + `release.yml`, `SHA256SUMS.txt`, script `VERIFY.ps1`, build riuscite), con tre cambi: **.NET 10** invece di 8 (finito il 10/11/2026), **anche ARM64**, l'exe direttamente nella release (non solo nello zip).
- Runner `windows-latest`, `dotnet publish` per `win-x64` e `win-arm64` (l'ARM64 si costruisce dal runner x64).
- **A ogni push**: i due exe come artifact del workflow.
- **A ogni tag `v*`**: una Release con `W11-cleaner-tool-x64.exe`, `W11-cleaner-tool-arm64.exe` e `SHA256SUMS.txt`.
- Solo il `GITHUB_TOKEN` automatico: **nessun segreto da creare**.
- Exe **non firmato** per ora: Windows SmartScreen avvisa alla prima apertura. La firma (a pagamento) si decide più avanti.
- Nota: su un repository privato i minuti dei runner Windows contano doppio sul piano gratuito; una build di qualche minuto ci sta ampiamente (da verificare con la prima build).

## 6 · Come si scarica e si prova
1. Sul PC Windows 11, con l'account GitHub collegato (il repository è privato): pagina **Releases** del repository.
2. Si scarica l'exe giusto (x64 per Intel/AMD, ARM64 per Snapdragon) e `SHA256SUMS.txt`.
3. In PowerShell: `Get-FileHash .\W11-cleaner-tool-x64.exe -Algorithm SHA256` → il codice deve essere uguale a quello del file.
4. All'apertura, se SmartScreen avvisa: «Ulteriori informazioni» → «Esegui comunque».
5. La prima build non cambia niente sul PC. Dalle funzioni che modificano in poi: prova prima in **Windows Sandbox** (solo Windows 11 Pro) o almeno con un punto di ripristino.

## 7 · Licenza GPL-3.0
- Dal 22/11/2026 la licenza è passata da MIT a **GNU GPL-3.0** (file `LICENSE` con testo ufficiale GPL-3.0).
- I file comuni (SECURITY, CONTRIBUTING, CODE_OF_CONDUCT) arrivano già dal repository `.github` dell'account: qui non si ripetono.

## 8 · Prima del primo commit
- In `docs/decisioni.md` due righe citano un nome proprio («scelta di …»): si sostituisce con «scelta del proprietario», così la storia pubblica non lo contiene.
- Nome dell'autore dei commit: lo stesso scelto al punto 7 (o il nome utente); email noreply di GitHub nella config locale.
