# Claude Code build prompt — Akiba Sacco Management System (.NET)

*Revision 2. Supersedes the earlier Laravel version and revision 1. Corrections in this revision are drawn from the Akiba Requirements Questionnaire (answered 9 September 2026), the Akiba Welfare Loan Application Form, the Revised Loan Application Form (rental income), the Emergency Loan Application Form, and the Akiba meeting minutes of 06/09/2026.*

*Paste everything below the line into Claude Code in an empty project directory.*

---

You are building Akiba, a Sacco (savings and credit co-operative) management system for a staff welfare group run by employees of Chabrin Agencies Limited, a property management company in Nairobi, Kenya.

This is a financial system of record. It replaces handwritten grid-paper ledgers in which officials calculated interest and instalments by hand, tracked concurrent loans for one member on the same page, and marked cleared payments with highlighters and circled numbers. Correctness, auditability and the ability to reproduce any historical figure matter more than features or delivery speed.

Read this entire brief before writing code. §12 lists things I have not decided — do not invent answers to those. Where this brief states a rule, it is because a source document states it. Where a rule is absent, it is absent deliberately.

## 1. Non-negotiable architectural principle

**Every movement of money is a balanced double-entry journal entry in an append-only ledger.**

There are no mutable balance columns anywhere. A member's shareholding, a loan's outstanding balance, the bank position, interest income and dividends payable are all derived by summing ledger entries as at a date. Nothing is updated or deleted; corrections are made by posting a reversing entry carrying a reason and an author.

Do not add a cached balance column for performance. If read speed becomes a problem, use a projection table rebuilt from the ledger by a single idempotent command, and prove the rebuild reproduces the derived figures exactly.

Chart of accounts (seeded, extensible):

| Type | Accounts |
|---|---|
| Asset | Bank, Loans Receivable (per loan), Interest Receivable |
| Liability | Member Shares (per member), Dividends Payable, Unallocated Receipts |
| Equity | Opening Balance Equity, Retained Surplus |
| Income | Loan Interest Income, Restructuring Fees |
| Expense | Bank Charges |

A `JournalEntry` aggregate holds: entry date, value date, narration, source document reference, created-by, and two or more `JournalLine` children that must sum to zero. Enforce this invariant in the aggregate constructor — an unbalanced entry must be impossible to construct, not merely rejected at save time.

## 2. Stack — decided, do not propose alternatives

- **.NET 10 (LTS), C# 14**
- **PostgreSQL 17**, `numeric(19,4)` for all monetary columns — never `float`, never `double`. (PostgreSQL 18 is current; 17 is chosen deliberately for maturity. Do not upgrade without asking.)
- **EF Core 10** with Npgsql
- **Clean architecture, four projects:**
  - `Akiba.Domain` — entities, value objects, aggregates, domain events, domain services. No EF Core dependency, no framework references.
  - `Akiba.Application` — commands, queries, handlers, validators, port interfaces
  - `Akiba.Infrastructure` — EF Core, repositories, identity, email/SMS, file storage, background jobs
  - `Akiba.Web` — Blazor Server admin panel
- **Blazor Server + MudBlazor** for the officials' panel. This is an internal LAN back-office tool used by four people — server-side rendering over a local network is the right trade, and it keeps one language across the whole system.
- **ASP.NET Core Identity** for authentication, roles and TOTP multi-factor
- **MediatR** for command/query dispatch, **FluentValidation** for input validation. *Note: MediatR is commercially licensed above a revenue threshold this group is far below, so the free tier applies. If that changes, the dispatch abstraction must be swappable — do not leak `IRequest` into the domain layer.*
- **Hangfire** for background jobs (statements, report generation, notification dispatch), with its dashboard behind the admin role
- **Serilog** to file and Seq
- **ClosedXML** for Excel schedules, **QuestPDF** for member statements and the AGM pack
- **Audit.NET** for the audit trail (chosen over hand-rolled `SaveChanges` interceptors — do not build both)
- **xUnit + FluentAssertions** with a real PostgreSQL server in integration tests, never SQLite or InMemory for anything touching money
- **Deployment onto a dedicated LAN machine** as a Windows service or systemd unit, against an installed PostgreSQL. *(Revised: Docker Compose was dropped — see `docs/deployment.md` for the reasoning.)*

No public internet exposure. No member-facing portal. No third-party API surface. The group operates informally and leadership requires confidentiality — the minutes of 06/09/2026 record the system as in-house, for operational use by authorised officials only. Bind to the LAN only.

