# Deploying to the Windows VM

One-time setup on the target VM, then every deploy is just clicking **Run workflow** on the
[`Deploy`](../../.github/workflows/deploy.yml) workflow in the GitHub Actions tab.

This matches the Kestrel-native / YARP Gateway architecture already hand-validated on the VM — see
[Documents/Kestrel-Gateway-Deployment-Setup.html](../../Documents/Kestrel-Gateway-Deployment-Setup.html)
for the full story (including every issue hit and how it was diagnosed). Summary:

- **FHIRBridge.Gateway** is the *only* public-facing process — binds ports 80/443, reverse-proxies
  `/api/**` and `/swagger/**` to the Api, and serves the Angular portal's static build directly for
  everything else (with SPA fallback to `index.html`).
- **FHIRBridge.Api** binds loopback-only (`127.0.0.1:5000`) — never reachable from outside the VM.
- **FHIRBridge.Worker** runs the scheduled dispatcher / pipeline processor / HL7 MLLP listener,
  also internal-only.
- **Demo_TestApp** is a standalone third-party demo client (a separate app entirely, used to show
  a customer that FHIRBridge can be called from an outside application). Its backend serves its
  own frontend directly (same origin), but calls FHIRBridge's Gateway *cross-origin* — unlike the
  main Portal, this one genuinely needs CORS, and its target URL is baked into its frontend at
  build time (see step 4).
- All four run as Windows Services.

The VM is not reachable from the internet, so GitHub Actions can't SSH/WinRM into it. Instead the
VM runs a **self-hosted GitHub Actions runner** that polls GitHub outbound — no inbound firewall
rule needed at all.

## 1. Prerequisites on the VM

- **.NET 9 ASP.NET Core Runtime** *and* the plain **.NET 9 Runtime** (`dotnet-runtime-9.0.x-win-x64.exe`)
  — installing only the ASP.NET Core Runtime installer has, in practice, sometimes not brought its
  `Microsoft.NETCore.App` dependency along. Install both explicitly. The publish is
  framework-dependent (not self-contained) to keep the deploy artifact small.
- If IIS is pre-installed on the VM image (common on Azure Windows Server images), **disable it** —
  it auto-binds port 80 and will silently block the Gateway from starting:
  ```powershell
  Stop-Service W3SVC -Force
  Set-Service W3SVC -StartupType Disabled
  ```
- PowerShell 5.1+ (built into Windows Server).
- Outbound HTTPS access to `github.com` / `*.actions.githubusercontent.com`.

## 2. Install the self-hosted runner

In the repo: **Settings → Actions → Runners → New self-hosted runner**, choose Windows, and follow
the generated `config.cmd` command — it includes a short-lived registration token, so copy the
exact command GitHub shows you rather than reusing this one. When prompted for labels, add
`fhirbridge-vm` (the workflow targets this label).

```powershell
# From an elevated PowerShell prompt on the VM, in the folder where you extracted the runner:
.\config.cmd --url https://github.com/<org>/<repo> --token <TOKEN_FROM_GITHUB_UI> --labels fhirbridge-vm

# Install it as a Windows service so it survives reboots and starts automatically:
.\svc.cmd install
.\svc.cmd start
```

Verify it shows up as **Idle** under Settings → Actions → Runners before continuing.

## 3. Self-signed certificate (no domain registered yet)

The Gateway terminates TLS. Until a real domain + Let's Encrypt cert (via the already-staged
`win-acme` tool) is set up, generate a self-signed cert once:

```powershell
$cert = New-SelfSignedCertificate `
  -DnsName "localhost", "<server-hostname>", "<server-lan-ip>" `
  -CertStoreLocation "cert:\LocalMachine\My" `
  -NotAfter (Get-Date).AddYears(5)

