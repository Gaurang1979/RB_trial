#!/usr/bin/env python3
"""GS1 AI table for the .NET GS1 check, generated from GS1's Barcode Syntax Dictionary.

The dictionary (https://github.com/gs1/gs1-syntax-dictionary, Apache-2.0) lists every GS1
Application Identifier with its components (N digits, X CSET 82, Y CSET 39, Z base64url;
fixed or variable length; optional parts), the check routines ("linters") of each component,
whether it needs no separator ("*", predefined length), whether GS1 Digital Link allows it as
a data attribute ("?"), and its association rules (req=, ex=, dlpkey). The code lists some
linters need (ISO 3166 countries, ISO 4217 currencies, package types) come from GS1's syntax
engine (https://github.com/gs1/gs1-syntax-engine, Apache-2.0).

  python3 tools/gs1/gen_gs1_table.py fetch [--release 2026-01-27]
      download the dictionary release and the engine's code lists into tools/gs1/
  python3 tools/gs1/gen_gs1_table.py            (or: generate)
      write dotnet/CodeReader/Gs1Dictionary.g.cs from the files in tools/gs1/ (no network)
  python3 tools/gs1/gen_gs1_table.py --check
      exit 1 when the generated file is not what the files in tools/gs1/ give

Refresh: fetch the new release, generate, run the CodeReader tests (a linter the C# check
does not know fails GeneratedTableIsComplete), and commit the three files.
"""
import argparse
import re
import sys
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
DICT = HERE / "gs1-syntax-dictionary.txt"
LISTS = HERE / "gs1-code-lists.txt"
OUT = ROOT / "dotnet" / "CodeReader" / "Gs1Dictionary.g.cs"
DICT_URL = "https://raw.githubusercontent.com/gs1/gs1-syntax-dictionary/{release}/gs1-syntax-dictionary.txt"
ENGINE = "https://github.com/gs1/gs1-syntax-engine"
LINT_URL = "https://raw.githubusercontent.com/gs1/gs1-syntax-engine/{ref}/src/c-lib/syntax/lint_{name}.c"
# code list -> how its bitfield index maps to a code
BITFIELD_LISTS = {"iso3166": "num3", "iso4217": "num3", "iso3166alpha2": "alpha2"}


def get(url: str) -> str:
    with urllib.request.urlopen(url, timeout=30) as r:
        return r.read().decode("utf-8")


