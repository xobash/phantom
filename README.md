# Phantom (Portable Admin Utility)

## What is included in this artifact
- WPF `.NET 8` project under `./Phantom` with a full shell:
  - Left navigation: Home, Store, Tweaks, Features, Fixes, Updates, Automation, Logs/About, Settings.
  - Main content with card/grid layouts.
  - Persistent right sidebar embedded console (in-window RichTextBox) with:
    - live command echo + output stream lines,
    - `Cancel`, `Copy Log`, and `Open Logs Folder` actions.
- Unified PowerShell operation engine (`OperationEngine`) with:
  - async execution,
  - cancellation support,
  - dry-run support,
  - prechecks (admin, platform, disk, offline guard),
  - dangerous gating with exact prompt string: `ARE YOU SURE? (Y/N)`,
  - best-effort state capture + undo persistence to `./data/state.json`,
  - managed/restricted surfacing through console/status handling.
- Offline-first behavior for network-required actions (blocked before execution with clear console error).
- Admin-only startup gate (no auto-elevation; exits before main window if non-admin).
- Local state and portability conventions:
  - `./data/settings.json`
  - `./data/telemetry-local.json`
  - `./data/state.json`
  - rolling logs under `./logs`
- Home tab implementation:
  - periodic refresh (default 5s, configurable),
  - manual refresh,
  - system cards, KPI tiles, searchable/virtualized apps/processes/services lists,
  - WinSAT on-demand score path with fallback to `Unavailable`.
- Store tab implementation:
  - winget/choco detection,
  - install missing manager operation,
  - install/uninstall/upgrade from JSON catalog,
  - import/export catalog JSON.
- Tweaks tab implementation:
  - JSON-defined tweak catalog (`Id/Name/Description/RiskTier/Scope/Reversible/Detect/Apply/Undo/StateCaptureKeys`),
  - refresh status, apply, undo, presets, import/export selection.
- Features/Fixes/Legacy panels:
  - optional feature toggles with status and reboot flow,
  - fix operations with logging,
  - legacy panel launch commands.
- Updates tab:
  - modes: Default / Security / Disable All,
  - apply and undo-to-default,
  - service/policy status display,
  - reset update components action.
- Automation tab:
  - export/import unified config JSON,
  - preview.
- CLI mode in app startup:
  - `Phantom.exe -Config <path> -Run`
  - dangerous gating requires config `confirmDangerous=true` plus `-ForceDangerous`.

## Latest UI refinements in this artifact
- Added a Settings toggle for `Dark mode` (`Settings > Dark mode`), persisted in `./data/settings.json`.
- Increased overall typography by ~2 points across controls, grids, and labels for better readability.
- Updated dark theme to a neutral gray palette (removed navy-heavy panel/navigation tones).
- Home tab cards and data grids now fully follow the active theme (dark or light), with legible foreground text.
- Fixed dark-mode contrast issues where black text could appear on dark backgrounds.
- Embedded right-side console is always black (both dark and light modes) to stay visually distinct.

## Latest functional fixes in this artifact
- Fixed runspace execution initialization to avoid compile errors and allow process-scope execution policy bypass setup before script execution.
- Store tab:
  - Package manager booleans now color-coded (`True` = green, `False` = red).
  - Replaced `Category` grid column with a human-readable `Name` column (bound to app display name).
- Home tab:
  - Added dedicated 1-second live refresh for `CPU %` and `Memory %` KPI tiles.
  - This 1-second refresh is independent of the user-configured Home refresh interval.
  - The configured interval still controls the full dashboard refresh cycle.

## Environment note for this build artifact
This packaging environment did not contain the `dotnet` SDK, so a compiled `Phantom.exe` could not be produced here.
The complete source and portable build scripts are included so the portable binary can be produced on Windows immediately.

## Build portable binary on Windows
1. Open elevated PowerShell in this folder (`dist/win-x64`).
2. Run:
   - `./build-portable.ps1`
3. Published output will be in:
   - `./app`

## Run
- GUI:
  - `./app/Phantom.exe`
- CLI:
  - `./app/Phantom.exe -Config <path> -Run`
  - add `-ForceDangerous` only with `confirmDangerous=true` in config.
