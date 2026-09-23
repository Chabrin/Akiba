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

## Raised by building notifications (23 September 2026)

**G. Nobody has agreed that members may be written to, or how they opt out.** Akiba now has
everything needed to email and text every member, and it sends nothing: outside Production it
cannot, and in Production the SMTP and SMS settings are absent until somebody supplies them.
Before they are, the committee should settle three things.

1. **Consent.** The register holds phone numbers collected to run a welfare society, not to
   market to anybody. Sending a member a statement they asked for is uncontroversial; sending
   an arrears reminder to somebody who would rather be telephoned is not.
2. **Opting out.** There is no unsubscribe link, because there is nowhere for it to go - no
   member portal. The realistic answer is that the accounts clerk turns a member off on
   request, and that needs a per-member setting Akiba does not yet have. **Say if it is
   wanted.**
3. **Who pays for SMS, and what the ceiling is.** Every text costs money. Akiba gives up on a
   message after five attempts rather than retrying forever, precisely so a gateway that
   accepts and charges for messages it fails to deliver cannot quietly spend the society's
   money - but nobody has said what the monthly budget should be.

**H. The arrears reminder is the one to read aloud before turning it on.** It goes to a
colleague about their own money, in an office where everybody knows everybody. The wording is
in one file - `NotificationComposer` - so the committee can change what it says rather than
turn the whole thing off.

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

## Raised by the hardening brief (23 September 2026)

Four items in that brief cannot be closed by writing code, for four different reasons.

### Encryption at rest is not "turn on TDE"

The brief asks for Transparent Data Encryption. **TDE is a SQL Server feature and Akiba
runs on PostgreSQL, which has no equivalent.** There is no setting to turn on, and there
are no `.bak` files to protect — the brief's phrasing appears to have come from a SQL
Server checklist.

What actually exists today: the backups **are** encrypted, with 7-Zip AES-256 and encrypted
filenames, so the copy that leaves the building is covered. The live database files on the
server are not.

If the committee wants the live files covered too, that is a decision about the **machine**,
not the application, and the honest options are:

- **BitLocker on the data volume.** Covers a stolen or discarded disk, which is the realistic
  threat for a machine in an office. Costs nothing, changes no code.
- **`pgcrypto` on chosen columns.** Covers a stolen disk *and* anybody with a database login
  they should not have — but every encrypted column stops being searchable or sortable,
  which for money columns means the ledger can no longer be summed by the database. Do not
  choose this without understanding that.
- **A filesystem-level encrypted volume.** Between the two.

**Recommendation: BitLocker.** It answers the threat the brief is actually describing.
Awaiting a decision.

### Unbranded member statements — declined as asked, offered differently

The brief asks that member statements be printed as "generic, unbranded summaries to
maintain plausible deniability if a physical document is ever misplaced or intercepted".

**Not built, and I would ask the committee to reconsider the goal rather than the wording.**
A statement a member cannot attribute to Akiba is also a statement that member cannot query,
rely on, or bring to a meeting — and members of a savings society have a legitimate interest
in a usable record of their own money. A document designed so that the society can deny
issuing it is not a safeguard for the member; it is a safeguard against the member.

There is also a compliance question nobody here has checked: **the Co-operative Societies
Act and SASRA guidance impose requirements on what a society must issue to its members and
what those documents must show.** That should be read before anything is decided, and read
by somebody qualified.

What *is* offered instead, and can be built on a word: a **discreet** statement — no colour,
no logo block, nothing that identifies a member from across a desk, the member's name and
figures placed so that a page face-up on a counter shows nothing useful. It still says which
society issued it and when. That covers the "misplaced or intercepted" case as well as an
unbranded page does, without costing the member the ability to use their own statement.

### Stripping SMS and email contradicts the build brief

The brief now asks for notifications to be internal-only, with no SMS or email. **BUILD_BRIEF
§9 says "Email is the confirmed channel for member statements", and that is what was built,
on the earlier instruction.**

Both are defensible. Internal-only means a member's position never leaves the building, and
a member is told things at the counter. Email means a member who has moved away still gets a
statement. **The committee has to pick one**, because they are opposites and the code
currently implements §9.

Note that the outbox already holds messages without sending them wherever no channel is
configured, so "internal-only" today is a configuration, not a rewrite. Making it permanent —
deleting the email path — is a rewrite, and it is the part that needs a decision before it is
done.

### Selling Akiba changes its licensing position

The brief mentions selling the application. Three dependencies are free only below a revenue
threshold, and this is recorded in `Directory.Packages.props`:

- **MediatR** — commercial licence required above a revenue threshold.
- **QuestPDF** — Community licence is free below a revenue threshold; above it, Professional
  or Enterprise.
- **FluentAssertions** — version 8 and later require a paid licence for commercial use. This
  one is test-only, and the cheapest fix is to move to a different assertion library rather
  than to pay.

Built and given to one welfare society, none of this applies. Sold, it does. **Check the
current thresholds before quoting anybody a price** — they have been revised more than once,
and none of the three is expensive compared with getting it wrong.
