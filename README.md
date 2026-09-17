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
| 9 | Receipts, three channels, allocation, clearance | ✅ Done (domain) |
| 10 | Payroll and bank reconciliation | ⬜ Not started |
| 11 | Restructuring | ✅ Done (domain) |
| 12 | Arrears and ageing | ✅ Done (domain) |
| 13 | Reporting suite | ⬜ Not started |
| 14 | Notifications | ⬜ Not started |
| 15 | Identity, roles, MFA, audit trail | ⬜ Not started |
| 16 | Dividend run | ⬜ Not started |
| 17 | Migration tooling | ⬜ Not started |
| 18 | Deployment, backups, treasurer's handbook | ⬜ Not started |

---

## For developers

### Prerequisites

- **.NET 10 SDK** — the version is pinned in `global.json`
- **Docker** — required, not optional. Integration tests run against real PostgreSQL via
  Testcontainers; SQLite and the in-memory provider are never used.

### Build and test

```bash
dotnet restore Akiba.sln
dotnet build Akiba.sln --configuration Release
dotnet test Akiba.sln
```

Warnings are errors. A build that emits a warning fails.

The integration tests start a PostgreSQL container, so Docker must be running. If your
Docker daemon is not available, point them at a PostgreSQL you already have — still a real
server, running the real migrations, with identical tests:

```bash
AKIBA_TEST_POSTGRES="Host=localhost;Port=5432;Database=akiba_tests;Username=postgres;Password=..." dotnet test Akiba.sln
```

CI always uses the container.

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

| Endpoint | What it shows |
|---|---|
| `/health` | Liveness, including the database |
| `/ledger/accounts` | The seeded chart of accounts |
| `/ledger/trial-balance` | The trial balance as at today, which must be zero |

These are a read-only window on the ledger while the Blazor panel is still to come.

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

Akiba runs on a **dedicated machine** — not the CAL server — via Docker Compose.

```bash
cp .env.example .env
#  edit .env: set POSTGRES_PASSWORD, and AKIBA_BIND_ADDRESS to the machine's LAN address
docker compose up -d
```

### Network exposure

This system holds member financial records and the group requires confidentiality.

- `AKIBA_BIND_ADDRESS` **defaults to `127.0.0.1`**, so an unconfigured deployment is
  reachable only from the machine itself.
- To serve the four officials, set it to that machine's **LAN address**, e.g.
  `192.168.1.40`. **Never set it to `0.0.0.0`.**
- PostgreSQL and Seq are bound to `127.0.0.1` and are not published to the LAN at all.
- The container runs as a non-root user.

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
