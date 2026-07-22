# Deploying to the Windows Server

One-time setup on the target server, then every deploy is either clicking **Run workflow** on the
[`Deploy`](../../.github/workflows/deploy.yml) workflow in the GitHub Actions tab, or pushing a
commit whose message is exactly `Publish` to a branch listed in that workflow's `on.push.branches`
(currently `main` — edit that list to change it).

The server is not reachable from the internet, so GitHub Actions can't SSH/WinRM into it. Instead
the server runs a **self-hosted GitHub Actions runner** that polls GitHub outbound — no inbound
firewall rule needed for that.

## Topology

Nothing here is IIS-hosted — every app is a standalone Kestrel process running as a Windows Service
(the same published output can later run under systemd on Linux with no code change, since each app
calls both `.UseWindowsService()`/`AddWindowsService()` and `.UseSystemd()`/`AddSystemd()`). The
`C:\inetpub\wwwroot\...` folder names are just a reused convention, not an IIS site path.

| Service name | Folder | Bind | Role |
|---|---|---|---|
| `FHIRBridge.Api` | `C:\inetpub\wwwroot\fhirbridge-api` | `127.0.0.1:<PROD_API_PORT>` (loopback only) | The API; not reachable from outside the server |
| `FHIRBridge.Gateway` | `C:\inetpub\wwwroot\fhirbridge-gateway` | `0.0.0.0:<PROD_GATEWAY_PORT>` (public) | YARP reverse proxy — routes `/api/**` and `/swagger/**` to the Api, serves the portal for everything else |
| *(no service)* | `C:\inetpub\wwwroot\fhirbridge-portal` | — | Angular portal build; static files only, served by Gateway |
| `FHIRBridge.Worker` | `C:\inetpub\wwwroot\fhirbridge-worker` | none (no HTTP endpoint) | Background scheduler/pipeline processor |
| `FHIRBridge.DemoApp` | `C:\inetpub\wwwroot\demoapp-api` | `0.0.0.0:<PROD_DEMOAPI_PORT>` (public) | Demo app backend — serves its own frontend, same origin |
| *(no service)* | `C:\inetpub\wwwroot\demoapp-portal` | — | Demo app Angular build; static files only, served by DemoApp |

`PROD_API_PORT`, `PROD_GATEWAY_PORT`, `PROD_DEMOAPI_PORT` are **GitHub Environment variables** (see
step 4) — pick any free ports on this host, there is no hardcoded default (a shared default port,
like Kestrel's own built-in `5000`, is exactly what caused this to silently collide with an
unrelated application on the box before). Both public entry points currently serve plain HTTP — TLS
termination is a deliberate later step, not yet configured.

## 1. Prerequisites on the server

- **ASP.NET Core 9.0 Runtime** (not the Hosting Bundle — that only matters for IIS/ANCM, which isn't
  used here): https://dotnet.microsoft.com/download/dotnet/9.0
- PowerShell 5.1+ (built into Windows Server).
- Outbound HTTPS access to `github.com` / `*.actions.githubusercontent.com`.
- Confirm the ports you intend to use for `PROD_API_PORT`/`PROD_GATEWAY_PORT`/`PROD_DEMOAPI_PORT`
  (step 4) aren't already bound by another application on this host —
  `Get-NetTCPConnection -LocalPort <port> -ErrorAction SilentlyContinue` (or `netstat -ano | findstr :<port>`).
- Inbound firewall rules opened for the Gateway and Demo app ports — this is a one-time manual step
  (`New-NetFirewallRule`), not something CI touches:
  ```powershell
  New-NetFirewallRule -DisplayName "FHIRBridge Gateway" -Direction Inbound -LocalPort <PROD_GATEWAY_PORT> -Protocol TCP -Action Allow
  New-NetFirewallRule -DisplayName "FHIRBridge Demo App" -Direction Inbound -LocalPort <PROD_DEMOAPI_PORT> -Protocol TCP -Action Allow
  ```

