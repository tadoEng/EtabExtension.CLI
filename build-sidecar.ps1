[CmdletBinding()]
param(
    [string]$Project = "src/EtabExtension.CLI/EtabExtension.CLI.csproj",
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputDir = "dist",
    [switch]$SkipClean
)

$ErrorActionPreference = "Stop"

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Get-TauriTargetTriple {
    param([string]$Rid)

    switch ($Rid) {
        "win-x64" { return "x86_64-pc-windows-msvc" }
        "win-arm64" { return "aarch64-pc-windows-msvc" }
        "win-x86" { return "i686-pc-windows-msvc" }
        default { return $Rid }
    }
}

$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$projectPath = Join-Path $scriptRoot $Project
$distPath = Join-Path $scriptRoot $OutputDir

if (-not (Test-Path $projectPath)) {
    throw "Project file not found: $projectPath"
}

[xml]$projectXml = Get-Content -Path $projectPath -Raw
$assemblyName = @($projectXml.Project.PropertyGroup.AssemblyName | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })[0]
if ([string]::IsNullOrWhiteSpace($assemblyName)) {
    $assemblyName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
}

Write-Host ""
Write-Host "================================================" -ForegroundColor Cyan
Write-Host "Building ETABS CLI Sidecar for Tauri" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

if (-not $SkipClean) {
    Write-Host "Cleaning previous builds..." -ForegroundColor Yellow
    Invoke-DotNet -Arguments @("clean", $projectPath, "-c", $Configuration) -FailureMessage "dotnet clean failed."
}

Write-Host "Restoring packages..." -ForegroundColor Yellow
Invoke-DotNet -Arguments @("restore", $projectPath) -FailureMessage "dotnet restore failed."

Write-Host "Publishing single-file executable..." -ForegroundColor Yellow
Invoke-DotNet -Arguments @(
    "publish", $projectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "--no-restore",
    "-o", $distPath
) -FailureMessage "dotnet publish failed."

$targetTriple = Get-TauriTargetTriple -Rid $Runtime
$sourceFile = Join-Path $distPath "$assemblyName.exe"
$renamedFile = Join-Path $distPath "$assemblyName-$targetTriple.exe"

if (Test-Path $sourceFile) {
    Copy-Item $sourceFile $renamedFile -Force

    $originalSize = (Get-Item $sourceFile).Length / 1MB

    Write-Host ""
    Write-Host "================================================" -ForegroundColor Green
    Write-Host "Build Successful!" -ForegroundColor Green
    Write-Host "================================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "Output files in ./$OutputDir/ folder:" -ForegroundColor Cyan
    Write-Host "  1. $assemblyName.exe" -ForegroundColor White
    Write-Host "     Size: $($originalSize.ToString('F2')) MB" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  2. $assemblyName-$targetTriple.exe" -ForegroundColor White
    Write-Host "     Size: $($originalSize.ToString('F2')) MB" -ForegroundColor Gray
    Write-Host "     (Tauri sidecar naming convention)" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Copy this file to your Tauri project binaries folder:" -ForegroundColor Yellow
    Write-Host "  .\$OutputDir\$assemblyName-$targetTriple.exe" -ForegroundColor Cyan
    Write-Host ""
} else {
    throw "Executable not found at $sourceFile"
}