### Money handling

Use C#'s native `decimal` — it is base-10 and exact, which is the reason this stack was chosen. Wrap it in a `Money` value object (`readonly record struct`) carrying amount and currency, with operators for add, subtract, multiply by a rate, and an explicit `Allocate(int parts)` method that distributes remainders deterministically so no cent is ever lost to rounding. `Money` never crosses a layer boundary as a bare `decimal`. Rounding is always `MidpointRounding.AwayFromZero` to 2 decimal places at the point of posting, and the rounding policy lives in one class.

## 3. Domain model

### Borrowers and members

- **`Borrower` is the borrowing party.** `Member` specialises it and additionally holds shares. Non-member clients borrow but hold no shares — do not force them into the member table.
- Membership begins at the **first share contribution**, not the application letter. `MembershipStartDate` derives from the first share ledger entry. (Joining also involves a letter to the chairman; record it as a document, not as the start date.)
- Members are CAL employees; some are also landlords whose deductions run on a separate schedule.
- Fields: names, payroll ID, national ID, phone, email, employment status, exit date, **zone**, KYC document attachments.

### Zone

Approval authority is organised by zone and by office: a loan is approved by *the member representatives for each zone and office*. Model `Zone` as a first-class entity with member representatives attached. It is referenced by the approval workflow (§6) and by loan portfolio reporting (§8). The list of zones is not in the source documents — seed it as configurable and empty, and surface it in §12.

### Shares

- Contributed monthly by salary deduction. **Cannot be withdrawn while the member is active.**
- Shareholding drives borrowing limit, guarantor requirement and dividend allocation.
- Each contribution posts: debit Bank, credit Member Shares.
- `ShareholdingAsAt(memberId, date)` is a domain query over the ledger. There is no `Shareholding` column.

### Loan products

Implement as `IInterestStrategy` implementations, registered by product in configuration:

- **Normal loan (member)** — flat 10% of principal, added once at disbursement. Not reducing-balance, not per annum. Term from the graduated scale in §4.
- **Emergency loan (member)** — maximum KSh 25,000, 5 months, flat 10%. 25,000 + 2,500 = 27,500 ÷ 5 = **KSh 5,500 per month**. Must reproduce to the cent.
- **Client loan (non-member)** — 3% per month on principal, maximum 5 months.
- **Rental-income loan (member)** — a salary-based loan and a rental-income-based loan are distinct products on the revised application form, and the applicant ticks which one they are applying for. A rental-income loan is secured by rental income rather than by payslip, and the application additionally captures:
  - property name
  - property location
  - monthly rental income (KSh)
  - number of rental units
  - attached proof of rental income (rent statements) — a required document

  Model security as a `LoanSecurity` value object with variants for Guarantors, Society Shares, Rental Income and Other, so a loan can carry more than one. **The interest rate, term scale and affordability basis for rental-income loans are not stated in any source document — see §12. Do not assume they match the salary-based loan.**

Maximum **2 concurrent loans** per member.

### Loan identity and top-ups

- Every loan carries a human-readable `LoanNumber` (the application form has a `LOAN NO.` field). Generate it; do not rely on the surrogate key.
- The official-use section of the form has a **`Loan top-up`** field. See §12 on net disbursement — this is almost certainly the same mechanic and must not be modelled twice.

### Guarantors

- A guarantor **must be an Akiba member and a current CAL employee**.
- **The number of guarantors is not limited**, but is expected to be a reasonable number. Do not enforce a maximum.
- **Emergency loans are unguaranteed unless the member's normal loan balance exceeds their total shares.** This is stated explicitly in the source and is the only shares-coverage rule that is confirmed.
- **Whether a normal loan fully covered by the borrower's own shares needs guarantors at all is NOT confirmed — see §12.** Until it is answered, require at least one guarantor on every normal loan and expose the coverage test as a *warning*, not a block. The form's official-use section asks "Do guarantors sufficiently cover the loan? (Yes / No)" — implement that as a computed indicator shown to the approver.
- No cap on how many loans one may guarantee; guaranteeing does not reduce their own borrowing limit.
- A guarantor may take their own loan while guaranteeing another.
- Each guarantor records a **share value** and a **guaranteed amount** (both are columns on the form).
- **Liability basis on default is contested — see §12.** The questionnaire says pro-rata: 50,000 guaranteed of a 200,000 total means liability for 0.25 of the outstanding balance. The signed application form says guarantors "accept joint and several liability". Implement liability as an `IGuarantorLiabilityStrategy` with `ProRata` as the default and `JointAndSeveral` available, selected by versioned configuration. Where pro-rata applies, use `Money.Allocate` so the split is exact.
- Recovery of guarantor liability is by salary deduction, but direct deposits to the account are also accepted.
- **A guarantor cannot withdraw their guarantee before the loan clears.**
- On guarantor exit from CAL: raise a domain event that flags every loan they guarantee, **blocks release of their own funds where any guaranteed loan's balance exceeds the borrower's shares**, and creates a task for the accounts clerk to have the borrower find a replacement.
- Guarantor exposure report: per member, total guaranteed across all loans and current at-risk amount.

