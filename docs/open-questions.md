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
| 10 | Share contribution: a fixed monthly figure, or member-chosen? | Member-chosen amount per member | 4 | ✅ | **Answered by the deduction register (Dec 2025 - Jun 2026): member-chosen.** Amounts in use are 1,000 / 1,500 / 2,000 / 2,500 / 3,000 / 4,000 / 5,000 / 10,000 / 25,000, steady month to month per member. The code already assumed this. |
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

## Raised by the deduction register (received 18 September 2026)

The register covers December 2025 to June 2026: 77 shareholders, total shareholding
**KES 24,369,204**. Five things in it contradict what was assumed.

**1. A shareholder need not have a payroll number.** Two shareholders share staff number
`0` — they are not on the CAL payroll. Akiba required a payroll number and enforced it as
unique, so **loading this register would have failed at the second row.** Fixed: the payroll
number is now optional, and uniqueness applies only where one is present.

**2. There is a "top up deposit", and Akiba had no idea.** A lump sum paid into shares
separately from the monthly contribution — 100,000 in one case, 862 in another. It is its own
column in every sheet. Akiba modelled monthly contributions only. Fixed: top-ups are a
distinct kind of share contribution, so a statement can show which is which.

**3. The society is roughly twice the size assumed.** 77 shareholders, not the ~40 the brief
implied, and shareholdings run to 2,355,000 — a borrowing limit of 4.71m, well past the
graduated scale's top band. Nothing breaks, but the 400,001+ band is not an edge case here,
it is where the largest members sit. That makes the unadopted tenure rule (item 8) more
pressing, not less.

**4. Every month's totals carry floating-point corruption.** Every shareholding in the file is
a whole number of shillings, yet each column total ends `.734195583`. That tail cannot come
from the data; it is accumulated binary floating-point error. The current total is wrong by a
fraction of a shilling and nobody can say why or since when.

This is the clearest possible argument for the whole design. Akiba stores money as
`numeric(19,4)` and computes it in `decimal`, which is base-10 and exact — the same sum here
produces a whole number, every time, and the trial balance proves it. **Worth showing the
treasurer.**

**5. The register is a running shareholding statement, not a deduction instruction.** Each
sheet restates every prior month and adds columns to the right, so the June sheet carries
December through August. The office is maintaining a hand-rolled ledger in a spreadsheet —
which is exactly what Akiba replaces, and it means the monthly HR export must reproduce this
shape to be recognisable.

**Still missing:** the opening **bank** balance. The register gives member shareholdings but
says nothing about what is in the account, and migration cannot commit an opening-balance
entry that nets to zero without it.

## Raised by building reconciliation (18 September 2026)

**A. The chart of accounts has one bank account; the society appears to run two.** The brief
distinguishes the **main** account, on which landlord rent offsets are drawn, from the
**business** account, on which employee payroll cheques are drawn - and says they reconcile
against different statements. The seeded chart has a single `1000 Bank`. Reconciliation is
keyed on an account id rather than on that one code, so splitting the chart later needs no
code change, but until the committee confirms whether there are two accounts and what they
are called, both channels land in one account and one statement. **Ask before go-live**: after
opening balances are posted, splitting them is a journal entry somebody has to justify.

**B. A month can only be closed once a signed-off statement covers its month end.** That is
how Akiba now enforces the brief's rule, and it has a consequence worth stating plainly:
because statements arrive **quarterly**, months will close in threes, a quarter at a time,
rather than one a month.

The alternative - letting a month close before its statement arrives - is worse than it
sounds. The bank charges that statement reveals are dated inside the closed month, and a
closed month cannot be posted into, so they would have to be posted in the wrong period or
not at all. But if the committee wants monthly closes, the answer is to ask the bank for
monthly statements, not to relax the rule. **Confirm which they want.**

**C. What does the office do with an over-deduction?** The payroll reconciliation reports
one and says it is the member's - either added to shares or refunded - which mirrors the
overpayment rule already agreed for receipts. Nobody has confirmed that the same rule applies
when the error is HR's rather than the member's.

## Raised by building the dividend run (18 September 2026)

**D. Open question 11 now has a screen that answers it with figures.** The dividend basis -
time-weighted shareholding or closing balance - is still unsettled, so both are implemented as
strategies and the run records which one it used. **Compare the two bases** on any computed run
shows, member by member, what each answer is worth in shillings.

Take that to the committee rather than the question. With every member contributing a steady
amount for a full year the two bases agree exactly; they only diverge for somebody who joined
part-way through, which is precisely the case nobody has ruled on.

**E. The dividend is declared but not settled, and that is deliberate.** Posting a run debits
Retained Surplus and credits Dividends Payable, one line per member. What happens next -
**added to the member's shares, or paid out** - is not in any source document, so Akiba stops
at the point where the society owes it. **Ask the committee**, and note that the answer changes
what a member's next borrowing limit is: a dividend added to shares raises it, a dividend paid
out does not.

**F. Members who left CAL during the year are included in the run.** They held shares for part
of it, and leaving is not obviously the same as forfeiting a year's dividend. This overlaps
open question 14, which nobody has answered.

## Found while building, not yet raised

**The graduated scale is a maximum, not a fixed term.** The application form's duration
guide is headed *"Maximum Repayment Period"* and the form asks the applicant to state the
period they want. The build brief treats the scale as giving *the* term. The code follows
the form: a member borrowing 60,000 may ask for eight months although the scale allows
twelve, and a longer request is refused. Worth confirming, because it changes what the
office may agree to at the counter.

## One thing to verify against the paper ledger

The emergency loan maximum is recorded as **KSh 25,000 over 5 months at 5,500/month**, and
milestone 5 hard-codes that as a test. A scanned ledger page appears to show an
emergency-type entry **above** 25,000, but the scan is not legible enough to be sure.
**Check the original book before milestone 5 is signed off** — if the cap has ever been
exceeded in practice, the rule as stated is wrong, or there is a product nobody has
described.
