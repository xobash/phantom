# Phantom

A portable, self-contained Windows admin utility built with WPF and .NET 8. Phantom provides a unified interface for system monitoring, tweaks, app management, Windows Update control, and automation — all from a single elevated window with a persistent in-app console.

> **Requires Administrator privileges.** Phantom will exit cleanly if launched without elevation.

---

## Features

### 🏠 Home
- Live system cards: motherboard, GPU (with driver info), storage, uptime, CPU, memory, Windows version, and WinSAT performance score
- KPI tiles with 1-second live refresh for CPU % and Memory %
- Searchable, virtualized lists of installed apps, running processes, and services
- Configurable full-dashboard refresh interval (default: 5s)
- On-demand WinSAT benchmark

### 🛒 Store
- Detects winget and Chocolatey; installs either if missing
- Install, uninstall, and upgrade apps from a local JSON catalog
- Import/export catalog JSON for sharing or version control
- Color-coded package manager status (green = available, red = missing)

### 🔧 Tweaks
- JSON-defined tweak catalog with `Id`, `Name`, `Description`, `RiskTier`, `Scope`, `Reversible` fields
- Per-tweak state capture and undo support persisted to `./data/state.json`
- Apply and undo individual tweaks or batch selections
- Basic and Advanced presets
- Import/export tweak selections
- Dry-run mode to preview changes without applying them

### ⚡ Features
- Toggle optional Windows features with current status detection
- Reboot prompts for features that require it

### 🩺 Fixes
- Curated fix operations with full console logging
- Launch legacy system panels directly

### 🔄 Updates
- Three update modes: **Default** (restore Windows defaults), **Security** (defer feature updates 365 days, quality updates 4 days), **Disable All** (stop and disable update services)
- View live `wuauserv` and BITS service status
- Read active Windows Update Group Policy summary
- Reset Update Components (clears SoftwareDistribution cache, restarts services)
- All update modes are reversible

### 🤖 Automation
- Export and import a unified config JSON covering Store, Tweaks, Features, Fixes, and Update mode
- Preview mode before execution
- CLI support for unattended runs

### 📋 Logs / About
- Rolling session logs stored under `./logs`
- Configurable log retention (max files and max total size)

### ⚙️ Settings
- Dark / Light mode toggle (persisted)
- Enable/disable destructive operations gate
- Home refresh interval
- Log retention limits

---

## In-App Console

Every operation streams output to an embedded right-side console (always dark, always visible). Controls:

| Button | Action |
|---|---|
| Cancel | Cancels the running operation |
| Copy Log | Copies the current session log to clipboard |
| Open Logs Folder | Opens `./logs` in Explorer |

---

## Risk Tiers

All operations are tagged with one of three risk tiers:

| Tier | Meaning |
|---|---|
| `Basic` | Safe, non-destructive, no reboot required |
| `Advanced` | Modifies system settings; undo available |
| `Dangerous` | Irreversible or high-impact; requires explicit confirmation (`ARE YOU SURE? (Y/N)`) |

Dangerous operations are additionally gated behind the **Enable Destructive Operations** toggle in Settings.

---

## Building (Windows)

**Prerequisites:** .NET 8 SDK, Windows 10/11 x64, elevated PowerShell

```powershell
# From the project root (elevated PowerShell)
./build-portable.ps1
```

Output is written to `./app/`. The build is self-contained — no .NET runtime installation required on the target machine.

---

## Running

**GUI mode:**
```powershell
./app/Phantom.exe
```

**CLI / unattended mode:**
```powershell
./app/Phantom.exe -Config <path-to-config.json> -Run
```

Add `-ForceDangerous` to allow dangerous operations in CLI mode. This flag is only respected when the config also includes `"confirmDangerous": true`.

```powershell
./app/Phantom.exe -Config ./my-config.json -Run -ForceDangerous
```

---

## Automation Config Format

Export a config from the Automation tab or create one manually:

```json
{
  "confirmDangerous": false,
  "storeSelections": ["googlechrome", "vscode"],
  "tweaks": ["tweak.disable.telemetry", "tweak.disable.cortana"],
  "features": [],
  "fixes": [],
  "updateMode": "Security"
}
```

---

## Local Data

All data is stored relative to the executable. Nothing leaves the machine.

| Path | Contents |
|---|---|
| `./data/settings.json` | UI and behavior preferences |
| `./data/state.json` | Undo state for applied tweaks |
| `./data/telemetry-local.json` | Local stats (space cleaned, first-run date, network baselines) |
| `./logs/` | Rolling session logs |

---

## Offline Behavior

Operations that require network access (`RequiresNetwork: true`) are blocked before execution if the machine is offline, with a clear error message in the console. No silent failures.

---

## App Catalog

The Store tab is driven by `./Data/catalog.apps.json`. Each entry follows this shape:

```json
{
  "id": "googlechrome",
  "name": "Google Chrome",
  "wingetId": "Google.Chrome",
  "chocoId": "googlechrome",
  "homepage": "https://www.google.com/chrome/",
  "category": "Browser"
}
```

---

## Tweaks Catalog

Tweaks are defined in `./Data/tweaks.json`. Each entry supports:

```json
{
  "Id": "tweak.disable.telemetry",
  "Name": "Disable Telemetry",
  "Description": "Sets telemetry level to Security (minimal).",
  "RiskTier": "Basic",
  "Scope": "System",
  "Reversible": true,
  "Detect": "...",
  "Apply": "...",
  "Undo": "...",
  "StateCaptureKeys": ["HKLM:\\..."]
}
```

---

## Dependencies

| Dependency | Version | Purpose |
|---|---|---|
| .NET | 8.0 | Runtime and WPF framework |
| Microsoft.PowerShell.SDK | 7.4.6 | In-process PowerShell execution |
| winget | any | App installs (auto-detected) |
| Chocolatey | any | App installs (auto-detected) |

---

## Security Notes

- Phantom requires and verifies administrator elevation on startup — it will not auto-elevate.
- All PowerShell scripts run in-process via the official `Microsoft.PowerShell.SDK`; no `powershell.exe` child processes are spawned for core operations.
- No network calls are made automatically. The only outbound URLs in the codebase are the official winget installer (`aka.ms/getwinget`) and the Chocolatey install script (`community.chocolatey.org/install.ps1`), both triggered only by explicit user action.
- Dangerous operations require both the Settings toggle and an in-prompt confirmation.
