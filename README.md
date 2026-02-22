---

░▒▓███████▓▒░░▒▓█▓▒░░▒▓█▓▒░░▒▓██████▓▒░░▒▓███████▓▒░▒▓████████▓▒░▒▓██████▓▒░░▒▓██████████████▓▒░  
░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░ ░▒▓█▓▒░  ░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░░▒▓█▓▒░ 
░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░ ░▒▓█▓▒░  ░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░░▒▓█▓▒░ 
░▒▓███████▓▒░░▒▓████████▓▒░▒▓████████▓▒░▒▓█▓▒░░▒▓█▓▒░ ░▒▓█▓▒░  ░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░░▒▓█▓▒░ 
░▒▓█▓▒░      ░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░ ░▒▓█▓▒░  ░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░░▒▓█▓▒░ 
░▒▓█▓▒░      ░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░ ░▒▓█▓▒░  ░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░░▒▓█▓▒░ 
░▒▓█▓▒░      ░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░ ░▒▓█▓▒░   ░▒▓██████▓▒░░▒▓█▓▒░░▒▓█▓▒░░▒▓█▓▒░ 
                                                                                                  
---
                                                                                                  
A portable, self-contained Windows administration utility built with WPF and .NET 8. Phantom provides a unified interface for system monitoring, tweaking, app management, Windows Update control, and automation — all from a single elevated window with a persistent in-app console.

> **Requires Administrator privileges.** Phantom will not auto-elevate and will exit cleanly if launched without elevation.

---

## Quick Start

Run the following in an **elevated** PowerShell window — no setup required:

```powershell
irm https://raw.githubusercontent.com/xobash/phantom/main/launch.ps1 | iex
```

The launcher will:

- Check for the .NET 8 SDK and install it via winget if missing
- Download and build Phantom from source
- Launch the application

Built output is saved to `%LOCALAPPDATA%\Phantom\app`. Subsequent launches can run `Phantom.exe` directly without rebuilding.

---

## What Is Phantom?

