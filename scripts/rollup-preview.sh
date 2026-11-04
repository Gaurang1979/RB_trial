#!/usr/bin/env bash
# Roll-up preview for one pull request (the GitHub Action
# .github/workflows/history-rollup.yml runs this when a pull request is
# marked ready, and again on each push while it is ready).
#
# Runs scripts/rollup.py on just the change notes this pull request adds
# or changes, in the Action's own checkout (nothing is pushed), and keeps
# one comment on the pull request with what the roll-up will add to
# docs/PROJECT-HISTORY.md once it is merged: the summary, the pending
# items, anything that would need a person, and the lines themselves.
# The real roll-up still runs only after the merge (scripts/rollup-pr.sh),
# so the history holds merged work only.
#
# Usage: scripts/rollup-preview.sh <pull request number>
# Needs git, gh (GH_TOKEN) and python3, and origin/main fetched.
set -euo pipefail
pr=${1:?usage: scripts/rollup-preview.sh <pull request number>}
cd "$(git rev-parse --show-toplevel)"
marker="<!-- history-rollup-preview -->"

notes=$(git diff --name-only --diff-filter=AM origin/main...HEAD -- 'docs/changes/*.md' | grep -v '/README\.md$' || true)
body=$(mktemp)
{
  echo "$marker"
  echo "### History roll-up preview"
  echo
  if [ -z "$notes" ]; then
    echo "This pull request adds no change note in \`docs/changes/\`, so the roll-up will add nothing for it."
    echo "Every pull request needs one (AGENTS.md step 3, \`scripts/coord.sh note\`)."
  else
    summary=$(mktemp)
    # shellcheck disable=SC2086
    python3 scripts/rollup.py --only $notes --summary "$summary" >/dev/null
    if grep -q '^Left alone' "$summary" || [ ! -s "$summary" ]; then
      echo "**Not ready to roll up:** a note here is not \`Status: done\`. Set it to done and fill it in (AGENTS.md step 6)."
      echo
    fi
    if [ -s "$summary" ]; then cat "$summary"; echo; fi
    added=$(git diff -U0 -- docs/PROJECT-HISTORY.md | grep '^+' | grep -v '^+++' | cut -c2- || true)
    if [ -n "$added" ]; then
      echo "<details><summary>Lines it will add to docs/PROJECT-HISTORY.md</summary>"
      echo
      echo '```markdown'
      echo "$added"
      echo '```'
      echo "</details>"
      echo
    fi
    echo "After the merge, the History roll-up pull request takes these at once."
    git checkout -q -- docs/PROJECT-HISTORY.md docs/changes
  fi
} > "$body"

repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner)
id=$(gh api "repos/$repo/issues/$pr/comments" --paginate \
  --jq ".[] | select(.body | startswith(\"$marker\")) | .id" | head -1)
if [ -n "$id" ]; then
  gh api -X PATCH "repos/$repo/issues/comments/$id" -F "body=@$body" >/dev/null
else
  gh pr comment "$pr" --body-file "$body"
fi
