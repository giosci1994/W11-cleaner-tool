# Ricerca: progetti simili su GitHub

Fatta il 18/11/2026 con `gh` e Microsoft Learn. Stelle, licenze e date dei primi sei progetti sono state ricontrollate con `gh api` lo stesso giorno. Quello che non è stato controllato davvero è segnato «non verificato».

## I progetti
| Progetto | Licenza | Stelle | Ultimo push | Tecnologia | Distribuzione | Note |
|---|---|---|---|---|---|---|
| [Raphire/Win11Debloat](https://github.com/Raphire/Win11Debloat) | MIT | 63.200 | 15/11/2026 | PowerShell, GUI facoltativa | zip + `Get.ps1`, `SHA256SUMS` nella release | debloat, telemetria; Windows 10 e 11 |
| [ChrisTitusTech/winutil](https://github.com/ChrisTitusTech/winutil) | MIT | 66.289 | 17/11/2026 | PowerShell + WPF | `winutil.ps1` nella release | tweak, installazione di app, fix; modalità semplice e avanzata |
| [farag2/Sophia-Script-for-Windows](https://github.com/farag2/Sophia-Script-for-Windows) | MIT | 9.968 | 16/11/2026 | modulo PowerShell | zip per Win10/Win11, LTSC, ARM | molto configurabile, per esperti |
| [hellzerg/optimizer](https://github.com/hellzerg/optimizer) | GPL-3.0 | 18.278 | 12/11/2026 | C# WinForms | `Optimizer-17.0.exe` nella release | ottimizzatore con GUI |
| [bleachbit/bleachbit](https://github.com/bleachbit/bleachbit) | GPL-3.0 | 7.530 | 17/11/2026 | Python | release v6.0.6 | solo pulizia (temp, cache, log) |
| [ds99-01/DSOptimizer](https://github.com/ds99-01/DSOptimizer) | MPL-2.0 | 21 | 16/11/2026 | C# | portable e installer | debloat, **annulla le modifiche con lo storico**, app con winget |
| [undergroundwires/privacy.sexy](https://github.com/undergroundwires/privacy.sexy) | AGPL-3.0 | 6.183 | 13/02/2026 | TypeScript, web | non verificato | privacy e sicurezza |
| [teeotsa/windows-11-debloat](https://github.com/teeotsa/windows-11-debloat) | MIT | 619 | 10/11/2026 | PowerShell | non verificato | debloat |

## Le funzioni previste: chi le fa già
| Funzione | Chi la fa | Note |
|---|---|---|
| Pulizia (temp, cache, cestino) | BleachBit; in parte Win11Debloat e winutil | vecchi aggiornamenti: non verificato |
| Ottimizzazione (avvio, energia) | winutil, Sophia, optimizer | comandi usati: non verificato |
| Debloat | Win11Debloat, winutil, Sophia, teeotsa | |
| Punto di ripristino | Win11Debloat e Sophia lo fanno o lo propongono | |
| Checklist gaming | **nessuno trovato** | |
| XMP della RAM e driver | **nessuno trovato** | |
| Niente sospensione durante i download | non verificato | |
| Log dei crash da esportare per un'IA | **nessuno trovato per utenti normali** | |
| Newsletter nella posta | **nessuno trovato** | |
| Duplicati in quarantena | **nessuno trovato** | |

Le funzioni «nessuno trovato» sono quelle che distinguono questo progetto.

## I problemi ricorrenti degli altri (dalle issue)
- **Antivirus**: falsi positivi. Win11Debloat #663 e #668 (una DLL compilata al momento fa scattare l'antivirus); winutil #2484.
- **Cose rotte dai tweak**: winutil #5088 («Telemetry - Disable» rovina Explorer), #5023 (cronologia degli appunti), #4872 (un programma del produttore non parte più); Win11Debloat #606, #576 (punti di ripristino persi).
- Lezione: ogni modifica con anteprima, punto di ripristino e un modo per annullarla.

## Licenze
- Il codice **MIT** (Win11Debloat, winutil, Sophia, teeotsa) si può riusare, tenendo l'avviso di copyright.
- Il codice **MPL-2.0** (DSOptimizer) si riusa solo file per file, e quei file restano MPL.
- Il codice **GPL-3.0 e AGPL-3.0** (optimizer, BleachBit, privacy.sexy) non si copia: si legge solo per le idee.
- Non è un parere legale.

## 32 o 64 bit
- **Windows 11 esiste solo a 64 bit**: i requisiti chiedono un «compatible 64-bit processor» ([Windows 11 requirements](https://learn.microsoft.com/en-us/windows/whats-new/windows-11-requirements)).
- Un processo a 32 bit su Windows a 64 bit viene reindirizzato da `System32` a `SysWOW64` ([File System Redirector](https://learn.microsoft.com/en-us/windows/win32/winprog64/file-system-redirector)): un tool di sistema a 32 bit vedrebbe le cartelle sbagliate.
- Quindi le build sensate sono **x64** e **ARM64**.

## Il nome
- «W11-cleaner-tool»: nessun repository con questo nome su GitHub (`gh search repos`, 18/11/2026). Marchi e altri siti: non verificato.

## Non ancora verificato
- Come costruiscono l'exe (dotnet publish, ps2exe), se firmano il codice, come gestiscono SmartScreen, se usano GitHub Actions per le release.
- I comandi e le API usati per ogni funzione: si guardano nel codice dei progetti MIT quando si scrive quella funzione.
