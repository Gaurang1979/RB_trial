# Vision System – Job File Format

This document specifies the job file: one self-contained `.vsjob` file holding a job's cameras,
acquisition steps, tool graph, parameters, tolerances, variants, PLC mapping, outputs, AI models,
trained templates and test images (PRD §8). It decides the container, the JSON layout of
`job.json`, the versioning and migration rules, encryption and signing, and how a job is
validated, including a job that uses an unlicensed tool. The runtime that executes jobs is
described in [ARCHITECTURE.md](ARCHITECTURE.md); tool parameters are listed per tool in
[TOOL-SPECIFICATIONS.md](TOOL-SPECIFICATIONS.md).

## 1. Container

A job file is a zip archive with the extension `.vsjob`.

```
label-date-dm.vsjob
├── job.json                 the job: everything in section 3
├── manifest.json            file list with SHA-256 hashes, sizes, schema version
├── signature.json           signatures over manifest.json (section 2.3)
├── models/                  ONNX models, always encrypted (*.onnx.enc)
├── templates/               trained tool state: locator patterns, OCR fonts, learned code geometry (*.bin)
├── calib/                   calibration results (*.json): lens model, pixel-to-world, residuals
├── images/
│   ├── reference/           the good part the job was taught on (shown in Teach and Run)
│   ├── test/                stored OK and NG images for the image-set panel (PRD §6.3)
│   └── product/             product photos for "change product by picture" (PRD §6.1)
├── i18n/                    job-specific text per language (section 3.11), optional
└── history/                 earlier revisions of job.json, optional (section 2.1)
```

Why a zip:

- **One file to copy, mail, back up and import** (PRD §8, "export and import as a single file").
- **Binary payloads stay binary.** Models and templates are not base64'd into JSON.
- **Open format.** Any tool can list the contents. Only `job.json` and `manifest.json` are read
  to show a job's summary; models load only when the job runs.
- **Standard libraries** on every platform the SDK targets (PRD §10).

Rules for the archive:

- Entries are stored with Deflate, except images and encrypted entries, which are stored.
- Paths use `/`, are UTF-8, relative, and must not contain `..` or a drive letter.
- `job.json` is UTF-8 JSON without a BOM. Numbers are JSON numbers; units are always explicit.
- Maximum sizes: `job.json` 4 MB; the whole archive 2 GB (target; images dominate).
- A `.vstool` file (one tuned tool, PRD §10) uses the same container with a `tool.json` that
  holds one node in the format of section 3.6, plus its `templates/` and `models/` entries.

On the PC, the runtime keeps released jobs in its job store as the same `.vsjob` files. The UI
edits a working copy and never writes over a running job (PRD §1, principle 3).

## 2. Versioning, migration, encryption and signing

### 2.1 Versions

There are three separate version numbers.

| Field | Meaning | Changes when |
| --- | --- | --- |
| `schemaVersion` (in `job.json` and `manifest.json`) | Layout of the file, as `major.minor` | The format changes |
| `job.revision` | Revision of this job's content | Every saved change |
| `nodes[].toolVersion` | Version of a tool's parameter layout | The tool's parameters change |

Each saved revision keeps its predecessor in `history/job.r<N>.json` (configurable, default the
last 20), with author, time and reason. The compare view in the designer (PRD §6.3) diffs these.
The full audit trail lives in the database, not in the file.

### 2.2 Migration rules

| Case | Behaviour |
| --- | --- |
| Same major, older minor | Opens normally. Missing optional fields take their defaults. |
| Same major, newer minor | Opens. Unknown fields are kept and written back unchanged on save. A banner says the job was made with a newer version. |
| Older major | Migrated in memory through each step (1→2→3). The user sees a migration report and must save explicitly; the original stays in `history/`. |
| Newer major | Refused with a plain message: "This job was made with a newer version (3.x). Update the software to open it." |
| Older `toolVersion` | The tool DLL migrates its own parameters (`migrateParams(from)`). The report lists every changed value. |
| Newer `toolVersion` than the installed DLL | The job opens read-only; that tool shows "needs a newer tool version". |

After any migration the software reruns the job on `images/test/` and compares verdicts and key
values with the results stored at the last release (`release.expected`). Any difference blocks
release until a supervisor accepts it. This is how "jobs from older versions open in newer ones"
(PRD §13) is verified rather than assumed.

#### 2.2.1 The JSON job of today (schema 0.x)

Until the `.vsjob` container is built, VisionCore keeps each job as one JSON file,
`jobs/<name>.json`, with its versions in `jobs/history/<name>/v<n>.json` and its taught
patterns in `jobs/assets/` (`JobStore`, `JobFile`). This first subset is schema major **0**;
the full format above is 1.0 and will migrate it as an older major. `SchemaVersion` is written
on every save (today `"0.1"`); a file without it was saved before versions were written and
reads as `0.0`.

Loading for the line (`JobStore.Load`) never gives a half-working job. It refuses, with a
reason, so the line stays NG and not ready:

- a file over 4 MB, empty, cut short or not JSON; a field written twice; nesting deeper than 32;
- an unknown field (a typo, or a field of a newer version), a wrong type, a number for a named
  value, an empty value or list entry where one is needed, a number that is not finite;
- a newer schema, major **or minor**. On the line a newer minor is refused too, not opened with
  a banner: its added fields would be ignored, and a check left out could pass a bad part;
- a file named after another job, more than 100 tools or 32 lines in a tool;
- anything the save refuses (`Job.Validate`), checked without redrawing a rendered font;
- an enabled Find the part tool with no pattern, a missing or damaged pattern file, or one
  outside `jobs/assets/`; a font reader with no font; the AI text model missing on this PC.

