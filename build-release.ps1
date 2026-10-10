# ==============================================================================
# Windows Dynamic Bar - Release Build & Packaging Script
# ==============================================================================
# Usage:
#   .\build-release.ps1
#   .\build-release.ps1 -CertPath "C:\path\to\cert.pfx" -CertPassword "pass"
# ==============================================================================

param(
    [string]$CertPath = "",
    [string]$CertPassword = "",
    [switch]$SkipTests = $false
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host " Building Windows Dynamic Bar Release Package" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# 1. Restore & Test
Write-Host "`n[1/5] Restoring and running tests..." -ForegroundColor Yellow
dotnet restore

if (-not $SkipTests) {
    dotnet run --project tests\WindowsDynamicBar.Tests\WindowsDynamicBar.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Tests failed! Aborting release build."
    }
}

# 2. Publish Self-Contained ReadyToRun (win-x64)
Write-Host "`n[2/5] Publishing win-x64 (Self-Contained, ReadyToRun, No Trimming)..." -ForegroundColor Yellow
$PublishDir = "$ScriptDir\src\WindowsDynamicBar\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish"

dotnet publish src\WindowsDynamicBar\WindowsDynamicBar.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:PublishTrimmed=false `
    -o "$PublishDir"

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed!"
}

# 3. Optional Code-Signing
Write-Host "`n[3/5] Code-signing pass..." -ForegroundColor Yellow
$TargetExe = Join-Path $PublishDir "WindowsDynamicBar.exe"
if ([string]::IsNullOrWhiteSpace($CertPath) -and $env:CODESIGN_CERT) {
    $CertPath = $env:CODESIGN_CERT
    $CertPassword = $env:CODESIGN_PASSWORD
}

if (-not [string]::IsNullOrWhiteSpace($CertPath) -and (Test-Path $CertPath)) {
    Write-Host "Signing $TargetExe with $CertPath..." -ForegroundColor Green
    $Signtool = (Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter "signtool.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
    if ($Signtool) {
        if ($CertPassword) {
            & $Signtool sign /f "$CertPath" /p "$CertPassword" /fd SHA256 /tr "http://timestamp.digicert.com" /td SHA256 "$TargetExe"
        } else {
            & $Signtool sign /f "$CertPath" /fd SHA256 /tr "http://timestamp.digicert.com" /td SHA256 "$TargetExe"
        }
        Write-Host "Signing succeeded." -ForegroundColor Green
    } else {
        Write-Warning "signtool.exe not found in Windows Kits. Binary remains unsigned."
    }
} else {
    Write-Host "No code-signing certificate provided. Binary is built unsigned." -ForegroundColor Gray
    Write-Host "(To sign: pass -CertPath <path.pfx> or set `$env:CODESIGN_CERT)" -ForegroundColor Gray
}

# 4. Packaging
Write-Host "`n[4/5] Packaging installer..." -ForegroundColor Yellow
$ReleaseDir = "$ScriptDir\release"
if (-not (Test-Path $ReleaseDir)) { New-Item -ItemType Directory -Path $ReleaseDir | Out-Null }

$IsccPaths = @(
    "iscc",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
)

$Iscc = $null
foreach ($p in $IsccPaths) {
    if (Get-Command $p -ErrorAction SilentlyContinue) { $Iscc = $p; break }
    if (Test-Path $p) { $Iscc = $p; break }
}

if ($Iscc) {
    Write-Host "Compiling installer with Inno Setup ($Iscc)..." -ForegroundColor Green
    & $Iscc "$ScriptDir\installer\WindowsDynamicBar.iss"
    Write-Host "Installer generated at: $ReleaseDir\WindowsDynamicBar-Setup.exe" -ForegroundColor Green
} else {
    Write-Warning "Inno Setup compiler (ISCC.exe) not found on system."
    Write-Host "Creating standalone portable zip in $ReleaseDir..." -ForegroundColor Yellow
    $ZipPath = Join-Path $ReleaseDir "WindowsDynamicBar-Portable-win-x64.zip"
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    Compress-Archive -Path "$PublishDir\*" -DestinationPath $ZipPath -CompressionLevel Optimal
    Write-Host "Portable package created at: $ZipPath" -ForegroundColor Green
    Write-Host "To compile the setup exe, install Inno Setup 6 and run:" -ForegroundColor Gray
    Write-Host "  iscc installer\WindowsDynamicBar.iss" -ForegroundColor Gray
}

# 5. Checksums
Write-Host "`n[5/5] Generating checksums..." -ForegroundColor Yellow
$ChecksumFile = Join-Path $ReleaseDir "checksums.txt"
$Checksums = @()
Get-ChildItem -Path $ReleaseDir -File | Where-Object { $_.Name -ne "checksums.txt" } | ForEach-Object {
    $hash = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash
    $Checksums += "$hash  $($_.Name)"
}
$Checksums | Out-File -FilePath $ChecksumFile -Encoding utf8
Get-Content $ChecksumFile | Write-Host -ForegroundColor Cyan

Write-Host "`n==================================================" -ForegroundColor Green
Write-Host " Release build completed successfully!" -ForegroundColor Green
Write-Host " Artifacts located in: $ReleaseDir" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
