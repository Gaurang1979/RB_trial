#!/usr/bin/env bash
# Tests scripts/coord.sh's prs, overlap and cleanup with a fake gh, in a git
# repository of its own (a bare origin and a branch), under the system
# bash (3.2 on the Mac). A pull request with over 100 files must be
# read in full: gh pr view stops at 100 (it missed #1353's
# services/router-agent/README.md for #1388).
# Usage: scripts/coord_test.sh   (takes ~2s)
set -uo pipefail
cd "$(dirname "$0")/.."
T=$(mktemp -d)
trap 'rm -rf "$T"' EXIT
fail=0; pass=0
ok(){ echo "PASS  $1"; pass=$((pass + 1)); }
no(){ echo "FAIL  $1"; fail=$((fail + 1)); }

# A repository with coord.sh, an origin with main, and a branch that
# changes services/router-agent/README.md and a.txt.
git init -q --bare "$T/origin.git"
git init -q -b main "$T/repo"
G="git -C $T/repo -c user.name=t -c user.email=t@t"
mkdir -p "$T/repo/scripts" "$T/repo/services/router-agent"
cp scripts/coord.sh "$T/repo/scripts/"
echo x > "$T/repo/a.txt"; echo x > "$T/repo/services/router-agent/README.md"
$G add -A; $G commit -qm base
$G remote add origin "$T/origin.git"; $G push -q origin main
$G checkout -q -b claude/mine
echo y > "$T/repo/a.txt"; echo y > "$T/repo/services/router-agent/README.md"
$G commit -qam change

# The fake gh. Pull requests: #7 has 150 files, the 150th being
# services/router-agent/README.md; #8 has 2 files, one of them a.txt;
# #9 has none; #10 is this branch's own. It logs every call.
mkdir "$T/bin"
cat > "$T/bin/gh" <<'GH'
#!/bin/bash
echo "$*" >> "$FAKEGH_LOG"
big(){ i=1; while [ $i -lt 150 ]; do echo "big/f$i.txt"; i=$((i + 1)); done; echo services/router-agent/README.md; }
files(){ case $1 in 7) big ;; 8) echo b.txt; echo a.txt ;; 10) echo a.txt ;; esac; }
case "$1 $2" in
  "api graphql") [ "${FAKEGH_GQL:-yes}" = yes ] ;;
  "pr list")
    if [ "$3 $4" = "--state all" ]; then
      # Every pull request, for cleanup: branch, state, head, number, label, merged.
      for b in antigravity/done gemini/done codex/done claude/done personal/done; do
        printf '%s\tMERGED\t%s\t#%s\tfalse\t2026-11-01\n' "$b" "$(git -C "$FAKEGH_REPO" rev-parse main)" "${#b}"
      done
      printf 'antigravity/open\tOPEN\t%s\t#30\tfalse\t\n' "$(git -C "$FAKEGH_REPO" rev-parse main)"
      exit 0
    fi
    printf '#7\tready\t2026-10-01T00:00:00Z\t2026-10-01T00:00:00Z\tclaude/big\tBig one\tbody\n'
    printf '#8\tdraft\t2026-10-02T00:00:00Z\t2026-10-02T00:00:00Z\tclaude/small\tSmall one\tbody\n'
    printf '#9\tdraft\t2026-10-03T00:00:00Z\t2026-10-03T00:00:00Z\tclaude/empty\tEmpty one\tbody\n'
    printf '#10\tdraft\t2026-10-04T00:00:00Z\t2026-10-04T00:00:00Z\tclaude/mine\tMine\tbody\n' ;;
  "pr view")
    # What gh pr view --json changedFiles,files gives: the count, then
    # at most 100 paths.
    n=$(files "$3" | grep -c .); echo "$n"; files "$3" | head -100 ;;
  "api repos/{owner}/{repo}/pulls?state=open&per_page=100&page=1")
    printf '#7\tready\t2026-10-01T00:00:00Z\t2026-10-01T00:00:00Z\tclaude/big\tBig one\tbody\n'
    printf '#8\tdraft\t2026-10-02T00:00:00Z\t2026-10-02T00:00:00Z\tclaude/small\tSmall one\tbody\n' ;;
  "api repos/{owner}/{repo}/pulls?state=open&per_page=100&page="*) ;;
  "api repos/{owner}/{repo}/pulls/"*)
    pr=${2#repos/\{owner\}/\{repo\}/pulls/}; pr=${pr%%/*}; page=${2##*page=}
    files "$pr" | sed -n "$(( (page - 1) * 100 + 1 )),$(( page * 100 ))p" ;;
  *) echo "fake gh: unexpected: $*" >&2; exit 1 ;;
