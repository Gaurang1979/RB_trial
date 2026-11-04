#!/usr/bin/env bash
# Coordination check for parallel agents (docs/PROJECT-HISTORY.md
# section 7). Answers "is this item taken?" and "does another open pull
# request touch my files?" from main and the open pull requests, which
# are the only places work in progress is recorded.
#
# Usage:
#   scripts/coord.sh taken <words...>  who holds an item, or has already
#                                      done it: [~] and [x] on main, change
#                                      notes on main, open pull requests,
#                                      and pull requests and commits merged
#                                      in the last 14 days. All words must
#                                      match; separate other wordings with
#                                      "|": taken "ip range | protected range"
#   scripts/coord.sh prs               open pull requests, oldest first,
#                                      with the files each one changes
#   scripts/coord.sh overlap           open pull requests that change a
#                                      file this branch changes
#   scripts/coord.sh note              create this branch's change note
#                                      in docs/changes/
#   scripts/coord.sh cleanup [--dry-run] [--include-current]
#                                      after a merge: remove the worktrees
#                                      and agent branches whose pull
#                                      request is merged or closed, when
#                                      nothing in them is uncommitted or
#                                      unpushed (the hooks in
#                                      .claude/settings.json run it)
#
# Needs git and an authenticated gh. Runs under macOS bash 3.2. Where gh
# has no GraphQL (Claude Code cloud sessions answer every GraphQL call
# with 403), the pull request reads use the REST API through gh api;
# COORD_REST=1 uses it everywhere. Self-test: scripts/coord_test.sh.
set -euo pipefail

