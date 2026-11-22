# 🧹 W11-cleaner-tool

**A simple cleaning and optimization tool for Windows 11 (Home and Pro): a few big buttons in
Base mode, every technical detail in Expert mode. One portable exe for x64 and ARM64, with
SHA256-verified releases.**

### 🌐 [giosci1994.github.io/W11-cleaner-tool](https://giosci1994.github.io/W11-cleaner-tool/)

<p align="center">
  <a href="https://github.com/giosci1994/W11-cleaner-tool/actions/workflows/build.yml"><img src="https://github.com/giosci1994/W11-cleaner-tool/actions/workflows/build.yml/badge.svg" alt="Build" /></a>
  <a href="https://github.com/giosci1994/W11-cleaner-tool/releases/latest"><img src="https://img.shields.io/github/v/release/giosci1994/W11-cleaner-tool?include_prereleases&color=00b585" alt="Release" /></a>
  <img src="https://img.shields.io/github/downloads/giosci1994/W11-cleaner-tool/total?color=34d399" alt="Downloads" />
  <img src="https://img.shields.io/badge/platform-Windows%2011%20x64%20%7C%20ARM64-0078d4?logo=windows&logoColor=white" alt="Windows 11 x64 | ARM64" />
  <img src="https://img.shields.io/badge/.NET-10-512bd4" alt=".NET 10" />
  <a href="LICENSE"><img src="https://img.shields.io/github/license/giosci1994/W11-cleaner-tool" alt="License" /></a>
  <a href="https://buymeacoffee.com/giosci1994u"><img src="https://img.shields.io/badge/Buy%20Me%20a%20Coffee-FFDD00?logo=buymeacoffee&logoColor=black" alt="Buy Me a Coffee" /></a>
</p>

<p align="center">
  <img src="docs/social-preview.png" width="800" alt="W11-cleaner-tool — checks your PC, changes nothing until you say so" />
</p>

> **Status: version 0.2.1 (pre-release).** Includes read-only diagnostics plus safe cleaning,
> optimization, conservative debloat, duplicate quarantine, change history with undo, and a
> temporary no-sleep toggle. Every system change goes through a **preview dialog**, an optional
> **restore point** (needs administrator rights), and a **JSON history log**. Cleaning and emptying
> the Recycle Bin cannot be undone. Real-PC verification in progress.

> **Note:** the app's interface is in Italian for now. The labels quoted below are the ones you
> will see on screen, with an English translation in brackets.

## Contents