## 2. Install the self-hosted runner

In the repo: **Settings → Actions → Runners → New self-hosted runner**, choose Windows, and follow
the generated `config.cmd` command — it includes a short-lived registration token, so copy the
exact command GitHub shows you rather than reusing this one. When prompted for labels, add
`fhirbridge-vm` (the workflow targets this label).

```powershell
# From an elevated PowerShell prompt on the server, in the folder where you extracted the runner:
.\config.cmd --url https://github.com/<org>/<repo> --token <TOKEN_FROM_GITHUB_UI> --labels fhirbridge-vm

# Install it as a Windows service so it survives reboots and starts automatically:
.\svc.cmd install
.\svc.cmd start
```

Verify it shows up as **Idle** under Settings → Actions → Runners before continuing.

## 3. Provision production secrets (once, never touched by CI)

Every service's config files (`appsettings.Production.json` etc.) are deliberately **not** part of
the build artifact. Instead they live under a separate config root,
`C:\inetpub\FHIRBridge_Configurations`, mirroring the same per-app folder names used under
`C:\inetpub\wwwroot`:

```
C:\inetpub\FHIRBridge_Configurations\fhirbridge-api\appsettings.Production.json
C:\inetpub\FHIRBridge_Configurations\fhirbridge-gateway\appsettings.Production.json
C:\inetpub\FHIRBridge_Configurations\fhirbridge-worker\appsettings.Production.json
C:\inetpub\FHIRBridge_Configurations\demoapp-api\appsettings.Production.json
C:\inetpub\FHIRBridge_Configurations\fhirbridge-portal\
C:\inetpub\FHIRBridge_Configurations\demoapp-portal\
```

After every deploy mirrors an app's published files into its `C:\inetpub\wwwroot\...` folder,
`Deploy-FHIRBridge.ps1` copies that app's `ConfigRoot` subfolder on top (see the script's
`ConfigRoot` parameter). Because config lives in a folder tree the artifact mirror never touches,
there's no risk of it being deleted or overwritten by a deploy — edit these files directly on the
server at any time; they take effect on that service's next restart.

**`fhirbridge-api`** — populate `ConnectionStrings:FHIRBridgeDb`, `Authentication:SigningKey`,
`DataProtection:KeyRingPath` (point this at a persistent folder, e.g. `C:\FHIRBridge\keys`, so
OAuth/launch tokens survive redeploys and restarts), `Portal:AllowedOrigins` (the public URL clients
use — since Gateway is what the browser actually talks to, this should be the Gateway's public
origin, not the Api's own loopback address), and `AllowedHosts` — see the `Key Configuration
Sections` table in the repo's `CLAUDE.md` for what each of these does.

**`fhirbridge-gateway`** — set `StaticFiles:RootPath` to
`C:\inetpub\wwwroot\fhirbridge-portal` (absolute path, so it doesn't matter what working directory
the service starts in), and `ApiBaseUrl` to `http://127.0.0.1:<PROD_API_PORT>/` (a single flat
setting — `Program.cs` builds the YARP routes/clusters in code and only reads the destination
address from this key). **Mandatory, no default** — `Program.cs` throws at startup outside
Development if this is unset, rather than silently falling back to another environment's Api
address, so this must match whatever `PROD_API_PORT` is set to in step 4.

**`fhirbridge-worker`** — populate `ConnectionStrings:FHIRBridgeDb`, `RuntimeWorker:Enabled`, and
`Messaging:Provider` (`InMemory` / `RabbitMQ` / `AzureServiceBus` — see `CLAUDE.md`), plus
`Hl7MllpOptions` if the MLLP listener is in use.

**`demoapp-api`** — populate `ConnectionStrings:Default` (its own SQL Server database — this is a
separate database from `FHIRBridgeDb`, used only by the demo app) and `AllowedFrontendOrigin` (set
to this app's own public URL, e.g. `http://<server>:<PROD_DEMOAPI_PORT>`, not the portal's origin —
CORS here only applies to any cross-origin caller, since the demo frontend is served same-origin
already).

