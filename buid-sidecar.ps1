Write-Warning "buid-sidecar.ps1 is deprecated. Use build-sidecar.ps1."
$newScript = Join-Path $PSScriptRoot "build-sidecar.ps1"
& $newScript @args
exit $LASTEXITCODE
