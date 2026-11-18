# W11-cleaner-tool

Tool di **pulizia e ottimizzazione per Windows 11** (Home e Pro), con una schermata semplice e pochi pulsanti.

- **Modalità Base**: poche schede grandi, risultati con ✅ ⚠️ ❌ e una frase semplice su cosa fare.
- **Modalità Esperto**: gli stessi controlli con i dettagli tecnici (chiavi di registro, valori, versioni).

> **Stato: versione 0.1, solo lettura.** Questa versione controlla il PC e **non cambia niente**. Pulizia, ottimizzazione e le altre funzioni arriveranno dopo, sempre con anteprima, punto di ripristino e annulla.

## Cosa fa oggi
| Scheda | Cosa controlla |
|---|---|
| **Gaming** | Modalità gioco, pianificazione GPU accelerata, piano energetico, frequenza dei monitor, integrità della memoria, registrazione in background della Game Bar, driver della scheda video, spazio sul disco di sistema, riavvio in sospeso |
| **RAM e driver** | velocità della RAM rispetto a quella dichiarata (XMP / EXPO / DOCP, indicativo), dispositivi con errori, driver di video, rete e audio con la loro data |
| **Problemi e crash** | schermate blu, spegnimenti improvvisi, errori hardware (WHEA), app che si chiudono o si bloccano, minidump degli ultimi 30 giorni; **rapporto da copiare o salvare per un'IA**, senza nome utente, nome del PC e percorsi personali |

## Scaricare e controllare
1. Dalla pagina **Releases** scarica l'exe giusto:
   - `W11-cleaner-tool-x64.exe` per i PC con processore Intel o AMD (quasi tutti);
   - `W11-cleaner-tool-arm64.exe` per i PC con processore ARM (Snapdragon).
2. Scarica anche `SHA256SUMS.txt` e `VERIFY.ps1` nella stessa cartella.
3. Controlla che i file siano quelli originali, in PowerShell:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\VERIFY.ps1
   ```
   oppure a mano: `Get-FileHash .\W11-cleaner-tool-x64.exe -Algorithm SHA256` e confronta il codice con `SHA256SUMS.txt`.
4. L'exe non è firmato: alla prima apertura Windows SmartScreen può avvisare. Scegli «Ulteriori informazioni» › «Esegui comunque».

Non serve installare niente: l'exe contiene già .NET. Parte senza diritti di amministratore; in modalità Esperto c'è «Riavvia come amministratore» per leggere anche i minidump.

## Costruire
L'exe lo costruisce **GitHub Actions** (`.github/workflows/`):
- a ogni push su `main`: test, exe x64 e ARM64 e `SHA256SUMS.txt` come artifact della build;
- a ogni tag `v*`: una Release con gli exe, `SHA256SUMS.txt` e `VERIFY.ps1`.

Su Windows con il .NET 10 SDK: `./scripts/publish.ps1` (i file finiscono in `artifacts/`).

Struttura: `src/W11CleanerTool` (interfaccia WPF) · `src/W11CleanerTool.Core` (i controlli, senza interfaccia) · `tests/` · `docs/` (decisioni, piano, ricerca).

## Licenza
[MIT](LICENSE). Nessun codice è copiato da altri progetti; le idee e i problemi noti degli altri tool sono in `docs/ricerca-progetti-simili.md`.
