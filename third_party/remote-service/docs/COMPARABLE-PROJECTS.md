# What comparable open-source projects do better

A review of open-source projects close to this module (2026-11-04), and what we could adopt. Each
item says what is missing here, how to adopt it, and its size (S, M or L). We take ideas, not code,
from copyleft projects (AGPL or GPL), because this module is proprietary. The licences below were
checked for Teleport only; check the others before quoting them.

| Area | Projects looked at |
| --- | --- |
| Remote support (agent and relay) | MeshCentral (Apache-2.0), RustDesk (AGPL-3.0), Apache Guacamole (Apache-2.0) |
| Access and audit | Teleport (AGPL-3.0 source; commercial licence for binaries since v16), HashiCorp Boundary (BUSL-1.1) |
| Outbound tunnels | cloudflared (Apache-2.0), frp (Apache-2.0), rathole (Apache-2.0) |
| Device updates | Mender (client Apache-2.0), RAUC (LGPL-2.1), SWUpdate (GPL-2.0), Eclipse hawkBit (EPL-2.0), TUF and Uptane (specifications) |

## Recommended, in order

**Done:** items 1, 2 and 3 (2026-11-04), each recorded in PROTOCOL.md section 5 and the software
packages section.

| # | Item | From | What is missing here | How we would adopt it | Size |
| --- | --- | --- | --- | --- | --- |
| 1 | **No reinstalling old packages** | TUF | Nothing stops someone reinstalling an old, validly signed package that has a known flaw | The agent itself refuses a version below the highest ever installed, kept in protected storage. Add an optional signed `minVersion` floor and an `expires` date to the manifest. Rollback only to the kept previous version, never below the floor | S |
| 2 | **Log chain head kept outside the machine** | Sigstore Rekor (the idea) | The host holds its own HMAC key, so it could rewrite its chain without anyone noticing | At each `session_end` the agent sends the chain head hash. The relay keeps it and passes it to ERPNext. A later rewrite on the machine no longer matches | S |
| 3 | **Acknowledged log delivery** | cloudflared, Teleport | `log` frames have no number or acknowledgement, so an entry can be lost or sent twice when the link drops | A `seq` number on each `log` frame and an `ack` frame from the relay. The agent resends what was not acknowledged; the relay ignores repeats | S |
| 4 | **Two-factor sign-in, checked again before risky steps** | Teleport | A relay password plus the day's code is enough to install software | WebAuthn or passkeys on the relay. A fresh check, no older than 5 minutes, before packages, uploads and corrections; the relay sends its time in the `req` frame and the agent enforces it | M |
| 5 | **Signed session grants** | OpenSSH certificates, Teleport | The agent trusts the relay's `user` and `role` fields, so a compromised relay could claim the OEM role | The relay (later a separate signing service) signs a short-lived grant: machine, user, role, expiry. The agent checks it with a public key it ships | M |
| 6 | **A key per machine instead of a link token** | MeshCentral | The relay stores a reusable token hash per machine | The machine makes its own key pair at enrolment (using the PC's protected store or TPM) and proves itself with a signed challenge or mutual TLS. Enrolment uses a one-time code; the agent pins the relay's TLS key | M |
| 7 | **Changing the signing key without visiting sites** | TUF root metadata, Uptane | If the PackageSign key leaks, every site needs a visit | Machines trust a small signed "trust file" listing the allowed package keys. A new trust file must be signed by an offline root key; optionally two keys must both sign OEM releases | M |
| 8 | **Automatic rollback when the new version does not come up** | Mender (commit step), RAUC (mark good) | An install that starts but does not work stays in place until someone rolls it back | After an install the host must report "healthy" (the program started and passed its own integrity check) within a set time. Otherwise the agent rolls back on its own and logs `package_auto_rollback` | M |
| 9 | **One origin per machine** (already open) | Guacamole, MeshCentral | Pages of one machine could read another machine open in the same browser | `<machine>.m.support.sundaramtech.com` with a wildcard certificate, the cookie kept to that host, and a strict page policy | S–M |
| 10 | **Sessions survive short link drops** | cloudflared | A dropped link ends the session at once; plant networks drop often | Keep the session for about 60 s after a drop; with item 3 nothing in the log is lost | M |
| 11 | **Request record** | Teleport, Guacamole (session recording) | We record actions, not every page the engineer opened | The relay keeps method, path and answer size of every request for the session; with the action log it shows exactly what the engineer saw | S–M |
| 12 | **Stable event codes** | Teleport audit events | Log kinds are words only | An event code per kind (`RS1001`…) and the relay's request id in each entry, for reliable ERPNext and security-system exports | S |
| 13 | **Flow control and binary data** | frp and rathole (stream windows) | The relay cuts a browser off after 64 queued chunks; base64 adds a third to every download | Credit frames per request, and binary WebSocket frames for data, offered as protocol version 2 | S |
| 14 | **Reconnect spread out, and "come back later"** | common practice | After a relay restart every machine dials at once | Random delay in the back-off, and a close code with `retryAfter` for maintenance | S |
| 15 | **Optional consent and a local "End" button** | MeshCentral, Teleport | The owner chose no approval and a neutral badge; some customer sites may still require consent | A host setting: ask the operator to allow each session. The badge always has "End", which ends the session with `endReason: operator` | S–M |
| 16 | **OEM rights only on request** | Teleport access requests | Every OEM account has OEM rights on its machines all the time | Service by default; OEM rights on one machine for a few hours after a second Sundaram person approves on the relay | M |

## Not to adopt

- **Copying code from RustDesk, SWUpdate or Teleport:** they are AGPL or GPL, and copying would bind
  this module to those licences. Boundary (BUSL-1.1) is not open source either.
- **A general network tunnel or port forwarding** (frp-style, Boundary targets): it brings back the
  unlimited access this design removes by allowing only declared capabilities.
- **Remote desktop or screen recording** (Guacamole, RustDesk, MeshCentral desktop): ruled out by
  REQ-11 and PROT-26, and it gets past every capability check.
- **A full TUF or Uptane client, or a hawkBit server,** for now: too heavy for installs that an
  operator confirms machine by machine. Items 1 and 7 take the parts we need.
- **Sigstore keyless signing:** it needs online services, and plant PCs may check offline.
- **A VPN mesh** (Tailscale-style): wider network access than needed, and hard to audit per action.

## Sources

- **Teleport:**
  - https://goteleport.com/docs/zero-trust-access/authentication/joining-sessions/
  - https://goteleport.com/docs/identity-governance/access-requests/
  - https://goteleport.com/blog/teleport-community-license
- **Updates:**
  - https://theupdateframework.io/security/
  - https://github.com/theupdateframework/specification
  - https://uptane.org
  - https://docs.mender.io/artifact-creation/state-scripts
- **Remote access and tunnels:**
  - https://github.com/Ylianst/MeshCentral
  - https://developers.cloudflare.com/cloudflare-tunnel/
