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
- All three run as Windows Services.

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

The Gateway's PFX password is deliberately **not** in its `appsettings.Production.json` — set it via
an environment variable on the service instead (step 6), so it isn't sitting in plaintext config.

Grant the service account read/write on `C:\inetpub\dataprotection-keys` and `C:\FHIRBridge-certs`.

## 5. First deploy

Go to the **Actions** tab → **Deploy** workflow → **Run workflow**. This:

1. Builds the Api, Gateway, Worker (`dotnet publish`, win-x64, framework-dependent) and the Angular
   portal (`ng build --configuration production`) on a GitHub-hosted runner.
2. Ships all four to the self-hosted runner on the VM, which mirrors each into
   `C:\inetpub\wwwroot\fhirbridge-{api,gateway,worker,portal}` (preserving each service's
   `appsettings.Production.json`), creates the three Windows Services if they don't exist yet,
   starts Api and Worker first, mirrors the Portal's static files, then starts the Gateway last.
3. Health-checks the Api directly over loopback (`http://127.0.0.1:5000/health`) and confirms the
   Gateway itself answers on plain HTTP (`http://localhost/`).

The very first run creates all three services for you; after that it's just start/stop/replace.

## 6. Register the services (first run only, if the workflow's own New-Service ever needs redoing)

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

Restart-Service FHIRBridge.Api, FHIRBridge.Worker, FHIRBridge.Gateway
```

`Set-ItemProperty` needs an explicit `[string[]]` cast — PowerShell otherwise builds a generic
`Object[]`, which `RegistryKey.SetValue` rejects for a `REG_MULTI_SZ` value.

## 7. First-run setup

The database schema self-provisions on boot (EF Core migrations run automatically, and the RBAC
bootstrapper seeds the permission catalog idempotently every startup) — but **no admin user is
seeded from config**. Create the first SuperAdmin interactively at:

```
https://<host>/setup
```

This route is one-shot — once any user exists, it locks itself out.

## 8. Optional: require an approval click before deploying

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
