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

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$hasNet8 = $false

if ($dotnet) {
    $sdks = & dotnet --list-sdks 2>$null
    $hasNet8 = $sdks | Where-Object { $_ -match "^8\." }
}

if (-not $hasNet8) {
    Write-Host "   .NET 8 SDK not found. Installing via winget..." -ForegroundColor Yellow

    $winget = Get-Command winget -ErrorAction SilentlyContinue
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

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
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

if (Test-Path $BuildDir) { Remove-Item $BuildDir -Recurse -Force }
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

if (-not (Test-Path $project)) {
    Write-Fail "Could not find Phantom.csproj at expected path: $project"
    Stop-LaunchTranscript
    Save-LaunchLogToApp
    return
}

if (Test-Path $AppDir) { Remove-Item $AppDir -Recurse -Force }

dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $AppDir

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
