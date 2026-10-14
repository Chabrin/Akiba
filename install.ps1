#Requires -RunAsAdministrator
<#
.SYNOPSIS
    First-time installation of Akiba on a new Windows machine.

.DESCRIPTION
    Run this ONCE on any machine that will host Akiba.
    It installs PostgreSQL 18, creates the database, copies the app,
    registers it as a Windows Service that starts automatically, and
    puts a shortcut on the desktop.

.PARAMETER AppSource
    Path to the published app files (the publish\win-x64 folder).
    Examples:
      .\install.ps1 -AppSource "\\192.168.5.12\AkibaPublish"   # network share
      .\install.ps1 -AppSource "E:\Akiba"                       # USB drive
      .\install.ps1 -AppSource "C:\Temp\Akiba"                  # local copy

.EXAMPLE
    .\install.ps1 -AppSource "\\192.168.5.12\AkibaPublish"
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$AppSource,

    [string]$InstallDir  = "C:\Akiba",
    [string]$ServiceName = "AkibaWelfare",
    [string]$DbName      = "akiba",
    [string]$DbPassword  = "123",          # postgres superuser password
    [int]   $AppPort     = 5280
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step([string]$msg) {
    Write-Host "`n==> $msg" -ForegroundColor Cyan
}
function Write-OK([string]$msg) {
    Write-Host "   OK: $msg" -ForegroundColor Green
}
function Write-Warn([string]$msg) {
    Write-Host "   !! $msg" -ForegroundColor Yellow
}

# ── 1. PostgreSQL ─────────────────────────────────────────────────────────────
Write-Step "Checking for PostgreSQL..."

$pgService = Get-Service -Name "postgresql*" -ErrorAction SilentlyContinue | Select-Object -First 1