$pwd = ConvertTo-SecureString -String "<pfx-password>" -Force -AsPlainText
New-Item -ItemType Directory -Path "C:\FHIRBridge-certs" -Force
Export-PfxCertificate -Cert $cert -FilePath "C:\FHIRBridge-certs\gateway.pfx" -Password $pwd
```

The `-DnsName` list must include whatever hostname/IP ends up in the browser's address bar, or
you'll get a name-mismatch warning stacked on top of the expected self-signed one.

## 4. Provision production secrets (once per service, never touched by CI)

`appsettings.Production.json` is deliberately **not** part of the build artifact — the deploy
script excludes it from its copy/mirror step so it's safe to hand-edit on the VM without CI ever
overwriting it. Create these once:

```
C:\inetpub\wwwroot\fhirbridge-api\appsettings.Production.json
C:\inetpub\wwwroot\fhirbridge-worker\appsettings.Production.json
C:\inetpub\wwwroot\fhirbridge-gateway\appsettings.Production.json
C:\inetpub\wwwroot\fhirbridge-demo\appsettings.Production.json
```

**Api** (`fhirbridge-api\appsettings.Production.json`):
```json
{
  "ConnectionStrings": { "FHIRBridgeDb": "Server=<sql-host>;Database=FHIRBridge;User Id=...;Password=...;TrustServerCertificate=True;Encrypt=True" },
  "Authentication": { "SigningKey": "<long random secret>" },
  "DataProtection": { "KeyRingPath": "C:\\inetpub\\dataprotection-keys" },
  "Swagger": { "Enabled": true },
  "AllowedHosts": "*"
}
```
`AllowedHosts: "*"` is safe here specifically because the Api is loopback-only — the Gateway is the
only thing that ever talks to it directly. `Swagger:Enabled` can be flipped back to `false` later
without a redeploy (just edit this file and restart the service).

**Gateway** (`fhirbridge-gateway\appsettings.Production.json`):
```json
{
  "ReverseProxy": {
    "Routes": {
      "api-route": { "ClusterId": "api-cluster", "Match": { "Path": "/api/{**catch-all}" } },
      "swagger-route": { "ClusterId": "api-cluster", "Match": { "Path": "/swagger/{**catch-all}" } }
    },
    "Clusters": { "api-cluster": { "Destinations": { "destination1": { "Address": "http://127.0.0.1:5000/" } } } }
  },
  "StaticFiles": { "RootPath": "C:\\inetpub\\wwwroot\\fhirbridge-portal" },
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://0.0.0.0:80" },
      "Https": { "Url": "https://0.0.0.0:443", "Certificate": { "Path": "C:\\FHIRBridge-certs\\gateway.pfx" } }
    }
  },
  "AllowedHosts": "*"
}
```

> **Watch the routes' nesting** — `swagger-route` must sit *inside* `Routes` alongside `api-route`,
> not as a sibling of `Routes`/`Clusters` under `ReverseProxy`. A misplaced brace here fails
> silently (YARP just never matches `/swagger/**`, no error logged).
>
> **Exact filenames only** — ASP.NET Core auto-loads `appsettings.json` and
> `appsettings.{Environment}.json` and nothing else. A typo'd filename is silently never loaded.

**Worker** (`fhirbridge-worker\appsettings.Production.json`): same `ConnectionStrings:FHIRBridgeDb`
and `DataProtection:KeyRingPath` as the Api (they share the same database and key ring).

**Demo** (`fhirbridge-demo\appsettings.Production.json`) — a separate database, own SQL Server
instance is fine, but a new database (not `FHIRBridge`) on the same instance is simplest:
```json
{
  "ConnectionStrings": { "Default": "Server=<sql-host>;Database=HealthAppDb;User Id=...;Password=...;TrustServerCertificate=True;Encrypt=True" },
  "AllowedFrontendOrigin": "http://localhost",
  "AllowedHosts": "*"
}
```
`AllowedFrontendOrigin` is effectively unused in production (the backend serves its own frontend,
same-origin) — leaving it at its dev default is harmless.

The Gateway's PFX password is deliberately **not** in its `appsettings.Production.json` — set it via
an environment variable on the service instead (step 6), so it isn't sitting in plaintext config.

Grant the service account read/write on `C:\inetpub\dataprotection-keys` and `C:\FHIRBridge-certs`.

### Registering the Demo app with FHIRBridge (one-time, per target, after its first deploy)

The Demo app's frontend calls FHIRBridge's Gateway directly from the browser — a genuinely
cross-origin request, since it's hosted at a different port. Sign in to the FHIRBridge portal as a
SuperAdmin and add the Demo app's own origin (e.g. `https://<vm-hostname>:5500` for production,
`https://<vm-hostname>:5600` for test) under **CORS Origins** — this takes effect live, no restart
needed, no redeploy needed. Until this is added, the Demo app's "Connect Get Data" flow will fail
with a CORS error in the browser console; everything else in the Demo app (login, its own
database) works regardless, since only that one call is cross-origin.

## 5. Testing CI/CD without touching your manual deployment

The **Run workflow** dialog asks for a `target`: `production` or `test`.

- **`production`** uses the exact same folders, service names, and ports as the manual deployment
  you already did (`C:\inetpub\wwwroot\fhirbridge-*`, ports 80/443/5000). Running this will stop
  and replace those same services.
- **`test`** deploys a fully separate, parallel copy that never touches the production one:

  | | production | test |
  |---|---|---|
  | Folders | `C:\inetpub\wwwroot\fhirbridge-{api,gateway,worker,portal,demo}` | `C:\inetpub\wwwroot\test\fhirbridge-{api,gateway,worker,portal,demo}` |
  | Service names | `FHIRBridge.Api` / `.Gateway` / `.Worker` / `.Demo` | `FHIRBridge.Api.Test` / `.Gateway.Test` / `.Worker.Test` / `.Demo.Test` |
  | Api port (loopback) | `127.0.0.1:5000` | `127.0.0.1:5100` |
  | Gateway ports (public) | `80` / `443` | `9080` / `9443` |
  | Demo app port (public) | `5500` | `5600` |

  These live as `-DeployRoot`, `-ApiServiceName`, etc. arguments in `deploy.yml`'s `deploy` job —
  change the numbers there if `9080`/`9443`/`5100`/`5600` collide with something else already
  running on the VM.

  The Demo app's frontend is rebuilt for each target with its cross-origin FHIRBridge URL baked
  in — that's what the **Run workflow** dialog's `vm_hostname` field is for (see step 6). Get this
  wrong and the Demo app's "Connect Get Data" button will fail with either a CORS error (wrong host)
  or a connection error (wrong port) — it won't silently point at the other target.

Before running with `test` for the first time, provision its own config the same way you did for
production (step 4), just under the `test` paths and ports instead:

`C:\inetpub\wwwroot\test\fhirbridge-api\appsettings.Production.json` — same as production's, plus
whatever DB you want the test instance to use (the same database is fine for a first smoke test).

`C:\inetpub\wwwroot\test\fhirbridge-gateway\appsettings.Production.json`:
```json
{
  "ReverseProxy": {
    "Routes": {
      "api-route": { "ClusterId": "api-cluster", "Match": { "Path": "/api/{**catch-all}" } },
      "swagger-route": { "ClusterId": "api-cluster", "Match": { "Path": "/swagger/{**catch-all}" } }
    },
    "Clusters": { "api-cluster": { "Destinations": { "destination1": { "Address": "http://127.0.0.1:5100/" } } } }
  },
  "StaticFiles": { "RootPath": "C:\\inetpub\\wwwroot\\test\\fhirbridge-portal" },
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://0.0.0.0:9080" },
      "Https": { "Url": "https://0.0.0.0:9443", "Certificate": { "Path": "C:\\FHIRBridge-certs\\gateway.pfx" } }
    }
  },
  "AllowedHosts": "*"
}
```
The same self-signed cert from step 3 works fine here too — a cert isn't tied to a port.

`C:\inetpub\wwwroot\test\fhirbridge-demo\appsettings.Production.json` — same shape as production's,
pointed at its own database (e.g. `HealthAppDb_Test`) so test runs never touch production demo data:
```json
{
  "ConnectionStrings": { "Default": "Server=<sql-host>;Database=HealthAppDb_Test;User Id=...;Password=...;TrustServerCertificate=True;Encrypt=True" },
  "AllowedFrontendOrigin": "http://localhost",
  "AllowedHosts": "*"
}
```
Remember to also add this instance's own origin (`https://<vm-hostname>:5600`) to FHIRBridge's
CORS Origins as a SuperAdmin, same as production (see the note in step 4) — production's and
test's origins are two separate entries, since they're two different ports.

Since the very first deploy to a target creates its Windows Services (via `New-Service`), the
loopback URL/environment name for `FHIRBridge.Api.Test` and the cert password for
`FHIRBridge.Gateway.Test` still need to be set once via the registry, same as step 6 below but
against the `.Test` service names and `5100`/cert-password values.

If the test Gateway needs to be reachable from other machines on the LAN (not just from the VM
itself), open `9080`/`9443` in the firewall the same way `80`/`443` were opened for production.

## 6. First deploy

Go to the **Actions** tab → **Deploy** workflow → **Run workflow**. It asks for two things:

- `target`: `production` or `test` (see step 5)
- `vm_hostname`: the VM's public hostname or IP, **no scheme, no port** (e.g. `demo.example.com` or
  `20.1.2.3`) — used only to build the Demo app's cross-origin FHIRBridge URL. Get this wrong and
  only the Demo app is affected (see step 5); Api/Gateway/Worker/Portal don't use it.

This:

1. Builds the Api, Gateway, Worker, Demo backend (`dotnet publish`, win-x64, framework-dependent)
   and both Angular apps — the main portal (`ng build --configuration production`, always) and the
   Demo app's frontend (`--configuration production` or `--configuration test` matching `target`,
   after substituting `vm_hostname` into its compiled-in FHIRBridge URL) — on a GitHub-hosted runner.
2. Ships everything to the self-hosted runner on the VM, which mirrors each into the chosen
   target's folders (preserving each service's `appsettings.Production.json`), creates the four
   Windows Services if they don't exist yet, starts Api and Worker first, mirrors the Portal's
   static files, starts the Gateway, then starts the Demo app last (its frontend rides along
   inside its own folder, no separate static-files step needed).
3. Health-checks the Api directly over loopback, confirms the Gateway itself answers on plain HTTP,
   and confirms the Demo app answers too — against whichever ports the chosen target uses.

The very first run against a given target creates all four of its services for you; after that
it's just start/stop/replace.

## 7. Register the services (first run only, if the workflow's own New-Service ever needs redoing)

The deploy script creates services automatically on first deploy. If you ever need to do it by
hand (e.g. after a `sc.exe delete`), the environment variables below are what make loopback
binding, the environment name, and the cert password work — set once via the registry, not
something the deploy script touches on every run:

```powershell
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\FHIRBridge.Api" -Name Environment -Value ([string[]]@(
  "ASPNETCORE_ENVIRONMENT=Production",
  "ASPNETCORE_URLS=http://127.0.0.1:5000"
)) -Type MultiString

Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\FHIRBridge.Gateway" -Name Environment -Value ([string[]]@(
  "ASPNETCORE_ENVIRONMENT=Production",
  "Kestrel__Endpoints__Https__Certificate__Password=<pfx-password>"
)) -Type MultiString

Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\FHIRBridge.Worker" -Name Environment -Value ([string[]]@(
  "ASPNETCORE_ENVIRONMENT=Production"
)) -Type MultiString

Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\FHIRBridge.Demo" -Name Environment -Value ([string[]]@(
  "ASPNETCORE_ENVIRONMENT=Production",
  "ASPNETCORE_URLS=http://0.0.0.0:5500",
  "DEMOAPP_PORTAL_PATH=C:\inetpub\wwwroot\fhirbridge-demo\portal"
)) -Type MultiString

Restart-Service FHIRBridge.Api, FHIRBridge.Worker, FHIRBridge.Gateway, FHIRBridge.Demo
```

`DEMOAPP_PORTAL_PATH` must be an **absolute** path — a Windows Service's working directory isn't
guaranteed to be its own exe's folder, so the app's relative `portal` default can resolve to the
wrong place (e.g. `C:\Windows\System32\portal`) if left unset. For the `test` target, use
`FHIRBridge.Demo.Test`, port `5600`, and `C:\inetpub\wwwroot\test\fhirbridge-demo\portal`.

`Set-ItemProperty` needs an explicit `[string[]]` cast — PowerShell otherwise builds a generic
`Object[]`, which `RegistryKey.SetValue` rejects for a `REG_MULTI_SZ` value.

## 8. First-run setup

The database schema self-provisions on boot (EF Core migrations run automatically, and the RBAC
bootstrapper seeds the permission catalog idempotently every startup) — but **no admin user is
seeded from config**. Create the first SuperAdmin interactively at:

```
https://<host>/setup
```

This route is one-shot — once any user exists, it locks itself out.

## 9. Optional: require an approval click before deploying

By default `workflow_dispatch` already requires a manual click to start the workflow. If you also
want a **second** approval gate right before the deploy job touches the VM: **Settings →
Environments → New environment → `production`**, add required reviewers. The `deploy` job in
`deploy.yml` already targets the `production` environment, so this takes effect immediately
without any workflow changes.

## Rolling back

Re-run the workflow from an earlier commit (Actions → Deploy → Run workflow → choose the branch/tag/SHA
to build from), or manually re-run `Deploy-FHIRBridge.ps1` against a previously-downloaded artifact
zip you've kept around. There's no automatic artifact retention beyond GitHub's 14-day default —
increase `retention-days` in `deploy.yml` if you want a longer rollback window.
