# Akiba industry-readiness review

Review date: 27 September 2026

## Scope and conclusion

Reviewed the documented accounting and lending rules, domain aggregates, application command/query handlers, persistence boundaries, identity policies, page-level authorization, primary workflow screens, and existing test organization. This was a source review, not a production penetration test, statutory accounting opinion, or full WCAG conformance audit. The screenshot is an example of the Development UI, not evidence that production is exposed.

**Do not use this build for live financial operations until the critical items below are resolved and revalidated.** A successful build and a passing subset of tests do not establish operational or regulatory readiness. The internal-only requirement should be implemented as defense in depth (network restriction, authentication, least privilege, audit, backups); it is not a basis for concealing records from lawful authorities.

## Findings

### Critical: mutation authorization is enforced at the web dispatch boundary; role matrix still needs acceptance

Pages are gated by `ViewsLedger`, and identity policies exist for the accounts clerk, treasurer, chairman, and HR. A web MediatR behavior now applies authorization to every `*Command` request: the default is `RecordsMoney`, while period close/reopen, dividend review/approval, and opening-balance import map to their named policies. This closes the prior UI-only write boundary for the current command naming convention. The Development seed path is exempt only for a non-Production loopback request; the hosted notification dispatcher has a separate no-request execution path. The behavior must be covered by authenticated role tests before release, and command naming should be enforced by architecture tests so future mutations cannot bypass the convention.

**Done (27 September 2026, follow-up review):** role-by-command tests now run every officer-specific command against all five roles using the policies the host actually registers ([CommandAuthorizationTests.cs](../tests/Akiba.Web.Tests/CommandAuthorizationTests.cs)), and an architecture test fails the build if any handler that can save handles a request not named `*Command` ([CommandNamingTests.cs](../tests/Akiba.ArchitectureTests/CommandNamingTests.cs)). Writing those tests found a real fault: `SignOffReconciliationCommand` defaulted to the clerk although `BankReconciliation.SignOff` takes the treasurer, so the treasurer was refused and the official who prepared a reconciliation could sign it off. It now requires the treasurer. The policy map matches command types rather than name strings, so a renamed command is a compile error instead of a silent fall-through to the clerk default, and a refusal is shown inline on the screen instead of replacing it with "an unexpected error occurred".

**Still required:** confirm the policy for loan representative decisions and dividend posting with the approved role matrix. That is a committee decision, not a code one. See [AkibaRoles.cs](../src/Akiba.Infrastructure/Identity/AkibaRoles.cs), [CommandAuthorizationBehaviour.cs](../src/Akiba.Web/CommandAuthorizationBehaviour.cs), and [IdentityConfiguration.cs](../src/Akiba.Web/IdentityConfiguration.cs).

### High: loan approval can exceed the request

The application handler accepts an arbitrary `ApprovedPrincipal`, and pricing is recomputed for that amount without enforcing `approved <= requested`. This permits approving more than the signed application requested. Added a domain guard to reject zero/negative amounts and approvals above the request, with a regression test in [LoanApplicationTests.cs](../tests/Akiba.Domain.Tests/Lending/LoanApplicationTests.cs). Reassess affordability, borrowing limit, guarantee coverage, and terms on the actual reduced approval amount; the current handler assesses the requested amount first, then reprices a reduced amount without rerunning those checks.

### High: application receipt and decision stages do not connect

`ReceiveLoanApplicationHandler` creates a Draft. The page previously offered representative-decision and approval actions for Draft and Submitted, but domain operations require Submitted. Added `SubmitLoanApplicationCommand` and a Draft-only Submit action; decision and approval actions are now shown only for Submitted applications. Verify this stage against the committee's paper-review practice. See [LendingCommands.cs](../src/Akiba.Application/Lending/LendingCommands.cs) and [Applications.razor](../src/Akiba.Web/Components/Pages/Applications.razor).

### High: representative governance is not fully modelled

Approval only requires at least one recorded decision and no rejection. It does not prove that required zone and office representatives each decided, that the actors correspond to the representatives assigned to the borrower’s zone, or that the approving user is authorized. `Zone` representatives and authenticated user identities are not shown to be linked. Approval authority and mapping must be defined before go-live; an arbitrary decision count is not equivalent to committee approval.

### High: loan product/data capture is incomplete in the UI

The receive form offers a Client product but its borrower list is loaded from members only, so a non-member client cannot be selected. It omits Rental Income, even though the domain supports rental data and the brief requires property details and rent statements. Several source-form fields and guarantee/document capture are also not reachable from the receive flow. Reconcile each field on the current approved paper form to a UI field and persistence test before treating applications as complete.

### High: business rules still block sign-off

The committee’s unresolved choices and missing source documents in [open-questions.md](open-questions.md) affect guarantees, payment allocation, top-ups, rental-income pricing, dividend basis/settlement, arrears, member exit, accounts and close cadence. These should be approved and recorded with the society’s constitution/by-laws and current forms. No code review can decide them on the society’s behalf.

### Medium: privacy blur is presentation-only

Figures are hidden by CSS in the screen UI, but data still reaches the authenticated browser and print styling removes the blur. This is a shoulder-surfing aid, not access control or redaction. Keep role authorization and physical/network controls primary; decide whether printing should require an explicit reveal/confirmation and verify exports do not bypass user expectations.

### Medium: Development indicators and screenshot copy

The screenshot shows “Development” and “Setup account”; those are environment/setup affordances, not appropriate for a live operational screen. Ensure production configuration suppresses development affordances and demo endpoints. The earlier misleading claim that figures are “not stored” has been corrected to say they are derived from stored journal entries. Configure the allowed production host and firewall to the intended officials’ devices/subnet; a LAN binding alone is not per-official access control.

