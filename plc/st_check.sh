#!/usr/bin/env bash
# Syntax and type check of the Delta ST programs with rusty (open-source IEC 61131-3 compiler, Docker),
# where ISPSoft is not at hand. It is not an ISPSoft compile: the copy it checks has the AT %... addresses
# dropped, the in-program VAR_GLOBAL moved to the top level and the array Set renamed (rusty reserves SET),
# and three ISPSoft conversions rusty's library lacks are stand-ins. Logic is not changed.
# Run: plc/st_check.sh        (exit 0 = both programs check clean; warnings are printed)
set -euo pipefail
IMAGE=ghcr.io/plc-lang/rusty:fix-string-escapes-x86_64   # 'latest' / 'master' manifests are broken on ghcr
ROOT=$(cd "$(dirname "$0")/.." && pwd)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

python3 - "$ROOT" "$WORK" <<'EOF'
import re, sys
root, work = sys.argv[1], sys.argv[2]
for src, dst in (('plc/delta-dvp/VisionLineDVP.st', 'dvp.st'), ('plc/delta-as/VisionLine.st', 'as.st')):
    s = open(f'{root}/{src}').read()
    s = re.sub(r'\s+AT\s+%[A-Z]+[0-9.]+', '', s)
    m = re.search(r'(PROGRAM \w+\s*\n)(VAR_GLOBAL\n.*?END_VAR\n)', s, re.S)
    s = s[:m.start()] + m.group(2) + '\n' + m.group(1) + s[m.end():]
    s = re.sub(r'\bSet\b(?=\s*[\[:])', 'LineSet', s)
    open(f'{work}/{dst}', 'w').write(s)
open(f'{work}/shim.st', 'w').write('''
FUNCTION UINT_TO_TIME : TIME
VAR_INPUT v : UINT; END_VAR
    UINT_TO_TIME := LINT_TO_TIME(UINT_TO_LINT(v) * 1000000);
END_FUNCTION
FUNCTION TIME_TO_UINT : UINT
VAR_INPUT v : TIME; END_VAR
    TIME_TO_UINT := LINT_TO_UINT(TIME_TO_LINT(v) / 1000000);
END_FUNCTION
FUNCTION TIME_TO_DINT : DINT
VAR_INPUT v : TIME; END_VAR
    TIME_TO_DINT := LINT_TO_DINT(TIME_TO_LINT(v) / 1000000);
END_FUNCTION
''')
EOF

# files are copied in, not mounted: a folder Docker does not share mounts empty, and rusty exits 0 on a
# missing file. bad.st must fail, which shows the check really ran.
printf 'PROGRAM Bad\nVAR x : INT; END_VAR\n    x := 1 + ;\nEND_PROGRAM\n' > "$WORK/bad.st"
C=$(docker create --platform linux/amd64 --entrypoint bash "$IMAGE" -c '
  cd /src; rc=0
  plc --check bad.st >/dev/null 2>&1 && { echo "rusty did not reject bad.st: check not run"; exit 2; }
  for f in dvp as; do
    echo "=== $f"; plc --check $f.st shim.st -i "/opt/rusty/stdlib/include/*.st" 2>&1 || rc=1
  done; exit $rc')
trap 'docker rm -f "$C" >/dev/null 2>&1; rm -rf "$WORK"' EXIT
docker cp "$WORK/." "$C:/src" >/dev/null
docker start -a "$C" 2>&1 | sed 's/\x1b\[[0-9;]*m//g'
exit "$(docker inspect "$C" --format '{{.State.ExitCode}}')"
