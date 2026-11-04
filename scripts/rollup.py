#!/usr/bin/env python3
"""History roll-up (docs/PROJECT-HISTORY.md section 7, rule 12).

Copies every finished change note in docs/changes/ into
docs/PROJECT-HISTORY.md and deletes the note, in the working tree:

  Timeline (section 3)    rows go into the section 3 table, in date order
  Decisions (section 4)   rows go at the end of the section 4 table
  Links (section 6)       lines go at the end of section 6
  Update log (section 8)  rows go into the section 8 table, in date order
  Section 5               see below

Section 5, for the item the note's "Section 5 item:" names (its first
words as on main; "new" for none):

  - [x] ...    replaces that item's line (the first [x] line only; any
               other [x] or [ ] line is added as a new item)
  - [ ] ...    is added as a new item at the end of section 5
  a backticked `[x] (done ...)` inside other text marks that item done
  any other line is added under that item as "Update <date> (PR #n): ..."

Pending (a "## Pending" section): each line or bullet is a follow-up the
session left; it is added at the end of section 5 as an open item,
"- [ ] <text> (pending from PR #n, <date>)", unless an open item with
the same text is there already (then "Still pending" goes under it).

Nothing in a note is dropped or flattened:

  a section the roll-up does not know ("Record format", "Found in review")
               goes under the note's item as "Details (PR #n), <heading>:"
               with its lines as written (tables, lists, paragraphs)
  a paragraph in a table section becomes a row of that table
  a line naming an item that does not exist ('- "Some decision": ...')
               goes under the note's own item, or the first item it adds

Only what has nowhere to go (no item named, found or added by the note)
is copied at the end of section 5, marked "Roll-up: place by hand", and
listed in the summary.

Notes whose header is not "Status: done" are left alone.

Usage: scripts/rollup.py [--dry-run] [--summary FILE] [--only NOTE ...]
(--only: just these notes, as the preview on a pull request marked
ready uses it; scripts/rollup-preview.sh)
Run from anywhere inside the repository. Python 3.8 or later, no packages.
"""

import argparse
import os
import re
import subprocess
import sys

HIST = os.path.join("docs", "PROJECT-HISTORY.md")
NOTES = os.path.join("docs", "changes")
DATE = re.compile(r"\|\s*(\d{4}-\d{2}-\d{2})\s*\|")
ITEM = re.compile(r"^- \[([ ~x])\] (?:\*\*Requested[^*]*\*\* )?(.*)$")
# `[x] (done ...)` or "[x] (done ...)" inside other text: a done-mark.
DONE_MARK = re.compile(r"`\[x\] \(([^`]*?)\)\.?`|[\"\u201c]\[x\] \((.*)\)\.?[\"\u201d]")
# "- New item ...: `[x] ...`" or "`[ ] ...`": a new item, written in backticks.
NEW_ITEM = re.compile(r"^-\s*New(?:\s+item)?\b[^`\[]*?(?:`(\[[ x]\]\s[^`]+)`|(\[[ x]\]\s.+?))\s*$", re.I)
# '- "Other item": ...': a line about another item than the note's own.
OTHER_ITEM = re.compile(r'^-\s*["\u201c]([^"\u201d]+)["\u201d]\s*:\s*(.*)$')
# "replace `old` with `new`": an exact edit of the item's text.
# The new words may be empty (with `` or to ""): words taken out.
REPLACE = re.compile(r"replace `([^`]+)` with `([^`]*)`|change \"([^\"]+)\" to \"([^\"]*)\"")
# A quoted name in a line ('In "X" ...', 'Mark "X" ...', 'The item "X"
# ...'): the line may be about that item rather than the note's own.
QUOTED = re.compile(r'["\u201c]([^"\u201d]{8,})["\u201d]')
SEPARATOR = re.compile(r"^\|(\s*:?-{3,}:?\s*\|)+\s*$")


def repo_root():
    out = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True)
    if out.returncode != 0:
        sys.exit("rollup: run this inside the repository")
    return out.stdout.strip()


# ---- notes ----