- [Features](#-features)
- [Requirements](#-requirements)
- [Download and verify](#-download-and-verify)
- [Usage](#-usage)
- [Privacy and safety](#-privacy-and-safety)
- [Roadmap](#-roadmap)
- [Development](#-development)
- [Changelog](#-changelog)
- [Support](#-support)
- [License](#-license)

## ✨ Features

Two modes, one switch at the top: **«Modalità Esperto»** (Expert mode). The choice is remembered.

- **Base mode** — a few big cards and plain words. Every result is ✅ / ⚠️ / ❌ with one sentence
  on what it means and what to do.
- **Expert mode** — the same checks with the technical details: registry keys, values, versions.

| Card | What it does |
|---|---|
| **Controlla tutto** (Check everything) | Runs all read-only diagnostics below in one pass |
| **Gaming** | Game Mode, hardware-accelerated GPU scheduling, power plan, monitor refresh rate vs. the maximum, memory integrity (reported only), Game Bar background recording, graphics driver, free space on the system drive, pending reboot |
| **RAM e driver** (RAM and drivers) | RAM speed vs. the rated speed (XMP / EXPO / DOCP, indicative), devices with errors, graphics, network and audio drivers with their date (Microsoft `2006` built-in drivers are recognized as normal Windows drivers) |
| **Problemi e crash** (Problems and crashes) | Blue screens, unexpected shutdowns, hardware errors (WHEA), apps that crash or hang, minidumps of the last 30 days, plus a **report to copy or save for an AI**, without user name, PC name or personal paths |
| **Pulisci** (Clean) | Estimates and cleans user `%TEMP%`, `Windows\Temp`, Windows Update download cache, Edge/Chrome/Firefox caches (cache only — passwords, cookies and history stay intact), and the Recycle Bin. Files touched in the last 24 hours stay, `%TEMP%` pointing to a drive root, the user profile, personal folders, Windows or Program Files is refused, and the Windows Update cache is skipped while Windows Update is running. **Deleted files cannot be restored** |
| **Ottimizza** (Optimize) | Lists startup programs (`StartupApproved`) and Windows power plans (`powercfg`) with reversible toggling and switching |
| **Debloat** | Conservatively detects safe preinstalled UWP bloatware for removal while strictly protecting Microsoft Store, Xbox/GamingServices, Windows Security, Edge, Calculator, Photos, Notepad, Terminal and system runtimes. Undo is best effort: it re-registers the app from the package left on disk, otherwise it opens Microsoft Store to reinstall it |
| **Duplicati** (Duplicates) | Scans chosen folders (size → 4 KB SHA256 → full SHA256), keeps the oldest copy and moves exact duplicates to **Quarantine** (never deletes directly) |
| **Storico e Quarantena** (History & Quarantine) | Undo individual changes or full sessions, reopen Microsoft Store links when an Appx needs reinstalling, restore quarantined files, or purge quarantined items older than 30 days |

A **«Niente sospensione (download)»** (No sleep for downloads) toggle in the top bar prevents Windows from sleeping while the app window stays open, without touching permanent Windows power settings.

## ✅ Requirements

- **Windows 11** Home or Pro, **x64** (Intel / AMD) or **ARM64** (Snapdragon).
- Nothing to install: the exe is self-contained and already includes .NET.
- No administrator rights required to start. **«Riavvia come amministratore»** (Restart as administrator) in the top bar, in both Base and Expert mode, enables reading crash minidumps, creating Windows restore points, cleaning `Windows\Temp` / Windows Update cache, and toggling machine-wide (`HKLM`) startup entries. If Windows asks for a different account's credentials, the app warns you that history, quarantine, `%TEMP%` and apps then belong to that other account.

## 📦 Download and verify

1. From the **[Releases](https://github.com/giosci1994/W11-cleaner-tool/releases)** page, download
   the right exe:
   - `W11-cleaner-tool-x64.exe` for Intel or AMD processors (almost every PC);
   - `W11-cleaner-tool-arm64.exe` for ARM processors (Snapdragon).
2. Download `SHA256SUMS.txt` and `VERIFY.ps1` into the same folder.
3. Check that the files are the original ones, in PowerShell:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\VERIFY.ps1
   ```

   or by hand: `Get-FileHash .\W11-cleaner-tool-x64.exe -Algorithm SHA256`, then compare the hash
   with `SHA256SUMS.txt`.

> **Windows SmartScreen:** the exe is not code-signed, so Windows may show a warning the first
> time. Click **More info → Run anyway**.

## 📖 Usage

1. Open the exe. On the left are the navigation cards; on the right, diagnostics, action lists or history.
2. For diagnostics, click **«Controlla tutto»** (Check everything) or a single card (**Gaming**, **RAM e driver**, **Problemi e crash**). In **Problemi e crash**, **«Copia il rapporto per l'IA»** or **«Salva il rapporto…»** gives you an anonymized summary for an AI assistant.
3. For **Pulisci**, **Ottimizza**, **Debloat**, or **Duplicati**, click the analyze button first, review and check/uncheck items, then click **«Anteprima e applica»** (or **«Sposta in quarantena»** for duplicates).
4. Review the **preview modal** (before → after, estimated space, restore point checkbox) and confirm.
5. Open **«Storico e Quarantena»** at any time to undo reversible changes or restore quarantined files. Each change is written to the history as soon as it is applied.

App data lives in `%LOCALAPPDATA%\W11-cleaner-tool\`:
- `settings.json` — Expert mode preference
- `history.json` — applied sessions and undo metadata (`history.json.bak` keeps the previous version)
- `quarantine\` — moved duplicate files with `manifest.json` and SHA256 hashes

## 🔐 Privacy and safety

- **Preview before changes:** nothing is modified during scans; every change shows *before → after* and requires explicit confirmation.
- **Restore points:** need administrator rights; enabled by default in the preview modal for system changes when running as administrator; never forces System Protection on if disabled. Windows creates at most one restore point every 24 hours: if no new point is created, nothing is applied until you choose to continue without one.
- **Undo & Quarantine:** startup entries, power plans and duplicate files (moved to quarantine, with manual purge suggested only after 30 days; restore checks the SHA256 first) can be reverted from **«Storico e Quarantena»**. Removed apps are re-registered from the local package when it is still there, otherwise you get a Microsoft Store link. **Cleaning (temporary files, caches) and emptying the Recycle Bin cannot be undone**, and files purged from quarantine are gone for good.
- **Quarantine space:** moving a file to quarantine does not free space until the quarantine is purged; files from another drive are copied (and verified) to the quarantine drive, which needs enough free space.
- **Anonymized crash report:** strips user name, PC name, domain and profile paths.
- **No telemetry, no network calls, no installation of apps (`winget`).**

## 🗺️ Roadmap

1. ✅ Read-only checks: gaming checklist, RAM and drivers (with Microsoft `2006` driver handling), crash report for an AI.
2. ✅ No sleep while downloads are running (only while the app is open).
3. ✅ Cleaning: temporary files, browser caches, Windows Update cache, recycle bin — an estimate first.
4. ✅ Optimization: reversible startup programs and power plan switching.
5. ✅ Debloat: conservative UWP bloatware removal with protected system packages and best-effort undo.
6. ✅ Duplicate files moved to quarantine (never deleted straight away), with restore and 30-day purge prompt.
7. Real-PC verification across Windows 11 Home/Pro and future newsletter cleanup module (separate).

## 💻 Development

The exe is built by **GitHub Actions** on a Windows runner:

- every push to `main` and every pull request: tests, x64 and ARM64 exe and `SHA256SUMS.txt` as a
  build artifact → [`.github/workflows/build.yml`](.github/workflows/build.yml);
- every `v*` tag: a release (marked pre-release while the version is 0.x) with both exe,
  `SHA256SUMS.txt` and `VERIFY.ps1` → [`.github/workflows/release.yml`](.github/workflows/release.yml).

On Windows with the **.NET 10 SDK**:

```powershell
dotnet test tests/W11CleanerTool.Tests/W11CleanerTool.Tests.csproj
./scripts/publish.ps1          # exe and SHA256SUMS.txt end up in artifacts/
```

### Project structure

```
src/W11CleanerTool/         WPF interface (.NET 10, Fluent theme, Base/Expert modes, PreviewDialog)
src/W11CleanerTool.Core/    Core engine (checks, KeepAwakeService, Safety/, Actions/, anonymizer)
tests/                      Unit tests (heuristics, anonymizer, safety, actions) and runner smoke tests
scripts/                    publish.ps1 (x64 + ARM64, SHA256) and VERIFY.ps1
docs/                       landing page, social preview, decisions, plan, research (Italian)
CHANGELOG.md                Release history
```

The project notes in `docs/` (decisions, plan, research on similar tools) are written in Italian.

## 📝 Changelog

Every version and what changed: [`CHANGELOG.md`](CHANGELOG.md).

## ☕ Support

If this tool is useful to you, you can support its development:
**[buymeacoffee.com/giosci1994u](https://buymeacoffee.com/giosci1994u)** ☕

## 📄 License

[MIT](LICENSE). No code is copied from other projects; ideas and known issues of similar tools are
collected in [`docs/ricerca-progetti-simili.md`](docs/ricerca-progetti-simili.md).
