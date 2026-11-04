"""python -m remote_service_relay <command>

  serve [--host 127.0.0.1] [--port 8090] [--data <folder>] [--insecure-dev]
      Runs the relay. In production it sits behind a TLS proxy (Caddy or nginx) at support.sundaramtech.com that
      sets X-Forwarded-Proto; --insecure-dev allows plain http/ws on a developer's PC only.
  add-user <name> "<full name>" --machines "M-1001,M-1002" | "*" [--role service|oem] [--employee HR-EMP-0001]
      Adds or changes an engineer account; the password is read from standard input, never from the command line.
      --employee is the ERPNext Employee the engineer's work time goes to.
  add-machine <machine number> "<name>" [--site "<site>"] [--licence <licence key>]
      Registers a machine and prints its link token once; the host application stores it in its protected secret
      store (Vision System: VisionStudio secrets set VS_SUPPORT_LINK_TOKEN).

ERPNext (finished sessions go there as a Remote Service Log and a draft Timesheet): set ERPNEXT_URL,
ERPNEXT_API_KEY and ERPNEXT_API_SECRET in the relay's environment. Without them sessions wait in the outbox.
  audit [--limit 200] [--data <folder>]
      Prints the newest audit entries (JSON lines).

The data folder defaults to $REMOTE_SERVICE_RELAY_DATA, else ./relay-data.
"""

from __future__ import annotations

import argparse
import getpass
import json
import os
import sys

from .store import Store


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="remote_service_relay", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--data", default=os.environ.get("REMOTE_SERVICE_RELAY_DATA", "relay-data"))
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("serve")
    s.add_argument("--host", default="127.0.0.1")
    s.add_argument("--port", type=int, default=8090)
    s.add_argument("--insecure-dev", action="store_true")
    u = sub.add_parser("add-user")
    u.add_argument("name")
    u.add_argument("full_name")
    u.add_argument("--machines", required=True)
    u.add_argument("--role", default="service", choices=["service", "oem"])
    u.add_argument("--employee", default="")
    m = sub.add_parser("add-machine")
    m.add_argument("machine")
    m.add_argument("name")
    m.add_argument("--site", default="")
    m.add_argument("--licence", default="")
    a = sub.add_parser("audit")
    a.add_argument("--limit", type=int, default=200)
    args = p.parse_args(argv)
    store = Store(args.data)

    if args.cmd == "serve":
        import uvicorn

        from .app import create_app
        from .erpnext import ErpNext
        # proxy headers from the TLS proxy on this host only
        uvicorn.run(create_app(store, secure=not args.insecure_dev, erp=ErpNext.from_env()), host=args.host, port=args.port,
                    proxy_headers=True, forwarded_allow_ips="127.0.0.1", ws_ping_interval=20, log_level="info")
        return 0
    if args.cmd == "add-user":
        password = sys.stdin.readline().rstrip("\n") if not sys.stdin.isatty() else getpass.getpass("Password: ")
        machines = ["*"] if args.machines.strip() == "*" else [x.strip() for x in args.machines.split(",") if x.strip()]
        try:
            store.save_account(args.name, args.full_name, password, machines, args.role, args.employee)
        except ValueError as e:
            print(e, file=sys.stderr)
            return 2
        print(f"account {args.name.lower()} saved in {store.folder}")
        return 0
    if args.cmd == "add-machine":
        try:
            token = store.add_machine(args.machine, args.name, args.site, args.licence)
        except ValueError as e:
            print(e, file=sys.stderr)
            return 2
        print(f"link token for {args.machine} (shown once; store it in the machine's protected secret store):")
        print(token)
        return 0
    for entry in store.audit_entries(args.limit):
        print(json.dumps(entry))
    return 0


if __name__ == "__main__":
    sys.exit(main())
