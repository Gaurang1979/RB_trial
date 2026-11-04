# Remote service

Lets Sundaram's service and OEM engineers work on a machine's software from any browser, with no
AnyDesk, TeamViewer or VNC. The machine connects outbound to `support.sundaramtech.com`. The engineer
signs in there, then on the machine with today's Service code.

They can download logs, images, reports and a diagnostics bundle, upload job files, run named
actions, and read data views. Every step goes into the machine's tamper-evident log, and each session
reaches ERPNext as a Remote Service Log with a draft Timesheet. Direct database edits are never
possible.

It is a reusable module: any application hosts the agent and plugs in its own folders, actions and
data views. Vision System is the first host.

- **Design:** [docs/DESIGN.md](docs/DESIGN.md)
- **Protocol** (for agents in other languages): [docs/PROTOCOL.md](docs/PROTOCOL.md)
- **Improvements from comparable open-source projects:** [docs/COMPARABLE-PROJECTS.md](docs/COMPARABLE-PROJECTS.md)

| Folder | What | Tests |
| --- | --- | --- |
| `dotnet/RemoteService.Agent/` | .NET 10 library the host application runs (link, sessions, API, console page) | `dotnet test dotnet/RemoteService.Agent.Tests` |
| `dotnet/RemoteService.SampleHost/` | A small console host showing how an application uses the agent | — |
| `dotnet/RemoteService.PackageTool/` | `rs-package`: make and check signed software packages (vendor's signing PC only) | covered by the agent tests |
| `relay/` | The relay at support.sundaramtech.com (Python, FastAPI) | `relay/.venv/bin/python -m pytest relay/tests` |
| `erpnext/remote_service/` | ERPNext app: Remote Service Log, settings, `record_session` | `python -m pytest erpnext/tests` |

## Hosting the agent

```csharp
var agent = new RemoteAgent(new RemoteServiceOptions
{
    Relay = "wss://support.sundaramtech.com/link", Machine = "M-1001",
    Token = secrets.Get("VS_SUPPORT_LINK_TOKEN"),          // from the host's protected store, never a settings file
    LocalWeb = new Uri("https://127.0.0.1:8443"),          // the host's own pages, if it has any
    DataFolder = protectedDataFolder,                      // versions installed, log entries not yet confirmed by the relay
}, new RemoteServiceHost
{
    Code = myServiceCodeCheck, Audit = myLedger,           // required; a ledger that also implements IAuditChain sends its head
    Machine = myLineState, Operator = myConfirmDialog,     // without these, nothing that needs them is allowed
    Roots = [new FileRoot("logs", logFolder), new FileRoot("jobs", jobFolder, Writable: true, Extensions: [".job"])],
    Actions = [new RemoteAction("restart-reader", "Restart the reader", "…", RestartReader)],
    Views = [new DataView("ng-today", "NG parts today", ["camera"], QueryNgToday)],
    Diagnostics = WriteBundle,
    Packages = new FolderPackageInstaller(programRoot, "Barcode Reader", [shippedPublicKeyPem]),   // or the host's own installer
});
await agent.RunAsync(stoppingToken);                      // in a BackgroundService
// the host's "Remote service active" badge: agent.Sessions.Badge (memory only, safe on any thread), agent.Sessions.Changed
```

The agent writes every remote event to `Audit`. If the audit store cannot write, the action is refused.

## Software packages

```bash
rs-package keygen signing-private.pem package-public.pem     # once, on the signing PC; ship package-public.pem
```

```bash
rs-package pack build/out signing-private.pem --product "Barcode Reader" --version 2.5.0 --out reader-2.5.0.rspkg
```

The engineer uploads the package on the console's Software tab. The machine checks the signature and
every file, then installs it once the machine is stopped and the operator confirms. "Go back to the
previous version" undoes it.

## Running the relay

```bash
cd relay && python3 -m venv .venv && .venv/bin/pip install -r requirements.txt
```

```bash
.venv/bin/python -m remote_service_relay add-machine M-1001 "Barcode Line 1" --site Pune --licence LIC-0042
```

```bash
.venv/bin/python -m remote_service_relay add-user anita "Anita" --machines "M-1001" --role oem --employee HR-EMP-0007
```

```bash
ERPNEXT_URL=https://erp.sundaramtech.com ERPNEXT_API_KEY=… ERPNEXT_API_SECRET=… .venv/bin/python -m remote_service_relay serve --port 8090
```

In production the relay runs behind a TLS proxy (Caddy or nginx) on the same host. The proxy sets
`X-Forwarded-Proto`.

**Before it goes live**, each machine needs its own origin (DESIGN.md section 6).
