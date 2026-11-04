#!/usr/bin/env python3
"""Dependency watch: what this repository uses, against the newest release of each.

Reads the manifests in the repository (the .NET projects, the plant server's
requirements and Docker files, the Flutter apps, the engine's vcpkg manifest,
the GitHub workflows), asks each registry for its newest stable release, and
writes a Markdown report: one row per component with the version in use, the
newest one, how far behind (patch, minor, major) and the end of support when
it is near. Standard library only, so it runs anywhere without installing.

    scripts/deps-check.py                 # report on stdout
    scripts/deps-check.py --out r.md      # report to a file; also r.json
    scripts/deps-check.py --email         # also e-mail it (SMTP_* and REPORT_TO
                                          # from the environment; skipped if unset)

A registry that cannot be reached gives "?" in its rows, never a failure: the
report is a reminder, and a half report is better than none. Upgrading is done
by a session per area, built and tested as any change (docs/vision-system/DEPENDENCIES.md).
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import re
import smtplib
import sys
import urllib.request
from email.message import EmailMessage
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TIMEOUT = 20

# End of support of the platforms, from their makers' published policies. Checked by hand when a
# new major release comes out (the registries do not publish these dates in one place).
END_OF_SUPPORT = {
    ("dotnet", "8"): "2026-11-10", ("dotnet", "9"): "2026-11-10", ("dotnet", "10"): "2028-11-14",
    ("python", "3.10"): "2026-10-31", ("python", "3.11"): "2027-10-31", ("python", "3.12"): "2028-10-31",
    ("python", "3.13"): "2029-10-31", ("python", "3.14"): "2030-10-31",
    ("postgres", "14"): "2026-11-12", ("postgres", "15"): "2027-11-11", ("postgres", "16"): "2028-11-09",
    ("postgres", "17"): "2029-11-08", ("postgres", "18"): "2030-11-14",
}
SOON_DAYS = 120


# ---- registries -----------------------------------------------------------------------------
def _get(url: str, headers: dict | None = None):
    req = urllib.request.Request(url, headers={"User-Agent": "vision-system-deps-check", **(headers or {})})
    with urllib.request.urlopen(req, timeout=TIMEOUT) as r:
        return json.loads(r.read().decode())


def _vkey(v: str):
    return [int(x) for x in re.findall(r"\d+", v)[:4]]


def _stable(vs):
    return [v for v in vs if re.fullmatch(r"\d+(\.\d+)*", v)]


def latest_nuget(pkg: str) -> str | None:
    vs = _stable(_get(f"https://api.nuget.org/v3-flatcontainer/{pkg.lower()}/index.json")["versions"])
    return max(vs, key=_vkey) if vs else None


def latest_pypi(pkg: str) -> str | None:
    return _get(f"https://pypi.org/pypi/{pkg}/json")["info"]["version"]


def latest_pub(pkg: str) -> str | None:
    return _get(f"https://pub.dev/api/packages/{pkg}")["latest"]["version"]


def latest_docker(image: str, suffix: str = "") -> str | None:
    repo = image if "/" in image else f"library/{image}"
    pat = re.compile(r"\d+(\.\d+)*" + re.escape(suffix))
    tags = []
    url = f"https://hub.docker.com/v2/repositories/{repo}/tags?page_size=100&ordering=last_updated"
    for _ in range(3):
        d = _get(url)
        tags += [t["name"] for t in d["results"] if pat.fullmatch(t["name"])]
        url = d.get("next")
        if not url:
            break
    return max(tags, key=_vkey) if tags else None


def latest_dotnet() -> str | None:
    """The newest released (not preview) .NET runtime, from the reference pack on NuGet."""
    return latest_nuget("microsoft.netcore.app.ref")


def latest_flutter(dart: bool = False) -> str | None:
    """The newest stable Flutter, or the Dart SDK it ships with."""
    d = _get("https://storage.googleapis.com/flutter_infra_release/releases/releases_linux.json")
    h = d["current_release"]["stable"]
    r = next(r for r in d["releases"] if r["hash"] == h)
    return r["dart_sdk_version"] if dart else r["version"]


def latest_action(repo: str) -> str | None:
    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN")
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    return _get(f"https://api.github.com/repos/{repo}/releases/latest", headers)["tag_name"].lstrip("v")


# ---- what the repository uses ---------------------------------------------------------------
def _rel(p: Path) -> str:
    return str(p.relative_to(ROOT))


def used() -> list[dict]:
    """(area, kind, name, version in use, where) for every pinned component."""
    rows: dict[tuple, dict] = {}

    def add(area, kind, name, version, where):
        key = (kind, name, version)
        r = rows.setdefault(key, {"area": area, "kind": kind, "name": name, "version": version, "where": []})
        if where not in r["where"]:
            r["where"].append(where)

    for p in sorted(ROOT.glob("dotnet/**/*.csproj")):
        t = p.read_text(encoding="utf-8", errors="replace")
        for tf in re.findall(r"<TargetFramework>net(\d+)\.\d+", t):
            add(".NET (line PC)", "dotnet", ".NET runtime and SDK", tf, _rel(p))
        for name, v in re.findall(r'PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"', t):
            add(".NET (line PC)", "nuget", name, v, _rel(p))
    for p in sorted(ROOT.glob("**/requirements*.txt")):
        if "node_modules" in p.parts or ".venv" in p.parts:
            continue
        for line in p.read_text().splitlines():
            m = re.match(r"\s*([A-Za-z0-9_.\-]+)(?:\[[^\]]*\])?\s*(?:>=|==|~=)\s*([0-9][^,;\s#]*)", line)
            if m:
                add("Python (plant server, tools)", "pypi", m.group(1), m.group(2), _rel(p))
    for p in sorted(ROOT.glob("mobile/*/pubspec.yaml")):
        t = p.read_text()
        if m := re.search(r'sdk:\s*">=\s*([0-9.]+)', t):
            add("Flutter (mobile app)", "dart", "Dart SDK (minimum)", m.group(1), _rel(p))
        for name, v in re.findall(r"^\s{2}([a-z0-9_]+):\s*\^([0-9][0-9.+\-a-z]*)\s*$", t, re.M):
            add("Flutter (mobile app)", "pub", name, v, _rel(p))
    for p in sorted(ROOT.glob("**/Dockerfile*")) + sorted(ROOT.glob("**/docker-compose*.yml")) + sorted(ROOT.glob(".github/workflows/*.yml")):
        for img, tag in re.findall(r"(?:^FROM|^\s*image:)\s+([a-z0-9./\-]+):([A-Za-z0-9.\-]+)", p.read_text(), re.M):
            add("Server images and databases", "docker", img, tag, _rel(p))
    for p in sorted(ROOT.glob(".github/workflows/*.yml")):
        t = p.read_text()
        # a commit hash pin is left out: it names no release to compare
        for repo, v in re.findall(r"uses:\s*([\w.\-]+/[\w.\-]+)@v?([0-9]+(?:\.[0-9]+)*)(?![\w])", t):
            add("Build and CI", "action", repo, v, _rel(p))
        for v in re.findall(r"dotnet-version:\s*['\"]?([0-9]+)", t):
            add("Build and CI", "dotnet", ".NET SDK in CI", v, _rel(p))
        for v in re.findall(r"python-version:\s*['\"]?([0-9]+\.[0-9]+)", t):
            add("Build and CI", "python", "Python in CI", v, _rel(p))
        for v in re.findall(r"flutter-version:\s*['\"]?([0-9][0-9.]*)", t):
            add("Build and CI", "flutter", "Flutter SDK in CI", v, _rel(p))
    for p in sorted(ROOT.glob("**/vcpkg.json")):
        d = json.loads(p.read_text())
        for dep in d.get("dependencies", []):
            name = dep if isinstance(dep, str) else dep["name"]
            add("C++ engine", "vcpkg", name, "baseline " + d.get("builtin-baseline", "?")[:7], _rel(p))
    return list(rows.values())


# ---- compare --------------------------------------------------------------------------------
def gap(have: str, new: str | None, floating: bool = False) -> str:
    """How far `have` is behind `new`. A floating tag ("2", "3.12-slim" of an image or an action)
    already takes the releases below the parts it names, so only those parts are compared."""
    if not new:
        return "?"
    a, b = _vkey(have), _vkey(new)
    if not a or not b:
        return "?"
    if floating:
        b = b[: len(a)]
    a += [0] * (3 - len(a)); b += [0] * (3 - len(b))
    if b[:3] <= a[:3]:
        return "current"
    return "major" if b[0] > a[0] else "minor" if b[1] > a[1] else "patch"


def latest_for(r: dict, cache: dict) -> str | None:
    kind, name, have = r["kind"], r["name"], r["version"]
    key = (kind, name, re.sub(r"[0-9.]+", "", have))
    if key in cache:
        return cache[key]
    try:
        if kind == "nuget":
            v = latest_nuget(name)
        elif kind == "pypi":
            v = latest_pypi(name)
        elif kind == "pub":
            v = latest_pub(name)
        elif kind == "docker":
            v = latest_docker(name, re.sub(r"^[0-9.]+", "", have))
        elif kind == "dotnet":
            v = latest_dotnet()
        elif kind in ("flutter", "dart"):
            v = latest_flutter(dart=kind == "dart")
        elif kind == "vcpkg":
            v = "vcpkg " + latest_action("microsoft/vcpkg")   # the baseline moves with vcpkg's releases
        elif kind == "action":
            v = latest_action(name)
        elif kind == "python":
            v = latest_docker("python", "-slim")
            v = v[:-5] if v else None
        else:
            v = None
    except Exception:  # noqa: BLE001 - an unreachable registry gives "?", never a failed report
        v = None
    cache[key] = v
    return v


def support_note(r: dict, today: dt.date) -> str:
    plat = {"dotnet": "dotnet", "python": "python"}.get(r["kind"])
    if r["kind"] == "docker" and r["name"] in ("postgres", "python"):
        plat = r["name"]
    if r["kind"] == "docker" and r["name"].endswith("timescaledb") and (m := re.search(r"pg(\d+)", r["version"])):
        plat, ver = "postgres", m.group(1)
    else:
        ver = ".".join(re.findall(r"\d+", r["version"])[: 2 if plat == "python" else 1])
    end = END_OF_SUPPORT.get((plat, ver)) if plat else None
    if not end:
        return ""
    days = (dt.date.fromisoformat(end) - today).days
    if days < 0:
        return f"**out of support since {end}**"
    if days <= SOON_DAYS:
        return f"**support ends {end} ({days} days)**"
    return f"supported until {end}"


def report(today: dt.date | None = None) -> tuple[str, list[dict]]:
    today = today or dt.date.today()
    cache: dict = {}
    rows = used()
    for r in rows:
        r["latest"] = latest_for(r, cache)
        r["gap"] = "?" if r["kind"] == "vcpkg" else gap(r["version"], r["latest"], floating=r["kind"] in ("docker", "action"))
        r["support"] = support_note(r, today)
    order = {"major": 0, "minor": 1, "patch": 2, "?": 3, "current": 4}
    rows.sort(key=lambda r: (r["area"], order[r["gap"]], r["name"].lower()))
    urgent = [r for r in rows if r["support"].startswith("**")]
    behind = [r for r in rows if r["gap"] in ("major", "minor", "patch")]
    out = [f"# Dependency report {today.isoformat()}", "",
           f"{len(rows)} components checked: {len(behind)} behind "
           f"({sum(r['gap'] == 'major' for r in rows)} major, {sum(r['gap'] == 'minor' for r in rows)} minor, "
           f"{sum(r['gap'] == 'patch' for r in rows)} patch), {len(urgent)} near or past end of support, "
           f"{sum(r['gap'] == '?' for r in rows)} not checked.", ""]
    if urgent:
        out += ["## Act now: end of support", ""]
        out += [f"- {r['name']} {r['version']} ({r['area']}): {r['support'].strip('*')}" for r in urgent]
        out += [""]
    out += ["## All components", "", "| Area | Component | In use | Newest | Behind | Support | Where |", "| --- | --- | --- | --- | --- | --- | --- |"]
    for r in rows:
        where = ", ".join(r["where"][:3]) + (f" and {len(r['where']) - 3} more" if len(r["where"]) > 3 else "")
        out.append(f"| {r['area']} | {r['name']} | {r['version']} | {r['latest'] or '?'} | {r['gap']} | {r['support']} | {where} |")
    out += ["", "How to act on this report: docs/vision-system/DEPENDENCIES.md (one pull request per area, "
            "built and tested once like any change, by the session that makes it).", ""]
    return "\n".join(out), rows


def email(text: str, subject: str) -> bool:
    host, to = os.environ.get("SMTP_HOST"), os.environ.get("REPORT_TO")
    if not host or not to:
        print("e-mail skipped: SMTP_HOST or REPORT_TO not set", file=sys.stderr)
        return False
    msg = EmailMessage()
    msg["Subject"], msg["To"] = subject, to
    msg["From"] = os.environ.get("SMTP_FROM") or os.environ.get("SMTP_USER") or to
    msg.set_content(text)
    port = int(os.environ.get("SMTP_PORT", "587"))
    with (smtplib.SMTP_SSL(host, port, timeout=30) if port == 465 else smtplib.SMTP(host, port, timeout=30)) as s:
        if port != 465:
            s.starttls()
        if os.environ.get("SMTP_USER"):
            s.login(os.environ["SMTP_USER"], os.environ.get("SMTP_PASSWORD", ""))
        s.send_message(msg)
    return True


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--out", help="write the Markdown report here (and the rows as JSON next to it)")
    ap.add_argument("--email", action="store_true", help="also e-mail the report (SMTP_* and REPORT_TO)")
    a = ap.parse_args()
    text, rows = report()
    if a.out:
        Path(a.out).write_text(text, encoding="utf-8")
        Path(a.out).with_suffix(".json").write_text(json.dumps(rows, indent=1), encoding="utf-8")
    else:
        print(text)
    if a.email:
        try:
            email(text, text.splitlines()[0].lstrip("# "))
        except Exception as e:  # noqa: BLE001 - a mail fault must not stop the report issue
            print(f"e-mail failed: {e}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