## 4. Business rules

### Borrowing limit

Maximum loan = **2 × current shareholding**.

### Affordability — Kenyan two-thirds rule

Total deductions must not exceed **two-thirds of gross salary**, checked at application, before approval. On breach, block approval and present the two sanctioned remedies: **reduce the loan**, or **reduce the monthly share deduction**. Gross salary is an input on the application — the system does not read payroll.

*For rental-income loans the affordability basis is not stated — see §12.*

### Graduated repayment scale (normal loans and restructured balances)

| Balance | Term |
|---|---|
| 30,000 – 50,000 | 8 months |
| 50,001 – 100,000 | 12 months |
| 100,001 – 150,000 | 16 months |
| 150,001 – 200,000 | 20 months |
| 200,001 – 400,000 | 24 months |
| 400,001 and above | 30 months |

Versioned configuration in the database. When the committee changes a band, existing loans keep the version they were written under. **Schedules are never retrospectively recalculated.**

### Tenure-based terms for 400,001 and above — status ambiguous, build behind a flag

Loans of 400,001+ may receive longer terms by membership length:

| Membership | Term |
|---|---|
| 7 – 9 years | 36 months |
| 10 – 14 years | 40 months |
| 15 years and above | 48 months |

**The status of this rule is genuinely unclear and you must not resolve it yourself.** The minutes of 06/09/2026 present it as *proposed*, and record no adoption. But the current loan application form already prints all three tiers in its Loan Payment Duration Guide, alongside the 30-month band, which suggests it is either in use or the form is ahead of the minutes. Build it as a versioned, feature-flagged extension to the graduated scale, **default off**, and raise the contradiction in your §12 summary. Membership length is counted from the first share contribution (§3).

### Grace period

One full month. Disbursed 20 September → October is grace → **first instalment November**.

### Application lock period — DEFINED

**Applications must be received at the Society's office on or before the 15th day of the month. Applications received after the 15th are considered in the succeeding month.** This appears on both the standard and the revised application forms.

Make the cutoff day a versioned configuration value (default 15) rather than a constant. An application submitted after the cutoff is accepted, recorded with its true submission date, and queued for the next cycle — it is not rejected. Loans approved but not yet disbursed because of the lock period are a visible queue (§6).

### Restructuring

- Only when paid down to **at least 50% of (principal + interest)**.
- **5% of the outstanding balance, deducted upfront**; restructured instalments begin after the fee.
- Restructured balance **attracts no fresh interest**.
- **Once only** per loan.
- Original guarantors carry over; the terms of the loan otherwise remain.
- New term from the graduated scale applied to the restructured balance.
- Post the fee to Restructuring Fees and close the old loan account into the new one **through the ledger**. Never mutate the original loan.

### Default and recovery

Escalation: **reminder → notify guarantors → deduct guarantors** according to the liability strategy in force. A member leaving CAL mid-loan: recover from **final dues**; any shortfall passes to guarantors. Build arrears ageing (current, 1–30, 31–60, 61–90, 90+). **No penalty calculation — no penalty rule exists yet** (see §12).

There are currently no loans to be written off, but build the write-off path as a ledger posting requiring chairman approval.

### Dividends

Interest earned, **net of bank charges**, divided by total shareholding, allocated by shareholding. The Akiba account earns no interest but incurs charges, and those charges are explicitly deducted when computing interest earned from loans. Annual.

Run as a reviewable draft: **compute → treasurer reviews → chairman approves → post to ledger**. Never auto-post. Allocation uses `Money.Allocate` so the total distributed equals the total available exactly.

### Period close

