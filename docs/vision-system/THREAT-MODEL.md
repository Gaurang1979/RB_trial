# Software protection threat model

This note is the threat model for protecting Vision_System against reverse
engineering and copying. It is the design companion to
[section 18 of the requirements](../REQUIREMENTS-VISION-SYSTEM.md#18-software-protection-against-reverse-engineering-and-copying),
which is the source of truth. It explains who the attacker is, what we protect,
what each of the three protection build items defends, and — stated plainly —
what the protection does **not** achieve. It reconciles with the licence-side
limits already in [LICENSING.md](LICENSING.md) section 9 and the package rules in
[SDK-API.md](SDK-API.md) section 0 and requirements section 10.

Owner decision, 2026-11-01: "whatever software and parts we develop should not
be reverse engineered or copied by anybody. Each and every part should be
protected." This widens the earlier licence-only stance (LICENSING.md section 9,
which used no obfuscation or anti-debug) to add model encryption, build/package
hardening and an anti-tamper runtime — built only in the fail-safe form that
line safety allows (section 7 below).

## 1. The realistic bar

**Software that runs on a machine we do not control cannot be made truly
impossible to reverse engineer or decrypt.** To run, code must become a form the
CPU executes; to decrypt a model, the key must be in memory. An attacker with the
delivered line PC and enough skill, time and tools can eventually reach both.
No software-only scheme changes this.

So the bar is **economic, not absolute**: raise the attacker's cost above the
cost of rebuilding the engine from scratch. Protection works when stealing the
know-how off a delivered PC is harder, slower and more expensive than a competent
competitor building their own reader. A bypass should be **slow** (real
reverse-engineering effort), **visible** (it leaves a trace support can see), and
**local** (it works on the one PC, not as a clean redistributable copy).

The product never claims "impossible", "uncrackable", "unbreakable" or
"impossible to decrypt" — not in docs, specifications, UI, alarms or marketing
(requirements 18.6).

## 2. The attacker

The attacker we design against is **a competitor who has obtained a complete
delivered system**: a line PC with Vision Studio or the Vision Package installed,
its AI models and product/learned data on disk, and — because the software is
delivered with the line — likely a valid licence and the company-supplied parts
(camera, PLC) for at least as long as they keep the PC. Their goal is one or more
of:

- Lift the AI models (codenet, ocrnet, enhancer) to use in their own product.
- Read the C++ engine or the .NET decision logic to copy the reading and OK/NG
  know-how.
- Copy the product master and learned/tuning state (a customer's recipes, our
  tuning).
- Run the software on PCs or with parts not bought from the company, or run it
  without a licence, at scale.

Assumed capabilities: physical access to the PC and its disk and memory, standard
reverse-engineering tools (disassembler, decompiler, debugger, memory dumper),
time, and moderate skill. **Not** assumed: a presence inside the company's build
pipeline or key custody (ERPNext KMS/HSM), or the ability to forge the company
signing keys. A supply-chain compromise of the build system is out of scope here
and handled by build-system security, not by these three items.

Out of scope for this note (covered elsewhere): network attackers on the plant
LAN or the licence service (TLS, tokens, rate limits — LICENSING.md section 8,
SDK-API.md section 11); licence-server availability (LICENSING.md section 8.4);
Part 11 record integrity (PART11-DESIGN.md).

## 3. What we protect

| Asset | Where it lives | Worth to the attacker |
| --- | --- | --- |
| C++ engine logic | native binaries (`vs_*.dll`), the package worker | The reading know-how that beats Cognex on the owner's images |
| .NET decision logic | VisionCore and app assemblies, the package | Duplicate memory, serial-run matching, GS1 check, OK/NG rules, job flow |
| AI models: codenet, ocrnet, enhancer | model files on disk; weights in memory at run time | Trained weights, costly to reproduce, the core of the accuracy |
| Product master and learned state | product/job files, learned state per product | A customer's recipes; a shortcut to our tuning |
| Product files exported by Vision Studio | files moved between Studio and the Vision Package | Teach, tuning, learned state, model choice in one file |
| Licence and part-binding scheme | `vs_lic`, the licence file, ERPNext records | Running unlicensed or on parts not bought from the company |

## 4. The three protection build items and what each defends