class Note:
    def __init__(self, path):
        self.path = path
        self.name = os.path.basename(path)
        raw = open(path, encoding="utf-8").read()
        text = re.sub(r"<!--.*?-->", "", raw, flags=re.S)
        self.title, self.fields, self.sections = "", {}, {}
        current = None
        for line in text.splitlines():
            if current is None and line.startswith("# ") and not self.title:
                self.title = line[2:].strip()
                continue
            m = re.match(r"^## (.+?)\s*$", line)
            if m:
                current = m.group(1)
                self.sections.setdefault(current, [])
                continue
            if current is None:
                f = re.match(r"^- ([A-Za-z0-9 ]+?):\s*(.*)$", line)
                if f:
                    self.fields[f.group(1).strip().lower()] = f.group(2).strip()
                continue
            self.sections[current].append(line.rstrip())
        for k in self.sections:
            while self.sections[k] and not self.sections[k][-1].strip():
                self.sections[k].pop()
            while self.sections[k] and not self.sections[k][0].strip():
                self.sections[k].pop(0)

    @property
    def done(self):
        return self.fields.get("status", "").lower().startswith("done")

    @property
    def pr(self):
        m = re.search(r"#\d+", self.fields.get("pull request", ""))
        return m.group(0) if m else ""

    @property
    def target(self):
        t = self.fields.get("section 5 item", "")
        # "new", or "new (...)", "new: ...": not an item that starts with "New ..."
        return "" if re.match(r"^new\s*(?:$|[(\[,:;])", t, re.I) else t

    def date(self):
        m = re.match(r"(\d{4}-\d{2}-\d{2})", self.name)
        return m.group(1) if m else ""

    def section(self, number):
        """Lines of the note section for history section number."""
        names = {3: "timeline", 4: "decisions", 5: "section 5", 6: "links", 8: "update log"}
        out = []
        for heading, lines in self.sections.items():
            if heading.lower().startswith(names[number]):
                if out:
                    out.append("")
                out += lines
        return out

    def pending(self):
        """Lines of the note's "## Pending" section(s): follow-ups."""
        out = []
        for heading, lines in self.sections.items():
            if heading.lower().startswith("pending"):
                out += [""] + lines if out else lines
        return out

    def unknown_sections(self):
        known = ("timeline", "decisions", "section 5", "links", "update log", "pending")
        return [h for h in self.sections if not h.lower().startswith(known) and self.sections[h]]


def blocks(lines):
    """Top-level bullets with their indented lines, and paragraphs."""
    out, cur = [], []
    for line in lines:
        if not line.strip():
            if cur:
                out.append(cur)
                cur = []
            continue
        if line.startswith("- ") and cur:
            out.append(cur)
            cur = []
        cur.append(line)
    if cur:
        out.append(cur)
    return out


# ---- the history file ----

