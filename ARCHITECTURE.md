# Akiba — Architecture

This document is for whoever maintains Akiba after the person who built it. It explains
the two decisions that everything else follows from: **how money is recorded**, and **how
money is represented in code**. If you understand those two things, the rest of the
codebase will make sense. If you change either without understanding them, you will
quietly corrupt a financial record that four officials rely on.

Read this before your first change.

---

## 1. What Akiba is

Akiba is a staff welfare Sacco — a savings and credit co-operative — run by employees of
Chabrin Agencies Limited in Nairobi. Members contribute shares by monthly salary
deduction and borrow against them. Four officials run it: a chairman, a treasurer, an
accounts clerk and a secretary.

Before this system, all of it lived in handwritten grid-paper ledgers. Interest and
instalments were worked out by hand, a member's two concurrent loans were tracked side by
side on the same page, and cleared payments were marked with a highlighter and a circled
number.

That matters, because it tells you what this software has to be. It is **a system of
record**, not a productivity tool. Its job is to be right, to show its working, and to
reproduce any figure any official could previously point to on paper. Being fast, or
having more features, is worth nothing if a member's balance cannot be explained.

---

## 2. The ledger: append-only double entry

### The rule

> Every movement of money is a balanced double-entry journal entry in an append-only
> ledger. There are no mutable balance columns anywhere.

There is no `Member.Shareholding` column. There is no `Loan.OutstandingBalance` column.
There is no `BankAccount.Balance`. **Those numbers are not stored. They are derived**, by
summing ledger entries up to a date:

```
ShareholdingAsAt(member, date)      = Σ (credits − debits) on that member's shares account, entry date ≤ date
OutstandingBalanceAsAt(loan, date)  = Σ (debits − credits) on that loan's receivable account, entry date ≤ date
BankBalanceAsAt(date)               = Σ (debits − credits) on the bank account, entry date ≤ date
```

Nothing is ever updated. Nothing is ever deleted. A mistake is corrected by posting a
**reversing entry** that carries a reason and the name of whoever posted it, and then
posting the correct entry. The wrong entry stays visible forever, with its reversal next
to it.

### Why it is built this way

Three reasons, in order of how much they matter.

**Any historical figure can be reproduced.** A member asks in December what their
shareholding was in June. A stored-balance system cannot answer that — the June number was
overwritten in July. A derived-balance system answers it by summing to 30 June, and gets
the same answer every time it is asked. There is an integration test that generates a June
statement in December and asserts the figures are identical; that test is the proof this
design works, and it must never be deleted or weakened.

**A wrong number has a cause you can find.** When a balance is stored, a bad figure tells
you nothing about how it got there. When a balance is derived, a bad figure means one of
the entries that make it up is wrong, and every entry carries its date, its narration, its
source document and its author. You can walk back to the posting that did it. The paper
ledger had this property. Software that loses it is a downgrade.

**No entry can silently unbalance the books.** The `JournalEntry` aggregate takes two or
more `JournalLine` children and **its constructor refuses to build an entry whose lines do
not sum to zero**. Not a validation attribute, not a check in a handler, not a database
constraint — the constructor. An unbalanced entry is not a thing that can exist in memory,
so it is not a thing that can reach the database. The trial balance is correct by
construction rather than by vigilance.

### The chart of accounts

| Type | Accounts |
|---|---|
| Asset | Bank, Loans Receivable (one per loan), Interest Receivable |
| Liability | Member Shares (one per member), Dividends Payable, Unallocated Receipts |
| Equity | Opening Balance Equity, Retained Surplus |
| Income | Loan Interest Income, Restructuring Fees |
| Expense | Bank Charges |

Member shares are a **liability**, not equity. Akiba owes that money back to the member.
Reading it any other way makes the balance sheet wrong.

`Unallocated Receipts` is where every incoming payment lands first — see §4.

### The rule you will be tempted to break