An older schema of the same major reads with a note (`Job.LoadNotes`); fields added since take
their defaults. A missing teach image is a note, not a refusal. The teach and setup screens use
`JobStore.LoadForEdit`, which opens a job that reads but cannot run yet (a pattern to teach
again) with the reasons in `Job.LoadProblems`; a file that does not read is refused there too.
The PLC selects a job by number only among jobs that load (`JobStore.FindByNumber`).

### 2.3 Encryption and signing

| Protection | What | How |
| --- | --- | --- |
| Model encryption | `models/*.onnx.enc`, always | AES-256-GCM. The key is bound to the licence (PRD §5, [LICENSING.md](LICENSING.md)). |
| Job store at rest | Every `.vsjob` in the runtime's job store | Windows DPAPI (machine scope) around the archive (PRD §12) |
| Password protection (optional) | The whole exported file | Outer envelope (below) |
| Integrity | Every entry | SHA-256 per entry in `manifest.json` |
| Signature | `manifest.json` | Ed25519, by the installation key of the PC that saved it |
| Release approval | `manifest.json` at release | Second signature by the approving user (supervisor); Part 11 e-signature in that mode |

**Password envelope.** A password-protected job is not a zip on the outside. It is a small
header (`VSJE`, envelope version, Argon2id salt and cost, AES-GCM nonce) followed by the
AES-256-GCM-encrypted zip. Wrong password, wrong tag or a truncated file all give the same
message: "Wrong password or damaged file." The password is never stored.

**Signature check on open.**

- Hash mismatch on any entry: the job opens read-only with "This job was changed outside the
  software". It cannot be released or run until an engineer re-saves it (a new signature and an
  audit entry).
- Unknown signing key (a job from another PC): allowed; the UI shows where it came from.
- Release signature missing: the job can be tested but cannot run in Run mode (PRD §8 lifecycle).

## 3. `job.json`

Top-level layout:

| Key | Type | Required | Section |
| --- | --- | --- | --- |
| `schemaVersion` | string `"1.0"` | yes | 2.1 |
| `job` | object | yes | 3.1 |
| `requires` | object | yes | 3.2 |
| `cameras` | array | yes (may be empty for a file-only job) | 3.3 |
| `acquisition` | object | yes | 3.4 |
| `calibrations` | array | no | 3.5 |
| `nodes`, `wires` | arrays | yes | 3.6 |
| `verdict` | object | yes | 3.7 |
| `variants` | object | no | 3.8 |
| `plc` | object | no | 3.9 |
| `outputs` | object | no | 3.10 |
| `labels` | object | no | 3.11 |
| `release` | object | no | 2.2, 3.1 |

### 3.1 Job metadata

| Field | Meaning |
| --- | --- |
| `id` | UUID, fixed for the life of the job |
| `name` | Name shown to engineers; free text |
| `labelKey` | Optional key for an operator-facing name in `i18n/` |
| `revision` | Integer, +1 on every save |
| `state` | `draft`, `tested`, `approved` or `released` (PRD §8) |
| `created`, `modified` | ISO 8601 UTC time and user ID |
| `appVersion` | Software version that last saved it |
| `units` | `mm` or `inch` for world values and tolerances (PRD §7) |
| `targetCycleMs` | Target cycle time for the timing bar and benchmark |
| `hardwareTier` | Recommended tier, `T1`…`T5` (PRD §11) |

### 3.2 Licence requirements

`requires.parents` lists the parent tools the job uses, by licence code, with the minimum tool
DLL version. It is derived from the nodes on save and checked against the nodes on load; a
mismatch is a validation error. The UI uses it to say which licences are missing before any DLL
is touched.

### 3.3 Cameras

Each camera entry identifies the device and holds named GenICam feature sets.

| Field | Meaning |
| --- | --- |
| `id` | Local ID used by acquisition steps and nodes (`cam1`) |
| `labelKey` | Operator name ("Top camera") |
| `match` | How to find the device: `serial` (preferred), else `userId`, else `vendor` + `model`; plus `transport`: `GigEVision` or `USB3Vision` |
| `driver` | `gentl` (default) or a vendor driver ID, only where GenICam is insufficient (PRD §8) |
| `pixelFormat` | GenICam `PixelFormat`, for example `Mono8`, `BayerRG8` |
| `demosaic` | For Bayer formats: `bilinear` or `edge-aware` |
| `roi` | Sensor `OffsetX`, `OffsetY`, `Width`, `Height` |
| `featureSets` | Named sets of GenICam features, applied in listed order (`{"ExposureTime": 800, "Gain": 0}`) |
| `trigger` | `software`, `action` (with `deviceKey`, `groupKey`, `groupMask`) or `freeRun` |
| `transportTuning` | `GevSCPSPacketSize`, `GevSCPD` and similar, applied after the feature sets |
| `ptp` | `required`, `preferred` or `off` |

Feature names are standard GenICam SFNC names. Vendor-specific features are allowed but listed
with `"vendor": true` so the validator can warn when the camera model changes. The features that
would use camera I/O wiring are rejected (section 4).

### 3.4 Acquisition steps and light sequences

An acquisition step is one image per camera, with the light the PLC must set first. Multi-light
sequences are several steps in the PLC handshake (PRD §8, [PLC-INTERFACE.md](PLC-INTERFACE.md)).