class History:
    def __init__(self, path):
        self.path = path
        self.lines = open(path, encoding="utf-8").read().split("\n")

    def bounds(self, number):
        start = next((i for i, l in enumerate(self.lines) if l.startswith("## %d." % number)), None)
        if start is None:
            sys.exit("rollup: %s has no section %d" % (self.path, number))
        end = next((i for i in range(start + 1, len(self.lines)) if self.lines[i].startswith("## ")), len(self.lines))
        return start, end

    def table_header(self, number):
        """The index of the header line of the section's first table, or
        None."""
        start, end = self.bounds(number)
        i = next((j for j in range(start, end) if self.lines[j].startswith("|")), None)
        if i is None or i + 1 >= end or not SEPARATOR.match(self.lines[i + 1]):
            return None
        return i

    def table_rows(self, number):
        """The body rows of the section's first table: the unbroken run of
        "|" lines under its header and separator."""
        start, end = self.bounds(number)
        i = self.table_header(number)
        if i is None:
            return []
        rows, j = [], i + 2
        while j < end and self.lines[j].startswith("|"):
            rows.append(j)
            j += 1
        return rows

    def add_row(self, number, row, by_date):
        rows = self.table_rows(number)
        if not rows:
            header = self.table_header(number)
            if header is None:
                sys.exit("rollup: section %d has no table" % number)
            self.lines.insert(header + 2, row)
            return
        at = rows[-1] + 1
        m = DATE.match(row)
        if by_date and m:
            # After the last row of the same date or earlier (older rows
            # are not always in order, so never before an early one).
            same_or_earlier = [i for i in rows if DATE.match(self.lines[i]) and DATE.match(self.lines[i]).group(1) <= m.group(1)]
            at = same_or_earlier[-1] + 1 if same_or_earlier else rows[0]
        self.lines.insert(at, row)

    def end_of(self, number):
        """Index after the last non-blank line of a section."""
        start, end = self.bounds(number)
        i = end
        while i > start + 1 and not self.lines[i - 1].strip():
            i -= 1
        return i

    def append(self, number, new_lines):
        at = self.end_of(number)
        self.lines[at:at] = new_lines

    def items(self):
        start, end = self.bounds(5)
        out = []
        for i in range(start, end):
            m = ITEM.match(self.lines[i])
            if m:
                out.append((i, m.group(1), m.group(2)))
        return out

    def find_item(self, words):
        """The section 5 item starting with words: an open or claimed one,
        else one already done."""
        open_items = [(i, norm(t)) for i, mark, t in self.items() if mark in " ~"]
        # An item already done may still get updates (a later pull request
        # adding to finished work); its text often starts with "(done ...)".
        done_items = [(i, norm(without_mark(t))) for i, mark, t in self.items() if mark == "x"]
        for candidates in (open_items, done_items):
            for a in lookups(words):
                hits = [i for i, t in candidates if t.startswith(a)]
                if len(hits) == 1:
                    return hits[0]
        # A name of four words or more found inside one item's text (the
        # item may begin with a bold label or a different first word).
        a = norm(words)
        if len(a.split()) >= 4:
            for candidates in (open_items, done_items):
                hits = [i for i, t in candidates if a in t]
                if len(hits) == 1:
                    return hits[0]
        return None

    def is_done(self, i):
        m = ITEM.match(self.lines[i])
        return bool(m) and m.group(1) == "x"

    def item_end(self, i):
        """Index after item i and its indented lines."""
        j = i + 1
        while j < len(self.lines) and self.lines[j].startswith("  "):
            j += 1
        return j

    def save(self):
        open(self.path, "w", encoding="utf-8").write("\n".join(self.lines))


def without_mark(t):
    """An item's text without its leading "(done ...)" mark, which may
    hold brackets of its own ("(done ..., PR #525: ... (owner and
    co-owners) ...)"): cut at the bracket that closes the first one."""
    if not t.startswith("("):
        return t
    depth = 0
    for i, c in enumerate(t):
        depth += {"(": 1, ")": -1}.get(c, 0)
        if depth == 0:
            return t[i + 1:].lstrip()
    return t


def norm(s):
    s = re.sub("[`*\"\u201c\u201d]", "", s)
    return re.sub(r"\s+", " ", s).strip().lower()


def lookups(words):
    """The normalised forms an item is looked up by: the name as written,
    which may be short, then shortened ones of at least three words."""
    out = []
    for n, attempt in enumerate(variants(words)):
        a = norm(attempt)
        if a and not (n > 0 and len(a.split()) < 3):
            out.append(a)
    return out


def variants(words):
    """words, then shorter forms of it to look an item up by."""
    seen, out = set(), []
    words = re.sub("[\"\u201c\u201d]", "", words)

    def add(v):
        v = v.strip().rstrip(",:;.").strip()
        if v and v not in seen:
            seen.add(v)
            out.append(v)
    add(words)
    w = words
    while re.search(r"\s*\([^()]*\)\s*$", w):
        w = re.sub(r"\s*\([^()]*\)\s*$", "", w)
        add(w)
    for cut in (", part", " — ", " - ", ":"):
        if cut in words:
            add(words.split(cut)[0])
            add(re.sub(r"\s*\([^()]*\)\s*$", "", words.split(cut)[0]))
    ws = words.split()
    for k in range(len(ws) - 1, 3, -1):
        add(" ".join(ws[:k]))
    return out


# ---- applying one note ----

