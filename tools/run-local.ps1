# Runs Akiba on this machine, against a local database, for use in a browser on this machine.
#
#   powershell -ExecutionPolicy Bypass -File tools\run-local.ps1
#
# Then open http://localhost:5280. Press Ctrl+C in this window to stop it.
#
# This is for development and demonstration. It serves plain HTTP, which is fine when the
# browser and the server are the same computer and nothing crosses a network. It is NOT how
# Akiba runs for the society: that is a Windows service over HTTPS, set up by following
# docs/deployment.md, and nothing in this script should be copied into that.

param(
    [string]$Database = 'akiba_demo',
    [string]$PgUser = 'postgres',
    [securestring]$PgPassword,
    [string]$PgHost = 'localhost',
    [int]$PgPort = 5432,
    [int]$Port = 5280
)

$ErrorActionPreference = 'Stop'

# The production database is never served over plain HTTP, by this or anything else. A demo
# script that can be pointed at the real register is how the real register ends up on a
# laptop with the password going across the office network in clear text.
if ($Database -eq 'akiba') {
    throw "Refusing to serve the production database 'akiba' over plain HTTP. See docs/deployment.md."
}

$repoRoot = Split-Path -Parent $PSScriptRoot

# ---------------------------------------------------------------- Is PostgreSQL up?
$pg = Get-Service -Name 'postgresql*' -ErrorAction SilentlyContinue | Select-Object -First 1

if ($null -eq $pg) {
    throw "No PostgreSQL service found. Install PostgreSQL first - see docs/deployment.md, section 1."
}

if ($pg.Status -ne 'Running') {
    Write-Host "PostgreSQL ($($pg.Name)) is stopped. Starting it..." -ForegroundColor Yellow
    Start-Service $pg.Name
}

# ---------------------------------------------------------------- Is Akiba already running?
# Checked before asking for a password, so somebody who already has it open is told so rather
# than typing a password to be met with a port error thirty seconds later.
$busy = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue

if ($busy) {
    $owner = Get-Process -Id $busy[0].OwningProcess -ErrorAction SilentlyContinue
    Write-Host ""
    Write-Host "Something is already listening on port $Port" -ForegroundColor Yellow
    if ($owner) {
        Write-Host "  ($($owner.ProcessName), process $($owner.Id))"
    }
    Write-Host ""
    Write-Host "If that is Akiba, it is already running - just open http://localhost:$Port"
    Write-Host "To restart it, stop that process first:  Stop-Process -Id $($busy[0].OwningProcess)"
    exit 1
}

# ---------------------------------------------------------------- Connection
# Asked for rather than written into the file. A password in a script in source control is a
# password that eventually gets used somewhere it should not be.
if (-not $PgPassword) {
    $PgPassword = Read-Host -AsSecureString "PostgreSQL password for '$PgUser'"
}

$plain = [System.Net.NetworkCredential]::new('', $PgPassword).Password

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = "http://localhost:$Port"
$env:Akiba__RequireHttps = 'false'
$env:ConnectionStrings__Akiba =
    "Host=$PgHost;Port=$PgPort;Database=$Database;Username=$PgUser;Password=$plain"

# ---------------------------------------------------------------- Open the browser when it answers
# In the background, because the server holds this window until Ctrl+C. It waits for the app to
# actually answer rather than guessing at a delay, so a slow first build does not open a browser
# onto an error page.
$url = "http://localhost:$Port"

Start-Job -ArgumentList $url -ScriptBlock {
    param($url)
    for ($i = 0; $i -lt 120; $i++) {
        try {
            Invoke-WebRequest -Uri $url -UseBasicParsing -MaximumRedirection 0 -TimeoutSec 2 `
                -ErrorAction Stop | Out-Null
            break
        }
        catch {
            # A redirect to the sign-in page throws here and means the app is up.
            if ($_.Exception.Response) { break }
            Start-Sleep -Seconds 1
        }
    }
    Start-Process $url
} | Out-Null

Write-Host ""
Write-Host "Starting Akiba against '$Database'..." -ForegroundColor Cyan
Write-Host "  It builds first, which takes a minute the first time."
Write-Host "  Your browser opens on $url once it is ready."
Write-Host "  Press Ctrl+C here to stop it."
Write-Host ""

# --no-launch-profile so the port above is the one used. The launch profile would otherwise set
# its own, and a script that silently serves on a different port from the one it printed is
# worse than no script.
dotnet run --project "$repoRoot\src\Akiba.Web" -c Release --no-launch-profile
