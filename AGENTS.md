# Agent rules for Vision_System

**This is the one rule file for every agent** that works on this repository:
Claude Code, Antigravity, Gemini, Codex and any other AI agent, their
subagents and helpers, and scripts acting for them (owner, 2026-11-03:
"Can you make single file which must be followed by claude, antigravity
and others"; requirements 17.9, AGR-01). `CLAUDE.md` and `GEMINI.md` only
import this file, so every tool loads the same rules. Change a rule here,
never in those files. One section at the end applies to Claude Code only;
everything else applies to every agent.

Several agents work on this repository at the same time. The full
process is in section 7 of [docs/PROJECT-HISTORY.md](docs/PROJECT-HISTORY.md);
these are the steps you must not skip. They are the process the owner
set for secure_me, adopted here on 2026-10-29 (owner: "Copy all points
and startup kit which are important from secure_me repo to
Vision_System repo").

One project (owner, 2026-10-29): the line PC software (C++ engine, .NET),
the plant server (`plant_server/`, Python, merged from production_measurement
with its history) and the mobile app (`mobile/`, Flutter) live here.

**Your agent name** is one of `claude`, `antigravity`, `gemini` or
`codex` (requirements 17.9, AGR-04). Name your branches `<agent>/<topic>`
and label your pull requests with your agent (for example `Antigravity`).
`scripts/coord.sh` treats branches of all four alike. A new agent asks the
owner to add its name to `AGENT_PREFIXES` in `scripts/coord.sh` and here
before it makes its first branch.

Read first: [docs/PROJECT-HISTORY.md](docs/PROJECT-HISTORY.md) (state
and open items), [docs/REQUIREMENTS-VISION-SYSTEM.md](docs/REQUIREMENTS-VISION-SYSTEM.md)
(the source of truth), [docs/vision-system/README.md](docs/vision-system/README.md)
(design documents) and [docs/CODING-STANDARDS.md](docs/CODING-STANDARDS.md).

0. **Requirement first, and only the owner merges (owner, 2026-11-03).**
   - **Any change, decision or request must be registered in the common
     requirement document ([docs/REQUIREMENTS-VISION-SYSTEM.md](docs/REQUIREMENTS-VISION-SYSTEM.md))
     first, before acting** (owner: "any change, decision or request must
     be registered in common requirement document first before acting.
     Make this rule for all agents."; REQ-01..04). Register the user
     request, feature scope, design decision, acceptance criteria and
     affected modules there BEFORE writing implementation code or changing
     project files. A new request is first added as a new item.
   - **Cite the item you serve.** Every commit, pull request and change
     note names the requirement section and item it implements (for
     example "Req 17.7 ENV-05", "AS-13", "PROT-02"), not REQ-01..04,
     which are the rule itself (REQ-03; requirements 17.9, AGR-07).
   - **One common document** (owner, 2026-11-03, from PR #262;
     requirements 17.9, AGR-08). Every requirement, decision and spec is
     written into the requirement document at its relevant section, not
     into separate competing proposal files, scattered designs or
     parallel specifications. Design documents the requirement document
     links (`docs/vision-system/`) are its parts, kept up to date, not
     rivals. Product requirements never go into this rule file.
   - **One UI baseline** (owner, 2026-11-03, from PR #262; AGR-08). The
     Vision Studio Workbench (`design/prototypes/vision-studio/index.html`,
     Operator Run and Engineer Workspace) is the permanent layout. Every
     agent's additions build on top of it and never change the layout.
   - **DO NOT MERGE DIRECTLY ANYTHING ON MAIN** (owner, 2026-11-03: "do
     not merge directly anything on main. i will check and manually
     update always."; REQ-05). No agent, subagent, script or tool ever runs
     `gh pr merge`, calls the merge API, pushes to `main` or merges a pull
     request into `main`. All work stops at the tested pull request,
     left open for the owner to check and merge by hand.

1. **Own worktree.** Never switch branches in the shared checkout:
   `git fetch origin && git worktree add <dir> -b <agent>/<topic> origin/main`,
   then `git -C <dir> submodule update --init` (vs-runtime builds with
   `third_party/remote-service`; a worktree does not fetch submodules by itself).
   Keep the worktree until the pull request is merged or closed
   (step 9); never remove it before.
2. **Check before choosing.** Read section 5 and `docs/changes/` on main,
   then run `scripts/coord.sh taken <words>`. An item is taken if main
   marks it `[~]`, or any open pull request claims it, drafts included.
   Never start a taken item. If the owner asks for one, say who holds it
   and ask first. Also read its "Done on main" and "Merged since"
   sections, and try other wordings (`taken "oee | line efficiency"`):
   the work may already be built under another name.
3. **Claim first, before reading code.** Run `scripts/coord.sh note` and
   fill in the header (`Status: claimed`). Commit only that file, push,
   and open a **draft** pull request titled `Claim: <item>`, using the
   template. Then run `scripts/coord.sh taken` again: if an older pull
   request claims the same item, close yours.
4. **Stay in your lane.** Before editing, and again before marking the
   pull request ready, run `scripts/coord.sh overlap`. Name every
   overlapping pull request in yours, with the merge order. Never edit
   files outside the item you claimed.
5. **Never append to docs/PROJECT-HISTORY.md.** Timeline, decision,
   section 5 and update-log entries go in your own `docs/changes/` note,
   in the forms `docs/changes/README.md` gives. List follow-ups you
   leave under `## Pending` in the note; each becomes an open section 5
   item. The roll-up runs by itself (GitHub Action
   `history-rollup.yml`): when the pull request is marked ready it
   comments a preview of what the note adds, and right after the merge
   it copies the note into the "History roll-up" pull request. Do not
   claim roll-ups yourself.
6. **Finish.** Set the note to `Status: done` and fill it in, with any
   `## Pending` follow-ups. Retitle the pull request (drop `Claim:`) and
   mark it ready. Ready means tested once and mergeable (step 10): if
   anything still needs testing, leave it a draft and say what is left.
   Check the roll-up preview comment and fix the note if it is wrong.
   Only the owner merges. NEVER merge directly to main (owner directive: "do not merge directly anything on main. i will check and manually update always"). Leave all PRs open for the owner to check and manually merge. Keep your worktree until the merge: a problem
   found before it is fixed there.
7. **Keeping up with main:** merge `origin/main` into your branch. Never
   rebase, never force-push, never push to main.
8. **Handing work to another session** (a task chip or a subagent) hands
   over the item. Do not do it yourself as well. The new session claims
   it as in step 3.
9. **Clean up after the merge or close.** Run `scripts/coord.sh cleanup`
   when your session starts and when it ends (Claude Code's hooks do it
   for Claude): it removes every worktree and agent branch (`claude/`,
   `antigravity/`, `gemini/`, `codex/`) whose pull request is merged, or closed unmerged, and that
   holds nothing uncommitted or unpushed. **No worktree is removed
   before its pull request is merged or closed.** Once yours is, run
   `scripts/coord.sh cleanup --include-current` yourself, and remove
   what only you know about: your `*_test` databases (`dropdb`), servers,
   emulators and simulators you started (your own PIDs only), and your
   scratch files, binaries and logs outside the repository.

**Keep keys, tokens, licence secrets and customer data out of the
repository.** Sample and production images are customer data: keep them
in a local folder (`VS_IMAGES`) and pass its path to the tools.

**Patents: build from them, never stop for them** (owner, 2026-11-01,
after advice from his lawyer: "Do not lookup for patent validity and
valid countries. Simply build the tools, take ideas from patent and
improve tools. We are not copying their exact model or codes and as per
my discussion with lawyer we can use patented methods with additional or
changed or improved approach. Always use patented methods with twist or
without twist. In future do not stop development due to patent
issue."). Take the best method from any patent or vendor and build it,
improved where we can; cite the patent as the idea's source. Don't
check validity, expiry or territories, don't design around a patent,
and don't hold a pull request or add a "legal review" note for one.
Never copy a vendor's code or model.

**Line safety comes first.** A change must never let a bad part pass:
when the camera, the PLC link, the job, a model or the licence is in
doubt, the result is NG and the line is told: zero false accepts
(requirements sections 12 and 18.5).

**Product Security, IP Protection & Camouflage Rules (MANDATORY FOR ALL SESSIONS - OWNER DIRECTIVE 2026-11-02)**:
Protect tool names, algorithms, and models from competitors and factory visitors. Every agent must always follow this pattern without needing the owner to repeat it:
1. **Process & Taskbar Camouflage**:
   - Binaries, services, and running process names must never disclose proprietary algorithm names (`Thermal`, `PatchCore`, `Photometric`, `Polarimetric`, `Deform`, `Haystack`).
   - Use generic industrial hardware/runtime names: e.g. `AdvantechEdgeHost.exe`, `SysIoRuntime.exe`, `FactoryRuntimeService.exe`.
   - Windows taskbar and HMI window titles must display generic machine/line identifiers (e.g. `"Machine Control System — Line 01"` or `CamouflageLabels.GetSafeProcessTitle()`), never proprietary software or engine names.
2. **Binary Monolith & Opaque Dispatcher (`corert64.dll` / `sys_dispatch`)**:
   - Never ship descriptive individual DLLs (`vs_thermal.dll`, `vs_anomaly.dll`, `vs_polar.dll`) on customer PCs.
   - Route native calls through the opaque unified dispatcher `corert64.dll` (`sys_dispatch(uint32_t op_code, ...)`), hiding algorithm names from export tables and string dumps.
3. **Model & Asset Protection (`SecureAssetVault`)**:
   - Deep learning ONNX weights and recipe templates must be stored on disk as encrypted `.dat` blobs (AES-256-GCM / `SecureAssetVault`) with generic cache names (e.g. `cache/0a7f19.dat`).
   - Decrypt strictly in volatile RAM buffers; never write unencrypted model weights or decoded templates to disk.
4. **Competitor-Proof UI Terminology**:
   - All external UI labels in `VisionStudio` and `Dashboard` must use standard factory terms from `CamouflageLabels`:
     - Thermal Sealing -> "Seal Quality Check"
     - Photometric Stereo -> "Surface Texture Check"
     - Polarimetric Glare Decoupler -> "Anti-Glare Filter"
     - PatchCore Anomaly -> "Pattern Match"
     - Overview Haystack -> "Process Stability Map"
     - Non-Rigid Deformer -> "Pouch Alignment"
5. **.NET Compilation, Native AOT & Symbol Stripping (MANDATORY FOR EVERY VERSION RELEASE)**:
   - Standalone runners and background services (`LineRunner`, `StationAgent`, `StationGuard`): Must be published with Native AOT (`-p:PublishAot=true -p:StripSymbols=true -p:DebugType=none -p:DebugSymbols=false`). This compiles C# directly to machine code, achieving **0.00% decompilation** in ILSpy / dnSpy / dotPeek.
   - Web HMIs / Dashboards (`VisionStudio`, `Dashboard`): Must use `<PublishSingleFile>true</PublishSingleFile>`, `<StripSymbols>true</StripSymbols>`, `<DebugType>none</DebugType>`, `<DebugSymbols>false</DebugSymbols>` so individual DLLs cannot be decompiled with `ILSpy`/`dnSpy`. All proprietary inspection algorithms must be called through `corert64.dll` native dispatcher.
6. **Staged Version Release Lifecycle (MANDATORY FOR ALL DEPLOYMENTS)**:
   - Every software version release must follow the 4-stage rollout lifecycle managed via `SundaramTelemetryService` / `ReleaseStaging`:
     1. `Canary`: 1 test station (e.g. Hosur Line 04).
     2. `Pilot`: 1 full production line.
     3. `Staged 50%`: 50% of plant lines across facilities.
     4. `General Availability (GA)`: 100% enterprise-wide deployment.
   - Automated Rollback Guard: If a deployed line encounters $\ge 5$ errors, the system flags `RollbackRequired` and auto-reverts to the previous stable GA release (`v2.3.9`).
7. **Claude Diagnostic Error Reporting (MANDATORY FOR ALL EXCEPTIONS & FAULTS)**:
   - All runtime errors, camera dropouts, PLC timeouts, and tool faults must generate a `ClaudeDiagnosticPackage` using `CamouflageLabels` so the owner can copy-paste the diagnostic prompt directly to Claude for immediate root-cause diagnosis and code fixes.
   - Never expose proprietary algorithm names or raw internal tool names in error prompts or logs submitted to external models.
8. **Open-Source License & Attribution Integrity (MANDATORY COMPLIANCE)**:
   - Never change, camouflage, or strip the names, trademarks, or required attributions of third-party open-source dependencies (e.g. Suricata, Zeek, WireGuard, ClamAV, OpenCV, ZXing, SkiaSharp, SQLite).
   - Third-party open-source tools and hardware protocols must retain their authentic names to remain 100% compliant with GPL, Apache 2.0, BSD, and MIT license terms (`CamouflageLabels.IsOpenSourcePreserved`).
   - Camouflage and IP protection apply strictly to our custom proprietary engines, algorithms, deep learning models, and business logic.

10. **Tests: the session that makes a change tests it, once** (owner,
    2026-10-30: "tested-pass concept not required as already today
    testing method defined to test any developed part only once. If
    further testing required then do not mark PR ready for merge.").
    Before marking ready, the session (or its checking helper) compiles the
    changed parts (cmake build, `dotnet build`, `flutter analyze`,
    `py_compile`), runs the tests of the changed and affected parts as
    scoped below, and runs the bench once when the reading code changed
    (rule 11); fix what fails. Under "Verified" write what ran, with the
    counts, and what was skipped and why. There is no separate testing
    session and no `tested-pass` label.
    **Scoped testing, no duplicate runs.** (1) **C++:** build the
    engine and run its test executables when `engine/` changed. (2)
    **.NET:** test the test projects of the changed projects and of the
    projects that reference them, not the whole solution. (3)
    **Flutter:** `flutter test` the tests of the changed `lib/` files
    and their feature folders, not the whole app. (4) **Escalate** to a
    whole app or solution only when a shared core changed: the engine C
    API, VisionCore's tool contract, the plant event schema, theme,
    router, l10n tables or test helpers. (5) **Never test a change
    twice:** a result already written for the current head (under
    "Verified", or a heavy-test marker) is the result.
    **Ready means mergeable.** Mark a pull request ready only when its
    one test passed and nothing more needs testing. If anything still
    does (a bench run "not judged", a try on hardware, a device or real
    images, a soak), keep it a draft and write what is left in its
    description; don't mark it ready. The owner merges ready pull
    requests.
    **No merge of a draft.** A `PreToolUse` hook in
    `.claude/settings.json` runs `scripts/merge-guard.sh`, which refuses
    `gh pr merge` and the REST merge call for a draft or `Claim:` pull
    request. History roll-up pull requests (`rollup/` branches) are
    exempt. Never get around the hook. The hook is only a backstop: under
    rule 0 no agent merges anything, ready or not; the owner merges.
    **On demand.** Emulators, simulators, Docker and long benchmarks are
    started only when a test needs them and stopped as soon as it ends,
    so the development machine keeps its memory.


11. **Test once** (owner, 2026-10-30: "automate one time testing and
    avoid second duplicate testing if not required to save token and
    time"). This gathers the test rules in one place; rule 10 has the
    details.
    - Each change is tested once, by the session that made it (rule
      10), before it is marked ready. Don't start a second pass over a
      change that already has its result. A session or subagent that
      runs a change's one test may be started automatically when the
      load allows (rules 12, 13); a "batch testing" pass over already
      tested pull requests needs the owner's ask.
    - **The `bench` heavy test is `python3 tools/bench/batch_check.py`.**
      It runs every sample batch (code type × case size) from
      `VS_IMAGES` and compares the results with
      `truth/batch-baseline.csv` on main. It skips by itself when
      nothing under `engine/`, `tools/bench/` or `truth/` changed, or
      when the same code already passed on this Mac. It runs a noisy
      batch twice before failing it, so don't rerun it to be sure. It
      doesn't run ctest: run the engine tests yourself when `engine/`
      changed.
      **Exit 2 means "not judged":** the Mac was too busy (load above
      half the cores before the run, or above that plus the cores
      during it). Reads run against a time budget, so a busy Mac
      fails good code. That result is neither a pass nor a failure:
      keep the pull request a draft and run it again when the Mac is
      quiet. Never refresh the baseline on a busy Mac.
    - **Baseline:** a pull request meant to read more codes or run
      faster runs `--update` and commits `truth/batch-baseline.csv`,
      with the old and new numbers in the pull request. That is the only
      time a development session runs the bench.
    - A result that already exists for the current head (a heavy-test
      marker, `checker`'s report, the "Verified" section) is the
      result. Read it; don't run the test again to confirm it. That
      also applies to a session or helper you handed work to: check
      its report and diff, not its tests.
    - **GitHub CI does not test anything right now.** As of 2026-10-30
      GitHub Actions has been stopped by the account's billing, so the
      `windows` heavy test ("Windows build") fails within seconds. Don't
      count a CI result as a pass or a failure, and don't rerun it.
      Write "CI: not run (billing)" under "Verified".
    - A full write-up (`docs/BATCH-TEST-RESULTS.md`, with per-image
      causes) is made only when the owner asks, before a release, or
      when new sample images are added.

12. **Sessions start themselves** (owner, 2026-10-31: "For any
    development requirement claude can start nested session
    automatically.").
    These rules hold for every agent on the shared Mac (requirements
    17.9, AGR-06).
    - The main session starts a nested session for any development
      requirement without asking first: one session per independent
      item, after `scripts/coord.sh taken` (rule 2) shows it free. The
      new session claims the item as in step 3.
    - It picks items from section 5 and the `## Pending` lists in
      `docs/changes/`, skips any that need the owner's line time,
      images or a decision, and skips any that touch files an open pull
      request is changing.
    - It starts new sessions, development or testing, by load, with no
      fixed session count (owner, 2026-10-31: "multiple testing
      sessions can be done automatically as per mac load. Claude can
      decide to utilize full capacity of mac."). Check
      `sysctl -n vm.loadavg` and `sysctl -n hw.ncpu` (8 on this Mac):
      while no one holds the heavy-test lock, start more work while the
      5-minute load is under the cores plus 2 (10 here). While the lock
      is held, start nothing heavy (rule 13) and let the load fall, so
      the timed run is judged. Stop starting when free memory is low
      (`memory_pressure` not "normal").
    - It archives a session once its pull request is merged (and so
      tested), or closed as not wanted, and nothing of it is left open.
    - It tells every session it starts the heavy-test lock rules.

13. **Heavy-test lock.** The bench and the soak run against a time
    budget, so they need a quiet Mac (rule 11). Sessions take turns:
    - Before a bench, an `ocr_bench` gate, a soak or a long end-to-end
      run whose timings are judged, take the lock:
      `until mkdir /tmp/vs-heavy-test.lock 2>/dev/null; do [ -n "$(find /tmp/vs-heavy-test.lock -maxdepth 0 -mmin +45)" ] && rm -rf /tmp/vs-heavy-test.lock; sleep 60; done; echo "<PR or session> $(date)" > /tmp/vs-heavy-test.lock/owner`
      Run (`batch_check.py --wait 30`), then release it with a trap:
      `rm -rf /tmp/vs-heavy-test.lock`, also on failure.
    - Only timed runs take the lock: the bench, soaks, `ocr_bench`
      gates and other runs judged against a time budget. Builds, unit
      and .NET tests, sanitizer builds, fuzzers and leak checks run in
      parallel without it (owner, 2026-10-31: "multiple testing
      sessions can be done automatically as per mac load. Claude can
      decide to utilize full capacity of mac."). How many run at once
      comes from the load (rule 12). A script that also has a timed
      part takes the lock for that part only.
    - While the lock exists and is not yours, start no heavy CPU work:
      big parallel builds (`cmake --build -j`, whole-solution
      `dotnet build`), fuzzers, sanitizer suites, model training. That
      load spoils the timings. Work already running finishes; editing,
      reading and single small compiles or test files are fine.
    - A timed run on a busy Mac comes back "not judged" (rule 11): run
      it again under the lock when the load drops, never count it.
    - A lock older than 45 minutes is stale: remove it.

14. **Claude self-test for hacking before every final-version release**
    (owner, 2026-11-01: "Claude Self Test for hacking before final
    version release each time without fail. If finds any gap then repair
    the gaps and issues... Claude can select appropriate model as per
    need."). Before any final-version release of the shipped package,
    without fail, Claude runs a security self-review / pentest of the
    package against the software-protection threat model
    ([docs/vision-system/THREAT-MODEL.md](docs/vision-system/THREAT-MODEL.md))
    and requirements section 18: it attacks its own build the way the
    attacker in the threat model would — read the engine and decision
    logic, lift the AI models and master data, run unlicensed or on
    parts not bought from the company, defeat the anti-tamper runtime —
    and checks the fail-safe behaviour (any protection failure gives a
    clean NG and tells the line, never a silent wrong read, PROT-01..06).
    Claude picks the model it needs for this. Any gap or issue found is
    repaired before release. **Tie to line safety (rule above and
    requirements section 12):** a release may not ship while a known
    protection gap is open that could let a bad part pass or leak the IP.
    The self-test and its result (gaps found, repairs made, or none) are
    recorded with the release. This rule is in the agent rules of both
    repositories (this file in Vision_System, and secure_me's).

## Claude Code only

Claude Code loads this file through `CLAUDE.md` (`@AGENTS.md`). The rules
above apply to Claude as to every agent; these add what only Claude Code
has (requirements 17.9, AGR-03). Other agents skip this section and use
their own strongest model for the same work.

C1. **Models and effort.** Start each main session on Opus at
    medium effort (the app's model picker). The main session plans,
    decides and writes the hard parts; it sends routine work to the
    Sonnet and Haiku helpers in `.claude/agents/`, with the model and
    effort set there:

    | Work | Helper | Model, effort |
    | --- | --- | --- |
    | Claim, coord.sh, pull request chores, cleanup | `coordinator` | Sonnet, low |
    | Finding code, checking whether something exists | `code-finder` | Haiku, low |
    | Compile and your new tests before ready | `checker` | Haiku, low |
    | Strings in the 15 languages | `translator` | Sonnet, medium |
    | READMEs, requirements wording, your change note | `docs-writer` | Sonnet, medium |
    | A clear change inside one component | `builder` | Sonnet, high |
    | Review before ready; a hard bug of unknown cause | `reviewer` | Opus, high |

    Keep on Opus, in the main session: design, product choices, changes
    across components, the engine C API, the job file format, the plant
    event schema and the mobile API, the PLC handshake, AI model
    training and acceptance, licensing, security and anything that
    decides OK or NG. A helper that is unsure stops and reports; Opus
    takes over. Never move a step from Opus down to Sonnet partway
    through. Skip a helper when the job is a single command or file
    read: handing over costs more than doing it. Haiku only reads,
    searches and runs fixed commands; anything needing judgement or
    wording users see (claims, translations, docs, code) stays on
    Sonnet. If `code-finder` comes back thin or unsure, ask again with
    Sonnet. "opus", "sonnet" and "haiku" mean the newest release; do
    not pin versions.

C2. **Hooks.** `.claude/settings.json` runs `scripts/coord.sh cleanup`
    when a session starts or ends (rule 9), and a `PreToolUse` hook runs
    `scripts/merge-guard.sh` before any merge call (rule 10). Never get
    around them.

C3. **Helpers** are defined in `.claude/agents/` with their model and
    effort; change a helper there, and its row in the table above.