**`fhirbridge-portal` / `demoapp-portal`** — these Angular builds have no runtime config file today;
their folders under `ConfigRoot` can stay empty. They exist for consistency and in case a
runtime-loaded config file is ever added later.

## 4. Set each service's bind address (GitHub Environment variables — no server-side step)

`Deploy-FHIRBridge.ps1` sets each service's `ASPNETCORE_URLS` (and, for the Demo app,
`DEMOAPP_PORTAL_PATH`) directly on that Windows Service's registry `Environment` value, on
**every** deploy — this used to be a one-time manual step via `services.msc` and silently drifted
back to Kestrel's own built-in default (port 5000) whenever that step was skipped or a service got
recreated. Nothing to touch on the server itself; instead, set the three port numbers once as
**Settings → Environments → `production` → Environment variables** in this repo:

| Variable | Used for |
|---|---|
| `PROD_API_PORT` | `FHIRBridge.Api` → `ASPNETCORE_URLS=http://127.0.0.1:<PROD_API_PORT>` |
| `PROD_GATEWAY_PORT` | `FHIRBridge.Gateway` → `ASPNETCORE_URLS=http://+:<PROD_GATEWAY_PORT>` |
| `PROD_DEMOAPI_PORT` | `FHIRBridge.DemoApp` → `ASPNETCORE_URLS=http://+:<PROD_DEMOAPI_PORT>` |

`FHIRBridge.Worker` has no HTTP endpoint, so no port is needed for it. There is no fallback if
these variables are unset — `deploy.yml` passes them straight through as mandatory parameters to
`Deploy-FHIRBridge.ps1`, and the workflow run fails immediately rather than deploying with a guessed
port. Remember `ApiBaseUrl` in `fhirbridge-gateway`'s config (step 3) must match `PROD_API_PORT`.

## 5. First deploy

Go to the **Actions** tab → **Deploy** workflow → **Run workflow**. This:

1. Builds Api, Gateway, Worker, and the Demo app backend (`dotnet publish`, win-x64,
   framework-dependent) and both Angular frontends (portal, demo) on a GitHub-hosted runner.
2. Ships one combined artifact (6 folders: `Api`, `Gateway`, `Worker`, `DemoApi`, `Portal`,
   `DemoPortal`) to the self-hosted runner on the server.
3. The runner's `Deploy-FHIRBridge.ps1`: mirrors the two static frontend folders first (so
   Gateway/DemoApp pick them up immediately on next start — both only wire up static-file serving if
   the folder already exists when the process starts), overlaying each from its `ConfigRoot`
   subfolder, then for each of the four services: stops it, mirrors the new published files,
   overlays that service's `ConfigRoot` subfolder (`appsettings.Production.json` etc.), creates the
   service if it doesn't exist yet, starts it, and health-checks it (Api via `/health`,
   Gateway/DemoApp via their root URL — Worker has no HTTP endpoint, so it's just checked for
   `Running` status).

The very first run creates all four Windows Services for you; after that it's just
stop/replace/start.

## 6. Optional: require an approval click before deploying

By default `workflow_dispatch` already requires a manual click to start the workflow. If you also
want a **second** approval gate right before the deploy job touches the server (e.g. so a different
person can review before build → deploy proceeds): **Settings → Environments → New environment →
`production`**, add required reviewers. The `deploy` job in `deploy.yml` already targets the
`production` environment, so this takes effect immediately without any workflow changes.

## Rolling back

Re-run the workflow from an earlier commit (Actions → Deploy → Run workflow → choose the branch/tag/SHA
to build from), or manually re-run `Deploy-FHIRBridge.ps1` against a previously-downloaded artifact
zip you've kept around. There's no automatic artifact retention beyond GitHub's 14-day default —
increase `retention-days` in `deploy.yml` if you want a longer rollback window.
