# Change notes

Each pull request adds **one new file** here instead of adding rows to
`docs/PROJECT-HISTORY.md`. Two pull requests never add the same file, so
they never conflict here, however many are open at once. See section 7
of the history file for the whole process.

- **Name:** `YYYY-MM-DD-<branch with / as ->.md`, dated the day the claim
  is opened. `scripts/coord.sh note` creates it from the template below.
- **Claim:** the claim commit adds the note with `Status: claimed`. The
  draft pull request that carries it is the claim.
- **Done:** before marking the pull request ready, set `Status: done`
  and fill in the sections. Leave out a section that has nothing in it.
- **Dropped:** close the pull request. The note never reaches main.
- **Edit only your own note.** Another session's note is changed only by
  the roll-up, which deletes it once copied.
- **Preview on ready (automatic):** when the pull request is marked
  ready for review (and on each push while it is ready), the GitHub
  Action `.github/workflows/history-rollup.yml` runs
  `scripts/rollup-preview.sh`: one comment on the pull request shows
  what the note will add to the history, or warns that the note is not
  `Status: done` or is missing. Fix the note before the owner merges.
- **Pending:** list follow-ups you leave (unit tests owed, a device
  try, a part not built) under `## Pending`, one line or bullet each.
  The roll-up adds each as an open item at the end of section 5,
  `- [ ] <text> (pending from PR #n, <date>)`, so the next session finds
  it with `scripts/coord.sh taken`. A line whose text is already an open
  item gets "Still pending" under that item instead.
- **Roll-up (automatic):** right after a merge that adds notes, the
  same Action runs
  `scripts/rollup.py`, which copies every note with `Status: done` into
  the history file and deletes it, in one standing pull request titled
  "History roll-up" for the owner to merge:
  - timeline rows go into section 3 and update-log rows into section 8,
    each after the last row of the same date or earlier;
  - decision rows go at the end of section 4, and lines under a
    `## Links (section 6)` heading at the end of section 6;
  - section 5, for the item named in `Section 5 item:` (its first words
    exactly as on main, or `new`):
    - a line starting `- [x]` replaces that item's line (the first one
      only; write the item's full text after the `(done ...)` part; the
      old text is kept under it as "Earlier text");
    - a line starting `- [ ]` is added as a new item;
    - `- New: [ ] ...` or ``- New item: `[x] ...` `` (with or without
      backticks) is added as a new item;
    - `- "Another item": ...` applies the rest of the line to that item
      (named by its first words) instead of the note's own; so does a
      line that names an item in quotes, as in `- In "Another item",
      ...` or `- Mark "Another item" ...`;
    - ``replace `old words` with `new words` `` or
      `change "old words" to "new words"` edits the item's text exactly,
      when every `old words` is found in it (the new words may be
      empty, ``with `` `` or `to ""`, to take words out);
    - `` `[x] (done ...)` `` or `"[x] (done ...)"` inside other text
      marks the item done, keeping its text;
    - any other line is added under the item as
      "Update <date> (PR #n): ...", and the item stays open.

  Section 5 lines are applied in dependency order: a note that names
  an item another finished note adds (as its own item, or in a
  `- "Other item": ...` line) is applied after that note, whatever
  their file names. Rows keep file-name order. `scripts/rollup_test.py`
  tests this, and the Action runs it first.

  Nothing in a note is dropped or flattened:
    - a section the roll-up does not know (`## Record format`,
      `## Found in review`) goes under the note's item as
      "Details <date> (PR #n), <heading>:" with its lines as written
      (tables, lists and paragraphs);
    - a paragraph in a timeline, decision or update-log section becomes
      a row of that table;
    - a `- "Name": ...` line whose name is not an item (a decision's
      topic, an old name) goes, whole, under the note's own item, or the
      first item the note adds;
    - a `new` note that adds no item becomes a done item named by the
      note's title, with its lines under it.

  Only text with nowhere to go (no item named, found or added by the
  note) is copied at the end of section 5, marked "Roll-up: place by
  hand", and listed in the pull request. If main
  changes the history file where the open roll-up did, the Action
  closes that pull request and makes a new one from main (the notes are
  still there, so nothing is lost).

  Until the roll-up is merged, main's state is section 5 **plus** the
  notes here, and `scripts/coord.sh taken` reads both. To run it by hand
  (if the Action is off): `scripts/rollup.py --dry-run` shows what it
  would do; without `--dry-run` it changes the working tree.

<!-- template -->
# <item, in the words of section 5 or a new one-line item>

- Status: claimed
- Session: <session name>, <YYYY-MM-DD>
- Pull request: #<n>
- Section 5 item: <first words of the existing item exactly as on main, or "new">
- Components/files: <components and shared files this will change>
- Depends on: <pull requests that must merge first, or none>

## Section 5
<!-- When done (forms in the README's roll-up rules): "- [x] (done YYYY-MM-DD, PR #n: ...) <the item's text>" replaces the item named above; "- [ ] ..." adds a new item; any other line is added under the item as an update. -->

## Timeline (section 3)
<!-- | YYYY-MM-DD | what was built and verified | requirement sections | -->

## Decisions (section 4)
<!-- | Topic | Decision and reason | requirement sections | -->

## Update log (section 8)
<!-- | YYYY-MM-DD | what this pull request changed in the docs | Claude (<machine>, branch <branch>) | -->

## Pending
<!-- Follow-ups this session leaves, one per line: "- Unit tests for ...". Each becomes an open section 5 item. Leave the section out if none. -->
<!-- end template -->