Phantom is a personal Windows admin tool in the spirit of [ChrisTitusTech/winutil](https://github.com/ChrisTitusTech/winutil) and WinToys. It aims to consolidate the scattered day-to-day tasks of Windows administration — debloating, tweaking, update management, app installs, and system diagnostics — into a single, auditable, offline-capable interface.

Everything runs locally. No telemetry leaves the machine, no background services are installed, and the app makes no outbound connections unless you explicitly trigger an action that requires one.

---

## Features

### Home

- Live system overview: motherboard, GPU (with driver version), storage, uptime, CPU, memory, Windows version, and WinSAT performance score
- KPI tiles with user-configurable refresh intervals
- Searchable, virtualized lists of installed apps, running processes, and active services
- On-demand WinSAT benchmark

### Store

- Detects winget and Chocolatey at launch; offers to install either if missing
- Install, uninstall, and upgrade applications from a local JSON catalog
- Import and export catalog JSON for sharing or version control

### Tweaks

- JSON-defined tweak catalog with fields for `Id`, `Name`, `Description`, `RiskTier`, `Scope`, and `Reversible`
- Per-tweak state capture and undo support, persisted to `./data/state.json`
- Apply or undo individual tweaks or batch selections
- Basic and Advanced presets
- Import and export tweak selections

### Features

- Toggle optional Windows features with live status detection
- Prompts for reboot when a feature change requires it

### Fixes

- Curated fix operations with full console logging
- Direct launch of legacy system panels (e.g., System Properties, Device Manager)

### Updates

Three discrete update modes:

| Mode | Behavior |
|---|---|
| Default | Restores Windows Update to out-of-box defaults |
| Security | Defers feature updates 365 days, quality updates 4 days |
| Disable All | Stops and disables Windows Update services |

Additional capabilities:
- Live `wuauserv` and BITS service status
- Active Windows Update Group Policy summary
- Reset Update Components — clears the SoftwareDistribution cache and restarts services
- All modes are reversible

### Automation

- Export and import a unified config JSON covering Store, Tweaks, Features, Fixes, and Update mode
- Preview mode to review what will run before execution
- CLI support for unattended/scripted runs

### Logs & Settings

- Rolling session logs stored under `./logs`
- Configurable log retention by file count and total size cap
- Dark/Light mode toggle (persisted across sessions)
- Enable/disable gate for destructive operations
- Home refresh interval control

---

## In-App Console

Every operation streams output to an embedded console panel on the right side of the window. It is always visible and cannot be dismissed. Controls:

| Button | Action |
|---|---|
| Cancel | Cancels the currently running operation |
| Copy Log | Copies the current session log to clipboard |
| Open Logs Folder | Opens `./logs` in Explorer |

---

## Risk Tiers

All operations carry one of three risk designations:

| Tier | Meaning |
|---|---|
| `Basic` | Safe, non-destructive, no reboot required |
| `Advanced` | Modifies system settings; undo is available |
| `Dangerous` | Irreversible or high-impact; requires explicit in-prompt confirmation and the Settings toggle |

Dangerous operations are gated behind both the **Enable Destructive Operations** toggle in Settings and an explicit `ARE YOU SURE? (Y/N)` prompt at runtime.

---

## Building

The `irm` one-liner above handles everything automatically. To build manually:

**Prerequisites:** Windows 10/11 x64, .NET 8 SDK, elevated PowerShell

```powershell
# From the project root (elevated PowerShell)
./build-portable.ps1
```

Output is written to `./app/`. The build is self-contained — no separate .NET runtime installation is required on the target machine.

---

## Usage

**GUI mode:**

```powershell
./app/Phantom.exe
```

**CLI / unattended mode:**

```powershell
./app/Phantom.exe -Config <path-to-config.json> -Run
```

Add `-ForceDangerous` to allow dangerous operations in CLI mode. This flag is only respected when the config also sets `"confirmDangerous": true`.

```powershell
./app/Phantom.exe -Config ./my-config.json -Run -ForceDangerous
```

### Caveats

- Administrator elevation is mandatory. The app will not prompt for it — launch from an already-elevated shell.
- The `-ForceDangerous` flag in CLI mode bypasses the interactive confirmation prompt. Use with care in automation pipelines.
- WinSAT scores may be stale if Windows has not run a formal assessment; Phantom can trigger one on demand but it takes several minutes.
- Feature toggles that require a reboot will not take effect until the system is restarted — Phantom will warn you but cannot force a reboot.
- The Chocolatey install script (`community.chocolatey.org`) requires an internet connection and is only fetched when you explicitly choose to install Chocolatey.

---

## Local Data

All data is stored relative to the executable. Nothing is written outside the paths below.

| Path | Contents |
|---|---|
| `%LOCALAPPDATA%\Phantom\app\` | Built application binaries (created by launcher) |
| `./data/settings.json` | UI preferences and behavior flags |
| `./data/state.json` | Undo state for applied tweaks |
| `./data/telemetry-local.json` | Local-only stats: space cleaned, first-run date, network baselines |
| `./logs/` | Rolling session logs |

Log retention is configurable in Settings by max file count and max total size. No data is sent off-machine. To fully clean up after Phantom, delete `%LOCALAPPDATA%\Phantom` and the directory you ran the app from.

---

## Offline Behavior

Phantom is designed to be useful without a network connection. Operations tagged `RequiresNetwork: true` are blocked before execution when the machine is offline, with an explicit error in the console — no silent failures or hanging requests.

The following capabilities require internet access and will be unavailable offline:

- Installing or upgrading apps via winget or Chocolatey
- Installing winget or Chocolatey if they are missing
- Any tweak, fix, or feature operation explicitly marked `RequiresNetwork`

All other features — monitoring, tweaks, update mode changes, fixes, log review, and settings — function fully without a connection.

---

## Dependencies

| Dependency | Version | Purpose |
|---|---|---|
| .NET | 8.0 | Runtime and WPF framework |
| Microsoft.PowerShell.SDK | 7.4.6 | In-process PowerShell execution |
| winget | any | Package management for app installs (auto-detected) |
| Chocolatey | any | Package management for app installs (auto-detected) |

The build output is self-contained — the .NET 8 runtime is bundled. winget and Chocolatey are optional; Phantom detects them at runtime and offers installation if absent.

---

## Security Notes

**Elevation model:** Phantom requires administrator privileges and verifies elevation on startup. It does not use UAC auto-elevation (`requireAdministrator` manifest), so the calling shell must already be elevated. This is intentional — it prevents privilege escalation via accidental double-click.

**PowerShell execution:** All PowerShell logic runs in-process via `Microsoft.PowerShell.SDK`. No `powershell.exe` or `pwsh.exe` child processes are spawned for core operations. This means Phantom's PowerShell commands are not visible to process monitors watching for child shell spawns, but it also means they are subject to the execution policy of the current session.

**No automatic outbound connections:** Phantom makes no network calls on its own. The only outbound URLs in the codebase are the official winget installer (`aka.ms/getwinget`) and the Chocolatey install script (`community.chocolatey.org/install.ps1`), both of which are only triggered by an explicit user action.

**Dangerous operation gates:** Irreversible operations require both a Settings toggle and an in-prompt confirmation. In CLI mode, `-ForceDangerous` bypasses the interactive prompt but still requires `"confirmDangerous": true` in the config file — preventing accidental execution from a bare command line.

**Supply chain considerations:** The `irm | iex` pattern downloads and executes a remote PowerShell script. If you are security-conscious about this, review `launch.ps1` in the repository before running it. The script's only external action is installing the .NET 8 SDK via winget and cloning/building from this repository.

**No persistence mechanisms:** Phantom installs no services, scheduled tasks, startup entries, or shell extensions. Removing the app directory and `%LOCALAPPDATA%\Phantom` leaves no running components behind.

**Local data only:** No usage data, diagnostics, or telemetry is transmitted externally. `telemetry-local.json` is local-only and contains non-sensitive operational stats (space cleaned, run dates, network baselines).

---

## License

[GPL-3.0](LICENSE)
