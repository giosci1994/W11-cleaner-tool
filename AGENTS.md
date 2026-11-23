# W11-cleaner-tool · scheletro

Ultimo aggiornamento: 22/11/2026 · repository pubblico, Pages attivo, completata la v0.2.1 (barra laterale nuova e correzioni della revisione; prima la v0.2.0: fix driver Microsoft 2006, niente sospensione, sicurezze/storico/quarantena, pulizia, ottimizzazione, debloat, duplicati e UI WPF Base/Esperto).

<!-- Questo file sta nel repository, che è pubblico: niente nomi propri, email, percorsi di macchine, token, chiavi o password. Le note private di lavoro stanno fuori dal repository. -->

## Che cos'è
- Tool di **pulizia e ottimizzazione per Windows 11**, sul modello di Microsoft PC Manager: schermata semplice con pochi pulsanti (**modalità Base**) e **modalità Esperto**.
- Un exe solo, **C# (.NET)**, licenza **GPL-3.0**, build **x64 e ARM64** con **SHA256** nelle Releases di GitHub.
- Decide il proprietario del repository. Le scelte prese sono in `docs/decisioni.md` e **non si ripropongono**.

## Come si entra
- Codice: questo repository. Build: **GitHub Actions** (nessun .NET sulla macchina di sviluppo).
- Prova: solo su un PC Windows 11 vero, scaricando l'exe dagli artifact di Actions o dalle Releases.
- **Mai password, token o chiavi** nei file: eventuali segreti dei workflow stanno in GitHub Secrets.

## La mappa
- `src/W11CleanerTool/`: l'interfaccia **WPF** (.NET 10, tema Fluent con `ThemeMode="System"`). `app.manifest`: `asInvoker`.
  - `MainWindow.xaml(.cs)`: schede Base/Esperto a sinistra (controlli in sola lettura + *Pulisci*, *Ottimizza*, *Debloat*, *Duplicati*, *Storico e Quarantena*), interruttore *Modalità Esperto* (salvato in `%LOCALAPPDATA%\W11-cleaner-tool\settings.json`) e interruttore *Niente sospensione (download)* (`KeepAwakeService`, rilasciato in `OnClosed`).
  - `PreviewDialog.xaml(.cs)`: modale di conferma «prima → dopo» con avvisi amministratore/Protezione sistema e checkbox *Crea punto di ripristino prima di applicare*.
  - `ViewModels.cs` · `ResultItem.cs` · `Settings.cs`.
- `src/W11CleanerTool.Core/`: logica senza interfaccia.
  - `Checks/GamingChecks.cs` · `Checks/HardwareChecks.cs` · `Checks/CrashReport.cs`: **solo lettura** (registro, WMI, registri eventi, API di Windows).
  - `Heuristics.cs`: le regole pure (RAM, età dei driver con riconoscimento `IsWindowsSystemDriver` per driver Microsoft 2006, spazio, frequenza, `FormatBytes`): **si provano nei test**.
  - `Anonymizer.cs`: toglie nome utente, nome del PC, dominio e profili dal rapporto dei crash.
  - `ProcessRunner.cs`: avvio di `powershell`/`powercfg` con letture asincrone, tempo massimo e chiusura dell'albero dei processi; codifica OEM della console.
  - `KeepAwakeService.cs`: `SetThreadExecutionState` temporaneo per impedire la sospensione finché l'app è aperta.
  - `Safety/`: `PlannedChange.cs` (modello «prima → dopo»), `ActionLog.cs` (`%LOCALAPPDATA%\W11-cleaner-tool\history.json`), `RestorePointService.cs` (WMI `SystemRestore.CreateRestorePoint`, non attiva mai Protezione sistema da sola), `QuarantineService.cs` (`%LOCALAPPDATA%\W11-cleaner-tool\quarantine\<sessione>\manifest.json` con SHA256, ripristino e pulizia oltre 30 giorni).
  - `Actions/`: `ActionExecutionResult.cs`, `CleanupService.cs`, `OptimizationService.cs`, `DebloatService.cs` (elenco curato + protezioni pacchetti critici, niente telemetria né winget), `DuplicateFinderService.cs` (dimensione → SHA256 4 KB → SHA256 completo, solo quarantena), `UndoCoordinator.cs`, `SessionRecorder.cs` (scrive nello storico ogni voce appena applicata).
