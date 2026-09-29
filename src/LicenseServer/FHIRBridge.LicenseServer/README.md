# FHIRBridge License Server

Standalone internal tool combining two things:

1. **License minting** — an admin form/UI to generate signed FHIRBridge product licenses covering every
   field the product supports (customer, edition, expiry, per-resource limits, allowed source types,
   allowed hospitals, feature flags).
2. **Usage check-in** — a `POST /api/checkin` endpoint deployed FHIRBridge instances call to report usage
   and have their license token's signature verified.

This project is **fully independent** of the main FHIRBridge repository (`FHIRBridge2/FHIRBridge`). It has
no project or package reference into it — it only mirrors the exact claim shape and ES256 signing/
validation approach used there (`src/FHIRBridge.Infrastructure/Licensing/SignedLicenseValidator.cs` and
`tools/FHIRBridge.LicenseMinter/Program.cs`), so every token minted here verifies unchanged against that
repo's validator, and vice versa.

## Running locally

```bash
dotnet build
dotnet run
```

By default this listens on `http://localhost:5220` (see `Properties/launchSettings.json`) and uses a local
SQLite file `licenseserver.db` (created automatically on first run — no external database needed). Swap
`ConnectionStrings:Default` for a Postgres/Azure SQL connection string later; the code only depends on
`DbContext`, so switching `UseSqlite(...)` to `UseNpgsql(...)`/`UseSqlServer(...)` in `Program.cs` is the
only code change needed (add the matching EF Core provider package first).

On first run, watch the startup console/log output for two important lines:

- **Admin password** — if `AdminCredentials:Password` isn't configured, a random one-time password is
  generated and printed clearly at startup (a new one every restart). Set `AdminCredentials:Password` (or
  the `AdminCredentials__Password` environment variable) to pin a real one.
- **Signing key mode** — a warning if this process is signing with the embedded throwaway DEV keypair
  rather than a real production PFX (see below). The admin UI also shows a persistent yellow banner
  whenever the dev key is active, so a dev-signed license can never be mistaken for a real one.

Log in at `/Account/Login`, then use the nav bar: **Dashboard** (installations that have checked in),
**Mint License** (the minting form), **Issued Licenses** (audit trail of everything minted).

## UI approach

**Razor Pages**, chosen over Blazor Server or a minimal-API-plus-hand-rolled-HTML approach because:

- No separate SPA build pipeline, no client-side state-management concerns (Blazor Server would add a
  SignalR circuit for what is fundamentally a couple of server-rendered forms and tables).
- Standard `<form method="post">` + model binding covers the mint form (including the fixed-size
  add/remove hospital rows and the source-type checklist) with zero JavaScript framework, just a few lines
  of vanilla JS to reveal/clear extra hospital rows.
- Built-in antiforgery token handling on every form post, and `[Authorize]`-by-default via a global
  fallback authorization policy, with `/Account/Login` and `/api/checkin` as the only two explicit
  exceptions.

## Authentication

A single configured admin account (`AdminCredentials:Username` / `AdminCredentials:Password`) gates every
page and the mint API via ASP.NET Core cookie authentication (`Pages/Account/Login.cshtml`). This is
**intentionally simple** — one shared username/password, no per-user accounts, no MFA, no IP allow-listing
— because the user explicitly chose convenience over strict isolation for this internal tool.

**Before this handles real customer data**, that trade-off should be revisited: real per-admin accounts,
MFA, and IP allow-listing (this is reachable from the internet) should all be added. Treat the current gate
as "not an open door," not as "production-grade."

`/api/checkin` deliberately has **no** cookie auth — it's called by deployed FHIRBridge instances, which
have no admin session. Its own protection is the license token's ES256 signature check (see below).

## Signing key: dev vs. production

`LicenseSigningOptions` (bound from the `LicenseSigning` config section in `appsettings.json`) controls
which ECDSA P-256 keypair this server signs — and, since this same process also runs `/api/checkin`,
verifies — license tokens with:

- **Dev/local (default)**: `LicenseSigning:PfxPath` is blank, so the server falls back to a throwaway
  ECDSA P-256 keypair generated once and embedded in
  `Licensing/LicenseSigningKeyProvider.cs` (`DevPrivateKeyPkcs8Base64`), clearly marked as dev-only. This
  is a **different** keypair than the one in the main FHIRBridge repo's own dev tooling — this is a fully
  separate project with no shared secrets. Tokens signed with it will **never** verify against a real
  deployed FHIRBridge instance (which trusts the real production public key).

- **Production**: set `LicenseSigning:PfxPath` to the real PegasusOne FHIRBridge Licensing PFX (a 10-year
  self-signed cert, `CN=PegasusOne FHIRBridge Licensing`) and `LicenseSigning:PfxPassword` to its password
  — prefer the `LicenseSigning__PfxPassword` environment variable over writing the password into
  `appsettings.json`. Example:

  ```json
  {
    "LicenseSigning": {
      "PfxPath": "C:\\secure\\pegasusone-fhirbridge-licensing.pfx"
    }
  }
  ```
  ```bash
  # or via environment variable instead of the JSON file:
  $env:LicenseSigning__PfxPath = "C:\secure\pegasusone-fhirbridge-licensing.pfx"
  $env:LicenseSigning__PfxPassword = "<the real password>"
  ```

  **The `.pfx` file and its password are not included in this repo — they must be supplied separately** and
  placed at a path you control; only point the config at that path.

`LicenseSigning:ExpectedPublicKeyBase64` (already set in `appsettings.json` to the real production public
key the user supplied) is purely an informational sanity check: if a PFX is configured but the key it
actually loads doesn't match this value, a warning is logged at startup — it never blocks the app from
running, it just flags "you probably pointed this at the wrong file."

**Design note**: unlike the main repo's split of a hardcoded public-key constant (verification only) and a
separately-configured private key (signing only), this project derives the public key used for
verification directly from whichever private key is active (`ECDsa.ExportSubjectPublicKeyInfo()` returns
only the public component even when the key object holds a private key too). That makes a dev/prod key
mismatch between minting and verification structurally impossible here, since the same process does both.

## Check-in API contract

### `POST /api/checkin`

No authentication — this is the endpoint deployed FHIRBridge instances call directly. Fast, and
side-effect-free beyond the single DB upsert/append described below.

**Request body:**

```json
{
  "installationId": "string — a stable id the deployed instance generates for itself",
  "licenseToken": "string — the signed license token this instance was configured with",
  "observedUtc": "2026-09-10T12:00:00Z",
  "counts": {
    "userCount": 7,
    "sourceConnectionCount": 2,
    "tenantCount": 1,
    "workflowCount": 4,
    "cumulativeConfiguredPipelineRunCount": 1200,
    "cumulativeRuntimeWorkflowRunCount": 340,
    "processedRecordsThisMonth": 58000
  }
}
```