Section 18.3 of the requirements names three build items. Each is a separate pull
request with its own tests, and each must honour the line-safety rule (section 7
below). This note maps each to the threats it addresses.

### 4.1 Model and data encryption

Defends: the AI models, the product master, the learned state and the exported
product files.

- Encrypted at rest with AES-256-GCM (as LICENSING.md sections 7.3 and 7.4
  already specify). The content key is **never stored on the PC in the clear**.
- The key reaches a PC only inside a **signed licence answer**, wrapped for that
  installation's own key pair (private key kept with Windows DPAPI at first
  start); the signature covers it (requirements section 10, LICENSING.md 7.4).
  A copy of the files on another PC decrypts to nothing.
- The key is in memory only while a licensed worker runs, for no longer than the
  job and the offline grace allow.
- Model keys are derived only when the relevant licence (DL for models) is valid,
  so patching one tool's licence check does not release a model key
  (LICENSING.md 7.3).

What it does **not** do: it cannot stop an attacker who runs the licensed,
untampered software from dumping a decrypted model out of that process's memory
while it is loaded. That is the residual risk in section 6; it is bounded by the
anti-tamper runtime (4.3) making the dump harder and visible, not eliminated.

### 4.2 Build and package hardening

Defends: the C++ engine logic and the .NET decision logic against casual and
moderate reading; and the integrity of every shipped binary.

- Release builds with debug symbols **stripped** and kept only in a **private
  symbol store** (so support can still symbolicate a crash; requirements 18.4).
- .NET assemblies protected against trivial decompilation (renaming/obfuscation
  of the decision logic), chosen to avoid antivirus heuristics that trip common
  endpoint protection on plant PCs.
- Every shipped native and .NET binary and the Vision Package **Authenticode
  code-signed**; the loader refuses an unsigned or changed binary (requirements
  sections 9, 10; LICENSING.md section 9 Authenticode row).
- The package/first-run **self-test** (requirements section 10) confirms the
  signed, expected build is the one running.
- Carried to the delivered PC by the Windows line-PC package (PR #146).

What it does **not** do: obfuscation and stripping **delay** a determined
reverse engineer; they do not make the logic unreadable. This is stated plainly
(requirements 18.1, LICENSING.md section 9).

### 4.3 Anti-tamper runtime

Defends: the integrity of the running system — that the binaries, models and data
in use are the signed, expected ones, and that no one is lifting keys or logic out
of the running worker.

- At run time the licensed worker detects: a patched or unsigned binary, a swapped
  model or data file whose signature/hash does not match, a failed decrypt, a
  debugger attached to the worker, and an invalid or missing licence.
- Every such detection resolves to the **single fail-safe response** in section 7
  (clean NG + tell the line). This is what reconciles LICENSING.md section 9 to
  the owner's 2026-11-01 decision: the earlier note avoided anti-debug because it
  "only delays a determined attacker" and raises AV false positives — now it is in
  scope, but built so a trip can only ever cause an honest NG, never a silent
  wrong read and never a line stop that hides a verdict.
- Its cost stays within the per-case time budgets (requirements section 17.1):
  integrity checks run on load and on their own cadence, off the hot path.

What it does **not** do: it cannot stop an attacker who patches the checks
themselves on one PC. As LICENSING.md section 9 says, a cracker who patches the
check can run on that one PC; the answer to scale is commercial and legal (serials
tied to customers and parts).

## 5. Threat → defence summary

| Threat | Primary defence | Residual risk (plainly) |
| --- | --- | --- |
| Copy model files to another PC and use them | Model encryption; key wrapped per installation (4.1) | None from the file alone; see memory-dump row |
| Dump a decrypted model from the running worker's memory | Anti-tamper runtime: debugger/dump detection → NG, visible (4.3) | A skilled attacker on their own PC can still dump; cost raised, not removed |
| Decompile .NET decision logic | Hardening/obfuscation (4.2) | Delayed, not prevented |
| Disassemble the C++ engine | Stripped release + hardening (4.2) | Delayed, not prevented |
| Swap in a patched binary or model | Authenticode + signature/hash checks → refuse/NG (4.2, 4.3) | Patching the checker on one PC still works locally |
| Copy product master / learned state | Encryption + per-installation key (4.1) | Same as model memory-dump row |
| Run unlicensed or on parts not bought from us | Licence file, part binding, periodic ERPNext check (section 9) | A local patch runs on one PC; traceable to the serial |
| Edit the licence file (tools, expiry) | Ed25519 signature (LICENSING.md section 9) | None without the signing key |

## 6. Residual risks, stated plainly

- A skilled, determined attacker with the delivered PC can eventually read the
  logic and lift a model from memory. Protection makes this slow, visible and
  local; it does not make it impossible.
- A cracker who patches `vs_lic`/`vs_core` or the anti-tamper checks can run on
  that one PC. No clean redistributable copy results, and the bypass is
  traceable to the serial and installation.
- Build-system and key-custody compromise are out of scope for these three items
  and must be handled by build/release security and KMS/HSM key custody.
- The answer to large-scale misuse is commercial and legal as well as technical.

## 7. Line safety on a protection failure (non-negotiable)

Line safety is the project's first rule (requirements section 12; AGENTS.md;
owner, 2026-11-01). Protection must never let a bad part pass and never stop the
line in a way that hides a result. For **every** protection failure — bad or
missing signature, failed decrypt, tamper or debugger detected, invalid/expired/
missing licence, a model or data file that does not match its hash, or a worker
that cannot prove it is the signed build — the response is fixed
(requirements 18.5, repeated here so the build items follow one rule):