die(){ echo "coord: $*" >&2; exit 2; }
root=$(git rev-parse --show-toplevel)
cd "$root"
branch=$(git rev-parse --abbrev-ref HEAD)
# Every agent names its branches <agent>/<topic> (AGENTS.md; requirements
# 17.9, AGR-04); these are the agents. Add a new agent's name here and in
# AGENTS.md.
AGENT_PREFIXES="claude antigravity gemini codex"
agent_branch(){ for a in $AGENT_PREFIXES; do case $1 in "$a"/*) return 0 ;; esac; done; return 1; }

# Lines of stdin holding every word of $1, case-insensitive; "|" in $1
# separates other wordings, any one of which may match.
allwords(){ awk -v w="$1" 'BEGIN{na=split(tolower(w),alt,"|")}
  {l=tolower($0)
   for(j=1;j<=na;j++){n=split(alt[j],a," "); if(n==0) continue; ok=1
     for(i=1;i<=n;i++) if(index(l,a[i])==0){ok=0; break}
     if(ok){print; next}}}'; }

# The date 14 days ago, on macOS (BSD date) or Linux (GNU date).
since14(){ date -u -v-14d +%Y-%m-%d 2>/dev/null || date -u -d '14 days ago' +%Y-%m-%d; }

# Whether gh can use GraphQL here; asked once. Without it (Claude Code
# cloud sessions) the reads below go through the REST API.
gql=""
rest(){
  [ "${COORD_REST:-}" = 1 ] && return 0
  [ -n "$gql" ] || { if gh api graphql -f query='query{viewer{login}}' >/dev/null 2>&1; then gql=yes; else gql=no; fi; }
  [ "$gql" = no ]
}

# restpages <path with ?...> <jq>: every page of a REST list, page by
# page (gh api --paginate follows GitHub's next links, which name the
# repository by number, and the cloud sessions' proxy refuses those).
restpages(){
  page=1
  while [ $page -le 30 ]; do
    out=$(gh api "$1&per_page=100&page=$page" --jq "$2")
    [ -n "$out" ] || break
    printf '%s\n' "$out"
    [ "$(printf '%s\n' "$out" | wc -l)" -ge 100 ] || break
    page=$((page + 1))
  done
}

# One line per open pull request: number, draft, created, updated,
# branch, title, body (newlines flattened), oldest first.
prlines(){
  if rest; then
    restpages 'repos/{owner}/{repo}/pulls?state=open' \
      '.[] | "#\(.number)\t\(if .draft then "draft" else "ready" end)\t\(.created_at)\t\(.updated_at)\t\(.head.ref)\t\(.title)\t\((.body // "")|gsub("[\r\n]+";" "))"' \
      | sort -t "$(printf '\t')" -k3,3
    return
  fi
  gh pr list --state open --limit 200 \
  --json number,isDraft,createdAt,updatedAt,headRefName,title,body \
  --jq 'sort_by(.createdAt)[] | "#\(.number)\t\(if .isDraft then "draft" else "ready" end)\t\(.createdAt)\t\(.updatedAt)\t\(.headRefName)\t\(.title)\t\(.body|gsub("[\r\n]+";" "))"'; }

# Files changed by one pull request. gh pr view returns at most 100
# files; a pull request with more (#1353 had 545, and overlap missed
# a README for #1388 in secure_me) is read page by page
# through the REST API, which lists up to 3000.
prfiles(){
  if rest; then restpages "repos/{owner}/{repo}/pulls/$1/files?" '.[].filename'; return; fi
  local out n
  out=$(gh pr view "$1" --json changedFiles,files --jq '"\(.changedFiles)", .files[].path')
  n=$(printf '%s\n' "$out" | head -1)
  if [ "$n" -gt "$(printf '%s\n' "$out" | sed 1d | grep -c .)" ] 2>/dev/null; then
    restpages "repos/{owner}/{repo}/pulls/$1/files?" '.[].filename'
  else
    printf '%s\n' "$out" | sed 1d | grep . || true
  fi; }

# Pull requests merged on or after $1 (YYYY-MM-DD), up to 300: number,
# merged day, title, body (newlines flattened).
prmerged(){
  if rest; then
    # Cloud sessions refuse the search API: closed pull requests, last
    # updated first (a merge updates one), until a page reaches before $1.
    for page in 1 2 3 4 5; do
      out=$(gh api "repos/{owner}/{repo}/pulls?state=closed&sort=updated&direction=desc&per_page=100&page=$page" \
        --jq '.[] | "\(.updated_at[0:10])\t#\(.number)\tmerged \((.merged_at // "")[0:10])\t\(.title)\t\((.body // "")|gsub("[\r\n]+";" "))"')
      [ -n "$out" ] || break
      printf '%s\n' "$out" | awk -F'\t' -v d="$1" '$3 != "merged " && substr($3,8) >= d' | cut -f2-
      if [ "$(printf '%s\n' "$out" | tail -1 | cut -f1)" \< "$1" ]; then break; fi
    done | head -300
    return
  fi
  gh pr list --state merged --limit 300 --search "merged:>=$1" --json number,mergedAt,title,body \
    --jq '.[] | "#\(.number)\tmerged \(.mergedAt[0:10])\t\(.title)\t\(.body|gsub("[\r\n]+";" "))"'; }

# The newest 1000 pull requests, newest first: branch, state (OPEN,
# MERGED or CLOSED), head commit, number, true when it carries the
# tested-pass label (no longer used, 2026-10-30), and the merge date (empty when not merged).
prall(){
  if rest; then
    # pulls lists newest first; ten pages are the newest 1000.
    for page in 1 2 3 4 5 6 7 8 9 10; do
      out=$(gh api "repos/{owner}/{repo}/pulls?state=all&per_page=100&page=$page" \
        --jq '.[] | "\(.head.ref)\t\(if .merged_at then "MERGED" elif .state == "open" then "OPEN" else "CLOSED" end)\t\(.head.sha)\t#\(.number)\t\([.labels[].name] | index("tested-pass") != null)\t\((.merged_at // "")[0:10])"')
      [ -n "$out" ] || break
      printf '%s\n' "$out"
    done
    return
  fi
  gh pr list --state all --limit 1000 --json headRefName,state,headRefOid,number,labels,mergedAt \
    --jq 'sort_by(-.number)[] | "\(.headRefName)\t\(.state)\t\(.headRefOid)\t#\(.number)\t\([.labels[].name] | index("tested-pass") != null)\t\((.mergedAt // "")[0:10])"'; }

# Ask once, here: the reads run in subshells, which cannot keep the answer.
case "${1:-}" in taken|prs|overlap|cleanup) rest || true ;; esac

notename(){ echo "docs/changes/$(date +%Y-%m-%d)-$(echo "$branch" | tr '/' '-').md"; }

case "${1:-}" in
taken)
  shift; [ $# -gt 0 ] || die "usage: coord.sh taken <words...> (\"|\" between other wordings)"
  words="$*"
  git fetch -q origin main
  echo "== Claimed on main (section 5, [~]):"
  git show origin/main:docs/PROJECT-HISTORY.md | grep -F -- '- [~]' | allwords "$words" | cut -c1-200 || true
  echo "== Change notes on main (docs/changes/, not yet rolled up):"
  for f in $(git ls-tree --name-only origin/main docs/changes/ 2>/dev/null | grep '\.md$' | grep -v README.md); do
    git show "origin/main:$f" | allwords "$words" | sed "s|^|$f: |" | cut -c1-200
  done
  echo "== Open pull requests (oldest first; the oldest claim wins):"
  prlines | allwords "$words" | cut -f1-3,5,6 || true
  # Already built? A different wording in a note can hide it (#216 and
  # #222 claimed what #209 had merged); read these before claiming.
  echo "== Done on main (section 5, [x]; the work may already be built):"
  git show origin/main:docs/PROJECT-HISTORY.md | grep -F -- '- [x]' | allwords "$words" | head -10 | cut -c1-200 || true
  since=$(since14)
  echo "== Merged since $since (pull requests, then commits on main):"
  prmerged "$since" | allwords "$words" | cut -f1-3 || true
  git log origin/main --no-merges --since="$since" --format='%h %ad %s' --date=short | allwords "$words" | head -10 || true
  ;;
prs)
  prlines | while IFS="$(printf '\t')" read -r num draft created updated head title body; do
    echo "$num $draft created $created updated $updated $head"
    echo "   $title"
    prfiles "${num#\#}" | sed 's/^/     /'
  done
  ;;
overlap)
  git fetch -q origin main
  mine=$( { git diff --name-only origin/main...HEAD; git diff --name-only HEAD; } | sort -u | grep -v '^docs/changes/' || true)
  [ -n "$mine" ] || { echo "This branch changes no files yet."; exit 0; }
  prlines | while IFS="$(printf '\t')" read -r num draft created updated head title body; do
    [ "$head" = "$branch" ] && continue
    both=$(prfiles "${num#\#}" | grep -Fx -f <(echo "$mine") || true)
    [ -n "$both" ] || continue
    echo "$num ($head): $title"
    echo "$both" | sed 's/^/     /'
  done
  echo "(docs/changes/ notes never conflict and are not listed.)"
  ;;
note)
  f=$(notename)
  [ ! -e "$f" ] || die "$f already exists"
  ls docs/changes/*"$(echo "$branch" | tr '/' '-')".md >/dev/null 2>&1 && die "this branch already has a note in docs/changes/"
  mkdir -p docs/changes
  sed -n '/^<!-- template -->$/,/^<!-- end template -->$/p' docs/changes/README.md | sed '1d;$d' > "$f"
  echo "$f"
  ;;
cleanup)
  # Owner's instruction 2026-10-12: once a pull request is merged or
  # closed, its session's worktree and branch go, so the machine keeps
  # only work in progress. Something is removed only when all of these
  # hold, so no session loses work:
  #   - the branch is an agent branch (AGENT_PREFIXES) and its newest pull request is
  #     merged or closed (an open one, a draft claim included, keeps it);
  #     (owner, 2026-10-30: no tested-pass label; a pull request is
  #     marked ready only once tested, so a merged one is finished); a
  #     closed, unmerged one was given up and goes;
  #   - every commit on it is on main or in that pull request's head;
  #   - its worktree has no uncommitted or untracked files (ignored
  #     build output goes with it);
  #   - it is not the worktree this runs in (unless --include-current,
  #     which the session-end hook passes).
  # Test databases, servers and files outside the repository are each
  # session's own to remove (AGENTS.md step 9).
  shift; dry=0; here=0
  for a in "$@"; do
    case $a in --dry-run) dry=1 ;; --include-current) here=1 ;; *) die "usage: coord.sh cleanup [--dry-run] [--include-current]" ;; esac
  done
  common=$(cd "$(git rev-parse --git-common-dir)" && pwd -P)
  main=$(dirname "$common")
  cur=$(pwd -P)
  # Acting from the main checkout lets the session-end hook remove the
  # worktree it runs in.
  cd "$main"
  # One cleanup at a time on this machine.
  lock="$common/coord-cleanup.lock"
  mkdir "$lock" 2>/dev/null || { echo "coord: another cleanup is running"; exit 0; }
  trap 'rmdir "$lock" 2>/dev/null' EXIT
  # Hand what tests started back to the machine once idle, when the
  # repository has scripts/ondemand.sh (secure_me's kit; not yet here).
  od="$root/scripts/ondemand.sh"
  if [ -x "$od" ]; then
    if [ $dry = 1 ]; then "$od" idle all --dry-run || true; else "$od" idle all || true; fi
  fi
  git fetch -q --prune origin
  # Every pull request: branch, state, head commit, number; newest first,
  # so the first line for a branch is its latest pull request.
  prs=$(prall)
  # finished <branch> <tip>: prints "#n STATE" when the branch may go.
  finished(){
    agent_branch "$1" || return 1
    l=$(printf '%s\n' "$prs" | awk -F'\t' -v b="$1" '$1==b{print; exit}')
    [ -n "$l" ] || return 1
    st=$(printf '%s' "$l" | cut -f2); oid=$(printf '%s' "$l" | cut -f3)
    case $st in MERGED|CLOSED) ;; *) return 1 ;; esac
    git merge-base --is-ancestor "$2" origin/main 2>/dev/null ||
      [ "$2" = "$oid" ] ||
      { git cat-file -e "$oid^{commit}" 2>/dev/null && git merge-base --is-ancestor "$2" "$oid"; } ||
      return 1
    echo "$(printf '%s' "$l" | cut -f4) $st"
  }
  act(){ [ $dry = 1 ] || "$@"; }
  would=""; [ $dry = 1 ] && would="would go: "
  removed=0 kept=0
  wts=$(git worktree list --porcelain | awk '/^worktree /{p=substr($0,10)} /^branch refs\/heads\//{print p "\t" substr($0,19)}')
  while IFS="$(printf '\t')" read -r p b; do
    [ -n "$p" ] || continue
    rp=$(cd "$p" 2>/dev/null && pwd -P) || continue
    [ "$rp" = "$main" ] && continue
    if [ "$rp" = "$cur" ] && [ $here = 0 ]; then continue; fi
    tip=$(git -C "$p" rev-parse HEAD 2>/dev/null) || continue
    why=$(finished "$b" "$tip") || continue
    if [ -n "$(git -C "$p" status --porcelain 2>/dev/null)" ]; then
      echo "kept $p ($b, $why): uncommitted or untracked files"
      kept=$((kept + 1)); continue
    fi
    echo "${would}removing $p and $b ($why)"
    act git worktree remove "$p" && act git branch -D "$b" >/dev/null && removed=$((removed + 1))
  done <<EOF2
$wts
EOF2
  # Branches with no worktree left.
  inuse=$(git worktree list --porcelain | awk '/^branch refs\/heads\//{print substr($0,19)}')
  for b in $(for a in $AGENT_PREFIXES; do git for-each-ref --format='%(refname:short)' "refs/heads/$a/"; done); do
    printf '%s\n' "$inuse" | grep -qFx "$b" && continue
    why=$(finished "$b" "$(git rev-parse "$b")") || continue
    echo "${would}deleting branch $b ($why)"
    act git branch -D "$b" >/dev/null && removed=$((removed + 1))
  done
  act git worktree prune
  echo "cleanup: $removed removed, $kept kept with uncommitted files$([ $dry = 1 ] && echo ' (dry run)')"
  ;;
*)
  sed -n '2,33p' "$0" | sed 's/^# \{0,1\}//'
  [ "${1:-}" = "" ] || exit 2
  ;;
esac
