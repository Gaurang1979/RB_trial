#!/usr/bin/env bash
# What a session runs to test its own change once (AGENTS.md steps 10, 11).
#
# Usage:
#   scripts/heavy-pick.sh queue          ready (not draft) pull requests whose
#                                        current head has no heavy-test result
#                                        yet, oldest first: number, branch, title;
#                                        docs-only pull requests carrying
#                                        testing-not-required are left out
#   scripts/heavy-pick.sh docs-only [--label]
#                                        open ready pull requests that change
#                                        only documentation, with "labelled" or
#                                        "unlabelled"; --label puts
#                                        testing-not-required on the unlabelled
#   scripts/heavy-pick.sh is-docs-only N exit 0 when every file pull request N
#                                        changes is documentation (docs/** or
#                                        *.md, renames by both names); else
#                                        prints the other files and exits 1
#   scripts/heavy-pick.sh tests [BASE]   the heavy tests for what HEAD changes
#                                        against BASE (default origin/main),
#                                        one per line, "<kind> <path> exclusive":
#     bench  tools/bench          engine/ changed: read rate and time on the
#                                 truth set (needs the local image folder,
#                                 VS_IMAGES; images are customer data)
#     ocr    tools/bench          engine/src/ocr or ml/ocr changed: OCR / OCV
#                                 accuracy and false accepts
#     soak   <job>                a line runtime (LineRunner, VisionStudio,
#                                 Station services) changed: long run, ask
#                                 the owner first
#     mobile mobile/<app>         a Flutter app changed: its integration_test
#     windows .github/workflows   the Windows package: GitHub Actions builds
#                                 it on every push (read its result)
#   Every heavy test runs alone (exclusive) until the repository has lanes.
#   scripts/heavy-pick.sh units [BASE]   the unit tests for what HEAD changes
#                                        against BASE, as commands with a
#                                        "# why" line each: engine ctest when
#                                        engine/ changed; the .NET test
#                                        projects of the changed projects and
#                                        of the projects that reference them;
#                                        flutter test on the tests of changed
#                                        lib/ files and their feature folders;
#                                        python unittest for changed ml/ tests;
#                                        pytest for plant_server/: the changed
#                                        test files and tests/test_<module>.py
#                                        of changed modules, the whole suite
#                                        when spi_common/ or backend/store.py
#                                        (shared by every module) changed;
#                                        a whole app or solution when a shared
#                                        core changed (AGENTS.md step 10)
#
# A heavy-test result is a pull request comment holding the marker
#   <!-- heavy-test sha=<head sha> result=pass|fail batch=<id> -->
# A new push makes the pull request untested again.
#
# Needs git, and for queue, docs-only and is-docs-only an
# authenticated gh. Runs under macOS bash 3.2 and Git Bash on Windows.
set -euo pipefail
die(){ echo "heavy-pick: $*" >&2; exit 2; }
cd "$(git rev-parse --show-toplevel)"

NOTEST=testing-not-required
# is_doc_files: reads file paths on stdin; prints those that are not
# documentation and fails when there are any, or no paths at all.
is_doc_files(){ awk '{ n++; if ($0 !~ /^docs\// && $0 !~ /\.md$/) { print; bad = 1 } } END { exit (n == 0 || bad) }'; }
# docs_only N: is_doc_files for pull request N's current files. The files
# API lists at most 3000; a count that differs from changedFiles fails.
docs_only(){
  local n=$1 files want
  want=$(gh pr view "$n" --json changedFiles --jq .changedFiles) || return 2
  files=$(gh api --paginate "repos/{owner}/{repo}/pulls/$n/files" \
    --jq '.[] | "f\t\(.filename)", (.previous_filename // empty | "p\t\(.)")') || return 2
  [ "$(printf '%s\n' "$files" | grep -c '^f')" = "$want" ] || { echo "(cannot list all $want changed files)"; return 1; }
  printf '%s\n' "$files" | cut -f2- | is_doc_files
}

case "${1:-}" in
queue)
  gh pr list --state open --limit 200 --json number,isDraft,createdAt,headRefName,headRefOid,title,labels \
    --jq 'sort_by(.createdAt)[] | select(.isDraft|not) | "\(.number)\t\(.headRefOid)\t\([.labels[].name] | index("'$NOTEST'") != null)\t\(.headRefName)\t\(.title)"' |
  while IFS="$(printf '\t')" read -r n sha notest ref title; do
    if [ "$notest" = true ] && docs_only "$n" >/dev/null; then continue; fi
    if gh pr view "$n" --json comments --jq '.comments[].body' | grep -qF "heavy-test sha=$sha "; then continue; fi
    printf '#%s\t%s\t%s\n' "$n" "$ref" "$title"
  done
  ;;
docs-only)
  [ "${2:-}" = "" ] || [ "${2:-}" = --label ] || die "usage: heavy-pick.sh docs-only [--label]"
  [ "${2:-}" != --label ] || gh label create "$NOTEST" --color c5def5 \
    --description "Changes only documentation; the owner may merge without running code tests" >/dev/null 2>&1 || true
  gh pr list --state open --limit 200 --json number,isDraft,createdAt,labels,title \
    --jq 'sort_by(.createdAt)[] | select(.isDraft|not) | "\(.number)\t\([.labels[].name] | index("'$NOTEST'") != null)\t\(.title)"' |
  while IFS="$(printf '\t')" read -r n notest title; do
    docs_only "$n" >/dev/null || continue
    state=labelled
    if [ "$notest" != true ]; then
      state=unlabelled
      if [ "${2:-}" = --label ]; then gh pr edit "$n" --add-label "$NOTEST" >/dev/null && state="labelled now"; fi
    fi
    printf '#%s\t%s\t%s\n' "$n" "$state" "$title"
  done
  ;;
