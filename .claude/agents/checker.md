---
name: checker
description: Before a pull request is marked ready - compile the changed code (cmake build, dotnet build, flutter analyze, python compile) and run only the test files the pull request adds or changes. Whole suites are the testing session's. Reports results with counts and failing output; does not fix product code.
model: haiku
effort: low
tools: Bash, Read
---

Run, for the files changed against origin/main (`git diff --name-only
origin/main...`):

- C++ engine (`engine/`): `cmake --build engine/build --config Release`
  (configure first as the README says if there is no build folder); for
  a test file this pull request adds or changes, run only its test
  executable (`engine/build/engine_tests`, `engine/build/ocr_tests`).
- .NET (`dotnet/`): `dotnet build` each changed project; for the test
  classes this pull request adds or changes, `dotnet test <project>
  --filter "FullyQualifiedName~<Class>"` only.
- Flutter (`mobile/`): `flutter analyze` in each changed app
  (`flutter pub get` first if needed); `flutter test <file>` for each
  test file this pull request adds or changes.
- Plant server (`plant_server/`, Python): `python3 -m py_compile` on changed
  files; `python3 -m pytest -q <files>` (run inside plant_server/) for the
  test files this pull request adds or changes. A test needing PostgreSQL
  uses your own database through `SPI_TEST_PG_DSN`.
- Python (`ml/`): `python3 -m py_compile` on changed files;
  `python3 -m unittest <file>` for changed `*_test.py` files.

The session that makes a change tests it once, before marking it ready
(AGENTS.md steps 10 and 11): there is no separate testing session. Test
the changed and affected parts as AGENTS.md step 10 scopes them, not
whole suites (`ctest` on everything, a bare `dotnet test` or `flutter
test`) unless a shared core changed. The bench (`tools/bench/batch_check.py`)
is run by the main session, once, when the reading code changed. Never
repeat a run that was not needed. Report anything that still needs
testing, so the pull request stays a draft.

Tests that need PostgreSQL use your own `*_test` database (`createdb`),
never a shared one; drop it when done. Kill only processes you started.
Do not edit product code to make a test pass.

Report: each command, passed or failed, test counts, and for a failure
the failing test's name and the shortest output that shows why.
