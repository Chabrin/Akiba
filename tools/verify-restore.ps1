# Proves the newest backup can actually be restored, and that what comes back is Akiba.
#
# An untested backup is a rumour. This takes the most recent encrypted dump, restores it into a
# scratch database, and checks four things that would all be true of the real ledger and would
# not all be true of a corrupt or truncated one:
#
#   1. it restores at all;
#   2. the tables that must have rows have rows;
#   3. every journal entry still sums to zero - the trial balance is zero;
#   4. the audit trail is still refusing to be edited.
#
# The third is the one worth having. A dump that restores but whose ledger no longer balances
# is a dump that would be discovered to be useless on the worst day of the society's year.
#
#   pwsh tools/verify-restore.ps1 -BackupPath 'C:\Akiba\backups'
#
# Run it quarterly. See docs/deployment.md section 6.

[CmdletBinding()]
param(
    [string]$BackupPath = 'C:\Akiba\backups',

    # Restored into here. Dropped and recreated every run.
    [string]$ScratchDatabase = 'akiba_restore_check',

    [string]$PgUser = 'postgres',
    [string]$PgHost = 'localhost',
    [int]$PgPort = 5432,

    # Keep the scratch database afterwards, to look at it.
    [switch]$KeepScratch
)

$ErrorActionPreference = 'Stop'

# Silence PostgreSQL's NOTICE messages at the source.
#
# "database does not exist, skipping" from DROP DATABASE IF EXISTS is not an error, but
# Windows PowerShell wraps anything a native executable writes to stderr in an ErrorRecord -
# which, with ErrorActionPreference set to Stop, aborts the script. Redirecting with 2>$null
# does not help; it is the wrapping that is the problem. Telling psql not to say it does.
$env:PGOPTIONS = '-c client_min_messages=warning'

if ($ScratchDatabase -eq 'akiba') {
    throw "Refusing to restore over a database called 'akiba' - that is the production name."
}

if (-not $env:PGPASSWORD) { throw "PGPASSWORD is not set." }
if (-not $env:AKIBA_BACKUP_PASSWORD) { throw "AKIBA_BACKUP_PASSWORD is not set - the backups are encrypted." }

# ---------------------------------------------------------------------------
# Tools and the newest backup.
# ---------------------------------------------------------------------------

function Find-PgTool([string]$name) {
    $tool = Get-ChildItem "C:\Program Files\PostgreSQL\*\bin\$name" -ErrorAction SilentlyContinue |
        Sort-Object FullName | Select-Object -Last 1
    if (-not $tool) { throw "$name was not found under C:\Program Files\PostgreSQL." }
    return $tool.FullName
}

$psql      = Find-PgTool 'psql.exe'
$pgRestore = Find-PgTool 'pg_restore.exe'

$sevenZip = Get-Command '7z.exe' -ErrorAction SilentlyContinue
if (-not $sevenZip) { $sevenZip = Get-Item 'C:\Program Files\7-Zip\7z.exe' -ErrorAction SilentlyContinue }
if (-not $sevenZip) { throw "7z.exe was not found." }

$newest = Get-ChildItem $BackupPath -Filter 'akiba-*.dump.7z' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $newest) { throw "No backup was found in $BackupPath. That is itself the finding." }

$age = (Get-Date) - $newest.LastWriteTime

Write-Host "Newest backup: $($newest.Name), $([int]$age.TotalHours) hours old." -ForegroundColor Cyan

if ($age.TotalDays -gt 2) {
    Write-Warning "The newest backup is $([int]$age.TotalDays) days old. The nightly job may not be running."
}

# ---------------------------------------------------------------------------
# Decrypt and restore.
# ---------------------------------------------------------------------------

$work = Join-Path ([System.IO.Path]::GetTempPath()) "akiba-restore-$(Get-Random)"
New-Item -ItemType Directory -Force -Path $work | Out-Null

$failures = @()