Monthly and annual close. Once closed, **no entry may post into that period** — corrections go to the open period referencing the original. The treasurer closes; the chairman may reopen with a logged reason. **Enforce at the repository level, not in the UI.**

## 5. Money in — three channels

One bank account. Every receipt posts to **Unallocated Receipts**, then is allocated to share contributions and loan instalments by an explicit, audited, reversible action. **Allocation is never silently inferred.**

1. **Payroll deduction (staff).** The accounts clerk generates the schedule; it **must reach HR by the 25th of the month**. Excel format: payroll ID, full name, share contribution, per-loan instalment, total. HR deducts, forwards to the payments team, who draw a cheque payable to Akiba **from the business account**.
2. **Landlord rent offsets.** CAL employees who are landlords; their deductions run on a **separate schedule** from the employee schedule. HR forwards the list to the payments team once payroll amounts tally. The cheque is drawn **from the main account** (client deductions), not the business account — the two channels must be distinguishable in the ledger. Where rent does not cover the deduction, the landlord account is treated as **overpaid** — no partial recovery, no skipping.
3. **Direct deposits.** Cash to an official, bank deposit, M-Pesa or cheque. Matched by the **member name written on the deposit slip**. Cheques must **mature before being treated as received** — model a `PendingClearance` state with an expected clearance date.

Bank statements arrive **quarterly**, so a deposit may sit unconfirmed for up to 90 days; any signatory may request an ad-hoc statement when something urgent must be confirmed. Receipt states: **Recorded → Cleared → Reconciled**. Balances must distinguish confirmed from unconfirmed.

### Overpayment

A member can overpay. It is rare, and there are exactly two sanctioned outcomes, both of which must be an explicit, audited choice by the accounts clerk — never automatic:

- **increase the member's shareholding** by the overpaid amount (transfer Unallocated Receipts → Member Shares), or
- **refund to the member by cheque** (transfer Unallocated Receipts → Bank, with cheque number recorded).

An overpayment that has been neither allocated nor refunded stays in Unallocated Receipts and appears on an ageing queue.

### Two reconciliations, not one

These are separate controls and both must exist:

- **Payroll reconciliation** — the deduction schedule sent to HR, reconciled against what payroll actually deducted for that period, per member. This is how the office confirms HR deducted what was requested. It applies to both the employee schedule and the landlord schedule.
- **Bank reconciliation** — import a statement (CSV/Excel), auto-match on amount and date, present an unmatched queue, and produce a reconciliation statement. **Both reconciliations must balance before the period can close.**

## 6. Money out

- Member applies on a **paper form**; the system records the application and stores a scan of the signed original.
- The form captures, at minimum: loan amount and amount in words, requested repayment period, monthly repayment amount, loan type (salary-based or rental-income-based), security offered, applicant personal and payroll details, bank details, date of enrolment, total share contributions to date, applicant and witness signatures, and the guarantor table (payroll no., name, share value, guaranteed amount, signature) with a total guaranteed amount.
- Official-use fields to capture and derive: loan top-up, total shares, total outstanding loans, emergency loan balance, **loan amount approved**, **cheque amount**, and the guarantor-sufficiency indicator.
- **Approval is by the member representatives for each zone and office** — support multiple approvers with recorded decisions, timestamps and comments, resolved against the borrower's `Zone`.
- **Disbursement by cheque, two signatories.** Record cheque number and voucher reference; the voucher carrying cheque details is signed and attached to the loan application — attach the scan.
- **Lock period:** the 15th-of-month cutoff in §4. Applications after the cutoff wait for the next cycle.
- States: **Draft → Submitted → Approved → Disbursed**, plus **Rejected** and **Withdrawn**. Approved but not disbursed is a visible queue — the office explicitly tracks loans approved but awaiting disbursement because they were applied for after the lock period.

## 7. Access, security, audit

| Role | Access |
|---|---|
| Accounts Clerk | **The only role that creates or edits records** |
| Treasurer | View all; verify balances, close periods, review dividend runs; holds break-glass credentials |
| Chairman | View all; approve dividend runs, reopen periods, approve write-offs; holds break-glass credentials |
| Secretary | View all |
| HR | Download the monthly deduction schedules only |

Known officials at time of writing (seed as configuration, not as fixtures): Dennis Gitonga — Chairman; Wilfred Wamai — Treasurer; Oliver Kamau — Accounts Clerk; Florence Karimi — Secretary. Bank signatories: Mr. Mutinda and Mr. Kimathi. Statement view access: signatories, treasurer, accounts clerk, chairman.