if ($pgService) {
    Write-OK "PostgreSQL service found: $($pgService.Name)"
} else {
    Write-Step "PostgreSQL not found. Installing via winget..."
    Write-Host "   When the installer asks for a password, enter: $DbPassword" -ForegroundColor Yellow
    Write-Host "   Leave the port as 5432 and click through the rest." -ForegroundColor Yellow

    winget install --id PostgreSQL.PostgreSQL.18 `
        --silent `
        --accept-package-agreements `
        --accept-source-agreements

    if ($LASTEXITCODE -ne 0) {
        Write-Host @"

   winget could not install PostgreSQL automatically.
   Please install it manually:

   1. Go to: https://www.postgresql.org/download/windows/
   2. Download PostgreSQL 18 for Windows x64
   3. Run the installer — set the postgres password to: $DbPassword
   4. Leave the port as 5432
   5. After installation, re-run this script.

"@ -ForegroundColor Red
        exit 1
    }

    # Give the service a moment to start
    Start-Sleep -Seconds 5
    $pgService = Get-Service -Name "postgresql*" -ErrorAction SilentlyContinue | Select-Object -First 1
    Write-OK "PostgreSQL installed: $($pgService.Name)"
}

# Ensure it is running
if ($pgService.Status -ne "Running") {
    Start-Service -Name $pgService.Name
    Write-OK "PostgreSQL started."
}

# ── 2. Find psql ─────────────────────────────────────────────────────────────
Write-Step "Locating psql..."

$pgBin = Get-ChildItem "C:\Program Files\PostgreSQL" -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    Select-Object -First 1 |
    ForEach-Object { Join-Path $_.FullName "bin" }

if (-not $pgBin -or -not (Test-Path "$pgBin\psql.exe")) {
    Write-Host "   Cannot find psql.exe under C:\Program Files\PostgreSQL." -ForegroundColor Red
    Write-Host "   Make sure PostgreSQL is installed, then re-run." -ForegroundColor Red
    exit 1
}

Write-OK "psql found at $pgBin"
$env:PATH     += ";$pgBin"
$env:PGPASSWORD = $DbPassword

# ── 3. Create the database ───────────────────────────────────────────────────
Write-Step "Setting up database '$DbName'..."

# Check if database already exists
$dbExists = & "$pgBin\psql.exe" -U postgres -tAc `
    "SELECT 1 FROM pg_database WHERE datname='$DbName';" 2>$null

if ($dbExists -eq "1") {
    Write-OK "Database '$DbName' already exists — skipping create."
} else {
    & "$pgBin\psql.exe" -U postgres -c "CREATE DATABASE $DbName;"
    Write-OK "Database '$DbName' created."
}

# ── 4. Copy the application ──────────────────────────────────────────────────
Write-Step "Copying application from '$AppSource' to '$InstallDir'..."

if (-not (Test-Path $AppSource)) {
    Write-Host "   Source path not found: $AppSource" -ForegroundColor Red
    Write-Host "   Make sure the USB drive is plugged in or the network share is accessible." -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

# Stop the service before overwriting files
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne "Stopped") {
    Stop-Service -Name $ServiceName -Force
    $svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
}

Copy-Item -Path "$AppSource\*" -Destination $InstallDir -Recurse -Force
Write-OK "Files copied to $InstallDir"

# ── 5. Write production config ───────────────────────────────────────────────
Write-Step "Writing appsettings.Production.json..."

$config = @"
{
  "ConnectionStrings": {
    "Akiba": "Host=localhost;Port=5432;Database=$DbName;Username=postgres;Password=$DbPassword"
  },
  "Akiba": {
    "RequireHttps": false
  },
  "Urls": "http://*:$AppPort"
}
"@

$config | Out-File -FilePath "$InstallDir\appsettings.Production.json" -Encoding utf8 -Force
Write-OK "Config written."

# ── 6. Register the Windows Service ─────────────────────────────────────────
Write-Step "Registering Windows Service '$ServiceName'..."

$webExe = "$InstallDir\Akiba.Web.exe"

$existingSvc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingSvc) {
    Write-OK "Service already registered — skipping."
} else {
    & sc.exe create $ServiceName `
        binPath= "`"$webExe`"" `
        DisplayName= "Akiba CAL Staff Welfare" `
        start= auto | Out-Null
    & sc.exe description $ServiceName `
        "Akiba CAL Staff Welfare Society management system" | Out-Null
    Write-OK "Service registered."
}

# ── 7. Start the service ─────────────────────────────────────────────────────
Write-Step "Starting service..."
Start-Service -Name $ServiceName
$svc = Get-Service -Name $ServiceName
$svc.WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
Write-OK "Service is running."

# ── 8. Health check ──────────────────────────────────────────────────────────
Write-Step "Health check..."
Start-Sleep -Seconds 5
try {
    $r = Invoke-WebRequest -Uri "http://localhost:$AppPort" -TimeoutSec 15 -UseBasicParsing
    Write-OK "App is up — HTTP $($r.StatusCode)"
} catch {
    Write-Warn "No response yet — app may still be starting. Check http://localhost:$AppPort in a moment."
}

# ── 9. Desktop shortcut for Akiba.exe ───────────────────────────────────────
Write-Step "Creating desktop shortcut..."

$desktopExe = "$InstallDir\Akiba.exe"
if (Test-Path $desktopExe) {
    $shell    = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut("$env:PUBLIC\Desktop\Akiba.lnk")
    $shortcut.TargetPath       = $desktopExe
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.Description      = "Akiba CAL Staff Welfare Society"
    $shortcut.Save()
    Write-OK "Shortcut created on desktop for all users."
} else {
    Write-Warn "Akiba.exe not found — desktop shortcut skipped."
}

# ── Done ─────────────────────────────────────────────────────────────────────
Write-Host @"

Done! Akiba is installed and running.

  Desktop app  : Double-click 'Akiba' on the desktop
  Browser      : http://localhost:$AppPort
  LAN access   : http://<this-machine-IP>:$AppPort

The Windows Service starts automatically every time Windows boots.

"@ -ForegroundColor Cyan
