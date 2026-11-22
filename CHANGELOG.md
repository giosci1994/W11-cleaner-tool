# Changelog

All notable changes to this project are listed here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/). From the next release, every version gets its section
here before the tag is pushed.

## [Unreleased]

## [0.2.1] - 2026-11-22

### Changed
- **Sidebar** redesigned like Windows 11 Settings: compact flat items with a short caption (full
  description in the tooltip), accent pill on the active item, a divider, and room for the overlay
  scrollbar so it no longer covers the buttons.

### Fixed
- **Cleaning safety**: a cleanup folder that is a drive root, the user profile (or contains it),
  Desktop, Documents, Downloads, Pictures, Videos, Music, Windows, `System32` or Program Files is
  refused with the reason shown in the preview and in the result; files in `%TEMP%` and
  `Windows\Temp` touched in the last 24 hours are kept.
- **Windows Update cache** is skipped ("Windows Update is running: try again later") while
  `wuauserv` or `BITS` is running; services are never stopped by the app.
- **Restore points**: Windows silently skips a second restore point within 24 hours and still
  returns 0. The newest restore point (WMI `SystemRestore`, sequence number) is now compared before
  and after: if no new point exists the result says so, with the date of the existing one.
  `END_SYSTEM_CHANGE` is not used (WMI does not return the sequence number; `BEGIN_SYSTEM_CHANGE`
  alone already creates a complete point, like `Checkpoint-Computer`).
- **No change without the requested restore point**: Clean, Optimize, Debloat and Duplicates stop
  before applying anything and the app asks "Continue without a restore point?" (Yes/No).
- **History (`history.json`)**: a read failure caused by a locked file or missing permissions no
  longer returns an empty list that the next save would write over the real history; the
  operation does not start. Every save keeps `history.json.bak`. Each applied item is written to
  the history right away, so a crash mid-way keeps the trail. The window cannot be closed while a
  change is being applied, undone or restored; closing during a read-only scan cancels the scan.
  If `history.json` is corrupt, the history is recovered from `history.json.bak`, and the corrupt
  file never replaces the good backup.
- **Duplicates**: before moving a copy, the kept original must still exist and both files must
  still match the SHA256 from the scan. One list of seen files for all chosen folders, and folders
  inside other chosen folders are dropped (a file could show up as a copy of itself).
- **Quarantine**: files from another drive are copied, verified with SHA256 and only then deleted,
  after checking free space on the quarantine drive (1 GB margin). Restore checks the SHA256 first
  and refuses a changed file. After purging items older than 30 days, their history entries are
  marked as no longer undoable. The summary no longer says space was freed by moving to quarantine.
- **External processes** (`powershell`, `powercfg`): asynchronous stdout/stderr reading, timeout
  (2 minutes / 15 seconds), the whole process tree is killed on timeout with a clear error.
- **Power plans**: `powercfg` output is read with the console OEM code page (accented plan names);
  a plan switch is not applied if the active plan cannot be read (it could not be undone).
- **Restart as administrator** passes the original user SID; if the elevated instance runs as a
  different account, a warning says that history, quarantine, `%TEMP%` and apps belong to it.
- **Honest promises**: README and landing page say exactly what can be undone (not cleaning nor the
  Recycle Bin), that restore points need administrator rights, and that the restart button is in
  both modes. Debloat items are marked as partly reversible (local package or Microsoft Store). The
  unexpected-error message no longer claims nothing was changed.

### Added
- Tests for `RestorePointService` (return codes, before/after comparison), `UndoCoordinator`,
  `DuplicateFinderService.Apply`, cleanup with junctions/symlinks, protected `%TEMP%` subfolders,
  dangerous cleanup folders, locked `history.json`, quarantine from another drive (simulated) and
  power plan switching without an active plan.

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

[Unreleased]: https://github.com/giosci1994/W11-cleaner-tool/compare/v0.2.1...HEAD
[0.2.1]: https://github.com/giosci1994/W11-cleaner-tool/releases/tag/v0.2.1
[0.2.0]: https://github.com/giosci1994/W11-cleaner-tool/releases/tag/v0.2.0
[0.1.0]: https://github.com/giosci1994/W11-cleaner-tool/releases/tag/v0.1.0
