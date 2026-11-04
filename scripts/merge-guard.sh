#!/usr/bin/env bash
# The merge gate for Claude sessions. Each change is tested once, by the
# session that made it, and the pull request is marked ready only when
# that test passed and nothing more needs testing (owner, 2026-10-30:
# "tested-pass concept not required as already today testing method
# defined to test any developed part only once. If further testing
# required then do not mark PR ready for merge."). So ready means
# mergeable. A PreToolUse hook in .claude/settings.json runs this
# before every Bash command. It refuses a command that merges a pull
# request (gh pr merge, or the REST merge call through gh api) while
# that pull request is a draft or still a "Claim:". History roll-up
# pull requests (rollup/ branches) may always merge.
#
# The repository is private on GitHub Free, which offers no branch
# protection or rulesets, so a merge on the GitHub website is not
# blocked here.
#
# Input: the hook's JSON on stdin. Exit 2 refuses the command (the
# reason goes to stderr); exit 0 lets it run. Runs under macOS bash 3.2.
set -uo pipefail

cmd=$(jq -r '.tool_input.command // empty' 2>/dev/null) || exit 0
[ -n "$cmd" ] || exit 0

refuse(){ echo "merge-guard: $*" >&2; exit 2; }

# Which pull requests the command merges: "gh pr merge [n|url|branch]"
# and "gh api ... pulls/<n>/merge".
targets=""
if printf '%s' "$cmd" | grep -Eq '(^|[^[:alnum:]_-])gh[[:space:]]+pr[[:space:]]+merge([[:space:]]|$)'; then
  arg=$(printf '%s' "$cmd" | sed -nE 's/.*gh[[:space:]]+pr[[:space:]]+merge[[:space:]]+([^-[:space:];&|][^[:space:];&|]*).*/\1/p' | head -1)
  targets="${arg:-CURRENT}"
fi
api=$(printf '%s' "$cmd" | grep -Eo 'pulls/[0-9]+/merge' | grep -Eo '[0-9]+' || true)
[ -n "$api" ] && targets="$targets $api"
[ -n "$targets" ] || exit 0

for t in $targets; do
  if [ "$t" = CURRENT ]; then
    info=$(gh pr view --json number,headRefName,isDraft,title 2>/dev/null)
  else
    info=$(gh pr view "$t" --json number,headRefName,isDraft,title 2>/dev/null)
  fi
  [ -n "$info" ] || refuse "cannot read pull request '$t' to check that it is ready; merge refused"
  n=$(printf '%s' "$info" | jq -r .number)
  ref=$(printf '%s' "$info" | jq -r .headRefName)
  case $ref in rollup/*) continue ;; esac
  [ "$(printf '%s' "$info" | jq -r .isDraft)" != true ] ||
    refuse "#$n is a draft: its own test has not passed or something still needs testing (AGENTS.md step 10)."
  case $(printf '%s' "$info" | jq -r .title) in
    Claim:*) refuse "#$n is still a claim (title starts with \"Claim:\"); it is not finished (AGENTS.md step 6)." ;;
  esac
done
exit 0