### UI review against a standard baseline

The current layout has clear module groupings and a consistent navigation shell. The screenshot still suggests a prototype: large unused horizontal space, old-fashioned serif rendering in the sign-in image (likely a font loading/fallback issue), development/setup controls visible, and no obvious control explaining the date context beyond “As at.” Verify the shipped font is available or bundle a licensed web font; a CSS font name alone does not guarantee Century Gothic on every workstation.

Use WCAG 2.2 AA as the accessibility acceptance baseline: keyboard-only review, visible and unobscured focus, contrast, zoom/reflow, target sizes, form labels/errors, screen-reader names, and reduced motion. Existing focus and reduced-motion styles help, but have not been measured against all criteria. Use a keyboard and screen-reader pass on every major workflow and test at 200% zoom. W3C recommends WCAG 2.2 for current accessibility work: [WCAG 2.2](https://www.w3.org/TR/WCAG22/). Use OWASP ASVS as the application-security verification baseline, especially authentication, authorization, session handling, data protection, and logging: [OWASP ASVS](https://owasp.org/www-project-application-security-verification-standard/).

## Module review map

| Area | Reviewed | Recheck needed |
|---|---|---|
| Ledger and periods | Balanced journal invariants, derived balances, append/reversal model, closed-period repository check | Concurrency around close/post; zero-value journal lines; production PostgreSQL migration/restore rehearsal |
| Membership and shares | Member/borrower distinction, share balances from journal, exit and guarantor concepts | Confirm exit release policy and that every form field is captured |
| Lending and guarantees | Application stages, pricing/assessment, disbursement, repayment schedule and guarantee model; cheque-book inventory, leaf reservation, voucher generation, cancellation/void and issuance links | Complete role/representative mapping; validate reduced approvals; complete Client/Rental workflows; resolve committee rules; PostgreSQL concurrency/idempotency tests for simultaneous voucher preparation |
| Receipts and allocations | Record, clear, reconcile, allocate flows and associated ledger postings | Verify posting-date chronology and duplicate references against source documents; UI and authorization test for every action |
| Bank/payroll reconciliation | Statement import, match suggestions, manual confirmations, sign-off and charge posting | Real bank statement fixtures, duplicate import/idempotency, two-account decision, role separation and concurrent sign-off tests |
| Dividends | Run compute/review/approve/post/withdraw stages | Enforce named roles at command boundary; settle basis and pay/retain policy; verify idempotency and posted-run immutability |
| Notifications | Outbox status/retry/suppression model and dispatch gates | Consent/opt-out, approved templates, production channel test, authorization and budget controls before enabling delivery |
| Reporting and audit | Reports and audit query endpoints/pages | Least-privilege field access, export authorization, retention/restore policy, access audit verification |
| Identity and deployment | Identity, TOTP policy, fallback policy and LAN deployment documentation | Mutation role enforcement, production firewall/host configuration, secrets handling, backup encryption and disaster-recovery rehearsal |
| UI/accessibility | Shared navigation, dashboard, forms, privacy mask and global CSS | WCAG 2.2 AA keyboard/screen-reader/contrast/zoom pass and responsive checks across every workflow |

## Verification status

**Follow-up review, 27 September 2026.** The whole suite now runs against a real PostgreSQL: **545 of 545 pass** (domain 352, application 1, architecture 22, web 78, persistence 92), on two consecutive runs, with zero build warnings. The 108 database and web tests this review originally could not run all pass, and the `ChequeBookControl` migration applies cleanly.

Running them, and reviewing the cheque-book code against the database, found and fixed:

- **A prepared voucher could strand a loan.** The disburse panel never loaded an existing voucher, so after preparing one and closing the panel - or being signed out by the five-minute idle timer while the cheque was taken for signature - reopening offered "prepare" again, was refused, and left the loan unpayable, the cheque unvoidable and the book unclosable. The voucher now travels on the application summary and is restored on open.
- **Cheques could be issued out of serial order.** Leaves came back from the database unordered and `ReserveNext` took the first available. The book now keeps its leaves in serial order and reserves the lowest explicitly.
- **Stale reads within a session.** The database context lives for an official's whole Blazor session, and the cheque-book repository read with tracking - unlike every other repository - so a session saw a book as it first loaded it, and could be offered a leaf another official had already reserved. Reads are now fresh and the concurrency check compares against the revision the command read.
- **One refused save locked an official out.** A failed save left its changes in the session's context, so every later save retried and failed until the page was reloaded. The unit of work now discards a refused command in full, and turns a concurrency refusal or duplicate into a message the screen shows inline.
- **A flaky test.** The web and persistence suites shared one database while the persistence suite truncates it between tests. The web suite now has its own.

Each concurrency fix has an integration test that holds two sessions open side by side; the stale-read test was confirmed to fail against the previous code.

**Still unverified:** installation as an actual Windows service (needs administrator rights on the target machine); the development seed endpoint's loopback restriction has no test of its own; real bank-statement fixtures, duplicate-import idempotency and concurrent sign-off remain as listed in the module map.

## Go-live gates

1. Run role-matrix tests against every command and verify that the production identity provider, MFA, lockout, and session settings match the expected officials.
2. Close the application workflow gaps and verify all loan products and signed-form fields end to end.
3. Obtain written decisions for all blocking business questions and reconcile code, forms, and reports to them.
4. Run integration suites against a dedicated disposable PostgreSQL instance; rehearse migration, backup restore, and recovery.
5. Complete a WCAG 2.2 AA manual UI review and an OWASP ASVS security verification pass; record findings and retest fixes.
6. Deploy only on the intended private network with named accounts, MFA, least privilege, monitored backups, and tested incident/recovery procedures.
