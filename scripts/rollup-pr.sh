#!/usr/bin/env bash
# Runs the history roll-up and keeps one pull request with the result
# (the GitHub Action .github/workflows/history-rollup.yml runs this).
#
# - If a "History roll-up" pull request is open, its branch is brought up
#   to date by merging origin/main (new notes come in), the roll-up runs
#   again and the result is pushed: one standing pull request.
# - Otherwise a new branch rollup/history-<date> is made from main.
# - A roll-up claimed by an agent (any other open pull request with
#   "roll-up" in its title) is left alone; this does nothing meanwhile.
# Never force-pushes and never pushes to main. Needs git, gh and python3.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
git config user.name "github-actions[bot]"
git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
git fetch -q origin

open=$(gh pr list --state open --limit 200 --json number,headRefName,title \
  --jq '.[] | select(.headRefName | startswith("rollup/history-")) | "\(.number) \(.headRefName)"' | head -1)
others=$(gh pr list --state open --limit 200 --json number,headRefName,title \
  --jq '.[] | select((.headRefName | startswith("rollup/history-")) | not) | select(.title | test("^(claim: *)?history roll-?up"; "i")) | "#\(.number) \(.title)"')
if [ -n "$others" ]; then
  echo "rollup: an agent's roll-up is open, leaving it to them:"; echo "$others"; exit 0
fi

if [ -n "$open" ]; then
  pr=${open%% *}; branch=${open#* }
  git checkout -q -B "$branch" "origin/$branch"
  if ! git merge -q --no-edit origin/main; then
    # main changed the history file where this roll-up did. Its notes
    # are still on main (only this branch deleted them), so closing it
    # and rolling up again from main loses nothing.
    git merge --abort || true
    gh pr close "$pr" --delete-branch --comment "Superseded: main changed the history file where this roll-up did (a merge conflict). A new roll-up pull request is being made from main with the same notes."
    open=""
  fi
fi
if [ -z "$open" ]; then
  pr=""; branch="rollup/history-$(date -u +%Y%m%d-%H%M%S)"
  git checkout -q -B "$branch" origin/main
fi

summary=$(mktemp)
python3 scripts/rollup.py --summary "$summary"
git add -A docs/PROJECT-HISTORY.md docs/changes
if ! git diff --cached --quiet; then
  n=$(sed -n 's/^Rolled up \([0-9]*\) .*/\1/p' "$summary")
  git commit -q -m "History roll-up: ${n:-0} note(s)" -m "$(cat "$summary")"
fi
if [ "$(git rev-list --count "origin/main..HEAD")" = 0 ]; then
  echo "rollup: nothing to roll up"; exit 0
fi
git push -q origin "HEAD:refs/heads/$branch"

if [ -z "$pr" ]; then
  body=$(mktemp)
  { echo "Automatic history roll-up (docs/PROJECT-HISTORY.md section 7, rule 12), made by scripts/rollup.py."
    echo "This pull request is kept up to date as more notes reach main; merge it whenever you like."
    echo; cat "$summary"
    echo; echo "🤖 Made by the History roll-up GitHub Action"; } > "$body"
  if ! gh pr create --base main --head "$branch" --title "History roll-up" --body-file "$body"; then
    # Do not leave a branch behind for every refused run.
    git push -q origin ":refs/heads/$branch" || true
    echo "rollup: GitHub refused to open the roll-up pull request. Turn on Settings > Actions > General >"
    echo "rollup: \"Allow GitHub Actions to create and approve pull requests\", then run this workflow again."
    echo "rollup: (or run scripts/rollup.py on a branch by hand)."
    exit 1
  fi
elif [ -s "$summary" ]; then
  gh pr comment "$pr" --body-file "$summary"
fi
