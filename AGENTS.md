# W11-cleaner-tool · scheletro

Ultimo aggiornamento: 19/11/2026 · dall'ingegnere, dopo README inglese, landing page EN/IT e social preview.

<!-- Questo file va nel repository, che diventerà pubblico: niente nomi propri, email, percorsi di macchine, token, chiavi o password. Le note private di lavoro stanno fuori dal repository. -->

## Che cos'è
- Tool di **pulizia e ottimizzazione per Windows 11**, sul modello di Microsoft PC Manager: schermata semplice con pochi pulsanti (**modalità Base**) e **modalità Esperto**.
- Un exe solo, **C# (.NET)**, licenza **MIT**, build **x64 e ARM64** con **SHA256** nelle Releases di GitHub.
- Decide il proprietario del repository. Le scelte prese sono in `docs/decisioni.md` e **non si ripropongono**.

## Come si entra
- Codice: questo repository. Build: **GitHub Actions** (nessun .NET sulla macchina di sviluppo).
- Prova: solo su un PC Windows 11 vero, scaricando l'exe dagli artifact di Actions o dalle Releases.
- **Mai password, token o chiavi** nei file: eventuali segreti dei workflow stanno in GitHub Secrets.

## La mappa
- `src/W11CleanerTool/`: l'interfaccia **WPF** (.NET 10, tema Fluent con `ThemeMode="System"`). `MainWindow.xaml(.cs)`: schede Base a sinistra, risultati a destra, interruttore «Modalità Esperto» (salvato in `%LOCALAPPDATA%\W11-cleaner-tool\settings.json`). `app.manifest`: `asInvoker`.
- `src/W11CleanerTool.Core/`: i controlli, senza interfaccia. Ogni controllo restituisce `CheckResult` (Area, Title, Status, Explanation per Base, Detail per Esperto).
  - `Checks/GamingChecks.cs` · `Checks/HardwareChecks.cs` · `Checks/CrashReport.cs`: **solo lettura** (registro, WMI, registri eventi, API di Windows).
  - `Heuristics.cs`: le regole pure (RAM, età dei driver, spazio, frequenza): **si provano nei test**.
  - `Anonymizer.cs`: toglie nome utente, nome del PC, dominio e profili dal rapporto dei crash.
- `tests/W11CleanerTool.Tests/`: test delle regole e dell'anonimizzazione, più `SmokeTests` che fanno girare i controlli veri sul runner Windows (i risultati sono nel log della build).
- `scripts/publish.ps1`: exe x64 e ARM64 self-contained in file singolo, `SHA256SUMS.txt`. `scripts/VERIFY.ps1`: il controllo per chi scarica.
- `.github/workflows/build.yml` (push su `main`, PR, a mano) · `release.yml` (tag `v*`, release segnata «pre-release» finché la versione è 0.x).
- `docs/decisioni.md` (le scelte) · `docs/piano.md` (il piano approvato) · `docs/ricerca-progetti-simili.md`.
- **Vetrina** (come gli altri repository del proprietario, design system Arborae: fondo `#070b0a`, verde `#00b585`, Space Grotesk / Inter / JetBrains Mono):
  - `README.md`: **solo in inglese** (niente seconda metà in italiano).
  - `docs/index.html` + `docs/css/style.css` + `docs/js/main.js`: la landing page, **una lingua per volta** con l'interruttore EN/IT (attributi `data-en` / `data-it`; `?lang=it` nell'indirizzo; la scelta resta nel browser). Sarà su `giosci1994.github.io/W11-cleaner-tool/` quando GitHub Pages sarà attivo (Source: `main`, cartella `/docs`, come gli altri).
  - `docs/social-preview.html` (sorgente) → `docs/social-preview.png` 1280×640, da caricare a mano in Settings › Social preview.

## Come si fa
- **Una build**: push su `main` → Actions «build» → artifact `W11-cleaner-tool-ci.<numero>` con i due exe e `SHA256SUMS.txt`.
- **Una release**: aggiornare `VersionPrefix` in `Directory.Build.props`, commit, `git tag vX.Y.Z` e push del tag → Actions «release».
- **La social preview**: dopo aver cambiato `docs/social-preview.html`, `firefox --headless --window-size=1280,640 --screenshot docs/social-preview.png docs/social-preview.html` (Firefox snap: copiare prima l'html in una cartella non nascosta della home).
- **Una scritta nuova nella landing**: sempre tutte e due le lingue (`data-en` e `data-it`), e il testo inglese anche dentro l'elemento.
- **Prima di ogni commit**: il controllo anti-dati del proprietario deve dire «Nessun dato trovato». Nei test i percorsi finti si compongono a pezzi (`UsersC + "mario"`) per non farlo scattare.
- **Le icone** (Segoe Fluent Icons) nel codice si scrivono come escape `\uE73E`, mai come carattere.
- **Una funzione nuova che modifica il sistema**: anteprima, punto di ripristino, registro, annulla (`docs/piano.md`, punto 4); «funzionante» solo dopo la prova sul PC.

## Cosa non toccare
- **Visibilità e impostazioni di sicurezza del repository**: le decide solo il proprietario.
- Nessun dato personale in file, commit, workflow e release: il repository diventerà pubblico **con tutta la storia**.
- Codice di altri progetti: solo MIT, con l'attribuzione. GPL e AGPL si leggono solo per le idee.
- Niente privacy/telemetria, niente installazione di app con winget (`docs/decisioni.md`).

## Le decisioni
In `docs/decisioni.md`.

## Da verificare
- Come gli altri progetti firmano l'exe e gestiscono SmartScreen (la firma si decide più avanti).
- **Falsi avvisi sui driver**: i driver di Windows (fornitore Microsoft) hanno la data convenzionale 21/06/2006 e risultano «più vecchi di un anno» (visto nella build 1 sul runner): da trattare come «driver di Windows», non come avviso.
- Sul PC vero: la regola della RAM (XMP), la chiave `HistoricalCaptureEnabled` della Game Bar, le frequenze dei monitor, la dimensione dell'exe.
- Quali API usare per ogni funzione (da guardare nel codice dei progetti MIT quando si scrive la funzione).
- GitHub Pages su un repository privato: secondo la documentazione di GitHub serve un piano a pagamento (Pro); con il piano gratuito si attiva quando il repository diventa pubblico, con l'ok del proprietario.
- I badge del README (shields.io, download, licenza) funzionano solo quando il repository è pubblico.
- La landing e la social preview mostrano un'**illustrazione** dell'app: le schermate vere si aggiungono dopo la prova sul PC.

## Il registro dei lavori
| Data | Cosa si è fatto | Cosa si è scoperto |
|---|---|---|
| 18/11/2026 | Creato lo scheletro; piano approvato; v0.1 in sola lettura, repository privato, prima build su Actions | il codice non si compila sulla macchina di sviluppo: gli errori si vedono solo nel log di Actions |
| 19/11/2026 | README solo in inglese; landing page EN/IT e social preview nello stile degli altri repository | Pages non attivabile finché il repository è privato (piano gratuito) |