is-docs-only)
  [ -n "${2:-}" ] || die "usage: heavy-pick.sh is-docs-only N"
  docs_only "${2#\#}"
  ;;
tests)
  base="${2:-origin/main}"
  files=$(git diff --name-only "$base"...HEAD)
  [ -n "$files" ] || exit 0
  {
    if echo "$files" | grep -qE '^engine/(src|include|CMakeLists)'; then echo "bench tools/bench exclusive"; fi
    if echo "$files" | grep -qE '^(engine/src/ocr|ml/ocr|tools/bench/ocr)'; then echo "ocr tools/bench/ocr_bench.cpp exclusive"; fi
    if echo "$files" | grep -qE '^dotnet/(LineRunner|VisionStudio|VisionCore|HikCamera|PlantServer|Station)[^/]*/'; then echo "soak dotnet exclusive"; fi
    for a in $(echo "$files" | sed -n 's#^mobile/\([^/]*\)/.*#\1#p' | sort -u); do
      [ -d "mobile/$a/integration_test" ] && echo "mobile mobile/$a exclusive"
    done
    if echo "$files" | grep -qE '^plant_server/(edge|backend/mqtt)'; then echo "plant-server plant_server/tests/test_mqtt.py exclusive"; fi
    if echo "$files" | grep -qvE '^(docs/|.*\.md$|mobile/|ml/|plant_server/)'; then echo "windows .github/workflows/windows-build.yml exclusive"; fi
  } | sort -u
  ;;
units)
  base="${2:-origin/main}"
  files=$(git diff --name-only "$base"...HEAD)
  [ -n "$files" ] || exit 0
  if echo "$files" | grep -qE '^engine/'; then
    echo "# why: engine/ changed (C++ engine and OCR)"
    echo "cmake --build engine/build --config Release && ctest --test-dir engine/build -C Release --output-on-failure"
  fi
  # .NET: the changed projects, then every project that references one of
  # them, repeated until nothing new is added; then the test projects among them.
  projs=$(echo "$files" | sed -n 's#^dotnet/\([^/]*\)/.*#\1#p' | sort -u)
  if [ -n "$projs" ]; then
    all="$projs"
    while :; do
      more=$(for p in $all; do grep -l "$p/$p.csproj\|$p\\\\$p.csproj" dotnet/*/*.csproj 2>/dev/null || true; done |
        sed 's#^dotnet/\([^/]*\)/.*#\1#' | sort -u)
      new=$(printf '%s\n%s\n' "$all" "$more" | grep . | sort -u)
      [ "$new" = "$all" ] && break
      all=$new
    done
    for p in $all; do
      case $p in *.Tests) echo "# why: $p covers $(echo $projs | tr ' ' ',')"; echo "dotnet test dotnet/$p -c Release" ;; esac
    done
    for p in $projs; do
      case $p in *.Tests) ;; *) echo "# why: $p changed (build)"; echo "dotnet build dotnet/$p -c Release" ;; esac
    done
  fi
  # Flutter apps under mobile/: tests of changed lib/ files and their
  # feature folders; the whole app when a shared core changed.
  for a in $(echo "$files" | sed -n 's#^mobile/\([^/]*\)/.*#\1#p' | sort -u); do
    echo "# why: mobile/$a changed"
    echo "(cd mobile/$a && flutter analyze)"
    if echo "$files" | grep -qE "^mobile/$a/(lib/(core|theme|l10n|router|models)/|pubspec|test/helpers)"; then
      echo "# why: a shared core of mobile/$a changed: whole app"
      echo "(cd mobile/$a && flutter test)"
    else
      t=$(echo "$files" | sed -n "s#^mobile/$a/lib/\(features/[^/]*\)/.*#test/\1#p; s#^mobile/$a/\(test/.*_test\.dart\)\$#\1#p" | sort -u)
      t=$(for x in $t; do [ -e "mobile/$a/$x" ] && echo "$x"; done)
      [ -z "$t" ] || echo "(cd mobile/$a && flutter test $(echo $t))"
    fi
  done
  # Plant server (Python, pytest): the changed tests and the tests of changed
  # modules; the whole suite when a module every other one uses changed.
  ps=$(echo "$files" | grep -E '^plant_server/.*\.py$' || true)
  if [ -n "$ps" ]; then
    if echo "$ps" | grep -qE '^plant_server/(spi_common/|backend/store\.py|tests/conftest\.py)'; then
      echo "# why: a shared part of plant_server changed: whole suite"
      echo "(cd plant_server && python3 -m pytest -q)"
    else
      t=$(for f in $ps; do
        # Leading "(" on each pattern: bash 3.2 (macOS) misparses ")" inside $( ).
        case $f in
          (plant_server/tests/test_*.py) echo "${f#plant_server/}" ;;
          (*) b=$(basename "$f" .py); [ -e "plant_server/tests/test_$b.py" ] && echo "tests/test_$b.py" ;;
        esac
      done | sort -u)
      [ -z "$t" ] || { echo "# why: plant_server modules changed"; echo "(cd plant_server && python3 -m pytest -q $(echo $t))"; }
    fi
  fi
  # Python: changed *_test.py files and the tests beside changed modules.
  py=$(echo "$files" | grep -E '^ml/.*\.py$' || true)
  for f in $py; do
    case $f in *_test.py) t=$f ;; *) t=${f%.py}_test.py ;; esac
    [ -e "$t" ] && { echo "# why: $f changed"; echo "python3 -m unittest $t"; }
  done | awk '!seen[$0]++'
  ;;
*) die "usage: heavy-pick.sh queue | label | docs-only [--label] | is-docs-only N | tests [BASE] | units [BASE]" ;;
esac
