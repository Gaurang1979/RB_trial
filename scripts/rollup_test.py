#!/usr/bin/env python3
"""Tests for rollup.py's order and item matching (run: python3 scripts/rollup_test.py).

Each case is one found on main on 2026-10-10, on a small history."""
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rollup  # noqa: E402

HISTORY = """# History

## 3. Timeline

| Date | What | Sections |
|---|---|---|
| 2026-10-01 | Earlier work | 5R |

## 4. Decisions

| Topic | Decision | Sections |
|---|---|---|

## 5. Open items

- [x] (done 2026-10-01, PR #1: one choice per hardware class; a browser check) Admin panel: edit a profile's and a type's hardware choices
- [x] (done 2026-10-02, PR #4: the screen (owner and co-owners) with Rename) Owner app: device names kept on the router
- [ ] Mobile User app, next: Home, the share sheet and joining a router by invite (a seat)
- [ ] Owner app: on a PC's seat, pin this PC's network address

## 6. Links

## 8. Update log

| Date | What | Who |
|---|---|---|
"""


def note(status="done", pr="#9", target="new", s5="", s3=""):
    return """# A note

- Status: %s
- Pull request: %s
- Section 5 item: %s

## Section 5
%s

## Timeline (section 3)
%s
""" % (status, pr, target, s5, s3)