def apply_note(h, note, final, report):
    """Applies note to h. Returns False to try again later (the item it
    names may come from a note not applied yet), unless final."""
    s5 = note.section(5)
    target = note.target
    idx = h.find_item(target) if target else None
    if target and idx is None and not final and s5:
        return False
    tag = "PR %s" % note.pr if note.pr else note.name
    placed = []

    done = set()  # items already marked done by this note
    added = []  # items this note added, for lines that name no real item

    def insert(at, new_lines):
        nonlocal idx
        h.lines[at:at] = new_lines
        if idx is not None and at <= idx:
            idx += len(new_lines)
        added[:] = [a + len(new_lines) if at <= a else a for a in added]

    def add_item(new_lines):
        added.append(h.end_of(5))
        h.append(5, new_lines)

    def home():
        """Where lines go that name no item: the note's own, else the
        first item it added."""
        return idx if idx is not None else (added[0] if added else None)

    def mark_done(i, mark):
        im = ITEM.match(h.lines[i])
        h.lines[i] = "- [x] (%s) %s" % (mark, im.group(2))
        done.add(i)
        placed.append("section 5: item marked done")

    for b in blocks(s5):
        first = b[0]
        text = " ".join(l.strip() for l in b)
        body = re.sub(r"^- ", "", text)
        m = re.match(r"^- \[([ x])\] ", first)
        if m and m.group(1) == "x" and idx is not None and idx not in done and not h.is_done(idx):
            # The item's old text is kept under it, so nothing is lost.
            old = ITEM.match(h.lines[idx])
            h.lines[idx:idx + 1] = b + ["  - Earlier text: " + old.group(2)]
            done.add(idx)
            placed.append("section 5: item marked done")
            continue
        if m and m.group(1) == "x" and idx is not None and h.is_done(idx):
            # Already done: what this note adds goes under it.
            insert(h.item_end(idx), ["  - Update %s (%s): %s" % (note.date(), tag, re.sub(r"^- \[x\] ", "", text))])
            placed.append("section 5: update under the item (already done)")
            continue
        if m and m.group(1) == "x" and target and idx is None:
            h.append(5, ["- [ ] **Roll-up: place by hand** (%s, item \"%s\" not found, so not marked done): %s" % (tag, target, body)])
            report.append("%s: the section 5 item \"%s\" was not found, so it is not marked done; the text is at the end of section 5, marked \"Roll-up: place by hand\"" % (note.name, target))
            continue
        if m:
            add_item(b)
            placed.append("section 5: new item " + ("(done)" if m.group(1) == "x" else "(open)"))
            continue
        nm = NEW_ITEM.match(text)
        if nm:
            item = nm.group(1) or nm.group(2)
            add_item(["- " + item])
            placed.append("section 5: new item " + ("(done)" if item.startswith("[x]") else "(open)"))
            continue
        at_item, body_here = idx, body
        qm = OTHER_ITEM.match(text)
        if qm:
            at_item, body_here = h.find_item(qm.group(1)), qm.group(2)
            if at_item is None and home() is not None:
                # Not an item (a decision's topic, an old name): the line
                # goes, whole, under the note's own item.
                at_item, body_here = home(), body
            if at_item is None:
                h.append(5, ["- [ ] **Roll-up: place by hand** (%s, item \"%s\" not found): %s" % (tag, qm.group(1), body)])
                report.append("%s: the section 5 item \"%s\" was not found; its text is at the end of section 5, marked \"Roll-up: place by hand\"" % (note.name, qm.group(1)))
                continue
        else:
            # 'In "X" ...', 'Mark "X" ...': the first quoted name that is
            # an item, if any, else the note's own item.
            for name in QUOTED.findall(text):
                i = h.find_item(name)
                if i is not None:
                    at_item = i
                    break
        if at_item is None:
            at_item = home()
        if at_item is not None:
            edits = [(a or c, b_ or d) for a, b_, c, d in REPLACE.findall(text)]
            line = h.lines[at_item]
            if edits and all(a in line for a, _ in edits):
                for a, b_ in edits:
                    line = line.replace(a, b_, 1)
                h.lines[at_item] = line
                placed.append("section 5: item text edited")
                continue
            dm = DONE_MARK.search(text)
            if dm and at_item not in done and not h.is_done(at_item):
                mark_done(at_item, dm.group(1) or dm.group(2))
            insert(h.item_end(at_item), ["  - Update %s (%s): %s" % (note.date(), tag, body_here)])
            placed.append("section 5: update under the item")
            continue
        if not target:
            # A finished "new" note that adds no item: its title becomes
            # a done item, and the line goes under it.
            add_item(["- [x] (done %s, %s) %s" % (note.date(), tag, note.title or note.name)])
            placed.append("section 5: new item (done, from the note's title)")
            insert(h.item_end(home()), ["  - Update %s (%s): %s" % (note.date(), tag, body)])
            placed.append("section 5: update under the item")
            continue
        h.append(5, ["- [ ] **Roll-up: place by hand** (%s, item \"%s\" not found): %s" % (tag, target, body)])
        report.append("%s: the section 5 item \"%s\" was not found; its text is at the end of section 5, marked \"Roll-up: place by hand\"" % (note.name, target))

    note.pending_added = []
    for b in blocks(note.pending()):
        text = re.sub(r"^-\s*(?:\[ \]\s*)?", "", b[0].strip())
        if not text:
            continue
        same = [i for i, mark, t in h.items() if mark in " ~" and norm(re.sub(r"\s*\(pending from [^()]*\)$", "", t)) == norm(text)]
        if same:
            insert(h.item_end(same[0]), ["  - Update %s (%s): Still pending." % (note.date(), tag)])
            placed.append("section 5: still pending (item already open)")
            continue
        rest = [l if l.startswith("  ") else "  " + l.strip() for l in b[1:]]
        add_item(["- [ ] %s (pending from %s, %s)" % (text, tag, note.date())] + rest)
        note.pending_added.append(text)
        placed.append("section 5: pending item (open)")

    for heading in note.unknown_sections():
        lines = details(note.sections[heading])
        if home() is not None:
            insert(h.item_end(home()), ["  - Details %s (%s), %s:" % (note.date(), tag, heading)] + lines)
            placed.append("section 5: details under the item")
            continue
        h.append(5, ["- [ ] **Roll-up: place by hand** (%s, from its \"%s\" section): %s" % (tag, heading, note.title)] + lines)
        report.append("%s: has a section \"%s\" and no section 5 item to put it under; added at the end of section 5" % (note.name, heading))

    note.placed_list = placed
    return True