try {
    Write-Host "Decrypting..." -ForegroundColor Cyan
    & $sevenZip.FullName x "-p$($env:AKIBA_BACKUP_PASSWORD)" "-o$work" $newest.FullName -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not decrypt $($newest.Name). Check AKIBA_BACKUP_PASSWORD." }

    $dump = Get-ChildItem $work -Filter '*.dump' -Recurse | Select-Object -First 1
    if (-not $dump) { throw "The archive did not contain a .dump file." }

    Write-Host "Restoring into $ScratchDatabase..." -ForegroundColor Cyan

    & $psql -U $PgUser -h $PgHost -p $PgPort -d postgres -q -c "DROP DATABASE IF EXISTS $ScratchDatabase;" | Out-Null
    & $psql -U $PgUser -h $PgHost -p $PgPort -d postgres -q -c "CREATE DATABASE $ScratchDatabase;" | Out-Null

    & $pgRestore --host=$PgHost --port=$PgPort --username=$PgUser `
        --dbname=$ScratchDatabase --no-owner --no-privileges $dump.FullName

    # pg_restore warns about ownership and extensions on a restore into a fresh database. Those
    # are expected; a non-zero exit is not automatically a failure, so the checks below decide.
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "pg_restore exited $LASTEXITCODE. The checks below decide whether that mattered."
    }

    # -----------------------------------------------------------------------
    # The checks.
    # -----------------------------------------------------------------------

    # SQL goes through a file, not through -c.
    #
    # This is not fussiness. PowerShell strips the double quotes out of a string on its way to a
    # native executable, so `-c 'SELECT SUM("SignedAmount") ...'` reaches psql as
    # SUM(SignedAmount) - which PostgreSQL folds to lowercase, fails to find, and errors on.
    # The query then returned nothing, the empty string cast to 0, and this script cheerfully
    # reported that the ledger balanced. A verification that passes whatever it is given is
    # worse than none, because somebody trusts it.
    #
    # Every identifier in Akiba's schema is quoted, so every query here needs this.
    function Invoke-Scalar([string]$sql) {
        $file = Join-Path $work "query-$(Get-Random).sql"
        Set-Content -Path $file -Value $sql -Encoding UTF8

        # -q suppresses psql's command tags. Without it a script containing a DO block answers
        # with two lines - "DO" and then the result - and a caller comparing the whole thing to
        # "REFUSED" concludes the check failed. That is exactly what happened, and the symptom
        # was this script reporting that a restored audit trail accepted an edit when it had
        # refused one.
        $lines = & $psql -U $PgUser -h $PgHost -p $PgPort -d $ScratchDatabase `
            -q -A -t -v ON_ERROR_STOP=1 -f $file

        $failed = $LASTEXITCODE -ne 0
        Remove-Item $file -Force -ErrorAction SilentlyContinue

        if ($failed) { throw "The query failed against the restored database:`n$sql" }

        # The last non-empty line, so a stray blank or tag cannot become the answer.
        $value = ($lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -Last 1 | Out-String).Trim()

        if ([string]::IsNullOrWhiteSpace($value)) { throw "The query returned nothing:`n$sql" }

        return $value
    }

    Write-Host ""
    Write-Host "Checking what came back..." -ForegroundColor Cyan

    # 1. The tables that must have rows.
    foreach ($table in @('accounts', 'journal_entries', 'journal_lines', 'borrowers')) {
        $count = Invoke-Scalar "SELECT COUNT(*) FROM akiba.`"$table`";"

        if (-not $count -or [int]$count -eq 0) {
            $failures += "akiba.$table came back empty."
            Write-Host "  $table : EMPTY" -ForegroundColor Red
        }
        else {
            Write-Host ("  {0,-16} {1,8} rows" -f $table, $count) -ForegroundColor Gray
        }
    }

    # 2. The trial balance. Every journal entry sums to zero by construction, so the whole
    #    ledger does too - unless the restore lost or mangled a line.
    $difference = Invoke-Scalar 'SELECT COALESCE(SUM("SignedAmount"), 0) FROM akiba."journal_lines";'

    Write-Host ""
    if ([decimal]$difference -ne 0) {
        $failures += "The restored ledger does not balance: the journal lines sum to $difference, not zero."
        Write-Host "  Trial balance    $difference  <-- NOT ZERO" -ForegroundColor Red
    }
    else {
        Write-Host "  Trial balance    0.0000  (the restored ledger balances)" -ForegroundColor Green
    }

    # 3. The audit trail is still append-only. The trigger travels with the dump; if it did
    #    not, a restored database would quietly lose the guarantee.
    #
    #    Asked two ways. The catalog query is the deterministic one - it either exists or it
    #    does not, and no output has to be parsed. The tamper attempt is the one that proves it
    #    actually fires, and it reports its own verdict on stdout rather than letting the
    #    trigger write to stderr - Windows PowerShell renders anything a native executable
    #    puts there as an alarming red block, in the middle of a check that is passing.
    $triggers = Invoke-Scalar @"
SELECT COUNT(*)
FROM pg_trigger t
JOIN pg_class c ON c.oid = t.tgrelid
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'akiba'
  AND c.relname = 'audit_entries'
  AND t.tgname = 'audit_entries_no_update_or_delete';
"@

    $auditRows = Invoke-Scalar 'SELECT COUNT(*) FROM akiba."audit_entries";'

    # The tamper attempt is wrapped in a DO block that catches its own exception and records
    # the verdict in a temporary table, so the answer arrives on stdout as a word. A bare
    # UPDATE would write the trigger's message to stderr, which Windows PowerShell renders as
    # an alarming red block in the middle of a check that is passing.
    # A literal here-string: PostgreSQL's dollar quoting is $$, and in an expandable
    # here-string PowerShell would try to read that as a variable.
    $verdict = Invoke-Scalar @'
DO $$
BEGIN
    UPDATE akiba."audit_entries" SET "ActorName" = 'tampered';
    CREATE TEMP TABLE tamper_result AS SELECT 'ACCEPTED' AS verdict;
EXCEPTION WHEN others THEN
    CREATE TEMP TABLE tamper_result AS SELECT 'REFUSED' AS verdict;
END
$$;
SELECT verdict FROM tamper_result;
'@

    if ([int]$triggers -eq 0) {
        $failures += "The immutability trigger is not on the restored audit_entries table. It did not survive the dump."
        Write-Host "  Audit trail      TRIGGER MISSING" -ForegroundColor Red
    }
    elseif ([int]$auditRows -eq 0) {
        # An UPDATE matching no rows never fires a row trigger, so the attempt proves nothing.
        # The trigger is there, which is what this dump can tell us.
        Write-Host "  Audit trail      trigger present; no rows in this dump to test it on" -ForegroundColor Yellow
    }
    elseif ($verdict -eq 'REFUSED') {
        Write-Host "  Audit trail      still refuses to be edited ($auditRows rows)" -ForegroundColor Green
    }
    else {
        $failures += "The restored audit trail accepted an UPDATE although the trigger is present."
        Write-Host "  Audit trail      ACCEPTED AN EDIT" -ForegroundColor Red
    }

}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue

    if (-not $KeepScratch) {
        & $psql -U $PgUser -h $PgHost -p $PgPort -d postgres `
            -q -c "DROP DATABASE IF EXISTS $ScratchDatabase;" | Out-Null
    }
    else {
        Write-Host ""
        Write-Host "Scratch database $ScratchDatabase kept, as asked." -ForegroundColor Yellow
    }
}

Write-Host ""

if ($failures.Count -gt 0) {
    Write-Host "RESTORE VERIFICATION FAILED" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host ""
    Write-Host "Do not assume the other backups are any better. Take a fresh one by hand and run this again." -ForegroundColor Red
    exit 1
}

Write-Host "Restore verified: $($newest.Name) restores, and what comes back is Akiba." -ForegroundColor Green
Write-Host "Tell the treasurer. This is the check that turns a backup from a rumour into a fact." -ForegroundColor Green
