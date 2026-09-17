# Akiba

Sacco management system for the **Akiba Welfare Society**, the staff welfare group of
Chabrin Agencies Limited, Nairobi.

It replaces the handwritten grid-paper ledgers in which officials calculated interest and
instalments by hand. It is an **internal, LAN-only system** used by four officials. There
is no member portal, no public internet exposure and no third-party API.

- **What Akiba does and why it is built this way** → [`ARCHITECTURE.md`](ARCHITECTURE.md)
- **The full specification, including the sixteen open questions** → [`BUILD_BRIEF.md`](BUILD_BRIEF.md)

---

## Status

Built milestone by milestone, with a review at each one. See `BUILD_BRIEF.md` §13.

| # | Milestone | Status |
|---|---|---|
| 1 | Solution skeleton, dependency rules, Docker Compose, CI | ✅ Done |
| 2 | `Money` value object, including `Allocate` | ✅ Done |
| 3 | Ledger core: accounts, journal entries, balances as at a date, period close | ✅ Done — persisted |
| 4 | Borrowers, members, zones, shares | ✅ Done |
| 5 | Loan products and interest strategies | ✅ Done |
| 6 | Schedule generation with the one-month grace period | ✅ Done |
| 7 | Guarantors, coverage, liability, exposure | ✅ Done |
| 8 | Applications, lock period, approvals, disbursement | ✅ Done |
| 9 | Receipts, three channels, allocation, clearance | ✅ Done |
| 10 | Payroll and bank reconciliation | ⬜ Not started |
| 11 | Restructuring | 🟡 Domain done; no screen yet |
| 12 | Arrears and ageing | ✅ Done |
| 13 | Reporting suite | ⬜ Not started |
| 14 | Notifications | ⬜ Not started |
| 15 | Identity, roles, MFA, audit trail | ⬜ Not started |
| 16 | Dividend run | ⬜ Not started |
| 17 | Migration tooling | ⬜ Not started |
| 18 | Deployment, backups, treasurer's handbook | 🟡 Deployment guide written; backups and restore verification outstanding |

---

## For developers

### Prerequisites

- **.NET 10 SDK** — the version is pinned in `global.json`
- **PostgreSQL 17 or later**, running locally. The integration tests use a real server;
  SQLite and the in-memory provider are never used, because both differ from PostgreSQL
  exactly where a ledger is sensitive.

### Build and test

```bash
dotnet restore Akiba.sln
dotnet build Akiba.sln --configuration Release
dotnet test Akiba.sln
```

Warnings are errors. A build that emits a warning fails.

Create the test database once:

```bash
createdb akiba_tests
```

The tests default to `localhost:5432` as `postgres`. Point them elsewhere with:

```bash
AKIBA_TEST_POSTGRES="Host=localhost;Port=5432;Database=akiba_tests;Username=postgres;Password=..." dotnet test Akiba.sln
```

### Check the architecture rules on their own

```bash
dotnet test tests/Akiba.ArchitectureTests/Akiba.ArchitectureTests.csproj
```

This fails if any layer takes a dependency it is not allowed to — most importantly if
anything in `Akiba.Domain` acquires a reference to Entity Framework Core.

### Check formatting

```bash
dotnet format Akiba.sln --verify-no-changes --severity warn
```

### Run locally

Akiba needs a PostgreSQL database. Create one, then point the app at it:

```bash
ConnectionStrings__Akiba="Host=localhost;Port=5432;Database=akiba_dev;Username=postgres;Password=..." dotnet run --project src/Akiba.Web
```

Migrations are applied and the chart of accounts is seeded at startup. No member, loan or
balance is ever seeded — opening balances arrive through the migration tooling, which is a
reviewed and signed-off process, not a side effect of starting the application.

The panel is at <http://localhost:5280>. The JSON endpoints below remain under `/api`, so
anything scripted against them keeps working and the panel is not the only way to read a
figure.