| Field | Meaning |
| --- | --- |
| `mode` | `plc` (default), `freeRun`, `timed` (with `periodMs`) or `simulator` |
| `steps[].id` | Step ID; its images appear as graph source ports `<stepId>.<cameraId>` |
| `steps[].cameras` | Camera IDs exposed in this step |
| `steps[].featureSet` | Feature set to apply before the step (switched between steps if it differs) |
| `steps[].light` | Light code the PLC must set before the trigger (for example `1` = bright-field); `null` = no change |
| `steps[].sync` | For several cameras: `action` (one action command) or `sequential` |
| `timeoutMs` | Trigger-to-frame timeout per step |
| `simulator.folder` | Folder of images for the file simulator; files are matched to steps by name pattern |

### 3.5 Calibrations

Each entry names a calibration resource in `calib/`, its camera, the spaces it connects
(`image` → `world`, or `world` → `world:robot`), its residual error in µm and its date. Tools
reference calibrations by ID. Coordinate spaces are defined in
[ARCHITECTURE.md](ARCHITECTURE.md#5-coordinate-spaces-fixturing-and-calibration).

### 3.6 Tool graph: nodes and wires

A node is one tool instance.

| Field | Meaning |
| --- | --- |
| `id` | Unique in the job, `[a-z][a-zA-Z0-9_]*` |
| `tool` | Tool type ID, `<family>.<tool>` (`locate.geometric`, `code.read`) |
| `toolVersion` | Parameter layout version |
| `labelKey` | Operator-facing name |
| `space` | Coordinate space of the node's ROI: `image`, `fixture:<nodeId>` or `world` |
| `roi` | Shape (`rect`, `rotRect`, `circle`, `annulus`, `polygon`) in that space; `null` = whole input |
| `params` | Tool parameters, validated against the tool's parameter schema |
| `limits` | Tolerances: output port → `{min, max, unit}` or `{equals}`; out of limits = Fail |
| `minConfidence` | 0–100; below it the node fails (PRD §4) |
| `onError` | `fail` (default) or `ignore` (allowed only for nodes with `"advisory": true`) |
| `advisory` | `true` = result shown and logged but not part of the verdict |
| `state` | Path of the node's trained state in `templates/` or a model in `models/` |
| `ui` | Designer layout only: canvas position, colour, collapsed flag. Ignored by the runtime |

A wire connects an output port to an input port: `{"from": "findLabel.pose", "to": "date.fixture"}`.
Ports are typed (`Image`, `Pose`, `Region`, `Number`, `Text`, `Bool`, `CodeList`, …, see
[ARCHITECTURE.md](ARCHITECTURE.md#41-c-interface-sketch)). An `Any` input accepts any type. The
sources are acquisition images (`acq.<stepId>.<cameraId>`) and job inputs (`input.<name>`, such as
text sent by the PLC).

#### 3.6.1 Blob node (`blob.analyse`, "Count / spot")

`params` of a Blob node (tool spec 4.1 and 4.4; engine `vs_blob.h`). Areas are pixels until the
node's space is `world`. Every field is optional; the default applies when it is missing.

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `threshold` | `Auto` / `Fixed` / `Band` / `Local` | `Auto` | Otsu; a fixed level; a grey band; darker (lighter) than the local mean |
| `polarity` | `DarkOnLight` / `LightOnDark` | `DarkOnLight` | Which side of the level is a blob (not used by `Band`) |
| `level`, `levelHigh` | 0–255 | 128, 255 | `Fixed`: the level (dark blobs at or below it); `Band`: low and high end |
| `localWindow`, `localOffset` | px (odd, ≥ 3), grey levels | 31, 10 | `Local`: window side and distance from the local mean |
| `minContrast` | grey levels | 15 | `Auto`: the level stays this far from the background's mean, so a clean area's noise is never split and a small spot is still found |
| `connectivity` | 4 / 8 | 8 | Pixel neighbours that join a blob |
| `fillHoles` | bool | false | Holes count as part of the blob |
| `excludeEdge` | bool | true | Blobs touching the area's edge are left out |
| `morphology` | `None` / `Open3` / `Close3` | `None` | Clean-up before labelling |
| `minArea`, `maxArea` | px | 10, 0 | Filter; 0 = no upper limit |
| `minCircularity`, `maxCircularity` | 0–1 | 0, 0 | Filter on 4πA/P²; 0 = no limit |
| `minElongation`, `maxElongation` | ≥ 1 | 0, 0 | Filter on major / minor axis of the same-moment ellipse; 0 = no limit |
| `sortBy`, `sortDescending` | `None` `Area` `X` `Y` `Perimeter` `Circularity` `Elongation` `MeanGrey`, bool | `None`, false | Order of the blobs reported |
| `keepFirst` | int | 0 | Keep the first N after sorting; 0 = all |
| `presence` | `Any` / `Present` / `Absent` | `Any` | Pass rule: at least one blob, or none |
| `minCount`, `maxCount` | int or null | null | Pass rule: count range |
| `blobAreaMin`, `blobAreaMax` | px or null | null | Pass rule: every counted blob's area |
| `totalAreaMin`, `totalAreaMax` | px or null | null | Pass rule: the counted blobs' total area |
| `minRegionMean`, `maxRegionMean` | grey level | 8, 250 | A darker (brighter) area is a dark (white) frame: Fail (`explain.blob.image_dark`, `explain.blob.image_bright`), never "no spots" |
| `timeBudgetMs` | ms | 50 | A run past it is not checked: Fail (`explain.blob.incomplete`) |
| `checkOutline` | bool | false | Measure each counted blob's outline (tool spec 4.2, "Check outline"): subpixel outline, length, convexity, dents, deviation from a taught outline |
| `edgeLevel` | 0–255 or null | null | Grey level the outline is placed on; null = halfway between the blob and a 2 px ring round it |
| `outlineSmoothing` | px | 1 | Gaussian sigma before the outline is placed; 0 = none |
| `minDefectDepth` | px | 1 | Indents shallower than this are not dents |
| `maxDentDepth` | px or null | null | Pass rule: no dent deeper (`explain.contour.dent`) |
| `minConvexity` | 0–1 or null | null | Pass rule: area / convex hull area at least this (`explain.contour.convexity`) |
| `taughtOutline` | list of numbers or null | null | The taught outline, x and y pairs round its centroid (taught by clicking a good part) |
| `maxDeviation` | px or null | null | Pass rule with `taughtOutline`: no outline point further than this from it, either way, after placing it on the blob (`explain.contour.deviation`) |
| `minEdgeShare` | 0–1 | 0.9 | An outline with fewer of its points on a grey edge, or cut by the area's edge, or a part left out for touching the area's edge, is not checked: Fail (`explain.contour.not_closed`), when an outline rule is set |

Filters leave blobs out (shown grey with the reason); pass rules fail the part. The validator
refuses a node whose pass rules no part can meet (highest count below the lowest, `Absent` with a
lowest count, a lowest count above `keepFirst`, a band whose high end is below its low end), and
`keepFirst` together with a highest count, `Absent`, an area rule or an outline rule, which it would
hide blobs from; also outline rules with `checkOutline` off or without a lowest count of 1 or more
(`Present` or `minCount`), `maxDeviation` without a taught outline, and a taught outline without
`maxDeviation`. Blobs left out only for touching the area's edge still count against `Absent`, `maxCount`
and `totalAreaMax`. An area cut by the picture's edge (also after the part moved it) fails with
`explain.blob.region`.

Output ports: `Blobs`, `Count`, `TotalArea`, and per run the values `count`, `total_area`,
`threshold` and `confidence` (tool spec 4.1); with `checkOutline` also each blob's outline and
the values `min_convexity`, `max_dent` and (taught, every blob compared) `max_deviation` (tool spec 4.2).
The outline in `Blobs` is simplified to 0.25 px for display; the engine's full outline is
`vs_blob_get_outline`.

In the current VisionCore job file (`jobs/<name>.json`) the same fields are a tool with
`"kind": "blob"`, written in PascalCase (`Threshold`, `Level`, `MinCount`, ... as in
`BlobToolConfig`), next to `Name`, `Camera`, `Roi` and `Enabled`; a camera's Find the part tool
moves the `Roi` with the part.

### 3.7 Verdict

`verdict.rule` is `all` (every non-advisory node passes) or an expression over node results,
for example `"findLabel && (code || date)"`. `verdict.reasons` orders which failed node is
reported first on the Run screen and to the PLC.

### 3.8 Recipes and variants

Variants share one job and differ only in values (PRD §8). Each variant overrides parameters,
limits and expected values by path. A path is `<nodeId>.params.<name>`,
`<nodeId>.limits.<port>.<min|max|equals>` or `cameras.<id>.featureSets.<set>.<feature>`.

| Field | Meaning |
| --- | --- |
| `default` | Variant ID used when nothing selects one |
| `selectBy` | `plc`, `operator` or `both` |
| `items[].id` | Variant ID |
| `items[].plcCode` | Integer the PLC writes to job-select for this variant |
| `items[].labelKey`, `items[].photo` | Operator name and product photo (`images/product/`) |
| `items[].set` | Map of path → value |

Only values may vary. A variant cannot add or remove nodes; that needs a separate job. This keeps
every variant testable on the same graph.

### 3.9 PLC mapping

The PLC section binds the standard handshake and result values to tags or addresses. The
protocol details and the handshake timing are in [PLC-INTERFACE.md](PLC-INTERFACE.md).

| Field | Meaning |
| --- | --- |
| `connection` | Protocol (`s7`, `profinet`, `ethernetip`, `modbusTcp`, `mcProtocol`, `fins`, `tcp`, `opcua`) and its address settings |
| `handshake` | Standard signals → address: `trigger`, `triggerId`, `step`, `busy`, `done`, `ok`, `ng`, `error`, `errorCode`, `jobSelect`, `heartbeatIn`, `heartbeatOut` |
| `heartbeatMs` | Heartbeat period; link lost after 3 missed beats (target default) |
| `inputs` | PLC values read at trigger time into job inputs (`input.<name>`) |
| `results` | Node output → address, type and scaling (for example mm × 100 as INT) |

Several jobs can share one PLC connection; each uses its own handshake block.

### 3.10 Outputs and image saving

| Field | Meaning |
| --- | --- |
| `record.fields` | Values written to the result database for each inspection (PRD §12) |
| `csv` | Optional CSV export: folder, English column headers (PRD §7), rotation |
| `images.save` | `all`, `ng`, `everyN` (with `n`), or `none` (PRD §12) |
| `images.format` | `png` (lossless, default) or `raw`; `jpeg` only for thumbnails |
| `images.overlay` | Also save a copy with graphics drawn |
| `images.quotaGb` | Disk quota for this job; oldest images are cleaned up first |

Image names and folders are the station's, not the job's (owner, 2026-10-31,
PRD §12): each camera image is named `L01_C2_J045_261031-104512_000123`
(line, camera, the job's PLC number from `Number`, local date and time, the
day's part serial) and saved in `<images root>/L01/2026-10-31/J045/C1/NG/`
(or `OK/`). The line number is the station setting `Studio:LineNumber` in
`appsettings.json` (1-99, default 1); a job without a PLC number is `J000`.
Details: [NG-REVIEW.md](NG-REVIEW.md) section 2.
| `network` | TCP, MQTT, OPC UA or SQL outputs (Enterprise ones need that licence) |

### 3.11 Labels

`job.json` never holds operator-visible text in a particular language. Every name is a key.

- Keys starting with `tool.`, `port.`, `explain.`, `alarm.` come from the software's language packs
  ([LOCALISATION.md](LOCALISATION.md)).
- Keys starting with `job.` are this job's own text (a product name, a custom alarm). Their
  translations are in `i18n/<language>.json`, for example `i18n/hi.json`. A missing translation
  falls back to `i18n/en.json`, then to the key itself.
- Expected text that is checked on the product (an OCV string, a GTIN) is data, not a label, and
  is never translated.

## 4. Validation

The validator runs on open, on every edit in the designer, before release and on load in the
runtime and the SDK job runner. Errors block running; warnings do not.

| Rule | Level |
| --- | --- |
| `job.json` matches the JSON Schema for its `schemaVersion` (`schemas/job-1.0.schema.json`) | Error |
| Every entry in `manifest.json` exists with the right hash; no extra entries | Error (read-only, section 2.3) |
| Node IDs unique; every wire joins existing ports of compatible types; the graph has no cycle | Error |
| Every required input port is wired | Error |
| Parameters within their schema ranges; units present on every length | Error |
| `requires.parents` matches the tools used | Error |
| Every `state`, model, calibration and image path exists | Error |
| A tool marked "needs calibration" (Code Verify, world-unit tolerances) has a calibration | Error |
| Camera features `TriggerSource = Line*`, `LineMode = Output`, `StrobeEnable`, `LineSource` set to an exposure signal, or any serial/I/O feature | Error: camera I/O wiring is not used (PRD §1) |
| More than 8 cameras, or more than 1 camera without the Multi-Camera licence | Error |
| `steps[].sync = action` on a camera that does not support action commands | Error |
| Variant `set` path does not exist, or a variant tries to change the graph | Error |
| PLC addresses overlap or exceed the configured block | Error |
| `jobSelect` codes not unique across variants | Error |
| Vendor-specific feature used; PTP `preferred` but not locked at job start | Warning |
| Sum of tool timings on the test images exceeds `targetCycleMs` | Warning |
| No OK and NG images in `images/test/` | Warning; release needs at least one of each |

### 4.1 Unlicensed tools

A job that uses a tool without a licence opens read-only and says which licence it needs. It
never crashes and never silently skips the tool (PRD §9).

| Where | Behaviour |
| --- | --- |
| Designer | The job opens. Unlicensed nodes show a lock icon and keep all their parameters and trained state. Editing is disabled for the whole job, because changes elsewhere could not be tested. A banner lists the missing parent tools with "Start 30-day trial" and "Request licence" buttons (PRD §9). |
| Run mode | Run is disabled; the screen shows "This job needs the OCR / OCV licence. Ask your supervisor." The PLC receives the error code "job not runnable" on job select. |
| Saving | The file is never rewritten by a PC that cannot run it, so no parameters are lost. "Save as" to a copy is allowed. |
| Runtime and SDK job runner | Load fails with `VS_E_UNLICENSED` and the list of missing parents; no partial job runs. |
| Licence expires while running | The job keeps running until the next restart; the supervisor is warned 14 days ahead (PRD §9). |

## 5. Example: label, date and DataMatrix check

One 5 MP GigE camera looks at a carton label. The job finds the label, checks it is straight,
verifies the printed lot and expiry date, reads the GS1 DataMatrix, and checks that the code's
batch matches the printed lot. The PLC selects one of two products and sends the lot number.

```json
{
  "schemaVersion": "1.0",
  "job": {
    "id": "6f1c2e0a-3b7d-4a51-9c2e-8d4f1a7b9e10",
    "name": "Carton label, date and code",
    "labelKey": "job.name",
    "revision": 14,
    "state": "released",
    "created": { "at": "2026-11-02T08:15:00Z", "by": "eng.rao" },
    "modified": { "at": "2026-11-05T13:40:00Z", "by": "sup.patil", "reason": "Lot font changed" },
    "appVersion": "1.2.0",
    "units": "mm",
    "targetCycleMs": 120,
    "hardwareTier": "T1"
  },
  "requires": {
    "parents": [
      { "code": "BASE", "minVersion": "1.0" },
      { "code": "LOC", "minVersion": "1.0" },
      { "code": "MEA", "minVersion": "1.0" },
      { "code": "OCR", "minVersion": "1.1" },
      { "code": "COD", "minVersion": "1.0" }
    ]
  },
  "cameras": [
    {
      "id": "cam1",
      "labelKey": "job.cam.top",
      "match": { "transport": "GigEVision", "serial": "DA1234567" },
      "driver": "gentl",
      "pixelFormat": "Mono8",
      "roi": { "OffsetX": 0, "OffsetY": 0, "Width": 2448, "Height": 2048 },
      "featureSets": {
        "default": { "ExposureTime": 600, "Gain": 0, "GammaEnable": false }
      },
      "trigger": { "type": "software" },
      "transportTuning": { "GevSCPSPacketSize": 8164, "GevSCPD": 0 },
      "ptp": "off"
    }
  ],
  "acquisition": {
    "mode": "plc",
    "timeoutMs": 500,
    "steps": [
      { "id": "main", "cameras": ["cam1"], "featureSet": "default", "light": 1 }
    ],
    "simulator": { "folder": "images/test", "pattern": "*.png" }
  },
  "calibrations": [
    {
      "id": "calTop",
      "camera": "cam1",
      "file": "calib/calTop.json",
      "from": "image",
      "to": "world",
      "residualUm": 18,
      "made": { "at": "2026-11-02T07:50:00Z", "by": "eng.rao" }
    }
  ],
  "nodes": [
    {
      "id": "findLabel",
      "tool": "locate.geometric",
      "toolVersion": "1.0",
      "labelKey": "tool.locate.findPart",
      "space": "image",
      "roi": { "shape": "rect", "x": 200, "y": 150, "w": 2050, "h": 1750 },
      "params": { "angleRange": [-15, 15], "scaleRange": [0.95, 1.05], "maxInstances": 1, "acceptScore": 60 },
      "minConfidence": 60,
      "state": "templates/findLabel.bin"
    },
    {
      "id": "topEdge",
      "tool": "measure.findLine",
      "toolVersion": "1.0",
      "labelKey": "job.check.labelStraight",
      "space": "fixture:findLabel",
      "roi": { "shape": "rotRect", "cx": 0, "cy": -610, "w": 1500, "h": 80, "angle": 0 },
      "params": { "polarity": "darkToLight", "calipers": 12, "edgeThreshold": 15, "calibration": "calTop" },
      "limits": { "angleDeg": { "min": -2.0, "max": 2.0, "unit": "deg" } }
    },
    {
      "id": "lotDate",
      "tool": "ocr.ocv",
      "toolVersion": "1.1",
      "labelKey": "tool.ocr.checkText",
      "space": "fixture:findLabel",
      "roi": { "shape": "rect", "x": -700, "y": 120, "w": 900, "h": 260 },
      "params": {
        "engine": "ai",
        "lines": [
          { "pattern": "LOT {lot}" },
          { "pattern": "EXP {expiry:MM/yyyy}" }
        ],
        "dateCheck": { "field": "expiry", "relativeTo": "today", "addMonths": 24, "toleranceDays": 31 },
        "minCharScore": 70
      },
      "minConfidence": 70
    },
    {
      "id": "code",
      "tool": "code.read",
      "toolVersion": "1.0",
      "labelKey": "tool.code.read",
      "space": "fixture:findLabel",
      "roi": { "shape": "rect", "x": 350, "y": 60, "w": 420, "h": 420 },
      "params": { "symbologies": ["DataMatrix"], "expectedCount": 1, "gs1": true, "timeBudgetMs": 60 },
      "limits": { "gs1.01": { "equals": "09501101530003" } },
      "minConfidence": 50,
      "state": "templates/code.learned.txt"
    },
    {
      "id": "lotMatch",
      "tool": "logic.compare",
      "toolVersion": "1.0",
      "labelKey": "job.check.lotMatchesCode",
      "params": { "op": "equals", "caseSensitive": true }
    }
  ],
  "wires": [
    { "from": "acq.main.cam1", "to": "findLabel.image" },
    { "from": "acq.main.cam1", "to": "topEdge.image" },
    { "from": "findLabel.pose", "to": "topEdge.fixture" },
    { "from": "acq.main.cam1", "to": "lotDate.image" },
    { "from": "findLabel.pose", "to": "lotDate.fixture" },
    { "from": "input.lot", "to": "lotDate.expected.lot" },
    { "from": "acq.main.cam1", "to": "code.image" },
    { "from": "findLabel.pose", "to": "code.fixture" },
    { "from": "lotDate.fields.lot", "to": "lotMatch.a" },
    { "from": "code.gs1.10", "to": "lotMatch.b" }
  ],
  "verdict": {
    "rule": "all",
    "reasons": ["findLabel", "code", "lotDate", "lotMatch", "topEdge"]
  },
  "variants": {
    "default": "p250",
    "selectBy": "both",
    "items": [
      {
        "id": "p250",
        "plcCode": 1,
        "labelKey": "job.variant.p250",
        "photo": "images/product/p250.png",
        "set": { "code.limits.gs1.01.equals": "09501101530003" }
      },
      {
        "id": "p500",
        "plcCode": 2,
        "labelKey": "job.variant.p500",
        "photo": "images/product/p500.png",
        "set": {
          "code.limits.gs1.01.equals": "09501101530010",
          "lotDate.params.dateCheck.addMonths": 36,
          "cameras.cam1.featureSets.default.ExposureTime": 750
        }
      }
    ]
  },
  "plc": {
    "connection": { "protocol": "s7", "host": "192.168.10.20", "rack": 0, "slot": 1, "db": 210 },
    "heartbeatMs": 250,
    "handshake": {
      "trigger": "DBX0.0", "busy": "DBX2.0", "done": "DBX2.1", "ok": "DBX2.2", "ng": "DBX2.3",
      "error": "DBX2.4", "heartbeatIn": "DBX0.7", "heartbeatOut": "DBX2.7",
      "triggerId": "DBW4", "jobSelect": "DBW6", "step": "DBW8", "errorCode": "DBW10"
    },
    "inputs": [
      { "name": "lot", "address": "DBB20", "type": "string", "length": 16 }
    ],
    "results": [
      { "from": "verdict.failedNodeIndex", "address": "DBW12", "type": "int" },
      { "from": "topEdge.angleDeg", "address": "DBW14", "type": "int", "scale": 100 },
      { "from": "code.text", "address": "DBB40", "type": "string", "length": 64 }
    ]
  },
  "outputs": {
    "record": { "fields": ["code.text", "code.gs1.21", "lotDate.fields.lot", "lotDate.fields.expiry", "topEdge.angleDeg"] },
    "csv": { "enabled": true, "folder": "D:/VisionData/csv", "rotate": "daily" },
    "images": { "save": "ng", "format": "png", "overlay": true, "quotaGb": 50 }
  },
  "labels": {
    "keys": [
      "job.name", "job.cam.top", "job.check.labelStraight", "job.check.lotMatchesCode",
      "job.variant.p250", "job.variant.p500"
    ]
  },
  "release": {
    "approvedBy": "sup.patil",
    "approvedAt": "2026-11-05T14:02:00Z",
    "expected": "images/test/expected.json"
  }
}
```

`i18n/en.json` for this job holds, for example, `"job.check.lotMatchesCode": "Lot on label matches
the code"`, and `i18n/hi.json` holds the Hindi text for the same keys. The GTINs above are
examples, not real products.

## 6. Example: two synchronised cameras

A fragment showing two GigE cameras triggered together by one GigE Vision action command on a
PTP clock, followed by a dark-field step on the top camera only. This needs the Multi-Camera &
Enterprise licence.

```json
{
  "requires": { "parents": [ { "code": "BASE", "minVersion": "1.0" }, { "code": "MCE", "minVersion": "1.0" },
                             { "code": "MEA", "minVersion": "1.0" }, { "code": "CAL", "minVersion": "1.0" } ] },
  "cameras": [
    { "id": "left",  "match": { "transport": "GigEVision", "serial": "40012345" }, "pixelFormat": "Mono8",
      "featureSets": { "bf": { "ExposureTime": 400 }, "df": { "ExposureTime": 1500 } },
      "trigger": { "type": "action", "deviceKey": "0x5653", "groupKey": 1, "groupMask": "0x1" }, "ptp": "required" },
    { "id": "right", "match": { "transport": "GigEVision", "serial": "40012346" }, "pixelFormat": "Mono8",
      "featureSets": { "bf": { "ExposureTime": 400 } },
      "trigger": { "type": "action", "deviceKey": "0x5653", "groupKey": 1, "groupMask": "0x1" }, "ptp": "required" }
  ],
  "acquisition": {
    "mode": "plc",
    "timeoutMs": 400,
    "steps": [
      { "id": "both", "cameras": ["left", "right"], "featureSet": "bf", "light": 1, "sync": "action" },
      { "id": "dark", "cameras": ["left"], "featureSet": "df", "light": 2 }
    ]
  },
  "calibrations": [
    { "id": "stitch", "camera": ["left", "right"], "file": "calib/stitch.json", "from": "image", "to": "world:line", "residualUm": 25 }
  ],
  "wires": [
    { "from": "acq.both.left",  "to": "edgeL.image" },
    { "from": "acq.both.right", "to": "edgeR.image" },
    { "from": "edgeL.point",    "to": "width.a" },
    { "from": "edgeR.point",    "to": "width.b" }
  ]
}
```

Here `edgeL` and `edgeR` find the part's two ends in each camera, and `width` measures the
distance between them in `world:line` millimetres through the shared calibration (PRD §8,
"Synchronised" jobs).

## 7. Today's job file: the Measure tool (VisionCore)

VisionCore writes a first subset of this format (`dotnet/VisionCore/Jobs/Job.cs`): a flat `Tools`
list, each tool stored by `kind`. The Measure tool ("Check size", TOOL-SPECIFICATIONS.md tool 2)
is `kind: "measure"`. It holds the features it finds and the checks on them; on the target
format each feature is a `measure.caliper`, `measure.line`, `measure.circle`, `measure.ellipse`,
`measure.corner`, `measure.gap`, `measure.pitch`, `measure.edges` or `measure.geometry` node and each
check a `limits` entry or a `measure.geometry` node.

| Field | Meaning |
| --- | --- |
| `Features[]` | Features found by calipers, stored by `kind`: `caliper`, `line`, `circle`, `ellipse`, `corner`, `gap`, `pitch`, `edges`, or `construct` (made from features listed before it). Positions are in teach-image pixels (pixel centres on whole numbers); with a Find the part tool on the camera they move and turn with the part |
| `Features[].Name` | Unique in the tool |
| `Features[].Polarity` | `Any`, `DarkToLight` or `LightToDark`, along the search direction |
| `Features[].Threshold` | Lowest edge contrast in grey levels; `0` = auto (from the image's noise, at least 5) |
| `Features[].FilterHalfWidth` | Smoothing in px, 1–20 (default 2; pitch and edges 1, so a neighbouring edge stays out of each edge's window) |
| caliper: `Box` | `{Cx, Cy, Length, Height, AngleDeg}`: search along the length; angle counter-clockwise on the screen, 0 = left to right |
| caliper: `Mode`, `ExpectedWidth`, `ScoreBy` | `Single` or `Pair` (first edge of `Polarity`, the second opposite); `ExpectedWidth` px for `ScoreBy: Width`; `ScoreBy` `Contrast`, `Position` or `Width` |
| line: `X0, Y0, X1, Y1` | The expected edge; calipers search across it from its left to its right seen from the first end (top to bottom for a line drawn left to right) |
| line: `Calipers`, `SearchWidth`, `OutlierPx`, `Fit`, `MinShare` | 3–200 (10); px across the line (20); points farther from the fit are left out (1.5 px); `Ransac` (default) or `LeastSquares`; share of calipers whose point must be used (0.6), else the edge is broken |
| circle: `Cx, Cy, RInner, ROuter` | The ring searched; `StartDeg`, `SpanDeg` (0, 360) the arc |
| circle: `Calipers`, `Inward`, `OutlierPx`, `Fit`, `MinShare` | 8–360 (36); `true` = search from the outside in; as for a line |
| ellipse | The fields of a circle; the ring must hold the whole rim (`RInner` below half the smallest diameter, `ROuter` above half the largest) |
| corner: `Edge1`, `Edge2` | `{X0, Y0, X1, Y1}` each: two edges found as lines (searched as a line's), and where they meet; `Calipers`, `SearchWidth`, `OutlierPx`, `Fit`, `MinShare` as a line's, for both. Edges within 0.5° of parallel have no corner (fails) |
| gap: `Box`, `Samples` | A box as a caliper's, cut across its height into `Samples` slices (1–200, 20; each at least 1 px high); each slice measures the width between an edge of `Polarity` and the next, opposite one along the box. A slice without a width (a closed gap, an edge inside the gap such as a burr, or edges in doubt) fails. A gap needs a width check with a limit |
| pitch: `Box`, `ExpectedCount`, `MinWidth` | A box along a row of pins, starting and ending on the background; a pin is an edge of `Polarity` (`DarkToLight` = bright pins; `Any` = that of the first edge) and the next edge. A count other than `ExpectedCount` (required) fails, naming the missing pin where a pitch is half as long again as the usual one, as does an edge that belongs to no pin or a pin under half the row's usual contrast (a stub); pairs narrower than `MinWidth` px (1) are not pins. Pins need a `Pitch` check with a limit |
| edges: `Box`, `ExpectedCount` | Every edge of `Polarity` along the box (a thread: `Any` counts both sides of each crest); `ExpectedCount` 0 = not checked, and then a check with a limit is needed. `Pitch` holds every spacing, the two end ones too, to its limits: the first and last edge are placed as exactly as the inner ones on a soft image (the engine takes off their lean) |
| construct: `Operation`, `Inputs` | `Intersection` (two lines: a point; parallel fails), `Perpendicular` (a point and a line: the foot of the perpendicular), `Midpoint` (two points), `LineThrough` (two points: a line), `CircleThrough` (three points: a circle); `Inputs` names features listed before it. Points come from calipers, circles and ellipses (centres), corners and point constructions; lines from lines and `LineThrough` |
| `Checks[]` | `{Name, Value, Feature, Feature2, Unit, Min, Max}`: the part fails when the value is below `Min` or above `Max` (either may be left out; with neither the value is only reported). The values go with the part's result as `<tool>.<check>` |
| `Checks[].Value` | caliper: `Position` (along the box from its start), `Width` (pair); line: `Angle` (against the part when fixtured; also a `LineThrough`), `Straightness`; circle: `Diameter`, `Radius` (also a `CircleThrough`), `Roundness` (also an ellipse: largest minus smallest distance of every point found from the fitted rim); ellipse: `Major`, `Minor` (largest and smallest diameter), `AxisRatio` (minor / major); corner: `CornerAngle` (between the edges as they leave the corner, 0–180°); gap: `MinWidth`, `MaxWidth`, `MeanWidth`; pitch and edges: `Count`, `Pitch` (every pitch or spacing must lie within the limits; the worst is reported, and for pins the pin out of place is named), `MeanPitch`; two features: `Distance` (point to point, point to line, or from the middle of the first line to the second; a caliper's point is its edge or the pair's middle, a circle's its centre), `AngleBetween` (two lines' directions, 0–180°) |
| `Checks[].Unit` | `Px` or `Mm` for sizes, `Deg` for angles, `None` for `Count` and `AxisRatio`. `Mm` needs the camera's `Calibration`; the job is refused without it |
| `Cameras[].Calibration` | The camera's lens model and scale at the part's plane (`Fx, Fy, Cx, Cy, K1, K2, P1, P2, K3, MmPerPx, RmsPx`, as `cr_lens`, from the chessboard calibration); `null` = sizes in px only. Measuring is done on undistorted points, so the lens does not bend a straight edge or bias a distance |

Run rules: a feature not found, a faint edge, two edges nearly equally good (the choice in doubt),
a box off the image, a line, circle or ellipse with too few points agreeing, a corner whose edges
do not meet, a gap slice without a width, a pin count other than expected or an edge belonging to
no pin, or a construction that cannot be made (parallel lines, points in a line) fails the tool,
as does a value out of tolerance or a value in mm on a camera without a scale (line safety). A
measure library whose result sizes differ from the program's (another version) fails every
feature. The engine
calls are in `engine/include/vs_measure.h`.

Pixel check per tool (requirements AS-14), in every job file of today's VisionCore:

| Field | Meaning |
| --- | --- |
| `Cameras[].LensFocalMm` | Focal length of the camera's lens in mm; left out = not known. The pixel check then gives the lens remedy as a factor of the focal length instead of in mm |
| `Tools[].PixelsAccepted` | Read code and text tools: `{By, At, FoundPx}`, the engineer's "Accept as is" on a tool with fewer pixels than it needs, and the size found then; left out = not accepted. It holds while a picture's size is at least 95 % of `FoundPx`. The runtime writes `By` (the signed-in user) and `At` (its own time) when the job is saved with a new acceptance; a client cannot change them. Advice only: it never changes the tool's result |

## 8. Open questions

- PRD §12 says job files are encrypted. This design encrypts models always, the job store at rest
  and exported files only with a password. Should an export without a password also be
  encrypted (to a site key), at the cost of not being readable on a PC outside the site?
- Should `history/` travel in exported files, or stay only in the PC's job store?
- Whether variants may switch whole nodes on and off (an `enabled` flag), which the current rule
  forbids for testability.
