$ErrorActionPreference = 'Stop'

$RepoZip  = "https://github.com/xobash/phantom/archive/refs/heads/main.zip"
$InstallDir = "$env:LOCALAPPDATA\Phantom"
$BuildDir   = "$InstallDir\source"
$AppDir     = "$InstallDir\app"

function Write-Step($msg) {
    Write-Host "`n>> $msg" -ForegroundColor Cyan
}

function Write-OK($msg) {
    Write-Host "   $msg" -ForegroundColor Green
}

function Write-Fail($msg) {
    Write-Host "   ERROR: $msg" -ForegroundColor Red
}

# ── Admin check ────────────────────────────────────────────────────────────────
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Fail "Phantom requires an elevated PowerShell session. Re-run as Administrator."
    exit 1
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
        exit 1
    }

    winget install --id Microsoft.DotNet.SDK.8 --source winget --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "Failed to install .NET 8 SDK via winget."
        exit 1
    }

    # Refresh PATH so dotnet is available in this session
    $env:PATH = [System.Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" +
                [System.Environment]::GetEnvironmentVariable("PATH", "User")

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Write-Fail ".NET SDK installed but 'dotnet' still not found in PATH. Please restart your shell and re-run."
        exit 1
    }

    Write-OK ".NET 8 SDK installed."
} else {
    Write-OK ".NET 8 SDK found."
}

# ── Download source ────────────────────────────────────────────────────────────
Write-Step "Downloading Phantom source from GitHub..."

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
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
    exit 1
}

Write-OK "Extracted to $($extracted.FullName)"

# ── Build ──────────────────────────────────────────────────────────────────────
Write-Step "Building Phantom (this may take a minute on first run)..."

$project = Join-Path $extracted.FullName "Phantom\Phantom.csproj"

if (-not (Test-Path $project)) {
    Write-Fail "Could not find Phantom.csproj at expected path: $project"
    exit 1
}

if (Test-Path $AppDir) { Remove-Item $AppDir -Recurse -Force }

dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $AppDir

if ($LASTEXITCODE -ne 0) {
    Write-Fail "dotnet publish failed. See output above."
    exit 1
}

Write-OK "Build complete. Output: $AppDir"

# ── Launch ─────────────────────────────────────────────────────────────────────
Write-Step "Launching Phantom..."

$exe = Join-Path $AppDir "Phantom.exe"

if (-not (Test-Path $exe)) {
    Write-Fail "Phantom.exe not found in $AppDir after build."
    exit 1
}

Start-Process -FilePath $exe -Verb RunAs
Write-OK "Phantom launched."