def details(lines):
    """A note section's lines as written, indented to sit under an item's
    "Details" line (a blank line kept as an indented one, so the item
    does not end there)."""
    return ["    " + l if l.strip() else "    " for l in lines]


def paragraph_row(h, number, note, tag, text):
    """A row of section number's table holding a paragraph the note wrote
    there instead of a row."""
    header = h.table_header(number)
    columns = len(h.lines[header].strip().strip("|").split("|")) if header is not None else 3
    text = text.replace("|", "\\|")
    cells = {3: [note.date(), text, ""], 4: [note.title or tag, text, ""], 8: [note.date(), text, "Claude (%s)" % tag]}[number]
    cells = (cells + [""] * columns)[:max(columns, 2)]
    return "| " + " | ".join(cells) + " |"


def apply_rows(h, note, report):
    """Copies note's timeline, decision and update-log rows, its links
    and any section the roll-up does not know (after apply_note), and
    sums up what was placed."""
    tag = "PR %s" % note.pr if note.pr else note.name
    placed = note.placed_list
    for number, by_date in ((3, True), (4, False), (8, True)):
        para = []

        def flush():
            if para:
                h.add_row(number, paragraph_row(h, number, note, tag, " ".join(para)), by_date)
                placed.append("section %d: row (from a paragraph)" % number)
                del para[:]
        for line in note.section(number) + [""]:
            row = line.strip()
            if not row or row.startswith("|"):
                flush()
            if not row:
                continue
            if SEPARATOR.match(row) or re.match(r"^\|\s*(Date|Topic)\s*\|", row):
                continue  # a copied table header
            if row.startswith("|") and row.endswith("|"):
                h.add_row(number, row, by_date)
                placed.append("section %d: row" % number)
            else:
                para.append(row)
    links = [l for l in note.section(6) if l.strip()]
    if links:
        h.append(6, links)
        placed.append("section 6: %d line(s)" % len(links))
    counts = {}
    for p in placed:
        counts[p] = counts.get(p, 0) + 1
    note.placed = ", ".join("%s%s" % (k, " x%d" % v if v > 1 else "") for k, v in counts.items()) or "nothing to copy"
    return True


def added_items(note):
    """The normalised texts of the section 5 items note adds."""
    out = []
    for b in blocks(note.section(5)):
        text = " ".join(l.strip() for l in b)
        m = re.match(r"^- \[([ x])\] (.*)$", text)
        if m and m.group(1) == "x" and note.target:
            continue  # marks the note's own item done; adds nothing
        nm = None if m else NEW_ITEM.match(text)
        item = m.group(2) if m else (nm.group(1) or nm.group(2)) if nm else None
        if item:
            out.append(norm(without_mark(re.sub(r"^\[[ x]\]\s*", "", item))))
    for b in blocks(note.pending()):
        out.append(norm(re.sub(r"^-\s*(?:\[ \]\s*)?", "", b[0].strip())))
    return out


