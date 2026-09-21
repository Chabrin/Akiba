# Deploying Akiba

Akiba runs on **one dedicated machine on the CAL LAN**. Not the CAL server, and never exposed
to the internet.

This guide assumes you are comfortable installing software and editing a text file. It does
not assume you are a developer.

---

## Why there is no Docker here

Akiba used to ship with a `docker-compose.yml`. It was dropped deliberately.

Docker earns its keep when the same software must run identically across many environments.
Akiba has **one** environment: a single machine, four users, maintained by whoever is around
when something breaks. Against that, Docker Desktop on Windows needs WSL2, updates itself on
its own schedule, and fails in ways that are hard to read — which is exactly what happened
during development.

Without it, the moving parts are ones any IT contractor in Nairobi already knows: PostgreSQL,
a Windows service, and `pg_dump`. That is worth more to Akiba than image reproducibility.

The application code is unaffected. Nothing in Akiba knows or cares whether it is containerised.

---

## 1. Install PostgreSQL

Install **PostgreSQL 17 or later** from <https://www.postgresql.org/download/>.

During installation:

- Set a password for the `postgres` superuser and **write it down somewhere safe**. The
  treasurer and the chairman hold it, not ICT.
- Keep the default port, 5432.
- Leave PostgreSQL listening on `localhost` only. Akiba talks to it from the same machine, and
  nothing else needs a route to it.

Then create Akiba's database and its own login. Open **SQL Shell (psql)** and run:

```sql
-- The login that owns the schema. Used only when upgrading Akiba, never day to day.
CREATE USER akiba_owner WITH PASSWORD 'choose-a-long-random-password';
CREATE DATABASE akiba OWNER akiba_owner;

-- The login the application runs as. It reads and writes rows and owns nothing.
CREATE USER akiba_app WITH PASSWORD 'choose-a-different-long-random-password';
```

Then connect to the new database (`\c akiba`) and give the application login exactly what it
needs and nothing else:

```sql
GRANT USAGE ON SCHEMA akiba TO akiba_app;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA akiba TO akiba_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA akiba TO akiba_app;

-- And the same for anything a future upgrade adds.
ALTER DEFAULT PRIVILEGES FOR ROLE akiba_owner IN SCHEMA akiba
    GRANT SELECT, INSERT, UPDATE ON TABLES TO akiba_app;
ALTER DEFAULT PRIVILEGES FOR ROLE akiba_owner IN SCHEMA akiba
    GRANT USAGE, SELECT ON SEQUENCES TO akiba_app;
```

### Why two logins rather than one

Neither is `postgres`, so a compromised Akiba cannot touch another database. That much is
obvious. The split between the two is less obvious and matters more.

**No `DELETE` anywhere.** Akiba never deletes a financial record — corrections are reversing
entries — so the login it runs as has no way to delete one either. If a bug ever tried, the
database would refuse.

**The application does not own its tables.** The audit trail is protected by a trigger that
refuses `UPDATE` and `DELETE` on it, and the *owner* of a table can switch a trigger off.
Running as a login that owns nothing means the one account an attacker reaches through the web
application cannot quietly disable the thing that records what they did.

The cost is one extra step when upgrading: migrations run as `akiba_owner`, which is covered in
section 7.

---

## 2. Publish Akiba

On a machine with the .NET SDK installed:

```bash
dotnet publish src/Akiba.Web --configuration Release --output ./publish
```

Copy the `publish` folder to the Akiba machine — `C:\Akiba` is a reasonable home.

The Akiba machine needs the **ASP.NET Core Runtime** (not the full SDK), from
<https://dotnet.microsoft.com/download>.

---

## 3. Configure it

Akiba reads standard .NET configuration. The two settings that matter:

| Setting | What it is |
|---|---|
| `ConnectionStrings__Akiba` | `Host=localhost;Port=5432;Database=akiba;Username=akiba_app;Password=...` |
| `ASPNETCORE_URLS` | The address Akiba listens on |
| `AllowedHosts` | The address officials type, e.g. `akiba.local;192.168.1.40`. Akiba refuses a request for any other host |
| `Akiba__RequireHttps` | Leave unset. It defaults to `true` and should stay that way — see below |

### `Akiba__RequireHttps`

On, Akiba marks its cookies `Secure`, sends HSTS and redirects plain HTTP. **Leave it on.**

Off, every password and every session cookie crosses the office network in clear text. Anybody
who can see that traffic — another machine on the same switch, a compromised printer, somebody
with the wifi key — can read a member's position or take over an official's session. Akiba logs
a warning at startup every time it starts with this off, and the warning is not decoration.

If the machine has no certificate, get one before go-live. On an internal machine that means
either a certificate from the organisation's own authority, or a self-signed certificate
installed as trusted on the four machines that use Akiba. It is an afternoon's work once.

**On `ASPNETCORE_URLS`, read this twice.** Akiba holds member financial records.

- `http://127.0.0.1:8080` — reachable only from the Akiba machine itself. The safe default.
- `http://192.168.1.40:8080` — the machine's **own LAN address**, so the four officials can
  reach it. This is what you want in production.
