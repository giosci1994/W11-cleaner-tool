# Changelog

All notable changes to this project are listed here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/). From the next release, every version gets its section
here before the tag is pushed.

## [Unreleased]

### Added
- English README, bilingual landing page (EN/IT) in `docs/` and social preview with its source
  (`docs/social-preview.html`).
- This changelog.

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

[Unreleased]: https://github.com/giosci1994/W11-cleaner-tool/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/giosci1994/W11-cleaner-tool/releases/tag/v0.1.0
