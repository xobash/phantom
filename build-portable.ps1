$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Phantom\\Phantom.csproj'
$outDir = Join-Path $PSScriptRoot 'app'

try {
  $dotnet = Get-Command dotnet -ErrorAction Stop
} catch {
  $dotnet = $null
}

if (-not $dotnet) {
  Write-Error 'dotnet SDK is required to build Phantom.'
  exit 1
}

dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $outDir
if ($LASTEXITCODE -ne 0) {
  Write-Error 'dotnet publish failed.'
  exit $LASTEXITCODE
}

Write-Host "Published portable build to $outDir"