class RollupOrder(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.hist = os.path.join(self.dir, "PROJECT-HISTORY.md")
        with open(self.hist, "w") as f:
            f.write(HISTORY)

    def run_notes(self, notes):
        ready = []
        for name, text in notes.items():
            p = os.path.join(self.dir, name)
            with open(p, "w") as f:
                f.write(text)
            ready.append(rollup.Note(p))
        h = rollup.History(self.hist)
        report = []
        rollup.apply_all(h, ready, report)
        return h, report

    def item(self, h, start):
        hits = [l for l in h.lines if l.startswith("- [") and start in l]
        self.assertEqual(len(hits), 1, hits)
        return hits[0]

    def test_done_mark_waits_for_the_note_that_adds_the_item(self):
        # The finishing note sorts first by name; a shortened name of its
        # item ("Admin panel: edit a") also starts an older done item.
        h, report = self.run_notes({
            "2026-10-10-a-finish.md": note(pr="#637", target="Admin panel: edit a type's example in the other launch languages",
                                           s5="- [x] (done 2026-10-10, PR #637: built) Admin panel: edit a type's example in the other launch languages"),
            "2026-10-10-b-adds.md": note(pr="#612", target="Owner app: on a PC's seat",
                                         s5="- Update: noted.\n- New: [ ] Admin panel: edit a type's example in the other launch languages (English and Hindi only today)."),
        })
        self.assertEqual(report, [])
        self.assertTrue(self.item(h, "edit a type's example").startswith("- [x] (done 2026-10-10, PR #637"))
        older = h.lines.index(self.item(h, "edit a profile's"))
        self.assertFalse(any("PR #637" in l for l in h.lines[older:older + 2]), "the done mark went under the older item")

    def test_a_chain_of_notes_each_waiting_on_the_one_before(self):
        # The note adding the items names its own item loosely, so it can
        # only be placed on the last try; the notes finishing its items
        # sort before it and must still come after it.
        h, report = self.run_notes({
            "2026-10-09-a-finish-one.md": note(pr="#563", target="Invited seats in the other services",
                                               s5="- [x] (done 2026-10-09, PR #563: filed under the person) Invited seats in the other services that read seat tokens"),
            "2026-10-09-b-finish-two.md": note(pr="#549", target="Seat invitations, part 2 (the apps) (from PR #541's note)",
                                               s5="- [x] (done 2026-10-09, PR #549: both apps) Seat invitations, part 2 (the apps): the codes"),
            "2026-10-09-c-adds.md": note(pr="#541", target='part of "Mobile User app, next: ... joining a router by invite"',
                                         s5="- [ ] Seat invitations, part 2 (the apps): the codes\n- [ ] Invited seats in the other services that read seat tokens (found with PR #541)"),
        })
        self.assertEqual(report, [])
        self.assertTrue(self.item(h, "Seat invitations, part 2").startswith("- [x] (done 2026-10-09, PR #549"))
        self.assertTrue(self.item(h, "Invited seats in the other").startswith("- [x] (done 2026-10-09, PR #563"))
        self.assertFalse(any("place by hand" in l for l in h.lines))

    def test_a_done_item_whose_mark_has_brackets_inside_is_found(self):
        # "(done ..., PR #4: the screen (owner and co-owners) ...)": the
        # mark ends at the bracket closing the first, not the first ")".
        h = rollup.History(self.hist)
        i = h.find_item("Owner app: device names")
        self.assertIsNotNone(i)
        self.assertIn("device names kept on the router", h.lines[i])

    def test_rows_keep_name_order_whatever_order_section_5_takes(self):
        h, _ = self.run_notes({
            "2026-10-10-a-finish.md": note(pr="#2", target="Admin panel: edit a type's example",
                                           s5="- [x] (done 2026-10-10, PR #2: built) Admin panel: edit a type's example",
                                           s3="| 2026-10-10 | First by name | 5R |"),
            "2026-10-10-b-adds.md": note(pr="#1", target="Owner app: on a PC's seat", s5="- New: [ ] Admin panel: edit a type's example",
                                         s3="| 2026-10-10 | Second by name | 5R |"),
        })
        rows = [l for l in h.lines if l.startswith("| 2026-10-10")]
        self.assertEqual(rows, ["| 2026-10-10 | First by name | 5R |", "| 2026-10-10 | Second by name | 5R |"])

    def test_words_taken_out_of_an_item(self):
        # "replace `...` with ``" and 'change "..." to ""' remove words
        # (found 2026-10-10: three notes' removals became plain updates).
        h, report = self.run_notes({
            "2026-10-10-a-trim.md": note(pr="#543", target="Mobile User app, next",
                                         s5="- replace `Home, ` with ``\n- Leaving done (PR #543)."),
            "2026-10-10-b-trim.md": note(pr="#565", target="Mobile User app, next",
                                         s5='- change " and joining a router by invite (a seat)" to ""'),
        })
        self.assertEqual(report, [])
        self.assertEqual(self.item(h, "Mobile User app, next"), "- [ ] Mobile User app, next: the share sheet")
        self.assertFalse(any("replace `" in l or 'change "' in l for l in h.lines), "an edit was copied as an update")

    # Found 2026-10-17: three of eleven notes went to "place by hand",
    # and an unknown section was flattened into one open item.
    def test_an_unknown_section_goes_under_the_item_as_written(self):
        h, report = self.run_notes({"2026-10-17-a.md": note(pr="#987", target="Owner app: on a PC's seat", s5="- Built the pin.") +
                                    "\n## Record format\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\nA second paragraph.\n"})
        self.assertEqual(report, [])
        i = h.lines.index(self.item(h, "on a PC's seat"))
        self.assertEqual(h.lines[i + 2], "  - Details 2026-10-17 (PR #987), Record format:")
        self.assertEqual(h.lines[i + 3:i + 8], ["    | a | b |", "    |---|---|", "    | 1 | 2 |", "    ", "    A second paragraph."])
        self.assertFalse(any("place by hand" in l for l in h.lines))

    # Owner, 2026-10-18: follow-ups a session leaves under "## Pending"
    # become open section 5 items.
    def test_pending_lines_become_open_items(self):
        h, report = self.run_notes({"2026-10-18-a.md": note(pr="#1058", target="Owner app: on a PC's seat",
                                                            s5="- [x] (done 2026-10-18, PR #1058: built) Owner app: on a PC's seat, pin this PC's network address") +
                                    "\n## Pending\n\n- [ ] Unit tests for the pin screen\n  on the iPad too\n- Try the pin on a real router\n"})
        self.assertEqual(report, [])
        i = h.lines.index(self.item(h, "Unit tests for the pin screen"))
        self.assertEqual(h.lines[i:i + 2], ["- [ ] Unit tests for the pin screen (pending from PR #1058, 2026-10-18)", "  on the iPad too"])
        self.assertEqual(self.item(h, "Try the pin"), "- [ ] Try the pin on a real router (pending from PR #1058, 2026-10-18)")
        self.assertFalse(any("Details" in l for l in h.lines), "the Pending section was also copied as details")

    def test_a_pending_line_already_open_is_not_added_twice(self):
        h, _ = self.run_notes({"2026-10-18-a.md": note(pr="#7", target="Owner app: on a PC's seat", s5="- Built.") +
                               "\n## Pending\n\n- Mobile User app, next: Home, the share sheet and joining a router by invite (a seat)\n"})
        i = h.lines.index(self.item(h, "Mobile User app, next"))
        self.assertEqual(h.lines[i + 1], "  - Update 2026-10-18 (PR #7): Still pending.")

    def test_an_unknown_section_with_no_item_is_kept_whole_for_a_person(self):
        h, report = self.run_notes({"2026-10-17-a.md": note(pr="#5") + "\n## Overlaps\n\n- #1 first\n- #2 second\n"})
        self.assertEqual(len(report), 1)
        at = next(i for i, l in enumerate(h.lines) if "place by hand" in l)
        self.assertEqual(h.lines[at + 1:at + 3], ["    - #1 first", "    - #2 second"])

    def test_a_paragraph_in_a_table_section_becomes_a_row(self):
        h, report = self.run_notes({"2026-10-17-a.md": note(pr="#990") +
                                    "\n## Update log (section 8)\n| 2026-10-17 | READMEs | Claude |\n\nVerified: tests pass\nin two | services.\n"})
        self.assertEqual(report, [])
        rows = [l for l in h.lines if l.startswith("| 2026-10-17")]
        self.assertEqual(rows, ["| 2026-10-17 | READMEs | Claude |", "| 2026-10-17 | Verified: tests pass in two \\| services. | Claude (PR #990) |"])

    def test_a_quoted_name_that_is_no_item_goes_under_the_item_the_note_adds(self):
        h, report = self.run_notes({"2026-10-17-a.md": note(pr="#993", s5='- [ ] Android default SMS app, in five phases\n- "Android SMS blocking": decided, see the new item.')})
        self.assertEqual(report, [])
        i = h.lines.index(self.item(h, "Android default SMS app"))
        self.assertIn("Android SMS blocking", h.lines[i + 1])
        self.assertTrue(h.lines[i + 1].startswith("  - Update 2026-10-17 (PR #993)"))

    def test_a_new_note_with_no_item_becomes_a_done_item_named_by_its_title(self):
        h, report = self.run_notes({"2026-10-17-a.md": note(pr="#1004", s5="- Cause: a race.\n- Verified: go test -race.")})
        self.assertEqual(report, [])
        i = h.lines.index(self.item(h, "A note"))
        self.assertEqual(h.lines[i:i + 3], ["- [x] (done 2026-10-17, PR #1004) A note",
                                            "  - Update 2026-10-17 (PR #1004): Cause: a race.",
                                            "  - Update 2026-10-17 (PR #1004): Verified: go test -race."])

    def test_without_mark(self):
        self.assertEqual(rollup.without_mark("(done, PR #1: a (b) c) The item"), "The item")
        self.assertEqual(rollup.without_mark("The item (with brackets)"), "The item (with brackets)")
        self.assertEqual(rollup.without_mark("(unclosed"), "(unclosed")


if __name__ == "__main__":
    unittest.main()