To fill a development database with plausible activity so the screens have something to show:

```bash
pwsh tools/seed-demo.ps1 -Database akiba_demo
```

It drives the same commands the panel does, so anything it creates got there the way an
official would have put it there. **Never point it at the live database** — it creates people
who do not exist.

| Endpoint | What it shows |
|---|---|
| `/health` | Liveness, including the database |
| `/api/...` | The same queries the panel uses, as JSON |
| `/ledger/accounts` | The chart of accounts |
| `/ledger/trial-balance?asAt=` | The trial balance, which must be zero |
| `/members?asAt=yyyy-MM-dd` | Members with their shareholding as at a date |
| `/members/{id}/statement?asAt=` | A member's statement as at any date |

These are a read-only window on the same queries the Blazor panel will use.

Outside Production the panel runs as a fixed development user, and says so in the startup
log. Every ledger entry records its author, so attributing them all to one person is only
tolerable while ASP.NET Core Identity is still to come — it is disabled in Production.

### Project layout

```
src/
  Akiba.Domain           aggregates, value objects, domain events — references nothing
  Akiba.Application      commands, queries, handlers, validators, port interfaces
  Akiba.Infrastructure   EF Core, repositories, Identity, email/SMS, storage, jobs
  Akiba.Web              Blazor Server panel and composition root
tests/
  Akiba.Domain.Tests           pure unit tests, no database
  Akiba.Application.Tests      handler tests with in-memory ports
  Akiba.Infrastructure.Tests   integration tests on real PostgreSQL (Testcontainers)
  Akiba.ArchitectureTests      dependency rules, enforced in CI
```

Dependencies point inward. `Akiba.Domain` has no NuGet references at all. See
[`ARCHITECTURE.md`](ARCHITECTURE.md) §5.

---

## Deployment

Akiba runs on a **dedicated machine** — not the CAL server — as a Windows service or a
systemd unit, against an installed PostgreSQL. **Full instructions, written for a
non-developer: [`docs/deployment.md`](docs/deployment.md).**

There is no Docker. Docker earns its keep when the same software must run identically across
many environments, and Akiba has one: a single machine, four users, maintained by whoever is
around. Without it the moving parts are ones any IT contractor already knows — PostgreSQL, a
service, and `pg_dump`.

### Network exposure

This system holds member financial records and the group requires confidentiality.

- `ASPNETCORE_URLS` decides what Akiba listens on. Use the machine's **own LAN address**,
  e.g. `http://192.168.1.40:8080`. **Never `0.0.0.0`.**
- PostgreSQL listens on `localhost` only. Officials reach Akiba through the web application;
  nothing else needs a route to the database.
- Backups are encrypted, because a `pg_dump` is every member's financial position in one file.

---

## Security

- **TOTP multi-factor is mandatory** for every account.
- **Only the accounts clerk creates or edits records.** The treasurer, chairman and
  secretary have view access, plus their own approval actions. HR can download the monthly
  deduction schedules and nothing else.
- **Every change is audited**: actor, timestamp, before and after values, IP address.
  The trail is immutable and exportable.
- **Financial records are never hard-deleted.** Ledger tables carry no soft-delete flag.
  Corrections are reversing entries.
- **Break-glass credentials are held by the treasurer and the chairman**, not by ICT. The
  procedure is documented in milestone 18.
- `.env`, backups, scanned forms and generated reports are gitignored. **Never commit
  member data.**

---

## A note on the open questions

`BUILD_BRIEF.md` §12 lists sixteen rules the committee has not settled — including whether
a normal loan fully covered by the borrower's own shares needs guarantors, and whether
guarantor liability is pro-rata or joint and several, on which the questionnaire and the
signed application form contradict each other.

Those are implemented as swappable strategies with documented defaults, not as
assumptions. **Please do not resolve one by picking whichever reading seems sensible.**
A lending rule invented by a developer is indistinguishable, six months later, from a
lending rule the committee agreed to.
