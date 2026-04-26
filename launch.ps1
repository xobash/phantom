param(
    [switch]$Force,
    [switch]$NoGui
)

$ErrorActionPreference = 'Stop'

$InstallDir = Join-Path $env:LOCALAPPDATA 'Phantom'
$AppDir = Join-Path $InstallDir 'app'
$LaunchLogsDir = Join-Path $InstallDir 'launch-logs'
$LaunchLogPath = Join-Path $LaunchLogsDir ("launch-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))

$LaunchConfig = [ordered]@{
    RepoOwner = 'xobash'
    RepoName = 'phantom'
    RepoBranch = 'main'
    ReleaseAssetPattern = '^phantom-win-x64\.zip$'
    InstallDir = $InstallDir
    BuildDir = Join-Path $InstallDir 'source'
    AppDir = $AppDir
    InstallStatePath = Join-Path $AppDir 'install-state.json'
    LaunchLogPath = $LaunchLogPath
    ForceInstall = $Force -or ($env:PHANTOM_FORCE_UPDATE -eq '1')
    ConsoleMode = $false
}

$LaunchConfig['RepoFullName'] = "$($LaunchConfig['RepoOwner'])/$($LaunchConfig['RepoName'])"
$LaunchConfig['RepoCommitApi'] = "https://api.github.com/repos/$($LaunchConfig['RepoFullName'])/commits/$($LaunchConfig['RepoBranch'])"
$LaunchConfig['ReleaseApi'] = "https://api.github.com/repos/$($LaunchConfig['RepoFullName'])/releases/latest"
$LaunchConfig['BranchZip'] = "https://github.com/$($LaunchConfig['RepoFullName'])/archive/refs/heads/$($LaunchConfig['RepoBranch']).zip"

$WorkerScript = {
    param(
        [hashtable]$Config,
        [hashtable]$Sync
    )

    $ErrorActionPreference = 'Stop'

    function Send-Status([string]$level, [string]$message) {
        $line = '[{0}] [{1}] {2}' -f (Get-Date -Format 'HH:mm:ss'), $level, $message
        try {
            $logDir = Split-Path -Parent $Config.LaunchLogPath
            New-Item -ItemType Directory -Force -Path $logDir | Out-Null
            Add-Content -Path $Config.LaunchLogPath -Value $line -Encoding UTF8
        } catch {
        }

        [void]$Sync['Messages'].Add([pscustomobject]@{
            Level = $level
            Message = $message
            Line = $line
        })

        if ($Config.ConsoleMode) {
            $color = switch ($level) {
                'Error' { 'Red' }
                'Warning' { 'Yellow' }
                'Command' { 'DarkCyan' }
                'Output' { 'Gray' }
                default { 'Cyan' }
            }
            Write-Host $line -ForegroundColor $color
        }
    }

    function Complete-Launch([bool]$success, [string]$message = '') {
        if (-not [string]::IsNullOrWhiteSpace($message)) {
            $level = if ($success) { 'Info' } else { 'Error' }
            Send-Status $level $message
        }

        $Sync['Success'] = $success
        $Sync['IsDone'] = $true
    }

    function Test-IsAdministrator {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
        return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }

    function Try-GetCommand([string]$name) {
        try {
            return Get-Command $name -ErrorAction Stop
        } catch {
            Send-Status 'Warning' "Probe failed for '$name': $($_.Exception.Message)"
            return $null
        }
    }

    function Invoke-Native([string]$filePath, [string[]]$arguments, [string]$failureMessage) {
        Send-Status 'Command' "$filePath $($arguments -join ' ')"
        & $filePath @arguments 2>&1 | ForEach-Object {
            if ($null -ne $_ -and -not [string]::IsNullOrWhiteSpace([string]$_)) {
                Send-Status 'Output' ([string]$_)
            }
        }

        if ($LASTEXITCODE -ne 0) {
            throw "$failureMessage Exit code: $LASTEXITCODE"
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
                    Send-Status 'Error' "Failed to remove '$path': $($_.Exception.Message)"
                    return $false
                }

                Send-Status 'Warning' "Waiting for lock release ($attempt/$maxAttempts): $($_.Exception.Message)"
                Start-Sleep -Milliseconds $delayMs
            }
        }

        return $false
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
                    Send-Status 'Warning' "Stopped running Phantom instance (PID $($proc.ProcessId))."
                } catch {
                    Send-Status 'Warning' "Could not stop Phantom PID $($proc.ProcessId): $($_.Exception.Message)"
                }
            }
        } catch {
            Send-Status 'Warning' "Could not enumerate running Phantom processes: $($_.Exception.Message)"
        }

        if ($stopped -gt 0) {
            Start-Sleep -Milliseconds 700
        }
    }

    function Get-RemoteCommitSha {
        try {
            $response = Invoke-RestMethod -Uri $Config.RepoCommitApi -Headers @{ 'User-Agent' = 'PhantomLauncher' } -TimeoutSec 12
            if ($response -and $response.sha) {
                return [string]$response.sha
            }
        } catch {
            Send-Status 'Warning' "Commit update check skipped: $($_.Exception.Message)"
        }

        return ''
    }

    function Get-LatestReleaseAsset {
        try {
            $release = Invoke-RestMethod -Uri $Config.ReleaseApi -Headers @{ 'User-Agent' = 'PhantomLauncher' } -TimeoutSec 12
            $asset = @($release.assets) |
                Where-Object { $_.name -match $Config.ReleaseAssetPattern } |
                Select-Object -First 1

            if ($asset) {
                return [pscustomobject]@{
                    TagName = [string]$release.tag_name
                    AssetName = [string]$asset.name
                    DownloadUrl = [string]$asset.browser_download_url
                }
            }

            Send-Status 'Warning' "Latest GitHub release has no '$($Config.ReleaseAssetPattern)' asset."
        } catch {
            Send-Status 'Warning' "Release asset check skipped: $($_.Exception.Message)"
        }

        return $null
    }

    function Read-InstallState {
        if (-not (Test-Path $Config.InstallStatePath)) {
            return $null
        }

        try {
            return Get-Content -Path $Config.InstallStatePath -Raw -ErrorAction Stop | ConvertFrom-Json
        } catch {
            Send-Status 'Warning' "Install state unreadable; update may be required. $($_.Exception.Message)"
            return $null
        }
    }

    function Write-InstallState([string]$commitSha, [object]$releaseAsset) {
        try {
            New-Item -ItemType Directory -Force -Path $Config.AppDir | Out-Null
            $releaseTag = ''
            $releaseAssetName = ''
            if ($releaseAsset) {
                $releaseTag = $releaseAsset.TagName
                $releaseAssetName = $releaseAsset.AssetName
            }

            [pscustomobject]@{
                Repo = $Config.RepoFullName
                Branch = $Config.RepoBranch
                CommitSha = $commitSha
                ReleaseTag = $releaseTag
                ReleaseAsset = $releaseAssetName
                InstalledAt = (Get-Date).ToString('o')
                ReadyToRun = $true
            } | ConvertTo-Json -Depth 4 | Set-Content -Path $Config.InstallStatePath -Encoding UTF8
        } catch {
            Send-Status 'Warning' "Install state write failed: $($_.Exception.Message)"
        }
    }

    function Save-LaunchLogToApp {
        if (-not (Test-Path $Config.LaunchLogPath)) {
            return
        }

        try {
            $appLogsDir = Join-Path $Config.AppDir 'logs'
            New-Item -ItemType Directory -Force -Path $appLogsDir | Out-Null
            $dest = Join-Path $appLogsDir (Split-Path $Config.LaunchLogPath -Leaf)
            Copy-Item -Path $Config.LaunchLogPath -Destination $dest -Force
            Send-Status 'Info' "Launch log saved: $dest"
        } catch {
            Send-Status 'Warning' "Launch log copy failed: $($_.Exception.Message)"
        }
    }

    function Start-InstalledPhantom([string]$reason) {
        $exe = Join-Path $Config.AppDir 'Phantom.exe'
        if (-not (Test-Path $exe)) {
            return $false
        }

        if (-not [string]::IsNullOrWhiteSpace($reason)) {
            Send-Status 'Info' $reason
        }

        Send-Status 'Info' 'Launching Phantom...'
        Start-Process -FilePath $exe -Verb RunAs
        Send-Status 'Info' 'Phantom launched.'
        Save-LaunchLogToApp
        return $true
    }

    function Install-ReleaseAsset([object]$releaseAsset) {
        $zip = Join-Path $Config.InstallDir 'phantom-win-x64.zip'
        $stage = Join-Path $Config.InstallDir 'release-stage'

        Send-Status 'Info' "Downloading release $($releaseAsset.TagName) ($($releaseAsset.AssetName))..."
        Invoke-WebRequest -Uri $releaseAsset.DownloadUrl -OutFile $zip -UseBasicParsing

        if (-not (Remove-DirectoryWithRetry $stage)) {
            throw 'Could not clean release staging directory.'
        }

        New-Item -ItemType Directory -Force -Path $stage | Out-Null
        Expand-Archive -Path $zip -DestinationPath $stage -Force
        Remove-Item $zip -Force -ErrorAction SilentlyContinue

        $exe = Get-ChildItem -Path $stage -Filter 'Phantom.exe' -Recurse -File | Select-Object -First 1
        if (-not $exe) {
            throw 'Release asset did not contain Phantom.exe.'
        }

        Stop-RunningPhantomFromAppDir $Config.AppDir
        if (-not (Remove-DirectoryWithRetry $Config.AppDir)) {
            throw "Phantom appears to still be running from '$($Config.AppDir)'."
        }

        New-Item -ItemType Directory -Force -Path $Config.AppDir | Out-Null
        $sourceDir = Split-Path -Parent $exe.FullName
        Copy-Item -Path (Join-Path $sourceDir '*') -Destination $Config.AppDir -Recurse -Force
        Remove-DirectoryWithRetry $stage | Out-Null
        Write-InstallState '' $releaseAsset
        Send-Status 'Info' "Installed release $($releaseAsset.TagName)."
    }

    function Ensure-DotNetSdk {
        Send-Status 'Info' 'Checking for .NET 8 SDK...'
        $dotnet = Try-GetCommand 'dotnet'
        $hasNet8 = $false

        if ($dotnet) {
            $sdks = & dotnet --list-sdks 2>$null
            $hasNet8 = $sdks | Where-Object { $_ -match '^8\.' }
        }

        if ($hasNet8) {
            Send-Status 'Info' '.NET 8 SDK found.'
            return
        }

        Send-Status 'Warning' '.NET 8 SDK not found. Installing via winget...'
        $winget = Try-GetCommand 'winget'
        if (-not $winget) {
            throw 'winget is not available. Install the .NET 8 SDK manually from https://dot.net and re-run.'
        }

        Invoke-Native 'winget' @(
            'install',
            '--id', 'Microsoft.DotNet.SDK.8',
            '--source', 'winget',
            '--accept-package-agreements',
            '--accept-source-agreements',
            '--disable-interactivity'
        ) 'Failed to install .NET 8 SDK via winget.'

        $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
            [System.Environment]::GetEnvironmentVariable('PATH', 'User')

        if (-not (Try-GetCommand 'dotnet')) {
            throw ".NET SDK installed but 'dotnet' still was not found in PATH. Restart the shell and re-run."
        }

        Send-Status 'Info' '.NET 8 SDK installed.'
    }

    function Install-FromSource([string]$remoteCommitSha) {
        Ensure-DotNetSdk

        $zip = Join-Path $Config.InstallDir 'phantom-source.zip'
        $repoZipToDownload = if ([string]::IsNullOrWhiteSpace($remoteCommitSha)) {
            $Config.BranchZip
        } else {
            "https://github.com/$($Config.RepoFullName)/archive/$remoteCommitSha.zip"
        }

        Send-Status 'Info' 'Downloading Phantom source from GitHub...'
        Invoke-WebRequest -Uri $repoZipToDownload -OutFile $zip -UseBasicParsing

        Send-Status 'Info' 'Extracting source...'
        if (-not (Remove-DirectoryWithRetry $Config.BuildDir)) {
            throw 'Could not clean source build directory.'
        }

        Expand-Archive -Path $zip -DestinationPath $Config.BuildDir -Force
        Remove-Item $zip -Force -ErrorAction SilentlyContinue

        $extracted = Get-ChildItem -Path $Config.BuildDir -Directory | Select-Object -First 1
        if (-not $extracted) {
            throw 'Extraction produced no folder.'
        }

        $project = Join-Path $extracted.FullName 'Phantom\Phantom.csproj'
        $nugetConfig = Join-Path $extracted.FullName 'NuGet.Config'
        if (-not (Test-Path $project)) {
            throw "Could not find Phantom.csproj at expected path: $project"
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

        Stop-RunningPhantomFromAppDir $Config.AppDir
        if (-not (Remove-DirectoryWithRetry $Config.AppDir)) {
            throw "Phantom appears to still be running from '$($Config.AppDir)'."
        }

        Send-Status 'Info' 'Building Phantom ReadyToRun package...'
        Invoke-Native 'dotnet' @(
            'restore', $project,
            '-r', 'win-x64',
            '--configfile', $nugetConfig,
            '-p:Configuration=Release',
            '-p:SelfContained=true',
            '-p:PublishReadyToRun=true',
            '-p:PublishTrimmed=false'
        ) 'dotnet restore failed. Confirm this VM can reach https://api.nuget.org/v3/index.json.'

        Invoke-Native 'dotnet' @(
            'publish', $project,
            '-c', 'Release',
            '-r', 'win-x64',
            '--self-contained', 'true',
            '--no-restore',
            '-p:PublishSingleFile=false',
            '-p:PublishReadyToRun=true',
            '-p:PublishTrimmed=false',
            '-o', $Config.AppDir
        ) 'dotnet publish failed.'

        Write-InstallState $remoteCommitSha $null
        Send-Status 'Info' "Build complete. Output: $($Config.AppDir)"
    }

    function Invoke-PhantomLaunch {
        New-Item -ItemType Directory -Force -Path $Config.InstallDir | Out-Null
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Config.LaunchLogPath) | Out-Null

        if (-not (Test-IsAdministrator)) {
            throw 'Phantom requires an elevated PowerShell session. Re-run as Administrator.'
        }

        Send-Status 'Info' 'Checking installed Phantom...'
        $releaseAsset = Get-LatestReleaseAsset
        $remoteCommitSha = if ($releaseAsset) { '' } else { Get-RemoteCommitSha }
        $installState = Read-InstallState
        $installedReleaseTag = if ($installState -and $installState.ReleaseTag) { [string]$installState.ReleaseTag } else { '' }
        $installedCommitSha = if ($installState -and $installState.CommitSha) { [string]$installState.CommitSha } else { '' }
        $installedExe = Join-Path $Config.AppDir 'Phantom.exe'

        if (-not $Config.ForceInstall -and (Test-Path $installedExe)) {
            if ($releaseAsset -and $installedReleaseTag -eq $releaseAsset.TagName) {
                if (Start-InstalledPhantom "Installed release is current ($($releaseAsset.TagName)).") {
                    return
                }
            }

            if (-not $releaseAsset) {
                if ([string]::IsNullOrWhiteSpace($remoteCommitSha)) {
                    if (Start-InstalledPhantom 'Update check unavailable; using the existing install.') {
                        return
                    }
                }

                if (-not [string]::IsNullOrWhiteSpace($installedCommitSha) -and $installedCommitSha -eq $remoteCommitSha) {
                    if (Start-InstalledPhantom "Installed source build is current ($($remoteCommitSha.Substring(0, 7))).") {
                        return
                    }
                }
            }
        }

        if ($Config.ForceInstall) {
            Send-Status 'Info' 'Forced rebuild/update requested.'
        }

        if ($releaseAsset) {
            Install-ReleaseAsset $releaseAsset
        } else {
            Send-Status 'Warning' 'No release asset available; falling back to source build.'
            Install-FromSource $remoteCommitSha
        }

        if (-not (Start-InstalledPhantom 'Installed Phantom is ready.')) {
            throw "Phantom.exe not found in $($Config.AppDir) after install."
        }
    }

    try {
        Invoke-PhantomLaunch
        Complete-Launch $true
    } catch {
        Complete-Launch $false $_.Exception.Message
    }
}

