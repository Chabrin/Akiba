# Akiba nightly backup.
#
# Dumps the database, encrypts it, copies it somewhere that is not this machine, and prunes
# what is too old to be worth keeping.
#
# Akiba is the only record of who is owed what. A machine can be replaced; the ledger cannot.
#
#   pwsh tools/backup.ps1 -OffMachinePath '\\fileserver\akiba-backups'
#
# Schedule it nightly with Task Scheduler. See docs/deployment.md section 6.

[CmdletBinding()]
param(
    # The database to dump.
    [string]$Database = 'akiba',

    # The login to dump as. It needs to read everything, so this is the owner, not the
    # application login - see docs/deployment.md section 1.
    [string]$PgUser = 'akiba_owner',

    [string]$PgHost = 'localhost',
    [int]$PgPort = 5432,

    # Where dumps are written before they are copied away.
    [string]$BackupPath = 'C:\Akiba\backups',

    # Where a copy goes. A backup on the same disk as the database protects you from nothing.
    [Parameter(Mandatory = $true)]
    [string]$OffMachinePath,

    # How many days of local dumps to keep. The off-machine copies are not pruned by this
    # script - deciding how long the society keeps its records is not a script's business.
    [int]$KeepDays = 14
)

$ErrorActionPreference = 'Stop'

# Silence PostgreSQL's NOTICE messages at the source.
#
# "database does not exist, skipping" from DROP DATABASE IF EXISTS is not an error, but
# Windows PowerShell wraps anything a native executable writes to stderr in an ErrorRecord -
# which, with ErrorActionPreference set to Stop, aborts the script. Redirecting with 2>$null
# does not help; it is the wrapping that is the problem. Telling psql not to say it does.
$env:PGOPTIONS = '-c client_min_messages=warning'

# ---------------------------------------------------------------------------
# Credentials come from the environment, never from this file.
# ---------------------------------------------------------------------------

if (-not $env:PGPASSWORD) {
    throw "PGPASSWORD is not set. The scheduled task must supply it; it does not belong in this script."
}

if (-not $env:AKIBA_BACKUP_PASSWORD) {
    throw "AKIBA_BACKUP_PASSWORD is not set. A dump is every member's financial position in one file and is not written unencrypted."
}

# ---------------------------------------------------------------------------
# Find the tools.
# ---------------------------------------------------------------------------

$pgDump = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\pg_dump.exe' -ErrorAction SilentlyContinue |
    Sort-Object FullName | Select-Object -Last 1

if (-not $pgDump) { throw "pg_dump.exe was not found under C:\Program Files\PostgreSQL." }

$sevenZip = Get-Command '7z.exe' -ErrorAction SilentlyContinue
if (-not $sevenZip) { $sevenZip = Get-Item 'C:\Program Files\7-Zip\7z.exe' -ErrorAction SilentlyContinue }
if (-not $sevenZip) { throw "7z.exe was not found. Install 7-Zip; the dump is not left unencrypted." }

New-Item -ItemType Directory -Force -Path $BackupPath | Out-Null

$stamp     = Get-Date -Format 'yyyy-MM-dd-HHmm'
$dump      = Join-Path $BackupPath "akiba-$stamp.dump"
$encrypted = "$dump.7z"

# ---------------------------------------------------------------------------
# Dump.
# ---------------------------------------------------------------------------

Write-Host "Dumping $Database..." -ForegroundColor Cyan

& $pgDump.FullName `
    --host=$PgHost --port=$PgPort --username=$PgUser `
    --format=custom --file=$dump $Database

if ($LASTEXITCODE -ne 0) { throw "pg_dump failed with exit code $LASTEXITCODE. Nothing was backed up." }

$size = (Get-Item $dump).Length

# A dump of Akiba with real data is never a few kilobytes. A file that small means pg_dump
# wrote an empty database, which is the failure that looks most like success.
if ($size -lt 50KB) {
    Remove-Item $dump -Force
    throw "The dump is only $size bytes. That is too small to be the society's ledger - refusing to keep it."
}

Write-Host ("  {0:N0} bytes" -f $size) -ForegroundColor Gray

# ---------------------------------------------------------------------------
# Encrypt, then remove the plain dump.
# ---------------------------------------------------------------------------

Write-Host "Encrypting..." -ForegroundColor Cyan

# -mhe=on encrypts the file names too, so the archive does not announce what it is.
& $sevenZip.FullName a -t7z -mhe=on "-p$($env:AKIBA_BACKUP_PASSWORD)" $encrypted $dump | Out-Null

if ($LASTEXITCODE -ne 0) { throw "Encryption failed with exit code $LASTEXITCODE. The plain dump has been left at $dump - encrypt or delete it." }

Remove-Item $dump -Force

# ---------------------------------------------------------------------------
# Copy it off the machine.
# ---------------------------------------------------------------------------

Write-Host "Copying to $OffMachinePath..." -ForegroundColor Cyan

if (-not (Test-Path $OffMachinePath)) {
    throw "$OffMachinePath is not reachable. The backup exists locally at $encrypted, and a backup on the same disk as the database protects you from nothing - fix this before relying on tonight's run."
}

Copy-Item $encrypted -Destination $OffMachinePath -Force

$copied = Join-Path $OffMachinePath (Split-Path $encrypted -Leaf)

if (-not (Test-Path $copied)) { throw "The copy to $OffMachinePath did not arrive." }

if ((Get-Item $copied).Length -ne (Get-Item $encrypted).Length) {
    throw "The copy at $copied is a different size from the original. Do not trust it."
}

# ---------------------------------------------------------------------------
# Prune the local copies.
# ---------------------------------------------------------------------------

Get-ChildItem $BackupPath -Filter 'akiba-*.dump.7z' |
    Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-$KeepDays) } |
    ForEach-Object {
        Write-Host "  pruning $($_.Name)" -ForegroundColor DarkGray
        Remove-Item $_.FullName -Force
    }

Write-Host ""
Write-Host "Backed up to $encrypted and copied to $OffMachinePath." -ForegroundColor Green
Write-Host "A backup nobody has restored is a rumour. tools/verify-restore.ps1 runs quarterly." -ForegroundColor Yellow
