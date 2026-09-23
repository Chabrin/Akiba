# Security

What protects the society's records, where each control lives, and what is still outstanding.
Reviewed 21 September 2026.

Akiba holds every member's financial position and is the record the AGM is run from. It is
**internal and LAN-only**: four officials, one machine, no member portal, no public route, no
third-party API. Most of what follows is shaped by that — controls that matter for a system on
the open internet are sometimes irrelevant here, and one or two that nobody would bother with
on a public site matter a great deal on a shared office network.

---

## What an attacker would have to get past

| | Control | Where |
|---|---|---|
| Getting in | Password, minimum 12 characters, PBKDF2-HMAC-SHA256 at 600,000 iterations | `IdentityConfiguration.cs` |
| | **Mandatory TOTP** — a password-only session is permitted nothing but enrolment | `IdentityConfiguration.cs`, `AuthenticationEndpoints.cs` |
| | Account lockout: 5 attempts, 15 minutes | `IdentityConfiguration.cs` |
| | Rate limiting by machine: 20 attempts per 5 minutes on `/auth` | `RateLimiting.cs` |
| Staying in | Session cookie: HttpOnly, SameSite=Strict, Secure, 30-minute sliding expiry, not persistent | `IdentityConfiguration.cs` |
| | Antiforgery token on every form post that signs somebody in or out | `AuthenticationEndpoints.cs` |
| Doing anything | Role policies named for the action, not the person | `AkibaRoles.cs`, `IdentityConfiguration.cs` |
| | **Fallback policy**: a page that forgets to name one is locked, not open | `IdentityConfiguration.cs` |
| Reading the browser's way in | Content Security Policy, `script-src 'self'` with no `unsafe-inline` or `unsafe-eval` | `SecurityHeaders.cs` |
| | `nosniff`, `X-Frame-Options: DENY`, `frame-ancestors 'none'`, `Referrer-Policy: no-referrer` | `SecurityHeaders.cs` |
| | `Cache-Control: no-store` on every page that is not a static asset | `SecurityHeaders.cs` |
| Covering tracks | Append-only audit trail, immutable **at the database level** | `AuditTrail.cs`, migration `AuditTrail` |
| | Append-only ledger: no `Update`, no `Delete`, corrections are reversals | `IJournalRepository` |

---

## Decisions worth understanding before changing them

### Cross-site scripting

**There is no `MarkupString`, no `innerHTML` and no `Html.Raw` anywhere in Akiba.** Razor
encodes everything it renders, and nothing bypasses it. That — not the Content Security
Policy — is what actually stops a member's name being turned into a script.

There is exactly **one** JavaScript file, `wwwroot/js/akiba-shortcuts.js`, and one call site,
`MainLayout`. It registers a `keydown` handler for Ctrl+Shift+H and calls back into .NET to
toggle the privacy blur. It reads no data, writes nothing into the DOM, and takes no argument
from the page. It exists because a keyboard shortcut cannot be done any other way in Blazor
Server, and it is worth having because an official reaching for the mouse while somebody walks
up to the desk is the case the blur is for.

**If you add a second one, this section stops being true.** Anything that writes to the DOM
from JavaScript is outside Razor's encoding and has to be argued for on its own terms.

The policy is the second line, for the day somebody adds the first one of those. It allows
inline **styles**, because MudBlazor positions every popover and dialog by writing a `style`
attribute from JavaScript and without it the panel visibly breaks. It does **not** allow inline
scripts or `eval`, which is the half that matters.

There is a test asserting exactly that, so a well-meaning `'unsafe-inline'` added to
`script-src` to make something work fails the build rather than shipping.

### HTTPS

`Akiba:RequireHttps` is **on by default**. With it on, the session and antiforgery cookies are
marked `Secure`, HSTS is sent, and HTTP is redirected.