Field names are camelCase (ASP.NET Core minimal API JSON default). All `counts` fields are required
integers (use `0` if a count doesn't apply to a given deployment).

**Response — 200 OK** (license token's signature verified; the check-in was recorded):

```json
{
  "status": "ok",
  "installationId": "dev-vm-01",
  "customerName": "Acme Health System",
  "edition": "enterprise",
  "receivedAtUtc": "2026-09-10T08:26:04.2313259Z"
}
```

**Response — 401 Unauthorized** (signature verification failed — malformed token, wrong issuer, tampered
signature, wrong/garbage string):

```json
{
  "error": "License token rejected: <short diagnostic>"
}
```

**Response — 400 Bad Request**: `installationId` missing/blank.

On a successful check-in, the server:

1. Verifies `licenseToken`'s ES256 signature and `iss: "pegasusone"` claim against its own active signing
   key (see above). Rejects with 401 on any failure — expired tokens (`exp` in the past) still succeed here
   (signature-only check); expiry is a policy concern for the deployed instance/dashboard viewer, not this
   endpoint.
2. Upserts an `Installations` row keyed by `installationId` — latest `customerId`/`customerName`/`edition`
   (from the verified token, not from the request body), last-seen timestamp, and the latest counts.
3. Appends a `CheckIns` history row with the full counts and both the reported (`observedUtc`) and received
   (`receivedAtUtc`) timestamps — nothing is ever overwritten there.

## License Request API contract

### `POST /api/license-requests`

No authentication — this is the endpoint a customer install's own License Request screen calls directly
(main repo: `LicenseRequestService.AttemptSubmitAsync`). Upserts by `uniqueKey`, so a resubmission from the
same install updates the existing row rather than creating a duplicate.

**Request body:**

```json
{
  "clientName": "string",
  "email": "string",
  "companyName": "string or null",
  "address": "string or null",
  "phoneNumber": "string",
  "uniqueKey": "string — generated once by the requesting install, stable across its resubmissions"
}
```

**Response — 200 OK**: request recorded (or updated); it now appears, pending, on `/LicenseRequests`.

**Response — 400 Bad Request**: `clientName`, `email`, `phoneNumber` or `uniqueKey` missing/blank.

When a customer's install can't reach this endpoint directly, its License Request screen shows an
AES-256-GCM-encoded fallback code instead (same fields, encrypted with a key shared between both repos —
see `Licensing/LicenseRequestSharedKey.cs`) for the customer to paste on `/LicenseRequests` by hand.

Minting a license from a pending request (the "Create License" link on `/LicenseRequests`) pre-fills the
customer's contact details and embeds their `uniqueKey` as the token's `requestKey` claim, so that
install's own `SignedLicenseValidator` can confirm the license was minted for the request it actually made
before applying it (main repo: `LicenseService.ApplyAsync`).

## Project structure

```
FHIRBridge.LicenseServer.csproj
Program.cs                          # composition root: DI, cookie auth, EF Core, Razor Pages + /api/checkin
appsettings.json / appsettings.Development.json
Domain/
  IssuedLicense.cs                  # audit row: what was minted (not the private key)
  Installation.cs                   # latest snapshot per deployed instance
  CheckIn.cs                        # full check-in history
Data/
  LicenseServerDbContext.cs         # single EF Core DbContext, SQLite by default
Licensing/
  LicenseSigningOptions.cs          # PfxPath / PfxPassword / ExpectedPublicKeyBase64 config
  LicenseSigningKeyProvider.cs      # loads the active ECDSA keypair (PFX, or embedded dev fallback)
  LicenseFields.cs                  # every field a license can carry (form-agnostic)
  LicenseTokenMinter.cs             # signs LicenseFields into a compact-JWS token (ES256)
  LicenseTokenValidator.cs          # verifies a token's signature + pulls sub/customerName/edition
Security/
  AdminCredentialsOptions.cs
  AdminAccountProvider.cs           # resolves/generates the single admin account
Api/
  CheckInContracts.cs               # CheckInRequest/Response/Error DTOs
  CheckInEndpoints.cs               # POST /api/checkin (anonymous)
Pages/
  Shared/_Layout.cshtml             # nav + dev-key banner
  Index.cshtml(.cs)                 # dashboard: installations by last-seen
  Account/Login.cshtml(.cs), Logout.cshtml(.cs)
  Licenses/Create.cshtml(.cs)       # mint form (all license fields) + minted token display
  Licenses/Index.cshtml(.cs)        # audit trail of every issued license
  Error.cshtml(.cs)
wwwroot/css/site.css
```

## Security note (read before using this for real customers)

This is a deliberately lightweight internal tool. Before it handles real customer license data or is
relied on for production usage tracking, upgrade:

- **Auth**: real per-admin accounts (not one shared username/password), MFA, and IP allow-listing — this
  server is reachable from the internet by design (so deployed instances can reach `/api/checkin`), which
  makes the admin UI's current single-password gate the weakest link.
- **Secret storage**: the production PFX password currently lives in local config (an environment variable
  or `appsettings.json`) rather than a real secret store. Move it to Azure Key Vault (or equivalent) before
  this is trusted with real customer PFX credentials.