Someone will eventually notice that deriving a balance means summing rows, and propose
adding a cached balance column "just for the member list page".

**Do not do this.** The moment a stored balance exists, it can disagree with the ledger,
and when it does you have two numbers and no way to tell which is the real one. Every
argument for the cache is a performance argument, and Akiba has roughly forty members and
four concurrent users on a LAN.

If reads genuinely become slow, the sanctioned answer is a **projection table**: a
read-model rebuilt from the ledger by a single idempotent command, never written to
directly, with a test proving a rebuild reproduces the derived figures exactly. The
distinction that matters is that a projection is *disposable* — you can drop it and
rebuild it from the ledger, because the ledger is the truth. A cached column is not
disposable, because once it drifts there is nothing to rebuild it from.

### Period close

Periods close monthly and annually. Once a period is closed, **no entry may post into it**.
A correction discovered afterwards posts into the currently open period, referencing the
original entry.

This is enforced **at the repository level, not in the UI**. A closed period that can be
written to by any code path that forgets to check is not closed. The treasurer closes a
period; the chairman can reopen one, and the reason is logged.

---

## 3. Money in code

### Why `decimal`, and why the whole stack follows from it

Akiba is written in C# for one reason above all others: **`decimal` is a base-10 type and
is exact for the arithmetic Akiba does.**

`double` and `float` are base-2. They cannot represent 0.10 exactly, in any language. Add
ten of them and you do not get 1.00. In a system that must reconcile against a bank
statement and reproduce a figure a member wrote down, that is disqualifying. There is no
"we round at the end" that repairs it.

So: **all monetary columns are `numeric(19,4)` in PostgreSQL and `decimal` in C#. Never
`float`. Never `double`.** Not for a rate, not for a ratio, not for a report total, not
temporarily.

The four extra decimal places in `numeric(19,4)` are headroom for intermediate rate
arithmetic. Money is rounded to two places at the point of posting, never before.

### The `Money` value object

Bare `decimal` is not allowed to cross a layer boundary. Money is a
`readonly record struct` carrying an **amount and a currency**, with operators for add,
subtract and multiply-by-a-rate.

Rounding is `MidpointRounding.AwayFromZero` to 2 decimal places, applied at the point of
posting, and **the rounding policy lives in exactly one class**. When the treasurer asks
why a figure ends in a particular cent, there is one place to look and one answer.

### `Allocate`, and why it exists

Splitting money is where financial systems lose cents. Divide KSh 100 three ways and
naive rounding gives you 33.33 three times, which is 99.99. One cent has vanished, the
journal entry no longer balances, and the entry constructor rejects it.

`Money.Allocate(int parts)` distributes the remainder deterministically so that **the parts
always sum exactly to the whole**. There is a property-based test (FsCheck) asserting this
for any amount and any number of parts, not a handful of examples.

Everywhere Akiba divides money, it uses `Allocate`. There are two overloads, because
Akiba divides money two different ways:

**`Allocate(int parts)` — an even split**, used for loan instalments. It works in whole
minor units, where the division is exact, and hands the leftover cents to the **earliest
parts**. Earliest-first is a decision, not an accident of the loop: the odd cent lands on
the first instalment, which is the one closest to the disbursement and the easiest for a
member to check against a payslip. A 12-month schedule sums to principal plus interest
exactly.

**`Allocate(IReadOnlyList<decimal> weights)` — a proportional split**, used for guarantor
liability and dividends. It uses the **largest-remainder method**: everyone gets their whole
minor units, then the leftover cents go to whoever was cut off by the most, with ties broken
toward the earliest weight. Ties must break deterministically because a dividend run is
computed as a draft, reviewed by the treasurer, and approved by the chairman possibly days
later — recomputing has to produce the identical schedule or there is nothing meaningful to
approve.

- **Loan instalments** — a 12-month schedule must sum to principal plus interest exactly.
- **Guarantor pro-rata liability** — a guarantor who guaranteed 50,000 of 200,000 bears
  0.25 of the outstanding balance, and the guarantors' shares must sum to the whole. A lost
  cent here is a member being pursued for the wrong figure.