One thing that looks wrong and is not: **no `Strict-Transport-Security` header appears when
you test on `localhost`.** ASP.NET Core's HSTS middleware excludes `localhost`, `127.0.0.1` and
`[::1]` by default, because pinning HSTS on localhost breaks every other application a
developer runs. On the machine's real address it is sent.

Turning it off is a real decision with a real cost: over plain HTTP every password and every
session cookie crosses the office network in clear text, and anyone who can see that traffic
can read a member's position or take over a session. Startup logs a warning every single time
it is off, deliberately.

### Rate limiting

The panel itself is **not** rate limited. A Blazor Server session is one long-lived circuit
carrying every click an official makes, and a request limiter across it would count an
afternoon's work as an attack. What is limited is `/auth` (cheap to abuse) and `/reports`
(expensive to serve — an AGM pack is a whole PDF).

Account lockout and rate limiting do different jobs. Lockout stops five guesses at *one*
account. Rate limiting stops one machine working through *every* account — and stops somebody
locking all five officials out of their own system in a few seconds, which is a denial of
service wearing a security control's clothes.

### The audit trail cannot be edited, and that is enforced by PostgreSQL

`akiba.audit_entries` carries a trigger refusing `UPDATE` and `DELETE`. Nothing in Akiba maps
either operation, but "our code does not do it" is a weaker promise than an auditor deserves,
because the database has other users. There is a test that connects as the database user and
proves the refusal.

**This has a consequence for how the database login is set up — see below.**

### CORS

There is no CORS configuration anywhere, and that is the correct answer rather than an
oversight. No browser outside Akiba's own origin has any business calling it, so the browser's
same-origin rule is left to do its job and no `Access-Control-Allow-Origin` header is ever
emitted. A test sends a cross-origin `Origin` header and asserts nothing comes back.

---

## Outstanding — things the society still has to decide or do

### 1. The application's database login should not own its tables

The audit trigger stops anybody editing history. It does **not** stop the *owner* of the table,
who can run `ALTER TABLE ... DISABLE TRIGGER` and then do as they please.

Akiba applies its migrations at startup, which needs a login that can create and alter tables —
so as things stand, the login the application runs as is also the login that can disable the
trigger.

Closing that properly means two logins:

- a **migration** login that owns the schema, used only when upgrading;
- an **application** login that can read and write rows and owns nothing.

This is a deployment change, not a code change, and it is written up in
[`docs/deployment.md`](deployment.md). Until it is done, the immutability guarantee holds
against a mistake and against anybody without the database password — which is most of what it
is for — but not against a determined administrator.

### 2. `AllowedHosts` has to name the machine

It defaults to `localhost` so that an unconfigured deployment is not wide open. A LAN
deployment must set it to the address officials actually use, or nothing will answer. Startup
warns if it is left as `*`.

### 3. Break-glass credentials

The brief puts these with the treasurer and the chairman, not with ICT. The procedure is still
to be written.

---

## Things that were checked and found clean

- **No secrets in the repository or in its history.** Scanned for key material, tokens and
  connection strings across every commit.
- **No SQL injection surface.** One piece of raw SQL exists — the audit insert — and it is
  fully parameterised. Everything else goes through EF Core.
- **No vulnerable packages**, direct or transitive (`dotnet list package --vulnerable`).
- **No API keys of any kind.** Akiba integrates with nothing. If SMS or email is added in
  milestone 14, those credentials belong in environment variables, never in `appsettings.json`.
- **Nothing served that should not be.** `wwwroot` contains `app.css` and `favicon.svg`.
- **Input validation** is FluentValidation at the application boundary, plus the domain's own
  invariants — a `JournalEntry` whose lines do not sum to zero cannot be constructed at all.

---

## Running the checks again

```bash
dotnet list package --vulnerable --include-transitive
dotnet list package --outdated
dotnet test tests/Akiba.Web.Tests/Akiba.Web.Tests.csproj
```

The last of those is the one that matters most: it hosts the real application and makes real
requests, so a middleware registered in the wrong order fails a test instead of looking correct
in the file.
