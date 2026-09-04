# eClinicalWorks — Provider EMR Launch POC

Standalone proof-of-concept that validates the full **SMART on FHIR EHR launch**
round-trip against the eCW sandbox — **before** building it into FHIRBridge.

Proven end-to-end (test patient Shaikh Shaziya, FFBJCD sandbox): EHR launch →
authorization_code + PKCE → sign-in/consent → https callback → token exchange
(confidential, client_secret_basic) → multi-resource FHIR reads:
Patient, Condition (6), Observation (labs), MedicationRequest (23), AllergyIntolerance (1).

Listens on `https://localhost:5000` and handles the exact paths registered on the
eCW dev portal, so the portal's **Launch** button drives it with no re-registration:
- `GET /api/v1/oauth/launch`
- `GET /api/v1/oauth/callback`

> POC only — state is in-memory, no persistence.

## eCW quirks this POC encodes (carry into FHIRBridge)
1. **client_secret_basic** — `client_secret_post` returns `invalid_client`. See `AuthPlacement`.
2. **HTTPS callback** — eCW's authorize page sends `upgrade-insecure-requests`; an http listener fails at TLS.
3. **iframe breakout** — eCW frames the launch; its sign-in can't be framed, so the launch page navigates the top window.
4. **No `_count`** — eCW rejects `_count` (`Unsupported query parameter(s): _count`). Observation search needs `category`, not `_count`.

## 1. Configure (user-secrets — secret never touches the repo)

```bash
cd poc/ecw-ehr-launch-poc
dotnet user-secrets set "Ecw:ClientId" "<eCW portal → Manage → Sandbox → Client ID → Show>"
dotnet user-secrets set "Ecw:ClientSecret" "<eCW portal → Manage → Sandbox → Client Secret → Show>"
```

Endpoints/scopes/AuthPlacement are pre-filled in `appsettings.json` for the FFBJCD
sandbox (`https://staging-fhir.ecwcloud.com/fhir/r4/FFBJCD`).

## 2. Run

```bash
dotnet run --project poc/ecw-ehr-launch-poc
```

Serves `https://localhost:5000` (localhost dev cert; run `dotnet dev-certs https --trust` once).
Stop the FHIRBridge API first if it holds port 5000.

## 3. Trigger the launch

eCW dev portal → **Provider EMR** app → **Launch** → pick a Provider + test patient →
**Launch**. The callback page shows a per-resource result table + raw FHIR JSON.

The registered Launch/Redirect/Whitelist URLs must be **https**:
`https://localhost:5000/api/v1/oauth/launch/` and `.../oauth/callback`.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `invalid_client` at token | Use `AuthPlacement=basic` (default). |
| `invalid_grant` | redirect_uri must exactly match the registered (https) Redirect URL. |
| "localhost sent an invalid response" | Callback must be https (POC already is). |
| Broken/blank iframe on launch | Launch page breaks out to top window; click the "continue" link if scripted nav is blocked. |
| `Unsupported query parameter(s): _count` | Never send `_count` to eCW. |
| Hangs / 403 outside the US | eCW blocks non-US traffic — use a US VPN. |
