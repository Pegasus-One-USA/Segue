---
name: runsegue
description: >
  Stop, clean-build, and run the full FHIRBridge local stack — API (http://localhost:5000), Worker, and the Angular portal (http://localhost:4200) — against a local containerised database, prompting for the provider (PostgreSql / SqlServer) and database name before it starts. Defaults to PostgreSQL / FHIRBridge_v2 in container fhirbridge-controlplane-pg on localhost:5434. Kills any previously running API/Worker/ng processes and frees ports first, wipes bin/obj and Angular caches, restores + rebuilds the solution from clean, then launches all three in the background with logs. Trigger whenever the user says "runsegue", or asks to restart / rebuild / relaunch / "run everything" / "start the app" / "fresh build and run" for this project locally.
---

# runsegue — clean rebuild and run the local FHIRBridge stack

Brings up the three local processes — **API**, **Worker**, **portal** — from a clean build, pointed at the
**PostgreSQL** container in Docker Compose. Always stops what is already running first, so repeated
invocations are safe and never leave two copies of the API fighting over port 5000.

## Ask before you run

The script **prompts for the database target** every time, so a clean rebuild never silently lands on the
wrong database. Two questions, each accepting the default on Enter:

```
Provider - PostgreSql or SqlServer (default: PostgreSql)
Database name (default: FHIRBridge_v2)
```

Provider input is fuzzy-matched (`pg`, `postgres`, `postgresql` → PostgreSql; `sql`, `mssql`, `sql server`
→ SqlServer). Pass `-NoPrompt` for unattended runs, or set values explicitly with
`-Provider` / `-DatabaseName` (the prompts still appear unless `-NoPrompt` is given, pre-filled with
whatever you passed).

The chosen target is injected at launch as `ConnectionStrings__FHIRBridgeDb` and `Database__Provider`
environment variables, which override `appsettings.Development.json` at runtime — **no committed config
file is edited**.

## Ground truth for this repo

| Piece | Value |
|---|---|
| App database (default) | container `fhirbridge-controlplane-pg` → `localhost:5434`, db `FHIRBridge_v2`, user `postgres` |
| Connection string | `Host=localhost;Port=5434;Database=<name>;Username=postgres;` |
| SQL Server alternative | container `fhirbridge-controlplane-sql` → `localhost:1433`, user `sa` |
| Provider switch | `Database:Provider` = `PostgreSql` or `SqlServer` |
| API | `src/Api/FHIRBridge.Api` → http://localhost:5000 (Swagger at `/swagger`) |
| Worker | `src/Worker/FHIRBridge.Worker` (no HTTP port) |
| Portal | `portal/` → `ng serve` → http://localhost:4200 |
| Migrations | Applied automatically by the API at startup (`dbContext.Database.Migrate()` in `Program.cs`) — do **not** run `dotnet ef database update` separately |

Both hosts must run with `ASPNETCORE_ENVIRONMENT=Development` / `DOTNET_ENVIRONMENT=Development`, otherwise
they fall back to the SQL Server defaults in `appsettings.json` instead of Postgres.

## How to run it

Run the driver script from the repo root:

```powershell
powershell -ExecutionPolicy Bypass -File .claude/skills/runsegue/scripts/runsegue.ps1
```

Useful switches:

- `-Provider PostgreSql|SqlServer` — pre-select the engine
- `-DatabaseName <name>` — pre-select the database
- `-NoPrompt` — skip the two questions and use the defaults/passed values (for unattended runs)
- `-SkipPortal` — API + Worker only
- `-SkipWorker` — API + portal only
- `-NoClean` — incremental build instead of wiping `bin`/`obj` (much faster for a quick restart)
- `-StopOnly` — stop everything and exit, launching nothing
- `-DbPort` / `-DbUser` / `-DbPassword` — override connection details if the container was remapped

The script is the source of truth for the sequence. Do not hand-roll the steps; invoke the script.

## What the script does, in order

0. **Ask** — prompts for provider and database name (skipped with `-NoPrompt` or `-StopOnly`).
1. **Stop** — kills `FHIRBridge.Api`, `FHIRBridge.Worker` and an `ng serve` for this repo's portal, then
   force-frees TCP ports 5000 and 4200 for any straggler. Process matching is deliberately narrow (the app's
   own hosts only) so it never kills unrelated `dotnet`/`node` work — including the session running it.
2. **Verify the database** — checks the container is up and accepting connections (`pg_isready`, or
   `sqlcmd SELECT 1`). If it is not running, brings it up via `docker compose` and waits. Then ensures the
   named database **exists**, creating it if absent — the API's startup migration builds the schema, not the
   database.
3. **Clean** — removes `bin/` and `obj/` across `src/` and `tests/`, plus `portal/.angular/cache`
   (skipped under `-NoClean`).
4. **Build** — `dotnet restore` then `dotnet build FHIRBridge.sln -c Debug`. A build failure aborts the run;
   nothing is launched on a red build.
5. **Portal deps** — runs `npm install` in `portal/` only when `node_modules` is missing or `package-lock.json`
   is newer than it.
6. **Launch** — starts all three in the background, each with stdout/stderr redirected to
   `logs/runsegue/` (`api.log`, `worker.log`, `portal.log`). The API and Worker run from their built
   assemblies under `bin/Debug`, not via `dotnet run` (see gotchas). The chosen connection string and
   provider are injected as environment variables.
7. **Health check** — polls `http://localhost:5000/swagger/index.html` (then `/health`) and
   `http://localhost:4200` until each answers or the timeout elapses, then prints a status table with PIDs.

## After running

Report back to the user:

- The status table (API / Worker / Portal — running or failed, with PIDs)
- The URLs: API http://localhost:5000/swagger, portal http://localhost:4200, Seq http://localhost:5341
- On any failure, `tail` the relevant log under `logs/runsegue/` and surface the actual error rather than
  just saying it failed

## Gotchas worth knowing

- **The build passes `-p:UseAppHost=false`.** On a machine where an EDR/security policy blocks writing new
  `.exe` files under `bin\Debug` (observed on at least one dev box here), MSBuild's apphost-copy step fails
  with `MSB3021: ... Access to the path ... FHIRBridge.Api.exe is denied` even though the folder is otherwise
  writable — a plain `New-Item`/`Copy-Item` of a `.exe` there fails identically outside MSBuild, confirming
  it isn't a project/build bug. Skipping the native apphost sidesteps the write entirely; the script already
  launches hosts via `dotnet <dll>` when no `.exe` is present (`Get-BuiltAssembly`'s extension fallback), so
  this changes nothing about how the hosts start. If you ever see that MSB3021 again from a hand-rolled build
  outside this script, add the same flag rather than trying to grant the process write access.
- **Two Postgres containers look alike.** `fhirbridge-controlplane-pg` is the one actually published on
  host port **5434** and holding the dev data (`FHIRBridge`, `FHIRBridge_v2`, `ErrorLogger`). The
  compose-defined `fhirbridge-controlplane-postgres` also runs but does **not** publish 5434. The script
  targets `-pg` and falls back to the compose container only if `-pg` is absent. Running `docker exec`
  against the wrong one makes a database that exists look missing.
- **Port 5434, not 5432.** `5432` is the HAPI FHIR source Postgres; `5433` is the *output* destination
  Postgres. The app database is `5434`. Pointing the app at the wrong one produces confusing
  "relation does not exist" errors.
- **Postgres folds unquoted identifiers to lower case.** `CREATE DATABASE FHIRBridge_v2` yields
  `fhirbridge_v2`. The script double-quotes the name on create and matches with `ILIKE` on the existence
  check; an exact `datname = '<MixedCase>'` comparison silently misses databases that do exist.
- **A password is required over TCP.** The container enforces SCRAM-SHA-256. The committed
  `appsettings.Development.json` omits the password (it only works via container-local trust auth), so
  connecting from the host without one fails with *"No password has been provided"*. The script supplies
  `Your_password123` by default; override with `-DbPassword`.
- **`/health` returns 503 locally — that is expected and not a startup failure.** The Key Vault health
  check cannot resolve `fhirbridge-health-probe` from vault `fhirbridge-kv-test01` on a dev machine, so the
  aggregate health status is Unhealthy while the API serves requests normally. Readiness is therefore
  probed via `/swagger/index.html` first. Use Swagger, not `/health`, to judge whether the API is up.
- **Never launch the hosts with `dotnet run`.** It starts the real host as a *child* process and exits,
  so the handle reports "exited" while the app is actually running, and killing that handle orphans the
  app on port 5000. The script resolves and launches the built assembly under `bin/Debug` directly.
- **The API takes a while to boot** (roughly a minute on a cold start, longer after a clean build).
  The default health timeout is 180 s; raise it with `-HealthTimeoutSeconds` on slow machines.
- **A failed startup still holds the port.** If the API crashes during migration it can leave the socket in
  `TIME_WAIT`; the script's port-free step handles this, so rerun the skill rather than manually hunting PIDs.
- **Migration failures are the usual first-run error.** They surface in `logs/runsegue/api.log`, not in the
  console. If the schema is wedged, `scripts/reset-to-first-setup.ps1` exists to reset local data.
- **Do not `dotnet run` the API and Worker in the foreground** — they never exit, and will block the session.
  The script backgrounds them deliberately.
