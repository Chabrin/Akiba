# Fills a development database with plausible Akiba activity, so the panel has something to
# show and somebody evaluating it can see what the screens do.
#
# NEVER run this against the live database. It creates people who do not exist. Real opening
# balances arrive through the migration tooling, which is a reviewed and signed-off process.
#
#   pwsh tools/seed-demo.ps1 -Database akiba_demo

param(
    [string]$Database = 'akiba_demo',
    [string]$PgUser = 'postgres',
    [string]$PgPassword = '123',
    [string]$PgHost = 'localhost',
    [int]$PgPort = 5432,
    [int]$Port = 5280
)

$ErrorActionPreference = 'Stop'

if ($Database -eq 'akiba') {
    throw "Refusing to seed a database called 'akiba' - that is the production name."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$env:PGPASSWORD = $PgPassword
$psql = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\psql.exe' | Select-Object -Last 1

Write-Host "Recreating $Database..." -ForegroundColor Cyan
& $psql.FullName -U $PgUser -h $PgHost -p $PgPort -c "DROP DATABASE IF EXISTS $Database;" | Out-Null
& $psql.FullName -U $PgUser -h $PgHost -p $PgPort -c "CREATE DATABASE $Database;" | Out-Null

$env:ConnectionStrings__Akiba = "Host=$PgHost;Port=$PgPort;Database=$Database;Username=$PgUser;Password=$PgPassword"
$env:ASPNETCORE_ENVIRONMENT = 'Development'

Write-Host "Starting Akiba on port $Port..." -ForegroundColor Cyan
$app = Start-Process -FilePath 'dotnet' `
    -ArgumentList @('run', '--project', "$repoRoot/src/Akiba.Web", '--no-launch-profile',
                    '--urls', "http://localhost:$Port", '-c', 'Release') `
    -PassThru -NoNewWindow -RedirectStandardOutput "$env:TEMP/akiba-seed.log" `
    -RedirectStandardError "$env:TEMP/akiba-seed.err"

try {
    $deadline = (Get-Date).AddMinutes(3)
    do {
        Start-Sleep -Seconds 2
        $up = $false
        try {
            $up = (Invoke-WebRequest "http://localhost:$Port/health" -TimeoutSec 3).StatusCode -eq 200
        } catch { }
    } until ($up -or (Get-Date) -gt $deadline)

    if (-not $up) { throw "Akiba did not start. See $env:TEMP/akiba-seed.log" }

    Write-Host "Seeding..." -ForegroundColor Cyan
    $body = Invoke-RestMethod -Method Post -Uri "http://localhost:$Port/api/dev/seed-demo" -TimeoutSec 300
    $body | ConvertTo-Json -Depth 4 | Write-Host

    Write-Host ""
    Write-Host "Done. Run Akiba against it with:" -ForegroundColor Green
    Write-Host "  `$env:ConnectionStrings__Akiba = '$($env:ConnectionStrings__Akiba)'"
    Write-Host "  dotnet run --project src/Akiba.Web"
}
finally {
    if (-not $app.HasExited) { $app.Kill() }
}