- **TOTP multi-factor mandatory** for every account.
- **Full audit trail**: actor, timestamp, before and after values, IP address. Immutable, viewable in-panel, exportable. Every change to the system must be documented.
- Financial records are **never hard-deleted**, and ledger tables carry **no soft-delete flag at all**.
- Session timeout, LAN IP allowlist, login rate limiting.
- The system runs on a **separate machine**, not the CAL server.
- **Break-glass admin credentials held by treasurer and chairman** — document the procedure in the README. Data and backups are owned by the treasurer and chairman, not by IT.

## 8. Reporting

Each is a Hangfire-queued, downloadable report with a stored copy:

- **Monthly deduction schedule (HR)** and **landlord schedule (payments team)** — Excel, payroll ID and full name
- **Shareholding summary as at any date** (required monthly)
- **Member statement**: shares, loans, repayments, running balance — PDF, emailable
- **Loan portfolio**: active, by product, by zone
- **Arrears ageing**
- **Guarantor exposure**
- **Payroll reconciliation statement**
- **Bank reconciliation statement**
- **Trial balance; income and expenditure**
- **Dividend computation schedule**
- **AGM pack**: treasurer's report, chairman's report, income and expenditure summary

**Every report is reproducible as at a past date from the ledger.** Write an integration test that generates a June statement in December and asserts identical figures — this is the proof the append-only design works.

## 9. Notifications

