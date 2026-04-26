$ErrorActionPreference = 'Stop'

$RepoZip  = "https://github.com/xobash/phantom/archive/refs/heads/main.zip"
$InstallDir = "$env:LOCALAPPDATA\Phantom"
$BuildDir   = "$InstallDir\source"
$AppDir     = "$InstallDir\app"
$LaunchLogsDir = "$InstallDir\launch-logs"
$LaunchLogPath = Join-Path $LaunchLogsDir ("launch-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
$script:TranscriptStarted = $false

function Write-Step($msg) {
    Write-Host "`n>> $msg" -ForegroundColor Cyan
}

function Write-OK($msg) {
    Write-Host "   $msg" -ForegroundColor Green
}

function Write-Fail($msg) {
    Write-Host "   ERROR: $msg" -ForegroundColor Red
}

function Try-GetCommand([string]$name) {
    try {
        return Get-Command $name -ErrorAction Stop
    } catch {
        Write-Host "   Probe failed for '$name': $($_.Exception.Message)" -ForegroundColor Yellow
        return $null
    }
}

function Stop-RunningPhantomFromAppDir([string]$appPath) {
    if (-not (Test-Path $appPath)) {
        return
    }

    $normalizedAppPath = [System.IO.Path]::GetFullPath($appPath).TrimEnd('\')
    $stopped = 0

    try {
        $processes = Get-CimInstance Win32_Process -Filter "Name='Phantom.exe'" -ErrorAction Stop
        foreach ($proc in $processes) {
            if ([string]::IsNullOrWhiteSpace($proc.ExecutablePath)) {
                continue
            }

            $exePath = [System.IO.Path]::GetFullPath($proc.ExecutablePath)
            if (-not $exePath.StartsWith($normalizedAppPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            try {
                Stop-Process -Id $proc.ProcessId -Force -ErrorAction Stop
                $stopped++
                Write-Host "   Stopped running Phantom instance (PID $($proc.ProcessId))." -ForegroundColor Yellow
            } catch {
                Write-Host "   Could not stop Phantom PID $($proc.ProcessId): $($_.Exception.Message)" -ForegroundColor Yellow
            }
        }
    } catch {
        Write-Host "   Could not enumerate running Phantom processes: $($_.Exception.Message)" -ForegroundColor Yellow
    }

    if ($stopped -gt 0) {
        Start-Sleep -Milliseconds 700
    }
}

function Remove-DirectoryWithRetry([string]$path, [int]$maxAttempts = 8, [int]$delayMs = 600) {
    if (-not (Test-Path $path)) {
        return $true
    }

    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        try {
            Remove-Item $path -Recurse -Force -ErrorAction Stop
            return $true
        } catch {
            if ($attempt -ge $maxAttempts) {
                Write-Fail "Failed to remove '$path': $($_.Exception.Message)"
                return $false
            }

            Write-Host "   Waiting for lock release ($attempt/$maxAttempts): $($_.Exception.Message)" -ForegroundColor Yellow
            Start-Sleep -Milliseconds $delayMs
        }
    }

    return $false
}

function Stop-LaunchTranscript {
    if (-not $script:TranscriptStarted) {
        return
    }

    try {
        Stop-Transcript | Out-Null
    } catch {
    }

    $script:TranscriptStarted = $false
}

function Save-LaunchLogToApp {
    if (-not (Test-Path $LaunchLogPath)) {
        return
    }

    try {
        $appLogsDir = Join-Path $AppDir "logs"
        New-Item -ItemType Directory -Force -Path $appLogsDir | Out-Null
        $dest = Join-Path $appLogsDir (Split-Path $LaunchLogPath -Leaf)
        Copy-Item -Path $LaunchLogPath -Destination $dest -Force
        Write-OK "Launch log saved: $dest"
    } catch {
        Write-Host "   Launch log copy failed: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
New-Item -ItemType Directory -Force -Path $LaunchLogsDir | Out-Null
try {
    Start-Transcript -Path $LaunchLogPath -Force | Out-Null
    $script:TranscriptStarted = $true
} catch {
    Write-Host "   Warning: could not start transcript logging. $($_.Exception.Message)" -ForegroundColor Yellow
}

trap {
    Write-Fail ("Unhandled launch error: " + $_.Exception.Message)
    Stop-LaunchTranscript
    Save-LaunchLogToApp
    return
}

# ── Admin check ────────────────────────────────────────────────────────────────
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Fail "Phantom requires an elevated PowerShell session. Re-run as Administrator."
    Stop-LaunchTranscript
    Save-LaunchLogToApp
    return
}

Write-Host ""
Write-Host "  ██████╗ ██╗  ██╗ █████╗ ███╗   ██╗████████╗ ██████╗ ███╗   ███╗" -ForegroundColor DarkMagenta
Write-Host "  ██╔══██╗██║  ██║██╔══██╗████╗  ██║╚══██╔══╝██╔═══██╗████╗ ████║" -ForegroundColor DarkMagenta
Write-Host "  ██████╔╝███████║███████║██╔██╗ ██║   ██║   ██║   ██║██╔████╔██║" -ForegroundColor DarkMagenta
Write-Host "  ██╔═══╝ ██╔══██║██╔══██║██║╚██╗██║   ██║   ██║   ██║██║╚██╔╝██║" -ForegroundColor DarkMagenta
Write-Host "  ██║     ██║  ██║██║  ██║██║ ╚████║   ██║   ╚██████╔╝██║ ╚═╝ ██║" -ForegroundColor DarkMagenta
Write-Host "  ╚═╝     ╚═╝  ╚═╝╚═╝  ╚═╝╚═╝  ╚═══╝   ╚═╝    ╚═════╝ ╚═╝     ╚═╝" -ForegroundColor DarkMagenta
Write-Host ""

# ── .NET SDK check ─────────────────────────────────────────────────────────────
Write-Step "Checking for .NET 8 SDK..."

$dotnet = Try-GetCommand 'dotnet'
$hasNet8 = $false

if ($dotnet) {
    $sdks = & dotnet --list-sdks 2>$null
    $hasNet8 = $sdks | Where-Object { $_ -match "^8\." }
}

if (-not $hasNet8) {
    Write-Host "   .NET 8 SDK not found. Installing via winget..." -ForegroundColor Yellow

    $winget = Try-GetCommand 'winget'
    if (-not $winget) {
        Write-Fail "winget is not available. Please install the .NET 8 SDK manually from https://dot.net and re-run."
        Stop-LaunchTranscript
        Save-LaunchLogToApp
        return
    }

    winget install --id Microsoft.DotNet.SDK.8 --source winget --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "Failed to install .NET 8 SDK via winget."
        Stop-LaunchTranscript
        Save-LaunchLogToApp
        return
    }

    # Refresh PATH so dotnet is available in this session
    $env:PATH = [System.Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" +
                [System.Environment]::GetEnvironmentVariable("PATH", "User")

    $dotnet = Try-GetCommand 'dotnet'
    if (-not $dotnet) {
        Write-Fail ".NET SDK installed but 'dotnet' still not found in PATH. Please restart your shell and re-run."
        Stop-LaunchTranscript
        Save-LaunchLogToApp
        return
    }

    Write-OK ".NET 8 SDK installed."
} else {
    Write-OK ".NET 8 SDK found."
}

# ── Download source ────────────────────────────────────────────────────────────
Write-Step "Downloading Phantom source from GitHub..."
$zip = "$InstallDir\phantom-main.zip"

Invoke-WebRequest -Uri $RepoZip -OutFile $zip -UseBasicParsing
Write-OK "Downloaded."

# ── Extract ────────────────────────────────────────────────────────────────────
Write-Step "Extracting..."

if (-not (Remove-DirectoryWithRetry $BuildDir)) {
    Stop-LaunchTranscript
    Save-LaunchLogToApp
    return
}
Expand-Archive -Path $zip -DestinationPath $BuildDir -Force
Remove-Item $zip

# GitHub zips extract into a subfolder named "phantom-main"
$extracted = Get-ChildItem -Path $BuildDir -Directory | Select-Object -First 1
if (-not $extracted) {
    Write-Fail "Extraction produced no folder. The repo zip may be empty or structured differently."
    Stop-LaunchTranscript
    Save-LaunchLogToApp
    return
}

Write-OK "Extracted to $($extracted.FullName)"

# ── Build ──────────────────────────────────────────────────────────────────────
Write-Step "Building Phantom (this may take a minute on first run)..."

$project = Join-Path $extracted.FullName "Phantom\Phantom.csproj"
$nugetConfig = Join-Path $extracted.FullName "NuGet.Config"

if (-not (Test-Path $project)) {
    Write-Fail "Could not find Phantom.csproj at expected path: $project"
    Stop-LaunchTranscript
    Save-LaunchLogToApp
    return
}

if (-not (Test-Path $nugetConfig)) {
    @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
'@ | Set-Content -Path $nugetConfig -Encoding UTF8
}

Stop-RunningPhantomFromAppDir $AppDir
if (-not (Remove-DirectoryWithRetry $AppDir)) {
    Write-Fail "Phantom appears to still be running from '$AppDir'. Close Phantom and run launch.ps1 again."
    Stop-LaunchTranscript
    Save-LaunchLogToApp
    return
}

dotnet restore $project -r win-x64 --configfile $nugetConfig -p:Configuration=Release -p:SelfContained=true -p:PublishReadyToRun=true -p:PublishTrimmed=false

if ($LASTEXITCODE -ne 0) {
    Write-Fail "dotnet restore failed. Confirm this VM can reach https://api.nuget.org/v3/index.json, then re-run."
    Stop-LaunchTranscript
    Save-LaunchLogToApp
    return
}

dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:PublishTrimmed=false -o $AppDir

if ($LASTEXITCODE -ne 0) {
    Write-Fail "dotnet publish failed. See output above."
    Stop-LaunchTranscript
    Save-LaunchLogToApp
    return
}

Write-OK "Build complete. Output: $AppDir"

# ── Launch ─────────────────────────────────────────────────────────────────────
Write-Step "Launching Phantom..."

$exe = Join-Path $AppDir "Phantom.exe"

if (-not (Test-Path $exe)) {
    Write-Fail "Phantom.exe not found in $AppDir after build."
    Stop-LaunchTranscript
    Save-LaunchLogToApp
    return
}

try {
    Start-Process -FilePath $exe -Verb RunAs
} catch {
    Write-Fail ("Failed to start Phantom.exe: " + $_.Exception.Message)
    Stop-LaunchTranscript
    Save-LaunchLogToApp
    return
}
Write-OK "Phantom launched."
Stop-LaunchTranscript
Save-LaunchLogToApp
