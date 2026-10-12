# ==============================================================================
# Windows Dynamic Bar - Terminal CLI (island)
# ==============================================================================
# Usage:
#   island          - Stop any existing instance and launch the application
#   island -Stop    - Stop the application
#   island -Build   - Rebuild and launch the application
#   island -Config  - Reconfigure the saved repository path
# ==============================================================================

[CmdletBinding()]
param(
    [switch]$Stop,
    [switch]$Build,
    [switch]$Config
)

$configDir  = Join-Path $HOME ".island"
$configFile = Join-Path $configDir "config.json"

function Ensure-ConfigDir {
    if (-not (Test-Path -LiteralPath $configDir)) {
        New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    }
}

function Save-RepoPath([string]$path) {
    Ensure-ConfigDir
    $payload = @{
        RepoPath = $path
        ConfiguredAt = (Get-Date).ToString("o")
    } | ConvertTo-Json
    Set-Content -Path $configFile -Value $payload -Encoding UTF8
}

function Get-SavedRepoPath {
    if (Test-Path -LiteralPath $configFile) {
        try {
            $data = Get-Content -Path $configFile -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($data.RepoPath -and (Test-Path -LiteralPath $data.RepoPath)) {
                return $data.RepoPath
            }
        }
        catch { }
    }
    return $null
}

