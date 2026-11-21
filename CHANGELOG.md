# Changelog

All notable changes to this project are listed here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/). From the next release, every version gets its section
here before the tag is pushed.

## [Unreleased]

## [0.2.0] - 2026-11-21

### Added
- English README, bilingual landing page (EN/IT) in `docs/` and social preview with its source
  (`docs/social-preview.html`).
- **Keep-Awake toggle (`KeepAwakeService`)**: temporary "No sleep (download)" switch in the main bar using `SetThreadExecutionState` without changing Windows power settings; automatically released on window close.
- **Safety infrastructure (`Safety/`)**:
  - `PlannedChange` immutable before → after change model with Base explanation and Expert technical details.
  - `RestorePointService` via WMI (`SystemRestore.CreateRestorePoint`), never turning System Protection on by itself and reporting clear warnings if elevation or System Protection is missing.
  - `ActionLog` JSON session and entry log in `%LOCALAPPDATA%\W11-cleaner-tool\history.json`.
  - `QuarantineService` moving user files to `%LOCALAPPDATA%\W11-cleaner-tool\quarantine\<session>\` with `manifest.json`, SHA256 hashes, restore support, and 30-day expiration detection/manual purge.
- **Action services (`Actions/`)**:
  - `CleanupService`: estimates and cleans user `%TEMP%`, `Windows\Temp`, Windows Update download cache, Edge/Chrome/Firefox caches (Cache and Code Cache only — passwords, cookies, and history untouched), and Recycle Bin.
  - `OptimizationService`: reversible startup entry toggling (`StartupApproved`) and Windows power plan switching (`powercfg`).
  - `DebloatService`: conservative curated removal of safe preinstalled UWP apps for the current user, strictly protecting Microsoft Store, Xbox/GamingServices, Windows Security, Edge, Calculator, Photos, Notepad, Terminal, and runtime dependencies; undo re-registers locally or links to Microsoft Store.
  - `DuplicateFinderService`: exact duplicate detection (file size → partial 4 KB SHA256 → full SHA256) keeping the oldest file (or shortest path) and moving duplicates exclusively to Quarantine.
  - `UndoCoordinator`: per-entry and full-session undo across Optimization, Debloat, and Quarantine.
- **WPF UI updates (`src/W11CleanerTool/`)**:
  - Base and Expert modes across read-only checks, *Pulisci*, *Ottimizza*, *Debloat*, *Duplicati* (with native `OpenFolderDialog` and cancellable scan), and *Storico e Quarantena*.
  - Modal `PreviewDialog` showing exact selected changes, freeable space estimate, admin/System Protection warnings, and default-on restore point option for system changes.
- **Unit tests**: added tests for `ActionLog`, `QuarantineService`, `CleanupService`, `OptimizationService`, `DebloatService`, `DuplicateFinderService`, and Microsoft 2006 system driver detection.

### Fixed
- **Microsoft 2006 system driver false warnings**: `Heuristics.IsWindowsSystemDriver` recognizes Microsoft built-in drivers stamped with the conventional `2006-06-21` date as normal Windows drivers (`CheckStatus.Ok`) instead of flagging them as outdated.

## [0.1.0] - 2026-11-18

First version, **read-only**: it checks the PC and changes nothing.

### Added
- Base and Expert mode, with the choice remembered.
- **Gaming** checks: Game Mode, GPU scheduling, power plan, monitor refresh rate, memory
  integrity, Game Bar recording, graphics driver, free space, pending reboot.
- **RAM and drivers** checks: RAM speed vs. rated speed, devices with errors, driver dates.
- **Problems and crashes**: blue screens, unexpected shutdowns, WHEA errors, app crashes and
  hangs, minidumps of the last 30 days, and an anonymized report for an AI.
- Self-contained exe for x64 and ARM64, built by GitHub Actions, with `SHA256SUMS.txt` and
  `VERIFY.ps1` in the release.

### Known issues
- Some built-in Windows drivers are reported as old (dated 2006): it is the date Windows gives
  them, not a real problem.

[Unreleased]: https://github.com/giosci1994/W11-cleaner-tool/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/giosci1994/W11-cleaner-tool/releases/tag/v0.2.0
[0.1.0]: https://github.com/giosci1994/W11-cleaner-tool/releases/tag/v0.1.0