- **Dividend allocation** — the total distributed to members must equal the total
  available, to the cent.

`Money` carries the precision it was given and rounds **only when asked**, because rounding
twice is how a figure ends up a cent away from anything reproducible. `default(Money)` has
no currency and throws on arithmetic rather than behaving as zero shillings — a struct can
always be default-constructed, and an uninitialised field that silently reads as zero would
hide the bug that created it.

---

## 4. Money in: one door, then an explicit decision

Akiba has one bank account and three inbound channels: payroll deduction, landlord rent
offsets, and direct deposits (cash, bank, M-Pesa, cheque).

**Every receipt posts to `Unallocated Receipts` first.** It is then allocated to share
contributions and loan instalments by an explicit, audited, reversible action taken by the
accounts clerk.

Allocation is **never silently inferred**. It is tempting to guess — the amount matches an
instalment, so it must be that instalment — but a wrong guess in a system of record is
worse than no guess, because it looks like a decision somebody made. Direct deposits are
matched by the member's name written on a deposit slip, which is exactly as reliable as it
sounds. A human decides; the system records who, when, and what the money was moved to.

Two consequences worth knowing:

- **Cheques clear before they count.** A receipt moves `Recorded → Cleared → Reconciled`.
  Bank statements arrive quarterly, so a deposit can sit unconfirmed for up to 90 days.
  Balances therefore distinguish confirmed from unconfirmed money, and reports say which
  they are showing.
- **Two reconciliations, not one.** Bank reconciliation matches the ledger against the bank
  statement. Payroll reconciliation matches the deduction schedule sent to HR against what
  payroll actually deducted. They catch different failures and both must balance before a
  period closes.

---

## 5. Project layout and the dependency rule

Four projects. Dependencies point inward, always.

```
        Akiba.Web  ─────────────┐
     (Blazor Server,            │
      composition root)         │
            │                   ▼
            └──────────►  Akiba.Infrastructure
                          (EF Core, Identity,
            ┌──────────    Hangfire, reports)
            │                   │
            ▼                   ▼
        Akiba.Application  ◄────┘
     (commands, queries, ports)
            │
            ▼
        Akiba.Domain
   (aggregates, value objects,
    domain events — references
          NOTHING)
```

| Project | May reference | Holds |
|---|---|---|
| `Akiba.Domain` | **nothing at all** | Aggregates, value objects, domain events, domain services |
| `Akiba.Application` | `Akiba.Domain` | Commands, queries, handlers, validators, **port interfaces** |
| `Akiba.Infrastructure` | `Akiba.Application` | EF Core, repositories, Identity, email/SMS, storage, jobs |
| `Akiba.Web` | `Akiba.Application`, `Akiba.Infrastructure` | Blazor components, DI wiring |

`Akiba.Domain` has **no NuGet package references whatsoever**. Not EF Core, not MediatR,
not a JSON library. The domain model is plain C#. This is not purism: it is what lets you
unit-test every money rule in milliseconds with no database, and it is what stops
persistence concerns quietly reshaping the model. A `Loan` that knows about `DbContext`
will eventually be designed around what is easy to store rather than around what Akiba
actually does.

`Akiba.Application` defines the **ports** — `ILoanRepository`, `IClock`, `IEmailSender`,
`ISmsSender`, `IFileStore`, `IBackgroundJobScheduler` — and `Akiba.Infrastructure`
implements them. That is why the arrow runs from Infrastructure to Application and never
the other way.

`Akiba.Web` is the composition root. It is the only project allowed to reference
Infrastructure, and only to wire up dependency injection. **Entity Framework Core will
appear in Web's transitive dependency graph, and that is expected**; what is forbidden is
Web *declaring* a dependency on it, because a Blazor component opening a `DbContext` is a
component that will end up calculating money.