- `http://0.0.0.0:8080` — every network interface. **Never use this.**

Set them as machine-level environment variables so the service picks them up:

```powershell
[Environment]::SetEnvironmentVariable('ConnectionStrings__Akiba', 'Host=localhost;Port=5432;Database=akiba;Username=akiba_app;Password=...', 'Machine')
[Environment]::SetEnvironmentVariable('ASPNETCORE_URLS', 'https://192.168.1.40:8443', 'Machine')
[Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Production', 'Machine')
[Environment]::SetEnvironmentVariable('AllowedHosts', 'akiba.local;192.168.1.40', 'Machine')
```

---

## 4. Run it as a service

So that Akiba starts with the machine and nobody has to remember to launch it.

### Windows

```powershell
New-Service -Name Akiba `
            -BinaryPathName 'C:\Akiba\Akiba.Web.exe' `
            -DisplayName 'Akiba Sacco Management System' `
            -StartupType Automatic
Start-Service Akiba
```

Check on it with `Get-Service Akiba`. When it will not start, **Event Viewer → Windows Logs →
Application** says why.

### Linux

Create `/etc/systemd/system/akiba.service`:

```ini
[Unit]
Description=Akiba Sacco Management System
After=network.target postgresql.service

[Service]
WorkingDirectory=/opt/akiba
ExecStart=/usr/bin/dotnet /opt/akiba/Akiba.Web.dll
Restart=always
RestartSec=10
User=akiba
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://192.168.1.40:8080
Environment=ConnectionStrings__Akiba=Host=localhost;Port=5432;Database=akiba;Username=akiba;Password=...

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable --now akiba
sudo systemctl status akiba
```

---

## 5. Check it is up

From the Akiba machine:

```
http://localhost:8080/health
```

It should say `Healthy`. That checks the database too, so a healthy response means Akiba has
applied its migrations and can read and write.

From an official's machine on the LAN, the same but with the machine's address.

---

## 6. Back it up

**This is the most important section in this document.** Akiba is the only record of who is
owed what. A machine can be replaced; the ledger cannot.

A nightly `pg_dump`, encrypted, with a copy off the machine:

```powershell
$date  = Get-Date -Format 'yyyy-MM-dd'
$dump  = "C:\Akiba\backups\akiba-$date.dump"

& 'C:\Program Files\PostgreSQL\17\bin\pg_dump.exe' `
    --host=localhost --username=akiba_owner --format=custom --file=$dump akiba

# Encrypt it. The backup contains every member's financial position.
& 'C:\Program Files\7-Zip\7z.exe' a -tzip -p"$env:AKIBA_BACKUP_PASSWORD" "$dump.zip" $dump
Remove-Item $dump

# Then copy the .zip somewhere that is not this machine.
```

Schedule it with Task Scheduler (or `cron`) to run nightly.

**Off the machine** means off the machine: another computer in the office, an external drive
kept elsewhere, or cloud storage the treasurer controls. A backup sitting on the same disk as
the database protects you from exactly nothing.

### Test the restore

An untested backup is a rumour. Once a quarter, restore the newest dump into a scratch
database and check it opens:

```powershell
& 'C:\Program Files\PostgreSQL\17\bin\createdb.exe' --username=postgres akiba_restore_test
& 'C:\Program Files\PostgreSQL\17\bin\pg_restore.exe' --username=postgres --dbname=akiba_restore_test $dump
```

Then point a spare copy of Akiba at `akiba_restore_test` and confirm the trial balance still
reads zero and a member's shareholding looks right. Drop the scratch database afterwards.

---

## 7. Upgrading

1. Stop the service.
2. **Take a backup, and confirm the file exists and has a sensible size.**
3. Replace the files in `C:\Akiba` with the new publish output.
4. **Run the migrations as `akiba_owner`, then put the connection string back.** The login
   Akiba runs as day to day cannot create or alter tables, deliberately — see section 1:

   ```powershell
   # Once, as the owner, to apply any new migrations.
   $env:ConnectionStrings__Akiba = 'Host=localhost;Port=5432;Database=akiba;Username=akiba_owner;Password=...'
   C:\Akiba\Akiba.Web.exe --urls http://127.0.0.1:5999
   # Wait for "Now listening on", check the log says which migrations it applied, then Ctrl+C.
   ```

5. Start the service, which runs as `akiba_app` again.

Akiba applies any new migrations itself at startup and logs what it applied. If a migration
fails it will not start — which is the correct behaviour. Restore the backup and get help
rather than trying to patch the database by hand.

If you skip step 4, the service will fail to start with a permissions error rather than
silently running against a half-upgraded database. That is also the correct behaviour.

---

## Keeping it private

- Akiba is **never** exposed to the internet. No port forwarding, no reverse proxy to a public
  address, no VPN shortcut "just for now".
- PostgreSQL listens on `localhost` only.
- Backups are encrypted, because a `pg_dump` is every member's financial position in one file.
- Break-glass credentials — the `postgres` password, the backup password, and the Akiba admin
  account — are held by the **treasurer and the chairman**, not by ICT. If the IT officer
  leaves, the society still has its system.