Email (SMTP) and SMS (Africa's Talking, behind an interface so it can be swapped or disabled). Email is the confirmed channel for member statements; there is no member portal.

- Loan approved, loan disbursed
- Monthly statement
- Arrears reminder
- Shortfall notice when a deduction could not be made in full
- Guarantor alert when a guaranteed loan enters arrears, and when a co-guarantor exits CAL
- Dividend declared

All dispatched via Hangfire, logged, individually disableable. **Nothing sends outside Production.**

## 10. Data migration and go-live

- Import tooling for opening balances: members, shareholding, active loans with remaining schedules.
- A staged, reversible process: **load → validate → dry-run report → accounts clerk reviews → treasurer signs off → commit.** Data entry is by the accounts clerk; verification is by the treasurer.
- The commit posts opening-balance journal entries dated go-live against **Opening Balance Equity**, which must net to zero. **Refuse to commit if it does not balance.**
- Only **active balances** are migrated, not full ledger history.
- **Blocker:** the opening bank balance has not been supplied, and no go-live date has been agreed. The migration milestone cannot complete without both. Surface this to me rather than seeding a placeholder.

## 11. Testing standards

- Domain layer: pure unit tests, no database. **Every invariant has a test proving the invalid state cannot be constructed.**
- Every money calculation has a test using **real figures from the existing ledger**.
- Integration tests run against real PostgreSQL via Testcontainers. Never SQLite, never InMemory.
- Property-based tests (FsCheck) for `Money.Allocate`: for any amount and any number of parts, the parts sum exactly to the whole.
- A test asserting that no entity in `Akiba.Domain` references `Microsoft.EntityFrameworkCore`.
- CI on GitHub Actions: build, test, static analysis, format check. **Warnings as errors.**

## 12. Do NOT decide these — ask me

These are open because the source documents are silent, contradictory, or answer a different question than the one asked. Where I have named a default, implement it as a swappable strategy with a `// TODO: confirm with committee` and surface it in your milestone summary.

1. **Guarantor requirement for a fully-covered normal loan.** The source confirms only that *emergency* loans are unguaranteed unless the normal loan balance exceeds total shares. It says nothing about a normal loan covered by the borrower's own shares. Default: require at least one guarantor; show coverage as a warning.
2. **Guarantor liability basis.** The questionnaire says pro-rata; the signed application form says joint and several. Default: pro-rata. This must be resolved before the default-recovery module is built — the form is the document members actually sign.
3. **Payment allocation order** when a member has two loans and the deduction covers only part. The question was asked and answered with a description of restructuring, which does not answer it. Default: oldest first.
4. **Shortfall treatment.** Whether an uncollected amount is carried to next month, added to the end of the loan, or treated as arrears — not answered.
5. **Loans below KSh 30,000.** The scale starts at 30,000; no term is defined below it, on the form or in the minutes.
6. **Net disbursement / loan top-up.** The source says the full amount is disbursed *"apart from scenarios where the member had a loan balance of a quarter of the previous loan — this balance is deducted from the loan applied and the balance disbursed."* The official-use section of the form carries a **`Loan top-up`** field, which suggests this is a top-up: the existing balance is settled out of the new loan and only the difference paid over. Ask it that way. Do not model top-up and net disbursement as two separate mechanics until this is confirmed.
7. **Rental-income loans:** interest rate, repayment term scale, affordability basis (is the two-thirds rule applied to rental income, to gross salary, or to both?), and whether the rent-to-instalment ratio has a minimum. None of this is in any source document; the form establishes only that the product exists and what evidence is collected.
8. **Tenure-based terms for 400,001+.** Proposed in the minutes, never recorded as adopted, but already printed on the live application form. Which is authoritative?
9. **Zone list.** Approval routes through zone representatives, but no zone list exists in the documents.
10. **Share contribution amount** — fixed monthly figure, or member-chosen?
11. **Dividend basis** — time-weighted shareholding or closing balance? The source says only "interest earned divided by the total shareholding."
12. **Arrears for non-payroll borrowers.** Staff cannot miss a payment because deduction is at source. Client borrowers, exited staff and direct depositors can. No penalty rule exists, and no definition of a missed payment was given.
13. **All guarantors gone.** What happens when every guarantor on a loan has left CAL and the borrower cannot find replacements — not answered.
14. **Member exit.** What happens to a member's savings on exit, beyond the hold placed while they guarantee a live loan — not answered.
15. **Rule changes after go-live.** Who approves a change to an interest rate or a band — not answered. Until told otherwise, versioned configuration changes require chairman approval and are audited.
16. **M-Pesa Daraja integration** for auto-matching direct deposits. It would remove the name-on-a-slip matching problem but puts an external integration on a system meant to stay private and LAN-only. **Do not build it; raise it.**

## 13. Build order — stop for my review at each milestone

1. **Solution skeleton** — four projects, dependency rules enforced by an architecture test, Docker Compose with PostgreSQL, CI pipeline green.
2. **`Money` value object** with full unit and property-based test coverage, including `Allocate`.
3. **Ledger core** — accounts, journal entries, lines, balance derivation as at a date, period close. Tests proving an unbalanced entry cannot be constructed and that no balance is persisted. **Nothing else until this is right.**
4. **Borrowers, members, zones, shares, shareholding as at a date.**
5. **Loan products and interest strategies.** Tests first, using real ledger figures: 25,000 emergency → 27,500 over 5 months at 5,500; 60,000 normal → 66,000 over 12 months; 175,000 normal → 192,500 over 20 months. Rental-income loan is modelled as a product and a security type in this milestone, but its rate and term stay unimplemented pending §12.7.
6. **Schedule generation** with the one-month grace period.
7. **Guarantors**, shares-coverage indicator, liability strategy, exit flagging, exposure report.
8. **Applications**, the 15th-of-month lock period, multi-approver workflow by zone, disbursement, cheque and document records.
9. **Receipts**, three channels, allocation, overpayment disposal, clearance states.
10. **Payroll reconciliation and bank reconciliation.**
11. **Restructuring.**
12. **Arrears and ageing.**
13. **Reporting suite**, including the as-at-date reproduction test.
14. **Notifications.**
15. **Identity, roles, MFA, audit trail hardening.**
16. **Dividend run** with review and approval.
17. **Migration tooling.**
18. **Deployment:** service installation, encrypted nightly backups with an off-machine copy, automated restore verification, health checks, and a plain-English guide a non-developer treasurer can follow.

## 14. Conventions

- Domain logic lives in aggregates and domain services. **Never in Blazor components, never in EF configurations, never in controllers.**
- Aggregates have private setters and are mutated only through intention-revealing methods (`loan.Restructure(...)`, not `loan.Balance = x`).
- Domain events raised by aggregates; handlers perform ledger posting and notification dispatch.
- **Versioned configuration tables** for anything the committee can change: rates, bands, fees, limits, lock period cutoff day, guarantor liability basis, tenure-term feature flag.
- All timestamps stored UTC as `timestamptz`, displayed **Africa/Nairobi**. Financial periods follow the calendar year unless I say otherwise.
- Seed with realistic Kenyan names and plausible amounts. **Never seed real member data.**
- Write `ARCHITECTURE.md` explaining the ledger design and the money-handling policy to a maintainer who is not me.

---

**Start with step 1. Show me the solution structure and the dependency rules, and wait for my review before writing anything else.**