### This is enforced, not documented

`tests/Akiba.ArchitectureTests` fails the build if any of the above is violated. It checks
twice, deliberately:

- It **parses the `.csproj` files**, catching a forbidden dependency the moment it is
  declared. This matters because the C# compiler omits a reference to an assembly whose
  types are never used, so a stray EF Core reference in `Akiba.Domain` could sit unnoticed
  until the day somebody used it.
- It **inspects the compiled assemblies**, catching a dependency that is actually used,
  including one that arrived transitively.

Either check alone leaves a gap. CI runs them as a separate job so a dependency-rule
violation shows up as such, rather than buried in the full test run.

---

## 6. Conventions that are not negotiable

- **Domain logic lives in aggregates and domain services.** Never in Blazor components,
  never in EF configurations, never in controllers. If a `.razor` file contains
  arithmetic on money, that is a bug regardless of whether the number it produces is right.
- **Aggregates have private setters** and are mutated only through intention-revealing
  methods: `loan.Restructure(...)`, never `loan.Balance = x`. The method name is what
  tells a reader — and the audit trail — what actually happened.
- **Domain events are raised by aggregates**; handlers do the ledger posting and the
  notification dispatch. An aggregate does not know that an email exists.
- **Anything the committee can change is versioned configuration in the database**: interest
  rates, the graduated term bands, the restructuring fee, borrowing limits, the application
  cutoff day, the guarantor liability basis, feature flags. When the committee changes a
  band, **existing loans keep the version they were written under**. Schedules are never
  retrospectively recalculated — a member's agreed instalment does not change because a
  rule changed after they signed.
- **Financial records are never hard-deleted**, and ledger tables carry **no soft-delete
  flag at all**. A flag is a thing someone can set. Corrections are reversing entries.
- **Timestamps are stored UTC as `timestamptz`** and displayed in `Africa/Nairobi`.
- **Never seed real member data.** Seed data uses realistic Kenyan names and plausible
  amounts, and nothing else.

---

## 7. Testing, and what the tests are actually for

- **Domain layer: pure unit tests, no database.** Every invariant has a test proving the
  invalid state *cannot be constructed* — not that it is rejected somewhere downstream.
- **Every money calculation is tested against real figures from the existing paper ledger.**
  Not invented examples. The emergency loan test asserts 25,000 + 2,500 = 27,500 over five
  months at exactly 5,500 per month, because that is what the ledger says and what members
  expect to see.
- **Integration tests run against a real PostgreSQL server. Never SQLite, never the
  in-memory provider.** Both differ from PostgreSQL in precisely the areas that matter to a
  ledger — numeric precision, transaction isolation, constraint enforcement — so a green test
  against either would prove nothing about production. Akiba is not containerised, and
  `docs/deployment.md` explains why; the tests run against an installed server for the same
  reason.
- **Property-based tests for `Money.Allocate`**, because the failure mode is a specific
  amount and a specific number of parts that nobody thought to write an example for.
- **Warnings are errors.** In a financial system, "it's only a warning" is not a category.

---

## 8. Where the rules came from, and what is still unanswered

Every rule Akiba enforces traces to a source document: the answered requirements
questionnaire of 9 September 2026, the two loan application forms, the emergency loan form,
and the committee minutes of 6 September 2026.

**Section 12 of `BUILD_BRIEF.md` lists sixteen questions the committee has not answered.**
Some are silences, some are genuine contradictions between the questionnaire and the form
members actually sign — guarantor liability is pro-rata in one and joint-and-several in the
other.

Where a decision was unavoidable, it is implemented as a **swappable strategy with a
documented default and a `// TODO: confirm with committee`**, never as a hard-coded
assumption. If you find yourself about to resolve one of those sixteen questions by picking
whichever reading seems sensible: don't. Ask the committee. A lending rule invented by a
developer is indistinguishable, six months later, from a lending rule the committee agreed.
