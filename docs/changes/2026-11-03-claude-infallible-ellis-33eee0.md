# Install the licence content-key source in every host that loads models (LineRunner, Dashboard); no key = no inspection, NG and the line told (Req 18.3 item 1, ENC-HOST-01; PROT-01/04/05)

- Status: done
- Session: Claude desktop (content-key-hosts), 2026-11-03
- Pull request: #291
- Section 5 item: new (Pending item of docs/changes/2026-11-01-claude-encrypt-models-at-rest.md: "Install the key source in every host that loads models")
- Components/files: dotnet/VisionCore/Licensing/StoredLicenceKeySource.cs (new), InstallationKeys.cs (`TryLoad`); dotnet/LineRunner/ (Program.cs `--licence`, csproj references VisionCore); dotnet/Dashboard/ (Program.cs, Services/HostLicence.cs new, Services/Station.cs, Services/Options.cs `LicenceFolder`, appsettings.json); dotnet/VisionCore.Tests/StoredLicenceKeySourceTests.cs, dotnet/Dashboard.Tests/StationLicenceTests.cs (new); docs/REQUIREMENTS-VISION-SYSTEM.md 18.3 (ENC-HOST-01 registered)
- Depends on: none

## Overlap (CLAUDE.md step 4)
`scripts/coord.sh overlap`: only `docs/REQUIREMENTS-VISION-SYSTEM.md` is shared, with #273, #284, #288, #289, #290 — each edits another section (this PR adds one bullet under 18.3 item 1). Merge order: either.

## How it works
- **One key source for hosts that do not check the licence** (`VisionCore.Licensing.StoredLicenceKeySource`). LineRunner and the Dashboard station read the licence answer Vision Studio's `LicenceService` keeps on this PC: `licence.json`, `installation-key.bin`, `installation-id.txt` in its data folder. The key is handed out only while the answer is signed (checked with `licence-public.pem` from the **program folder only** — a key in the data folder could be swapped with a forged answer), for this installation, carries a content key this PC unwraps, and is Active or within offline grace. These hosts never call the server, so an overdue check counts as offline: the key goes when the grace ends unless Vision Studio checked again. The file is re-read when it changes, so a renewal reaches a running host. A host never makes an installation key of its own (`InstallationKeys.TryLoad`).
- **Installed before anything is read.** `StoredLicenceKeySource.Install(folder)` sets `ContentProtection.KeySource` at host start, before the product store or a model is touched. No folder set = development PC: nothing installed, plaintext artifacts load as before, an encrypted one still fails closed.
- **No key = no inspection (PROT-01/04/05).** With a licence folder set and the key not available, the host refuses before any product or model loads, so nothing falls back to a plaintext file:
  - **LineRunner** (`--licence <folder>`): prints `NG: ... not started: <reason>` and a `REJECT not started` line, raises plant alarm `licence_content_key` (High) and line state `Fault` with the reason, exits 3. An encrypted product or model that fails to decrypt takes the same path (no unhandled crash).
  - **Dashboard** (`Station:LicenceFolder`): `Station.Start` and `Calibrate` refuse with `... refused (NG): <reason>`; the Live page and `/api/station/start` show it (409), the station error reads `NG: ...`, the audit trail gets `start-refused` / `calibrate-refused`. A `ContentProtectionException` during start (encrypted product or model, no key) is turned into the same refusal.
- Vision Studio and the vs-runtime worker are unchanged: `LicenceService` already installs its own `LicenceContentKeySource`.

## Line safety (requirements section 12, 18.5)
The key is asked for only at start / product load, so a licence that lapses mid-batch takes effect at the next start (PROT-06). Any doubt — no answer, a forged or other-installation answer, no or corrupt content key, missing installation key or program-folder public key, expired grace, clock set back — is no key, and the station does not start; it is NG with the reason and the line is told. The reason texts never contain the key.

## Verified
Tested once on this Mac (2026-11-03):
- **Build** (`dotnet build -c Release`): LineRunner, Dashboard, VisionStudio, Runtime.Host — 0 errors, 0 warnings.
- **Dashboard.Tests**: 16/16 passed, including the new StationLicenceTests 2 (no key: start and calibrate refused NG with the reason, not running, station error `NG:`, audit `start-refused`/`calibrate-refused`; an encrypted product with no key source is refused, not read in the clear).
- **VisionCore.Tests**: the new StoredLicenceKeySourceTests 11 pass (good answer decrypts a model; no answer; forged signature; missing program-folder public key; other installation; empty / corrupt content key; rejected licence; overdue check within grace keeps the key, after grace drops it; a renewal written while running is picked up; a host never creates an installation key; Install without a folder installs nothing), with ContentKeyTests and the other licence tests: 20/20 in that filter. The whole project: 695 passed, 152 failed — every failure is a tool test that needs the native engine libraries (`DllNotFoundException` for `codereader`, `vs_blob`, `vs_measure` ..., or a tool reporting that load error); the engine is not built in this worktree and this PR changes no engine or tool code.
- **LineRunner** run on this Mac from an AnyCPU build of the same code (`-p:PlatformTarget=AnyCPU`; the shipped target is x64 Windows): with `--licence` and no public key, then with a key but no licence answer, it printed the NG reason and `REJECT not started` and exited 3; with no `--licence` and an encrypted product it refused the same way (no crash). The plant alarm path ran with the link off (no token); the camera run was not started.
- **Bench:** not applicable (no engine, `tools/bench/` or `truth/` change).
- **CI:** not run (billing).