function New-LaunchSync {
    return [hashtable]::Synchronized(@{
        Messages = [System.Collections.ArrayList]::Synchronized((New-Object System.Collections.ArrayList))
        IsDone = $false
        Success = $false
    })
}

function Copy-LaunchConfig([bool]$consoleMode) {
    $copy = @{}
    foreach ($key in $LaunchConfig.Keys) {
        $copy[$key] = $LaunchConfig[$key]
    }

    $copy['ConsoleMode'] = $consoleMode
    return $copy
}

function Invoke-ConsoleBootstrap {
    $config = Copy-LaunchConfig $true
    $sync = New-LaunchSync
    & $WorkerScript $config $sync
}

function Show-WpfBootstrap {
    if ([System.Threading.Thread]::CurrentThread.GetApartmentState() -ne [System.Threading.ApartmentState]::STA) {
        return $false
    }

    try {
        Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
    } catch {
        return $false
    }

    $xaml = @'
<Window
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Title="Phantom Launcher"
    Width="720"
    Height="440"
    WindowStartupLocation="CenterScreen"
    ResizeMode="CanResize"
    Background="#101614">
  <Grid Margin="18">
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto" />
      <RowDefinition Height="Auto" />
      <RowDefinition Height="*" />
      <RowDefinition Height="Auto" />
    </Grid.RowDefinitions>
    <TextBlock Grid.Row="0" Text="Phantom" FontSize="28" FontWeight="SemiBold" Foreground="#F2F6F3" />
    <TextBlock x:Name="StatusText" Grid.Row="1" Margin="0,8,0,12" Text="Preparing launch..." Foreground="#B9C9C0" />
    <Border Grid.Row="2" BorderBrush="#2E3B36" BorderThickness="1" CornerRadius="6" Background="#07100D">
      <TextBox x:Name="LogBox"
               Margin="10"
               BorderThickness="0"
               Background="#07100D"
               Foreground="#CFE3D7"
               FontFamily="Cascadia Mono, Consolas"
               FontSize="12"
               IsReadOnly="True"
               TextWrapping="Wrap"
               VerticalScrollBarVisibility="Auto"
               HorizontalScrollBarVisibility="Disabled" />
    </Border>
    <ProgressBar x:Name="Progress" Grid.Row="3" Height="10" Margin="0,14,0,0" IsIndeterminate="True" />
  </Grid>
</Window>
'@

    $config = Copy-LaunchConfig $false
    $sync = New-LaunchSync
    $runspace = [runspacefactory]::CreateRunspace()
    $runspace.ApartmentState = 'MTA'
    $runspace.ThreadOptions = 'ReuseThread'
    $runspace.Open()

    $powershell = [powershell]::Create()
    $powershell.Runspace = $runspace
    [void]$powershell.AddScript($WorkerScript.ToString()).AddArgument($config).AddArgument($sync)
    $handle = $powershell.BeginInvoke()

    [xml]$xml = $xaml
    $reader = New-Object System.Xml.XmlNodeReader $xml
    $window = [Windows.Markup.XamlReader]::Load($reader)
    $statusText = $window.FindName('StatusText')
    $logBox = $window.FindName('LogBox')
    $progress = $window.FindName('Progress')
    $uiState = @{ Index = 0; Completed = $false }

    $timer = New-Object Windows.Threading.DispatcherTimer
    $timer.Interval = [TimeSpan]::FromMilliseconds(100)
    $timer.Add_Tick({
        while ($uiState['Index'] -lt $sync['Messages'].Count) {
            $entry = $sync['Messages'][$uiState['Index']]
            $logBox.AppendText($entry.Line + [Environment]::NewLine)
            $logBox.ScrollToEnd()
            if ($entry.Level -ne 'Output' -and -not [string]::IsNullOrWhiteSpace($entry.Message)) {
                $statusText.Text = $entry.Message
            }
            $uiState['Index']++
        }

        if ($sync['IsDone'] -and -not $uiState['Completed']) {
            $uiState['Completed'] = $true
            $timer.Stop()
            try {
                $powershell.EndInvoke($handle)
            } catch {
                $logBox.AppendText($_.Exception.Message + [Environment]::NewLine)
            } finally {
                $powershell.Dispose()
                $runspace.Dispose()
            }

            $progress.IsIndeterminate = $false
            $progress.Value = if ($sync['Success']) { 100 } else { 0 }
            $statusText.Text = if ($sync['Success']) { 'Phantom launched.' } else { 'Launch failed. Review the log above.' }
            if ($sync['Success']) {
                $window.Dispatcher.InvokeAsync({
                    Start-Sleep -Milliseconds 700
                    $window.Close()
                }) | Out-Null
            }
        }
    })

    $window.Add_Closing({
        param($sender, $eventArgs)
        if (-not $sync['IsDone']) {
            $eventArgs.Cancel = $true
            $statusText.Text = 'Phantom is still preparing...'
        }
    })

    $timer.Start()
    [void]$window.ShowDialog()
    return $true
}

if (-not $NoGui -and (Show-WpfBootstrap)) {
    return
}

Invoke-ConsoleBootstrap
