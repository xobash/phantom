$exe = Join-Path $PSScriptRoot 'app\\Phantom.exe'
if (-not (Test-Path $exe)) {
  Write-Error "Portable binary not found: $exe. Run build-portable.ps1 first."
  exit 1
}

& $exe @args
exit $LASTEXITCODE
