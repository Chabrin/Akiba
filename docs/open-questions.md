# Open questions for the Akiba committee

Sixteen rules the source documents do not settle. Each is either a silence — the question
was never asked, or was answered with something else — or a genuine contradiction between
two documents.

**None of these may be resolved by a developer.** Where code had to do *something*, it does
it behind a swappable strategy with the default recorded below and a
`// TODO: confirm with committee` at the call site. Update the Answer column when the
committee rules, then remove the TODO and add the test.

Status: 🔴 blocks a milestone · 🟠 needed before go-live · 🟡 can ship with the default

| # | Question | Default in code | Blocks | Status | Answer |
|---|---|---|---|---|---|
| 1 | Does a normal loan fully covered by the borrower's own shares still need guarantors? The source confirms this only for *emergency* loans. | Require ≥1 guarantor; show coverage as a warning, not a block | 7, 8 | 🔴 | |
| 2 | Guarantor liability: pro-rata or joint and several? The questionnaire says pro-rata; the signed application form says joint and several. | `ProRata` | 7, 12 | 🔴 | |
| 3 | Payment allocation order when a member has two loans and the deduction covers only part. The question was asked and answered with a description of restructuring. | Oldest loan first | 9 | 🔴 | |
| 4 | Shortfall treatment: carried to next month, added to the end of the loan, or treated as arrears? | Carry forward, flagged | 9, 12 | 🟠 | |
| 5 | Loans below KSh 30,000 — the graduated scale starts at 30,000 and defines no term below it. | Reject below 30,000 | 5, 6 | 🟠 | |
| 6 | Net disbursement vs loan top-up. Source: full amount disbursed *"apart from scenarios where the member had a loan balance of a quarter of the previous loan"*. The form has a `Loan top-up` field, which suggests these are the same mechanic. | Not implemented; blocked | 8 | 🔴 | |
| 7 | Rental-income loans: interest rate, term scale, affordability basis (two-thirds of rental income, of gross salary, or both?), minimum rent-to-instalment ratio. The form establishes only that the product exists. | Product and security modelled; rate and term unimplemented | 5, 6 | 🔴 | |
| 8 | Tenure-based terms for 400,001+ (36/40/48 months). Proposed in the minutes of 06/09/2026, never recorded as adopted — but already printed on the live application form. Which document is authoritative? | Feature flag, default **off** | 5 | 🟠 | |
| 9 | The list of zones. Approval routes through zone representatives, but no zone list exists in any document. | `Zone` entity, seeded empty | 4, 8 | 🟠 | |
| 10 | Share contribution: a fixed monthly figure, or member-chosen? | Member-chosen amount per member | 4 | 🟠 | |
| 11 | Dividend basis: time-weighted shareholding, or closing balance? Source says only "interest earned divided by the total shareholding". | Closing balance | 16 | 🟠 | |
| 12 | Arrears for non-payroll borrowers. Staff cannot miss a payment — deduction is at source. Client borrowers, exited staff and direct depositors can. No penalty rule exists and no definition of a missed payment was given. | Ageing buckets only; no penalty | 12 | 🟠 | |
| 13 | What happens when every guarantor on a loan has left CAL and the borrower cannot find replacements. | Flag and escalate; no automatic action | 7 | 🟡 | |
| 14 | What happens to a member's savings on exit, beyond the hold placed while they guarantee a live loan. | Hold, manual release | 4 | 🟠 | |
| 15 | Who approves a rule change after go-live (an interest rate, a band)? | Chairman approval, audited | 15 | 🟡 | |
| 16 | M-Pesa Daraja integration for auto-matching direct deposits. Would remove the name-on-a-slip matching problem, but puts an external integration on a system that is meant to stay LAN-only and private. | **Not built.** Raised, not decided | — | 🟡 | |

## Also outstanding — not rules, but blockers

| Item | Needed for | Status |
|---|---|---|
| Opening bank balance for the Akiba account | Milestone 17 cannot commit an opening-balance entry that nets to zero without it | 🔴 Not supplied |
| Agreed go-live date | Milestone 17, 18 | 🔴 Not supplied |
| Current member count and active loan count | Sizing the migration dry-run | 🟠 Not supplied |
| Akiba constitution / by-laws | Verifying that the coded rules match the written ones | 🟠 Requested, not received |
| Most recent bank statement | Building and testing the bank reconciliation importer | 🟠 Requested, not received |
| A completed example loan application form | Confirming every captured field and its format | 🟠 Requested, not received |

## One thing to verify against the paper ledger

The emergency loan maximum is recorded as **KSh 25,000 over 5 months at 5,500/month**, and
milestone 5 hard-codes that as a test. A scanned ledger page appears to show an
emergency-type entry **above** 25,000, but the scan is not legible enough to be sure.
**Check the original book before milestone 5 is signed off** — if the cap has ever been
exceeded in practice, the rule as stated is wrong, or there is a product nobody has
described.
