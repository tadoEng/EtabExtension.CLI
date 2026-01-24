# build-sidecar.ps1
# Build single-file executable for Tauri sidecar
# Place this file in the root of your .NET CLI project

Write-Host ""
Write-Host "================================================" -ForegroundColor Cyan
Write-Host "Building ETABS CLI Sidecar for Tauri" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

# Clean previous builds
Write-Host "Cleaning previous builds..." -ForegroundColor Yellow
dotnet clean

# Restore packages
Write-Host "Restoring packages..." -ForegroundColor Yellow
dotnet restore

# Publish as single file executable
Write-Host "Publishing single-file executable..." -ForegroundColor Yellow
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./dist

# Check if build was successful
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Get the target triple for Windows x64
$targetTriple = "x86_64-pc-windows-msvc"

# Source and destination paths
$sourceFile = ".\dist\etab-cli.exe"
$renamedFile = ".\dist\etab-cli-$targetTriple.exe"

if (Test-Path $sourceFile) {
    # Create a renamed copy with Tauri sidecar naming convention
    Copy-Item $sourceFile $renamedFile -Force
    
    Write-Host ""
    Write-Host "================================================" -ForegroundColor Green
    Write-Host "Build Successful!" -ForegroundColor Green
    Write-Host "================================================" -ForegroundColor Green
    Write-Host ""
    
    # Get file sizes
    $originalSize = (Get-Item $sourceFile).Length / 1MB
    
    Write-Host "Output files in ./dist/ folder:" -ForegroundColor Cyan
    Write-Host "  1. etab-cli.exe" -ForegroundColor White
    Write-Host "     Size: $($originalSize.ToString('F2')) MB" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  2. etab-cli-$targetTriple.exe" -ForegroundColor White
    Write-Host "     Size: $($originalSize.ToString('F2')) MB" -ForegroundColor Gray
    Write-Host "     (Tauri sidecar naming convention)" -ForegroundColor Gray
    
    Write-Host ""
    Write-Host "================================================" -ForegroundColor Yellow
    Write-Host "Next Steps:" -ForegroundColor Yellow
    Write-Host "================================================" -ForegroundColor Yellow
    Write-Host "1. Copy the file to your Tauri project:" -ForegroundColor White
    Write-Host "   .\dist\etab-cli-$targetTriple.exe" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "2. Paste it to:" -ForegroundColor White
    Write-Host "   [tauri-project]/src-tauri/binaries/" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "3. Update tauri.conf.json:" -ForegroundColor White
    Write-Host "   `"bundle`": {" -ForegroundColor Gray
    Write-Host "     `"externalBin`": [`"binaries/etab-cli`"]" -ForegroundColor Gray
    Write-Host "   }" -ForegroundColor Gray
    Write-Host ""
    Write-Host "================================================" -ForegroundColor Yellow
    Write-Host ""
    
} else {
    Write-Host ""
    Write-Host "Error: Executable not found at $sourceFile" -ForegroundColor Red
    exit 1
}