- **PROT-01** Clean **NG with a specific reason**, never a silent wrong read and
  never a read reported for a case not fully inspected on a verified system.
- **PROT-02** Never a crash that could leave the line running with no verdict or
  be read as OK; a fault becomes an NG result and an event, not an unhandled
  exception.
- **PROT-03** Tell the line: NG to the PLC with its error code (PLC-INTERFACE.md),
  an alarm/event to the operator and supervisor, and — where a plant server is
  present — the plant events and audit trail (PART11-DESIGN.md). Light tower and
  reject follow the normal NG path.
- **PROT-04** No silent degrade to an unprotected model, an unlicensed tool or a
  reduced check.
- **PROT-05** Fail closed: when protection state is in doubt, the answer is NG and
  the line is told — the same rule as a doubtful camera, PLC link, job or model.
- **PROT-06** Entitlement faults (a licence expiring) take effect at the next
  start or product load and never stop a running batch (section 9, LICENSING.md
  7.2); **integrity** faults (tamper, bad signature, failed decrypt of the model
  in use) make the affected case NG at once, because the part cannot be trusted to
  have been inspected correctly.

## 8. Protection must not weaken reading

- Runs outside the inspection hot path; never pushes a good part past its per-case
  budget (requirements 17.1, 18.4).
- Never changes a reading result: the same part gives the same OK/NG with or
  without protection. Protection decides only whether the system may run, never
  what it reads.
- Antivirus and support cost are managed: code-signed binaries, hardening chosen
  to avoid common endpoint-protection heuristics, and a private symbol store so
  support can still read a crash.

## 9. Pre-release security self-test (owner, 2026-11-01)

Before every final-version release of the shipped package, a security self-review
/ penetration test is run against this threat model and requirements section 18,
without fail (requirements 18.7, PROT-10..12; AGENTS.md rule 14). It attacks the
actual build the way the attacker of section 2 would — read the engine and .NET
logic, lift the models and master data, run unlicensed or on parts not bought
from the company, defeat the anti-tamper runtime — and confirms the fail-safe
behaviour of section 7 (any protection failure gives a clean NG and tells the
line). Any gap is repaired before the release ships; the self-test and its result
are recorded with the release. A release may not ship while a known protection gap
is open that could let a bad part pass or leak the protected IP (hard release
gate, tied to line safety). The runner chooses the capability it needs for the
job.

## 10. Open questions

- Which .NET hardening/obfuscation tool, balancing decompilation resistance
  against antivirus false positives on plant PCs and the Part 11 validation pack.
- Whether debugger/dump detection can be made reliable enough on plant PCs
  (remote support tools, some endpoint agents attach) without false NGs — the
  cadence and thresholds that keep PROT-01..05 from firing on a legitimate PC.
- Native-code obfuscation for the C++ engine beyond stripping: is it worth the
  support and debugging cost, or is encryption of the valuable parts (models)
  enough?
- Key custody for the signing and content keys (KMS vs HSM), tied to the
  LICENSING.md section 10 build-or-buy decision.