esac
GH
chmod +x "$T/bin/gh"
export PATH="$T/bin:$PATH" FAKEGH_LOG="$T/calls" FAKEGH_REPO="$T/repo"
COORD="/bin/bash $T/repo/scripts/coord.sh"

echo "--- overlap through gh pr view (GraphQL)"
: > "$T/calls"
out=$(cd "$T/repo" && $COORD overlap 2>&1)
echo "$out" | grep -q '^#7 (claude/big): Big one$' && echo "$out" | grep -qx '     services/router-agent/README.md' \
  && ok "overlap finds the 150th file of a pull request with over 100" || no "overlap missed #7's 150th file: $out"
echo "$out" | grep -q '^#8 (claude/small): Small one$' && echo "$out" | grep -qx '     a.txt' \
  && ok "overlap finds a small pull request's file" || no "overlap missed #8: $out"
echo "$out" | grep -q '#9\|#10\|big/f' && no "overlap lists #9, this branch's own #10 or files not changed here: $out" \
  || ok "overlap leaves out the empty pull request, this branch's own and unshared files"
grep -q 'pulls/7/files' "$T/calls" && ok "the large pull request is paged through REST" || no "no REST paging for #7: $(cat "$T/calls")"
grep -q 'pulls/8/files\|pulls/9/files' "$T/calls" && no "small pull requests went through REST too: $(cat "$T/calls")" \
  || ok "pull requests of 100 files or fewer use gh pr view alone"

echo "--- prs lists every file"
out=$(cd "$T/repo" && $COORD prs 2>&1)
[ "$(echo "$out" | sed -n '/^#7 /,/^#8 /p' | grep -c '^     ')" = 150 ] && ok "prs lists all 150 files of #7" \
  || no "prs lists $(echo "$out" | sed -n '/^#7 /,/^#8 /p' | grep -c '^     ') files of #7"
[ "$(echo "$out" | sed -n '/^#9 /,/^#10 /p' | grep -c '^     ')" = 0 ] && ok "prs lists no files for an empty pull request" \
  || no "prs lists files for #9: $out"

echo "--- overlap through REST (no GraphQL)"
out=$(cd "$T/repo" && FAKEGH_GQL=no $COORD overlap 2>&1)
echo "$out" | grep -q '^#7 (claude/big)' && echo "$out" | grep -qx '     services/router-agent/README.md' \
  && ok "REST overlap finds the 150th file" || no "REST overlap: $out"

echo "--- cleanup treats every agent's branches alike (AGENTS.md; requirements 17.9, AGR-04)"
for b in antigravity/done gemini/done codex/done claude/done personal/done antigravity/open; do $G branch -q "$b" main; done
$G worktree add -q "$T/wt-ag" antigravity/done 2>/dev/null
out=$(cd "$T/repo" && $COORD cleanup 2>&1)
for b in antigravity/done gemini/done codex/done claude/done; do
  $G rev-parse -q --verify "refs/heads/$b" >/dev/null && no "cleanup kept merged $b: $out" || ok "cleanup removes merged $b"
done
[ -d "$T/wt-ag" ] && no "cleanup kept the worktree of merged antigravity/done: $out" || ok "cleanup removes an Antigravity worktree"
$G rev-parse -q --verify refs/heads/personal/done >/dev/null && ok "cleanup leaves a branch of no agent alone" || no "cleanup removed personal/done: $out"
$G rev-parse -q --verify refs/heads/antigravity/open >/dev/null && ok "cleanup keeps an agent branch with an open pull request" || no "cleanup removed antigravity/open: $out"

echo "$pass passed, $fail failed"
[ $fail = 0 ]
