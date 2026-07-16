# Deploying to the Windows VM

One-time setup on the target VM, then every deploy is just clicking **Run workflow** on the
[`Deploy`](../../.github/workflows/deploy.yml) workflow in the GitHub Actions tab.

The VM is not reachable from the internet, so GitHub Actions can't SSH/WinRM into it. Instead the
VM runs a **self-hosted GitHub Actions runner** that polls GitHub outbound — no inbound firewall
rule needed at all.

## 1. Prerequisites on the VM

- [ASP.NET Core 9 Hosting Bundle / Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) installed
  (the publish is framework-dependent, not self-contained, to keep the deploy artifact small).
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

## 3. Provision production secrets (once, never touched by CI)

`appsettings.Production.json` is deliberately **not** part of the build artifact — it's excluded
from the deploy script's copy/mirror step so it's safe to edit directly on the VM without CI ever
overwriting it. Create these once:

```
C:\FHIRBridge\Api\appsettings.Production.json
C:\FHIRBridge\Worker\appsettings.Production.json
```

Populate the real values for `ConnectionStrings:FHIRBridgeDb`, `Authentication:SigningKey`,
`DataProtection:KeyRingPath` (point this at a persistent folder, e.g. `C:\FHIRBridge\keys`, so
OAuth/launch tokens survive redeploys and restarts), `Portal:AllowedOrigins`, and `AllowedHosts`
(must include the VM's hostname/IP, not just `localhost`) — see the `Key Configuration Sections`
table in the repo's `CLAUDE.md` for what each of these does.

Also set the Kestrel bind address/port if the default (`http://localhost:5000`) isn't right for
this VM, either in `appsettings.Production.json` under `Kestrel:Endpoints`, or via an
`ASPNETCORE_URLS` environment variable on the two Windows Services once created (**Environment**
tab in `services.msc`, or `sc.exe` / `Set-Service` scripting — this is a one-time step per service,
not something the deploy script touches).

## 4. First deploy

Go to the **Actions** tab → **Deploy** workflow → **Run workflow**. This:

1. Builds the Api and Worker (`dotnet publish`, win-x64, framework-dependent) and the Angular
   portal (`ng build --configuration production`) on a GitHub-hosted runner.
2. Copies the portal's build output into the Api's `wwwroot` — the Api serves the portal directly
   over the same Kestrel process/port, no separate web server needed.
3. Ships the combined artifact to the self-hosted runner on the VM, which stops the two services,
   mirrors the new files into `C:\FHIRBridge\Api` and `C:\FHIRBridge\Worker` (preserving
   `appsettings.Production.json`), creates the services if they don't exist yet, starts them, and
   polls `/health` until the Api responds.

The very first run creates the `FHIRBridge.Api` and `FHIRBridge.Worker` Windows services for you;
after that it's just start/stop/replace.

## 5. Optional: require an approval click before deploying

By default `workflow_dispatch` already requires a manual click to start the workflow. If you also
want a **second** approval gate right before the deploy job touches the VM (e.g. so a different
person can review before build → deploy proceeds): **Settings → Environments → New environment →
`production`**, add required reviewers. The `deploy` job in `deploy.yml` already targets the
`production` environment, so this takes effect immediately without any workflow changes.

## Rolling back

Re-run the workflow from an earlier commit (Actions → Deploy → Run workflow → choose the branch/tag/SHA
to build from), or manually re-run `Deploy-FHIRBridge.ps1` against a previously-downloaded artifact
zip you've kept around. There's no automatic artifact retention beyond GitHub's 14-day default —
increase `retention-days` in `deploy.yml` if you want a longer rollback window.
