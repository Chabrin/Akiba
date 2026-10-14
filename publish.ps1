#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Publishes Akiba.Web, restores the production config, and restarts the Windows Service.

.DESCRIPTION
    Run this script from the repo root every time you want to deploy a new version.
    It stops the service, builds a self-contained win-x64 release, writes back the
    production appsettings (which dotnet publish overwrites), then restarts the service.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -ServiceName "AkibaWelfare"
#>

param(
    [string]$ServiceName = "AkibaWelfare",
    [string]$PublishDir  = "$PSScriptRoot\publish\win-x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Production config ────────────────────────────────────────────────────────
# Edit the connection string here if the PostgreSQL password or database name
# ever changes.  The Urls value "*" means Kestrel listens on ALL network
# interfaces on that port, so officials on the LAN can connect.
$ProductionConfig = @"
{
  "ConnectionStrings": {
    "Akiba": "Host=localhost;Port=5432;Database=akiba;Username=postgres;Password=123"
  },
  "Akiba": {
    "RequireHttps": false
  },
  "Urls": "http://*:5280"
}
"@

function Write-Step([string]$msg) {
    Write-Host "`n==> $msg" -ForegroundColor Cyan
}

function Test-ServiceExists([string]$name) {
    return $null -ne (Get-Service -Name $name -ErrorAction SilentlyContinue)
}

# ── 1. Stop the service if it is running ────────────────────────────────────
Write-Step "Stopping service '$ServiceName' (if running)..."
if (Test-ServiceExists $ServiceName) {
    $svc = Get-Service -Name $ServiceName
    if ($svc.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force
        $svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
        Write-Host "   Service stopped." -ForegroundColor Green
    } else {
        Write-Host "   Service was already stopped." -ForegroundColor Yellow
    }
} else {
    Write-Host "   Service not found — will publish without stopping." -ForegroundColor Yellow
}

# ── 2. Publish ───────────────────────────────────────────────────────────────
Write-Step "Publishing Akiba.Web (Release / win-x64 / self-contained)..."
& dotnet publish "$PSScriptRoot\src\Akiba.Web\Akiba.Web.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "`nPublish failed. Service NOT restarted." -ForegroundColor Red
    exit 1
}
Write-Host "   Publish succeeded." -ForegroundColor Green

Write-Step "Publishing Akiba.Desktop (the native window launcher)..."
& dotnet publish "$PSScriptRoot\src\Akiba.Desktop\Akiba.Desktop.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "`nDesktop publish failed. Service NOT restarted." -ForegroundColor Red
    exit 1
}
Write-Host "   Desktop publish succeeded." -ForegroundColor Green

# ── 3. Restore the production appsettings ───────────────────────────────────
Write-Step "Writing appsettings.Production.json..."
$configPath = Join-Path $PublishDir "appsettings.Production.json"
$ProductionConfig | Out-File -FilePath $configPath -Encoding utf8 -Force
Write-Host "   Written to $configPath" -ForegroundColor Green

# ── 4. Register the service the first time (idempotent) ─────────────────────
if (-not (Test-ServiceExists $ServiceName)) {
    Write-Step "Registering Windows Service '$ServiceName' for the first time..."
    $exePath = Join-Path $PublishDir "Akiba.Web.exe"
    & sc.exe create $ServiceName `
        binPath= "`"$exePath`"" `
        DisplayName= "Akiba CAL Staff Welfare" `
        start= auto | Out-Null
    & sc.exe description $ServiceName "Akiba CAL Staff Welfare Society — Blazor Server panel" | Out-Null
    Write-Host "   Service registered." -ForegroundColor Green
}

# ── 5. Start the service ─────────────────────────────────────────────────────
Write-Step "Starting service '$ServiceName'..."
Start-Service -Name $ServiceName
$svc = Get-Service -Name $ServiceName
$svc.WaitForStatus("Running", [TimeSpan]::FromSeconds(20))
Write-Host "   Service is running." -ForegroundColor Green

# ── 6. Quick health check ────────────────────────────────────────────────────
Write-Step "Health check (http://localhost:5280)..."
Start-Sleep -Seconds 3
try {
    $response = Invoke-WebRequest -Uri "http://localhost:5280" `
        -TimeoutSec 10 -UseBasicParsing -ErrorAction Stop
    Write-Host "   HTTP $($response.StatusCode) — app is up." -ForegroundColor Green
} catch {
    Write-Host "   No response yet — the app may still be starting. Check http://localhost:5280 in a moment." -ForegroundColor Yellow
}

Write-Host "`nDone.`n" -ForegroundColor Cyan
Write-Host "  • Chairman's desktop app : $PublishDir\Akiba.exe" -ForegroundColor White
Write-Host "  • LAN (other officials)  : http://<this-machine-IP>:5280`n" -ForegroundColor White
