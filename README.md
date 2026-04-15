```

░▒▓███████▓▒░░▒▓█▓▒░░▒▓█▓▒░░▒▓██████▓▒░░▒▓███████▓▒░▒▓████████▓▒░▒▓██████▓▒░░▒▓██████████████▓▒░  
░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░ ░▒▓█▓▒░  ░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░░▒▓█▓▒░ 
░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░ ░▒▓█▓▒░  ░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░░▒▓█▓▒░ 
░▒▓███████▓▒░░▒▓████████▓▒░▒▓████████▓▒░▒▓█▓▒░░▒▓█▓▒░ ░▒▓█▓▒░  ░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░░▒▓█▓▒░ 
░▒▓█▓▒░      ░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░ ░▒▓█▓▒░  ░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░░▒▓█▓▒░ 
░▒▓█▓▒░      ░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░ ░▒▓█▓▒░  ░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░░▒▓█▓▒░ 
░▒▓█▓▒░      ░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░▒▓█▓▒░░▒▓█▓▒░ ░▒▓█▓▒░   ░▒▓██████▓▒░░▒▓█▓▒░░▒▓█▓▒░░▒▓█▓▒░ 
                                                                                                  
```                                                                                                  
                                                                                                  
A portable, self-contained Windows administration utility built with WPF and .NET 8. Phantom provides a unified interface for system monitoring, tweaking, app management, Windows Update control, and automation — all from a single elevated window with a persistent in-app console.

> **Requires Administrator privileges.** If Phantom is launched without elevation, it will prompt for UAC elevation and relaunch itself.

---

## Quick Start

Run the following in an **elevated** PowerShell window — no setup required:

```powershell
irm https://raw.githubusercontent.com/xobash/phantom/main/launch.ps1 | iex
```

Safer alternative (recommended for bare-metal): download first, inspect, then execute.

```powershell
$scriptPath = Join-Path $env:TEMP "phantom-launch.ps1"
irm https://raw.githubusercontent.com/xobash/phantom/main/launch.ps1 -OutFile $scriptPath
Get-Content $scriptPath
& $scriptPath
```

The launcher will:

- Check for the .NET 8 SDK and install it via winget if missing
- Download and build Phantom from source
- Launch the application

Built output is saved to `%LOCALAPPDATA%\Phantom\app`. Subsequent launches can run `Phantom.exe` directly without rebuilding.

---

## What Is Phantom?

Phantom is a personal Windows admin tool in the spirit of [ChrisTitusTech/winutil](https://github.com/ChrisTitusTech/winutil) and WinToys. It aims to consolidate the scattered day-to-day tasks of Windows administration — debloating, tweaking, update management, app installs, and system diagnostics — into a single, auditable, offline-capable interface.

Everything runs locally. No telemetry leaves the machine, no background services are installed, and outbound network access only happens for explicit user-triggered actions such as the bootstrap launcher, package-manager installs, app installs/upgrades, or approved external tool downloads.

---

## Features

### Home

- Live system overview: motherboard, GPU (with driver version), storage, uptime, CPU, memory, Windows version, and WinSAT performance score
- KPI tiles with user-configurable refresh intervals
- Searchable, virtualized lists of installed apps, running processes, and active services
- On-demand WinSAT benchmark

### Store

- Detects winget and Chocolatey at launch; offers to install either if missing
- Install, uninstall, and upgrade applications from the bundled local JSON catalog
- Export and import Store selections through the unified automation config JSON

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

Add `-ForceDangerous` for dangerous operations in CLI mode, plus a second explicit acknowledgement token. This is only respected when the config also sets `"confirmDangerous": true`.

```powershell
./app/Phantom.exe -Config ./my-config.json -Run -ForceDangerous -AcknowledgeDangerous I_UNDERSTAND_NO_ROLLBACK
```

### Caveats

- Administrator elevation is mandatory. Phantom can prompt for UAC elevation on startup, or you can launch it from an already-elevated shell.
- Dangerous CLI execution requires all three: config `"confirmDangerous": true`, `-ForceDangerous`, and `-AcknowledgeDangerous I_UNDERSTAND_NO_ROLLBACK`.
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
| Microsoft.PowerShell.SDK | 7.4.6 | In-process execution for mutating operations |
| Windows PowerShell / PowerShell 7 host | built-in / optional | Guarded read-only queries and verification fallback |
| winget | any | Package management for app installs (auto-detected) |
| Chocolatey | any | Package management for app installs (auto-detected) |

The build output is self-contained — the .NET 8 runtime is bundled. winget and Chocolatey are optional; Phantom detects them at runtime and offers installation if absent.

---

## Security Notes

**Elevation model:** Phantom requires administrator privileges and verifies elevation on startup. If it is launched unelevated, it attempts a UAC relaunch of the current executable and exits the original process once the elevated instance starts. Launching from an already-elevated shell still works.

**PowerShell execution:** Mutating operations execute primarily in-process via `Microsoft.PowerShell.SDK` runspaces. Read-only detection and dashboard queries run through guarded `pwsh.exe` / `powershell.exe` child hosts, and verification steps can fall back to an external host when runspace execution is unavailable. PowerShell scripts are still validated against the operation allowlist, AST safety guards, and download-host restrictions before execution.

**User-triggered outbound connections only:** Phantom does not beacon or sync in the background. Network traffic occurs only when you explicitly trigger a networked action such as the bootstrap launcher, winget/Chocolatey bootstrap, app installs/upgrades, the Microsoft App Installer download, or the O&O ShutUp10 download/launch flow. The current allowlisted hosts in code are `aka.ms`, `community.chocolatey.org`, `www.oo-software.com`, `dl5.oo-software.com`, `github.com/xobash/phantom/*`, `github.com/microsoft/winget-cli/*`, and `raw.githubusercontent.com/xobash/phantom/*`.

**Dangerous operation gates:** Irreversible operations require both the **Enable Destructive Operations** toggle and explicit confirmation. In CLI mode, dangerous operations require `"confirmDangerous": true`, `-ForceDangerous`, and an explicit acknowledgement token (`-AcknowledgeDangerous I_UNDERSTAND_NO_ROLLBACK`). Restore point safeguards are not bypassed by force mode. Safety-backup compensation can restore captured registry, service, and scheduled-task artifacts, but file-system deletions may still require manual recovery.

**Supply chain considerations:** The `irm | iex` pattern downloads and executes a remote PowerShell script. If you are security-conscious about this, review `launch.ps1` in the repository before running it. The launcher can install the .NET 8 SDK via winget, download Phantom source from GitHub, and build it locally.

**No persistence mechanisms:** Phantom installs no services, scheduled tasks, startup entries, or shell extensions. Removing the app directory and `%LOCALAPPDATA%\Phantom` leaves no running components behind.

**Local data only:** No usage data, diagnostics, or telemetry is transmitted externally. `telemetry-local.json` is local-only and contains non-sensitive operational stats (space cleaned, run dates, network baselines).

---

## License

[GPL-3.0](LICENSE)
