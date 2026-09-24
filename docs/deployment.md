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
| `Akiba__Smtp__Host`, `__FromAddress` | The mail server messages go through, and who they come from |
| `Akiba__Smtp__UserName`, `__Password` | Its credentials, **from the environment, never from a file** |
| `Akiba__Sms__UserName`, `__ApiKey` | Africa's Talking, if the society wants SMS. Same rule |

### Messages

Akiba sends email and SMS only when `ASPNETCORE_ENVIRONMENT` is `Production`. Everywhere else
every message is composed, written to the outbox, shown on the **Messages** screen, and never
sent. That is not a setting; it is decided by the environment, because a development database
full of invented members with plausible phone numbers is exactly what must never be texted.

If the SMTP settings are absent, nothing breaks: messages queue, the attempt fails with "no
mail server is configured", and the Messages screen says so. The society can run without
notifications and turn them on later.

**Every SMS costs money.** Turn individual kinds off with `Akiba__Notifications__Disabled__0`,
`__1` and so on, using `Kind` or `Kind:Channel` — so `ArrearsReminder:Sms` stops the texts and
leaves the emails. Statements and shortfall notices are already SMS-disabled by default,
because either one arrives as three chargeable fragments and is unreadable.

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

Use the script. It dumps, refuses a dump too small to be the society's ledger, encrypts it,
copies it off the machine, checks the copy arrived intact, and prunes what is too old:

```powershell
$env:PGPASSWORD = '...'              # the akiba_owner password
$env:AKIBA_BACKUP_PASSWORD = '...'   # the archive password - keep it somewhere else

pwsh C:\Akiba	oolsackup.ps1 -OffMachinePath '\fileserverkiba-backups'
```

Schedule it nightly with Task Scheduler, running as a user that can reach the off-machine path.
Both passwords go in the task's own environment, never in the script.

**Off the machine** means off the machine: another computer in the office, an external drive
kept elsewhere, or cloud storage the treasurer controls. A backup sitting on the same disk as
the database protects you from exactly nothing, and the script refuses to pretend otherwise -
if the destination is unreachable it fails rather than reporting success.

**Keep the archive password somewhere other than the Akiba machine.** An encrypted backup whose
password is on the machine that died is not a backup.

### Test the restore

An untested backup is a rumour. **Once a quarter**, run:

```powershell
$env:PGPASSWORD = '...'
$env:AKIBA_BACKUP_PASSWORD = '...'

pwsh C:\Akiba	oolserify-restore.ps1
```

It takes the newest backup, restores it into a scratch database, and checks four things:

1. it decrypts and restores at all;
2. the tables that must have rows have rows;
3. **the restored ledger still balances** — every journal line sums to zero;
4. the audit trail is still refusing to be edited, so the immutability trigger survived the
   dump.

The third is the one worth having. A backup that restores but whose ledger no longer balances
would otherwise be discovered to be useless on the worst day of the society's year.

It prints a green line when it passes and exits non-zero when it does not. **Tell the treasurer
either way** — the quarterly check is only worth running if somebody hears the answer.

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

---

## 8. Encryption at rest

The backups are already encrypted — AES-256 with encrypted filenames, which is what covers the
copy that leaves the building. **The live database files on the server are not**, and that is a
separate decision about the machine rather than about Akiba.

### What was asked for, and why it does not exist

The hardening brief asked for Transparent Data Encryption. **TDE is a SQL Server feature.
Akiba runs on PostgreSQL, which has no equivalent**, and there are no `.bak` files to protect.
There is no setting to turn on; the request appears to have come off a SQL Server checklist.

### What to do instead

**Recommendation: BitLocker on the volume holding the PostgreSQL data directory.** It answers
the realistic threat — a machine or a disk leaving the building — costs nothing, and changes no
code. On Windows Server:

1. Confirm where the data actually lives:

   ```
   psql -U postgres -c "SHOW data_directory;"
   ```

2. Turn BitLocker on for that volume, with a TPM if the machine has one.

3. **Write the recovery key down and give it to the treasurer and the chairman**, alongside the
   other break-glass credentials. A recovery key held only by ICT, or only on the machine it
   unlocks, is not a recovery key. This is the step that actually matters, and it is the one
   that gets skipped.

4. Confirm it is on:

   ```
   manage-bde -status
   ```

The machine must still be able to boot unattended, or Akiba will not come back after a power
cut until somebody types a key. Test that before relying on it.

### What not to do without understanding it

`pgcrypto` on individual columns covers a stolen disk **and** anybody holding a database login
they should not have. It also stops those columns being searchable or sortable — which for the
money columns means the ledger can no longer be summed by the database, and Akiba derives every
figure it reports by summing the ledger. Do not choose this to tick a box.

A filesystem-level encrypted volume sits between the two and is a reasonable answer on Linux.

**Awaiting a committee decision.** Until one is made, the position is: backups encrypted, live
files not.
