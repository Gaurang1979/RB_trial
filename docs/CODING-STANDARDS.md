# Coding standards

The rules every change to Vision_System follows, people and Claude
sessions alike (adapted 2026-10-29 from secure_me's standards to this
repository's C++, .NET, Python and Flutter code). CI checks what a
machine can check; a pull request is not ready while CI is red. The
process for working beside other sessions is in `AGENTS.md`.

## Line safety (all code that decides a result)

- **Fail safe to NG.** A lost camera, a lost PLC heartbeat, a job that
  did not load, a model whose checksum or licence does not match, a
  time-out or an exception all give NG and an alarm, never OK.
- **Deterministic results:** the same image, job and model give the same
  result in Vision Studio, the SDK and the station runner (requirements
  section 15, acceptance). A test that compares them guards each tool.
- **Every result is recorded** with job version, model version and cycle
  time, so any part can be traced (requirements section 12).
- **Every change to a running job is versioned, audited and undoable**;
  21 CFR Part 11 mode adds e-signatures.

## C++ (`engine/`)

- C++17 today, C++20 for new tool DLLs (requirements section 14). Build
  with warnings on (`/W4`, `-Wall -Wextra`) and fix new warnings.
- **The C API is the contract** (`engine/include/*.h`): plain C types,
  caller-owned buffers, an error code on every call, no C++ exceptions
  across it. A change to it changes the .NET P/Invoke (`dotnet/*/Native.cs`,
  `OcrNative.cs`) in the same pull request and keeps old callers working.
- RAII for every resource; no raw `new`/`delete`; bounds checked on every
  image access built from input. Hot paths avoid per-frame allocation.
- Thread safety stated in each header; the reader pool never shares
  mutable state between threads without a lock.
- Tests in `engine/tests/` with `ctest`; a decode or OCR change adds a
  case and a benchmark run on the truth set (`tools/bench/batch_check.py`, run once by the session that made the change).

## .NET (`dotnet/`)

- .NET 10 (the current LTS release), nullable reference types on, warnings as errors for new
  projects. `dotnet format` style.
- `async` end to end for I/O; every outgoing call has a time-out and a
  cancellation token; nothing slow under a lock.
- Blazor pages keep logic in services (`Services/`), tested in a
  `*.Tests` project with xUnit.
- Web endpoints are authenticated and authorised on the server; roles as
  in requirements section 12; anti-forgery on forms; TLS outside the
  local machine.

## Plant server (`plant_server/`, Python)

- Python 3.10+, FastAPI, stdlib first; every change with pytest tests that
  run on SQLite and on PostgreSQL (`SPI_TEST_PG_DSN`).
- Raw facts at the edge, meaning in the backend: gateways and stations send
  what they saw; states, OEE and alarms are derived on the server.
- Every event is deduplicated by its device and sequence number; an invalid
  event is quarantined with its reason, never dropped silently; gaps are
  reported.
- Every endpoint checks the user's role and scope (org, plant, line) on the
  server; device tokens are stored only as hashes.

## Python (`ml/`)

- Python 3.11+, type hints on new code, `ruff` clean. Training is
  reproducible: seeds, dataset hash and code commit recorded with each
  exported model. Exported ONNX models are checked against the PyTorch
  output before they are kept.

## Dart and Flutter (`mobile/`)

- `flutter analyze` with the project's lints, no warnings; `dart format`;
  widget and unit tests (`flutter test`), integration tests in
  `integration_test/`.
- Strings only from ARB files in the 15 languages; no text in code.
- Tokens in the platform's secure storage, never in shared preferences or
  files. Certificates are checked; no "allow bad certificate" in release.
- The app never sends a command that changes the line (job change, stop,
  tolerance) without the server checking the user's role and, in Part 11
  mode, an e-signature.

## Security rules (all code)

- **No secrets in the repository**, in logs or in error messages: keys,
  tokens, passwords, licence signing keys and customer data come from the
  environment, a file outside the repository or a vault. Tests generate
  throwaway keys.
- **Customer images never in the repository** (`.gitignore` refuses image
  types); benchmarks read `VS_IMAGES`.
- **Input:** strictly decoded and sized; no string-built SQL
  (parameters only), no shell strings, no path built from input without
  checking it stays in its folder. Job and model files are signed and
  checked before loading.
- **Crypto:** platform libraries only (.NET `System.Security.Cryptography`,
  Dart `cryptography`, OpenSSL); Ed25519 for signatures, AES-GCM for
  encryption, TLS 1.2 or later. No home-made crypto.
- **Licences of dependencies** are checked before use: no GPL or AGPL code
  linked into shipped DLLs or apps without a commercial licence
  (requirements section 14).

## Shell scripts

`set -euo pipefail`; quote every variable; macOS bash 3.2, Linux and
Git Bash on Windows.

## Commits and pull requests

- One topic per pull request, claimed first (`AGENTS.md`). Commit messages
  say what changed and why.
- Main is only changed by merged pull requests. Never push to main, never
  rebase or force-push; to keep up, merge `origin/main`.
- Before ready: the code compiles, your new tests pass,
  `scripts/coord.sh overlap` is clean or its overlaps are named with the
  merge order, CI is green.

## What the owner sets on GitHub (Settings)

- Settings > Actions > General > Workflow permissions: "Read and write
  permissions" and "Allow GitHub Actions to create and approve pull
  requests" (the history roll-up needs both).
- Branch protection on `main` when the plan allows it; until then the
  owner merges only ready (not draft) pull requests.