function Register-PowerShellProfile {
    param([string]$scriptFilePath)

    $profilePaths = @($PROFILE.CurrentUserCurrentHost, $PROFILE.CurrentUserAllHosts) | Select-Object -Unique
    foreach ($prof in $profilePaths) {
        if (-not $prof) { continue }
        $profDir = Split-Path -Parent $prof
        if (-not (Test-Path -LiteralPath $profDir)) {
            New-Item -ItemType Directory -Path $profDir -Force | Out-Null
        }
        if (-not (Test-Path -LiteralPath $prof)) {
            New-Item -ItemType File -Path $prof -Force | Out-Null
        }

        $content = Get-Content -Path $prof -Raw -ErrorAction SilentlyContinue
        if ($null -eq $content) { $content = "" }

        $marker = "# Windows Dynamic Bar CLI (island)"
        $snippet = @"

$marker
function island {
    param(
        [switch]`$Stop,
        [switch]`$Build,
        [switch]`$Config
    )
    & "$scriptFilePath" @PSBoundParameters
}
"@

        if ($content -notmatch [regex]::Escape($marker)) {
            Add-Content -Path $prof -Value $snippet -Encoding UTF8
            Write-Host "Configured 'island' command in PowerShell profile: $prof" -ForegroundColor Green
        }
    }
}

# --- Initial Setup / First-Run Configuration ---
$repoPath = Get-SavedRepoPath

if ($Config -or [string]::IsNullOrWhiteSpace($repoPath)) {
    Write-Host ""
    Write-Host "==========================================================" -ForegroundColor Cyan
    Write-Host "  Windows Dynamic Bar - Terminal CLI Setup (`island`)" -ForegroundColor Cyan
    Write-Host "==========================================================" -ForegroundColor Cyan
    Write-Host "Configure the 'island' command so you can run it from any directory." -ForegroundColor Gray
    Write-Host ""

    $currentCandidate = $PSScriptRoot
    if (-not (Test-Path (Join-Path $currentCandidate "WindowsDynamicBar.sln"))) {
        $currentCandidate = (Get-Location).Path
    }

    $validPath = $null
    while (-not $validPath) {
        $promptMsg = "Please provide the location of your project repository"
        if (Test-Path (Join-Path $currentCandidate "WindowsDynamicBar.sln")) {
            $promptMsg += " [$currentCandidate]"
        }
        $entered = Read-Host $promptMsg

        if ([string]::IsNullOrWhiteSpace($entered)) {
            $entered = $currentCandidate
        }

        # Resolve path
        if (Test-Path -LiteralPath $entered) {
            $resolved = (Resolve-Path -LiteralPath $entered).Path
            if (Test-Path (Join-Path $resolved "WindowsDynamicBar.sln")) {
                $validPath = $resolved
            }
            elseif (Test-Path (Join-Path $resolved "src\WindowsDynamicBar")) {
                $validPath = $resolved
            }
            else {
                Write-Host "Warning: Could not find 'WindowsDynamicBar.sln' at '$resolved'. Please specify the repository root." -ForegroundColor Yellow
            }
        }
        else {
            Write-Host "Path '$entered' does not exist. Please enter a valid directory." -ForegroundColor Red
        }
    }

    $repoPath = $validPath
    Save-RepoPath $repoPath
    Write-Host "Repository location saved to: $configFile" -ForegroundColor Green

    # Copy script to user island directory and register in PowerShell profile
    Ensure-ConfigDir
    $permanentScript = Join-Path $configDir "island.ps1"
    if ($PSCommandPath -and (Test-Path -LiteralPath $PSCommandPath)) {
        Copy-Item -LiteralPath $PSCommandPath -Destination $permanentScript -Force
    }
    else {
        Copy-Item -LiteralPath (Join-Path $repoPath "island.ps1") -Destination $permanentScript -Force
    }

    Register-PowerShellProfile -scriptFilePath $permanentScript
    Write-Host "Setup complete! You can now run 'island' from any terminal." -ForegroundColor Green
    Write-Host ""

    if ($Config) { return }
}

# --- Action: Stop ---
function Stop-Island {
    $procs = Get-Process -Name "WindowsDynamicBar" -ErrorAction SilentlyContinue
    if ($procs) {
        Write-Host "Stopping Windows Dynamic Bar..." -ForegroundColor Yellow
        $procs | Stop-Process -Force
        Start-Sleep -Milliseconds 400
        Write-Host "Windows Dynamic Bar stopped." -ForegroundColor Green
    }
    else {
        Write-Host "Windows Dynamic Bar is not currently running." -ForegroundColor Gray
    }
}

if ($Stop) {
    Stop-Island
    return
}

# --- Action: Build ---
function Build-Island {
    Write-Host "Building Windows Dynamic Bar (Release)..." -ForegroundColor Cyan
    $slnPath = Join-Path $repoPath "WindowsDynamicBar.sln"
    if (-not (Test-Path -LiteralPath $slnPath)) {
        $slnPath = Join-Path $repoPath "src\WindowsDynamicBar\WindowsDynamicBar.csproj"
    }

    $buildOutput = & dotnet build $slnPath -c Release -r win-x64 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed with exit code $($LASTEXITCODE):" -ForegroundColor Red
        $buildOutput | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        return $false
    }

    Write-Host "Build succeeded." -ForegroundColor Green
    return $true
}

if ($Build) {
    Stop-Island
    $built = Build-Island
    if (-not $built) {
        return
    }
}

# --- Action: Launch (Default or after Build) ---
if (-not $Build) {
    Stop-Island
}

# Locate executable
$candidateExes = @(
    (Join-Path $repoPath "src\WindowsDynamicBar\bin\Release\net8.0-windows10.0.19041.0\win-x64\WindowsDynamicBar.exe"),
    (Join-Path $repoPath "src\WindowsDynamicBar\bin\Release\net8.0-windows10.0.19041.0\WindowsDynamicBar.exe"),
    (Join-Path $repoPath "src\WindowsDynamicBar\bin\Debug\net8.0-windows10.0.19041.0\WindowsDynamicBar.exe"),
    (Join-Path $repoPath "release\WindowsDynamicBar.exe"),
    (Join-Path $repoPath "versions\WindowsDynamicBar.exe")
)

$targetExe = $candidateExes | Where-Object { Test-Path -LiteralPath $_ } | Sort-Object { (Get-Item $_).LastWriteTime } -Descending | Select-Object -First 1

if (-not $targetExe) {
    Write-Host "Application executable not found. Running build first..." -ForegroundColor Yellow
    $built = Build-Island
    if (-not $built) {
        Write-Host "Could not build application. Please check build errors." -ForegroundColor Red
        return
    }
    $targetExe = $candidateExes | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

if (-not $targetExe) {
    Write-Host "Could not locate WindowsDynamicBar.exe after build." -ForegroundColor Red
    return
}

Write-Host "Launching Windows Dynamic Bar..." -ForegroundColor Cyan
Write-Host "Executable: $targetExe" -ForegroundColor DarkGray
Start-Process -FilePath $targetExe
Write-Host "Windows Dynamic Bar launched successfully." -ForegroundColor Green