- `tests/W11CleanerTool.Tests/`: test delle regole, dell'anonimizzazione, dei servizi di sicurezza e azione (`ActionLogTests`, `CleanupServiceTests`, `DebloatServiceTests`, `DuplicateFinderServiceTests`, `OptimizationServiceTests`, `QuarantineServiceTests`, `RestorePointServiceTests`, `UndoCoordinatorTests`), più `SmokeTests` sul runner Windows.
- `CHANGELOG.md`: registro delle versioni (`0.1.0`, `0.2.0`, `0.2.1`).
- `scripts/publish.ps1`: exe x64 e ARM64 self-contained in file singolo, `SHA256SUMS.txt`. `scripts/VERIFY.ps1`: il controllo per chi scarica.
- `.github/workflows/build.yml` (push su `main`, PR, a mano) · `release.yml` (tag `v*`, release segnata «pre-release» finché la versione è 0.x).
- `docs/decisioni.md` (le scelte) · `docs/piano.md` (il piano approvato) · `docs/ricerca-progetti-simili.md`.
- **Vetrina** (come gli altri repository del proprietario, design system Arborae: fondo `#070b0a`, verde `#00b585`, Space Grotesk / Inter / JetBrains Mono):
  - `README.md`: **solo in inglese** (niente seconda metà in italiano).
  - `docs/index.html` + `docs/css/style.css` + `docs/js/main.js`: la landing page, **una lingua per volta** con l'interruttore EN/IT (attributi `data-en` / `data-it`; `?lang=it` nell'indirizzo; la scelta resta nel browser). È su `giosci1994.github.io/W11-cleaner-tool/` (GitHub Pages: Source `main`, cartella `/docs`, come gli altri).
  - `docs/social-preview.html` (sorgente) → `docs/social-preview.png` 1280×640, caricata a mano in Settings › Social preview (si ricarica a mano quando cambia).

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
- Nessun dato personale in file, commit, workflow e release: il repository è pubblico **con tutta la storia**.
- Codice di altri progetti: solo MIT o GPL-3.0 compatibili, con l'attribuzione; AGPL si legge solo per le idee.
- Niente privacy/telemetria, niente installazione di app con winget (`docs/decisioni.md`).

## Le decisioni
In `docs/decisioni.md`.

## Da verificare
- Come gli altri progetti firmano l'exe e gestiscono SmartScreen (la firma si decide più avanti).
- **Falsi avvisi sui driver**: corretto in v0.2.0 (`Heuristics.IsWindowsSystemDriver` riconosce i driver Microsoft con data convenzionale 2006 come `Ok` / «driver di Windows»); da verificare sul PC reale.
- Sul PC vero: la regola della RAM (XMP), la chiave `HistoricalCaptureEnabled` della Game Bar, le frequenze dei monitor, la dimensione dell'exe, e tutte le funzioni operative v0.2.0 (pulizia, ottimizzazione, debloat, duplicati, punto di ripristino, annulla e quarantena).
- La landing e la social preview mostrano un'**illustrazione** dell'app: le schermate vere si aggiungono dopo la prova sul PC.
- **Correzioni della revisione v0.2.0** (in `CHANGELOG.md`, v0.2.1), scritte senza compilare: prima la build e i test su Actions, poi sul PC: il punto di ripristino quando ne esiste già uno di meno di 24 ore (deve chiedere «Continuare senza?»), la cache di Windows Update con il servizio attivo, i duplicati da un altro disco (copia verificata, spazio sul disco della quarantena), il riavvio come amministratore con un altro account (avviso sotto il titolo), i nomi accentati dei piani energetici, la chiusura della finestra durante una modifica (bloccata) e durante un'analisi (annulla e chiude), la barra laterale nuova (voce attiva, barra di scorrimento che non copre le voci).

## Il registro dei lavori
| Data | Cosa si è fatto | Cosa si è scoperto |
|---|---|---|
| 18/11/2026 | Creato lo scheletro; piano approvato; v0.1 in sola lettura, repository privato, prima build su Actions | il codice non si compila sulla macchina di sviluppo: gli errori si vedono solo nel log di Actions |
| 19/11/2026 | README solo in inglese; landing page EN/IT e social preview nello stile degli altri repository | Pages non attivabile finché il repository è privato (piano gratuito) |
| 21/11/2026 | Controllo dei dati su tutta la storia, repository **pubblico**, Pages attivo, social preview caricata | con il repository pubblico i badge del README funzionano |
| 21/11/2026 | v0.2.0: fix driver Microsoft 2006, `KeepAwakeService`, `Safety/` (`ActionLog`, `RestorePointService`, `QuarantineService`), `Actions/` (pulizia, ottimizzazione, debloat, duplicati, `UndoCoordinator`), modale `PreviewDialog` e UI WPF completa Base/Esperto con Storico e Quarantena | tutte le modifiche passano da `PlannedChange` + anteprima + storico JSON; gli elementi oltre 30 giorni in quarantena non si eliminano da soli senza conferma |
| 22/11/2026 | Correzioni della revisione v0.2.0 (15 punti): pulizia che rifiuta cartelle pericolose e lascia i file recenti, punto di ripristino verificato prima/dopo e domanda se manca, storico che non si sovrascrive mai (`history.json.bak`) e si scrive voce per voce, duplicati e quarantena verificati con SHA256, quarantena da un altro disco con copia verificata e controllo dello spazio, processi esterni con tempo massimo, `powercfg` con codifica OEM, SID passato al riavvio come amministratore, testi onesti su README e landing, test nuovi | Windows non crea un secondo punto di ripristino entro 24 ore ma risponde 0; scelta per la quarantena: resta in `%LOCALAPPDATA%` (niente cartelle nascoste sugli altri dischi, che sparirebbero con i dischi rimovibili) |
| 23/11/2026 | Landing (`docs/`) rifatta con Unslop: niente sfumature e bagliori, icone al posto delle emoji, tema chiaro con `prefers-color-scheme`, comparsa delle sezioni con Motion 14.10.1 da jsDelivr (con SRI, facoltativo: senza, la pagina si vede lo stesso) | approvato da Giovanni guardando le schermate |