def bitfield_codes(src: str, kind: str) -> list[str]:
    # the C23 form of the table: one 0b literal of 64 bits per line, lowest index first
    bits = "".join(re.findall(r"^\s*0b([01]{64}),?", src, re.M))
    if not bits:
        sys.exit("no 0b bitfield found: the engine's lint source changed form")
    codes = []
    for i, b in enumerate(bits):
        if b != "1":
            continue
        if kind == "num3":
            codes.append(f"{i:03d}")
        else:
            codes.append(chr(65 + i // 26) + chr(65 + i % 26))
    return codes


def fetch(release: str) -> None:
    text = get(DICT_URL.format(release=release))
    if f"# Release: {release}" not in text:
        sys.exit(f"the downloaded dictionary is not release {release}")
    DICT.write_text(text, encoding="utf-8")
    ref = get(f"https://api.github.com/repos/gs1/gs1-syntax-engine/commits/main")
    sha = re.search(r'"sha":\s*"([0-9a-f]{40})"', ref).group(1)
    out = [f"# Code lists from {ENGINE} at {sha} (src/c-lib/syntax/lint_*.c), Apache-2.0.",
           "# Written by tools/gs1/gen_gs1_table.py fetch; one list per line: name code code ..."]
    for name, kind in BITFIELD_LISTS.items():
        out.append(name + " " + " ".join(bitfield_codes(get(LINT_URL.format(ref=sha, name=name)), kind)))
    src = get(LINT_URL.format(ref=sha, name="packagetype"))
    block = re.search(r"packagetypes\[\]\[4\]\s*=\s*\{(.*?)\};", src, re.S).group(1)
    out.append("packagetype " + " ".join(re.findall(r'"([0-9A-Z]{1,3})"', block)))
    LISTS.write_text("\n".join(out) + "\n", encoding="utf-8")
    print(f"wrote {DICT.relative_to(ROOT)} (release {release}) and {LISTS.relative_to(ROOT)} (engine {sha[:12]})")


def parse_dictionary(text: str):
    rows = []
    for line in text.splitlines():
        if not line.strip() or line.startswith("#"):
            continue
        body, _, title = line.partition("#")
        tokens = body.split()
        ais = tokens.pop(0)
        flags = tokens.pop(0) if tokens and re.fullmatch(r"[*!?\"$%&'()+,\-./:;<=>@\[\\\]^_`{|}~]+", tokens[0]) else ""
        spec, attrs = [], []
        for t in tokens:
            (attrs if attrs or re.match(r"(req|ex|dlpkey)\b", t) else spec).append(t)
        lo, _, hi = ais.partition("-")
        rows.append((lo, hi or lo, flags, " ".join(spec), " ".join(attrs), title.strip()))
    return rows


def cs(s: str) -> str:
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def generate() -> str:
    text = DICT.read_text(encoding="utf-8")
    release = re.search(r"^# Release: (\S+)", text, re.M).group(1)
    lists_text = LISTS.read_text(encoding="utf-8")
    engine = re.search(r"at ([0-9a-f]{40})", lists_text).group(1)
    lists = {}
    for line in lists_text.splitlines():
        if line and not line.startswith("#"):
            name, *codes = line.split()
            lists[name] = codes
    rows = parse_dictionary(text)
    w = max(len(cs(r[0])) for r in rows)
    out = [
        "// <auto-generated>",
        "// Written by tools/gs1/gen_gs1_table.py from the GS1 Barcode Syntax Dictionary, release",
        f"// {release} (https://github.com/gs1/gs1-syntax-dictionary), and the code lists of GS1's syntax",
        f"// engine at {engine[:12]} ({ENGINE}). Both (c) GS1 AISBL and contributors, Apache License 2.0.",
        "// Do not edit: change the generator or refresh the source files (see the generator's header).",
        "// </auto-generated>",
        "namespace BarcodeReading;",
        "",
        "public static partial class Gs1Check",
        "{",
        f"    /// <summary>The release of the GS1 Barcode Syntax Dictionary the AI table comes from.</summary>",
        f"    public const string DictionaryRelease = {cs(release)};",
        "",
        "    // first AI, last AI of the range, flags (\"*\" predefined length: no separator needed; \"?\" a GS1",
        "    // Digital Link data attribute), components (type and length, then linters), rules, title",
        "    private static readonly (string From, string To, string Flags, string Spec, string Rules, string Title)[] Dictionary =",
        "    {",
    ]
    for lo, hi, flags, spec, attrs, title in rows:
        out.append(f"        ({cs(lo)}, {cs(hi)}, {cs(flags)}, {cs(spec)}, {cs(attrs)}, {cs(title)}),")
    out += ["    };", "", "    private static readonly System.Collections.Generic.Dictionary<string, string[]> CodeLists = new()", "    {"]
    for name, codes in lists.items():
        out.append(f"        [{cs(name)}] = new[]")
        out.append("        {")
        for i in range(0, len(codes), 16):
            out.append("            " + " ".join(cs(c) + "," for c in codes[i:i + 16]))
        out.append("        },")
    out += ["    };", "}", ""]
    return "\n".join(out)


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("command", nargs="?", default="generate", choices=["fetch", "generate"])
    p.add_argument("--release", default="2026-01-27", help="dictionary release tag to fetch")
    p.add_argument("--check", action="store_true", help="only check that the generated file is current")
    a = p.parse_args()
    if a.command == "fetch":
        fetch(a.release)
        return
    code = generate()
    if a.check:
        if not OUT.exists() or OUT.read_text(encoding="utf-8") != code:
            sys.exit(f"{OUT.relative_to(ROOT)} is not current: run tools/gs1/gen_gs1_table.py")
        print(f"{OUT.relative_to(ROOT)} is current")
        return
    OUT.write_text(code, encoding="utf-8")
    print(f"wrote {OUT.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
