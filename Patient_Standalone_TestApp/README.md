# Health App (demo third-party client)

A mobile-styled demo app used to show a customer, end to end, that FHIRBridge's
workflow can be triggered from an outside application and that the fetched data
lands correctly in that application's own database:

1. An **Admin** signs in and configures the **Workflow URL** to call (persisted
   in this app's own database).
2. A **Patient** signs in, sees a health dashboard (vitals + a "Connect Get
   Data" button), picks a hospital from a list stored in the database, and taps
   **Process**.
3. The backend calls the configured Workflow URL, expects back a JSON body of
   the form `{ "Resources": [{ "ResourceType", "ResourceId", "Payload" }] }`,
   and upserts each resource into its own `Patients` table.
4. The app then shows the **latest record** from that table — proof that the
   round trip (call workflow → fetch data → store locally → display) worked.

This app is intentionally decoupled from the real FHIRBridge API — it does not
call FHIRBridge directly or authenticate against it. Point the Workflow URL
setting at whatever endpoint (FHIRBridge or a stand-in) returns data in the
expected shape.

## Layout

```
Patient_Standalone_TestApp/
  backend/    ASP.NET Core minimal API (port 5500) — own database, auth, and workflow-run logic
  frontend/   Angular mobile-styled app (port 5501, max width 500px) — login, dashboard, hospital picker
```

Ports 5500/5501 avoid clashing with FHIRBridge's own services (API 5000,
portal 4200, Gateway 54649/54650, Docker Compose services).

## Database

A dedicated **`HealthAppDb`** database on the same SQL Server instance
FHIRBridge itself uses (`localhost,1433`, from `docker-compose up`), auto-created
on first run (`EnsureCreated`, no migrations) with seed data:

| Table | Contents |
|---|---|
| `Users` | 2 seeded accounts (see below); passwords are stored **in plain text** — dummy demo data only, not a real account store |
| `Hospitals` | 5 dummy hospitals, each with a name + `OrganizationId` |
| `Patients` | `ResourceType`, `ResourceId`, `Payload` (raw FHIR JSON), `RecordCreatedOn` (set once, at first insert), `UpdatedAtUtc` (moves on every re-fetch), plus a persisted column for every demographic field — full/first/middle/last name, gender, legal sex, sex for clinical use, pronouns, marital status, patient status, deceased flag, US Core race/ethnicity/sex, full address, every telecom channel, care-team references. These are populated once at ingestion time (`PatientFieldExtractor`, called from `POST /api/workflow/run`), not re-parsed from JSON on every read. Age and long-form Date of Birth are still computed at read time since they change daily. Seeded with one sample Epic Patient resource. |
| `WorkflowSettings` | Single row holding the admin-configured Workflow URL |

Seeded logins:

| Email | Password | Role |
|---|---|---|
| `admin@healthapp.local` | `Admin@123` | Admin — can view/edit the Workflow URL |
| `patient@healthapp.local` | `Patient@123` | Patient — dashboard + Connect Get Data flow |

To reset to this seed state, drop the database and restart the backend:

```sql
DROP DATABASE HealthAppDb;
```

## Run it

```bash
# Terminal 1 — backend
cd backend
dotnet run
# listens on http://localhost:5500

# Terminal 2 — frontend
cd frontend
npm install
npm start
# serves on http://localhost:5501
```

Open `http://localhost:5501`.

1. Log in as `admin@healthapp.local` / `Admin@123`, tap the gear icon, and set
   the **Workflow URL** to an endpoint that returns:
   ```json
   { "Resources": [{ "ResourceType": "Patient", "ResourceId": "...", "Payload": "<FHIR JSON string>" }] }
   ```
2. Log out, log back in as `patient@healthapp.local` / `Patient@123`.
3. Tap **Connect Get Data**, pick a hospital, tap **Process**.
4. A loading spinner shows while the backend calls the workflow URL; on
   success, the **Latest Record** card updates with the newly ingested patient.
5. Tap **View All Records** to open the **Records** screen — one card per
   patient row, header showing **Record Created On**, with left/right arrows
   to page through every ingested patient.

## API summary (backend, port 5500)

| Endpoint | Auth | Purpose |
|---|---|---|
| `POST /api/login` | — | Local login against `Users` table, sets an HttpOnly session cookie |
| `POST /api/logout` | session | Clears the session |
| `GET /api/hospitals` | session | List hospitals for the picker |
| `GET/POST /api/settings` | session, Admin role | Read/write the Workflow URL |
| `POST /api/workflow/run` | session | Calls the configured Workflow URL, ingests `Resources[]` into `Patients` |
| `GET /api/patients/latest` | session | Most recently updated patient record (generic table shape, home screen) |
| `GET /api/patients` | session | Every patient record as cards, newest `RecordCreatedOn` first (Records screen) |

## Configuration

`backend/appsettings.json`:

| Key | Purpose |
|---|---|
| `ConnectionStrings:Default` | SQL Server connection string (default targets `HealthAppDb` on `localhost,1433`, the same instance FHIRBridge's `docker-compose` starts, `sa`/`Your_password123`) |
| `AllowedFrontendOrigin` | CORS origin allowed to call this backend (default `http://localhost:5501`) |

`frontend/src/app/app.ts` has `BACKEND_BASE_URL` pointing at this backend
(default `http://localhost:5500`).