def named_items(note):
    """The items note names: its own, and the others its section 5 lines
    name ('- "Other item": ...', 'In "Other item" ...')."""
    out = [note.target] if note.target else []
    for b in blocks(note.section(5)):
        text = " ".join(l.strip() for l in b)
        qm = OTHER_ITEM.match(text)
        out += [qm.group(1)] if qm else QUOTED.findall(text)
    return out


def ordered(ready):
    """The finished notes in the order to apply them: by name, notes with
    no item of their own first, except that a note naming an item another
    note adds comes after that note. In plain name order a note could
    miss an item added later (found 2026-10-10: two 2026-10-09 notes
    placed by hand) or, by a shortened name, land on an older item
    already done (#637's done mark went under "Admin panel: edit a
    profile's and a type's ...", leaving its own item open)."""
    rest = sorted(ready, key=lambda n: (bool(n.target), n.name))
    adds = {n.name: added_items(n) for n in rest}
    names = {n.name: [a for w in named_items(n) for a in lookups(w)] for n in rest}

    def needs(a, b):  # a names an item b adds
        return a is not b and any(t.startswith(w) for w in names[a.name] for t in adds[b.name])
    out = []
    while rest:
        # The first note that no other remaining note must go before; in
        # a loop (each needing the other), the first by name.
        n = next((x for x in rest if not any(needs(x, y) for y in rest)), rest[0])
        out.append(n)
        rest.remove(n)
    return out


def apply_all(h, ready, report):
    """Applies the finished notes to h: their section 5 lines in
    dependency order (ordered; a note whose item is still not there gets
    one more try after the rest, then is placed by hand), then their
    rows in the usual order, so rows of the same date keep the order
    they always had."""
    pending = [n for n in ordered(ready) if not apply_note(h, n, False, report)]
    for n in pending:
        apply_note(h, n, True, report)
    for n in sorted(ready, key=lambda n: (bool(n.target), n.name)):
        apply_rows(h, n, report)


def main():
    ap = argparse.ArgumentParser(description="Copy finished change notes into docs/PROJECT-HISTORY.md and delete them.")
    ap.add_argument("--dry-run", action="store_true", help="show what would be done; change nothing")
    ap.add_argument("--summary", help="also write the summary (Markdown) to this file")
    ap.add_argument("--only", nargs="+", metavar="NOTE", help="roll up just these notes (paths or file names in docs/changes)")
    args = ap.parse_args()
    os.chdir(repo_root())

    paths = sorted(os.path.join(NOTES, f) for f in os.listdir(NOTES) if f.endswith(".md") and f != "README.md")
    if args.only:
        only = {os.path.basename(o) for o in args.only}
        paths = [p for p in paths if os.path.basename(p) in only]
    notes = [Note(p) for p in paths]
    ready = [n for n in notes if n.done]
    waiting = [n for n in notes if not n.done]
    if not ready:
        print("rollup: no finished notes to roll up")
        if args.summary:
            open(args.summary, "w").write("")
        return 0

    h = History(HIST)
    report = []
    apply_all(h, ready, report)

    lines = ["Rolled up %d change note(s) into docs/PROJECT-HISTORY.md and deleted them:" % len(ready), ""]
    for n in sorted(ready, key=lambda n: n.name):
        lines.append("- `%s` (%s): %s" % (n.name, n.pr or "no PR", n.placed))
    pending = [(n, t) for n in sorted(ready, key=lambda n: n.name) for t in n.pending_added]
    if pending:
        lines += ["", "Pending items added to section 5 (open):", ""] + ["- %s (%s)" % (t, n.pr or n.name) for n, t in pending]
    if waiting:
        lines += ["", "Left alone (not `Status: done`):", ""] + ["- `%s`" % n.name for n in waiting]
    if report:
        report = list(dict.fromkeys(report))  # each problem once
        lines += ["", "**Needs a person** (copied, marked \"Roll-up: place by hand\"):", ""] + ["- " + r for r in report]
    summary = "\n".join(lines) + "\n"
    print(summary)
    if args.summary:
        open(args.summary, "w").write(summary)
    if args.dry_run:
        return 0
    h.save()
    for n in ready:
        os.remove(n.path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
