# Medplum integration POC

Throwaway console app that validates the FHIRBridge → Medplum **write path** end-to-end against a live (hosted)
Medplum project — phase 1 of [docs/backend/15-medplum-integration-plan.md](../../docs/backend/15-medplum-integration-plan.md).
It is intentionally **not** part of `FHIRBridge.sln` and depends only on the BCL, so it stays isolated from the main
build/tests. It mirrors the auth + conditional-upsert logic that
`src/FHIRBridge.Infrastructure/Destinations/MappedMedplumDestinationWriter.cs` implements for real.

## What it does

Two modes.

**Default (write path):**
1. **Auth** — OAuth2 `client_credentials` → bearer token.
2. **Upsert** — `PUT {base}/Patient?identifier={system}|{value}` (idempotent conditional update).
3. **Read-back** — `GET {base}/Patient?identifier={system}|{value}` and prints the match count.

Run it **twice**: the Medplum resource id printed in step 2 must stay the same on the second run — proof the upsert
updates rather than duplicates.

**`--verify` (read-only confirmation, no mutation):**
1. **Auth** — same token.
2. **Search** — `GET {base}/Patient?identifier={system}|{value}` → resolves the resource + its server id, prints the
   full FHIR JSON.
3. **History** — `GET {base}/Patient/{id}/_history` → prints one line per version.

Confirms exactly one resource exists and that repeated runs produced multiple **versions under a single id** — the
resource-level idempotency proof, straight from the FHIR API (no browser needed).

## Setup (get credentials)

1. Create a free project at <https://app.medplum.com> (or point at your self-hosted instance).
2. In the Medplum App → **Project Admin → Clients** (`/admin/clients`), create a **ClientApplication**.
   Copy its **ID** and **Secret**.
3. Ensure the client's AccessPolicy allows `create`/`update`/`search` on `Patient` (default admin client does).

## Run

```bash
export MEDPLUM_CLIENT_ID=<ClientApplication ID>
export MEDPLUM_CLIENT_SECRET=<ClientApplication Secret>
# optional — defaults to hosted Medplum:
# export MEDPLUM_BASE_URL=https://api.medplum.com/fhir/R4
# export MEDPLUM_TOKEN_URL=https://api.medplum.com/oauth2/token   # derived from base url when omitted

dotnet run --project poc/Medplum.Poc

# read-only confirmation of the resource + version history:
dotnet run --project poc/Medplum.Poc -- --verify
```

On Windows PowerShell:

```powershell
$env:MEDPLUM_CLIENT_ID = "<ClientApplication ID>"
$env:MEDPLUM_CLIENT_SECRET = "<ClientApplication Secret>"
dotnet run --project poc/Medplum.Poc
dotnet run --project poc/Medplum.Poc -- --verify
```

Never commit real credentials — this POC only reads them from environment variables.

## Expected output

```
[1/3] Requesting client_credentials token from https://api.medplum.com/oauth2/token ...
      OK — got bearer token.
[2/3] Upserting Patient by identifier (conditional PUT) ...
      OK — Medplum resource id: 0f8b...
[3/3] Reading it back by identifier ...
      OK — search matched 1 resource(s) for https://fhirbridge.poc/patientId|POC-0001.

SUCCESS. Run again — the id above should stay the same (idempotent upsert, no duplicate).
```
