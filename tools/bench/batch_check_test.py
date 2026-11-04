#!/usr/bin/env python3
"""Tests of batch_check.py's wrong-read counter, with a stand-in bench (no engine, no images).

  python3 tools/bench/batch_check_test.py

The SHA-256 of bench and the generator (sha256.hpp) is checked against FIPS 180-4 by
`engine/build/adversarial --self-test`, run here when the engine is built.
"""
import csv
import hashlib
import json
import os
import subprocess
import sys
import tempfile
import textwrap
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import batch_check as bc  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def sha(text):
    return hashlib.sha256(text.encode()).hexdigest()


# a stand-in for engine/build/bench: reads what each image "decodes" from $FAKE_READS (JSON
# file -> texts) and writes --csv and --codes the way bench does, judging texts against --texts
FAKE_BENCH = textwrap.dedent("""\
    import csv, hashlib, json, os, sys
    args = sys.argv[1:]
    folder, op = args[0], args[1]
    opt = {args[i]: args[i + 1] for i in range(2, len(args) - 1) if args[i].startswith("--")}
    reads = json.load(open(os.environ["FAKE_READS"]))
    truth = {}
    for r in csv.reader(l for l in open(opt["--texts"]) if not l.startswith("#")):
        if len(r) >= 2 and r[0] != "file":
            truth.setdefault(r[0], set()).add(r[1].lower())
    ops = list(csv.DictReader(open(op)))
    with open(opt["--csv"], "w") as out, open(opt["--codes"], "w") as codes:
        out.write("file,format,expected,decoded,candidates,misses,locate_ms,total_ms_median,total_ms_max,"
                  "label_brightness,cpu_ms_median,wrong\\n")
        codes.write("file,text,step,x,y,sha256,in_truth\\n")
        for r in ops:
            texts = reads.get(r["file"], [])
            wrong = 0
            for t in texts:
                h = hashlib.sha256(t.encode()).hexdigest()
                ok = h in truth.get(r["file"], set())
                wrong += not ok
                codes.write(f"{r['file']},{t},plain,10,20,{h},{1 if ok else 0}\\n")
            out.write(f"{r['file']},qr,{r['expected']},{len(texts)},0,0,1,50,60,200,50,{wrong}\\n")
    """)

# a stand-in for the generator: one negative image and one with a printed code
FAKE_GENERATOR = textwrap.dedent("""\
    import hashlib, os, sys
    d = sys.argv[1]
    os.makedirs(d, exist_ok=True)
    for name in ("q10-noise.png", "q00-clean-a.png"):
        open(os.path.join(d, name), "wb").close()
    with open(os.path.join(d, "operator.csv"), "w") as f:
        f.write("file,format,expected,note\\nq00-clean-a.png,qr,24,\\nq10-noise.png,qr,24,\\n")
    with open(os.path.join(d, "codes.csv"), "w") as f:
        h = hashlib.sha256(b"VSA-2026-L07-SN000101").hexdigest()
        f.write(f"file,sha256,text\\nq00-clean-a.png,{h},VSA-2026-L07-SN000101\\n")
    """)


class WrongReads(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = self.tmp.name
        self.samples = os.path.join(self.dir, "samples")
        os.makedirs(self.samples)
        for name in ("a.bmp", "b.bmp"):
            open(os.path.join(self.samples, name), "wb").close()
        self.bench = self._script("bench", FAKE_BENCH)
        self.generator = self._script("adversarial", FAKE_GENERATOR)
        self.texts = os.path.join(self.dir, "codes.csv")
        with open(self.texts, "w") as f:
            f.write("# true texts\nfile,sha256,text\n")
            f.write(f"a.bmp,{sha('SN1').upper()},\n")  # upper-case hex is the same hash
            f.write(f"a.bmp,{sha('SN2')},\n")
            f.write(f"b.bmp,{sha('SN3')},SN3\n")
        self.items = [("a.bmp", "qr", 2), ("b.bmp", "qr", 1)]

    def tearDown(self):
        self.tmp.cleanup()

    def _script(self, name, body):
        path = os.path.join(self.dir, name)
        with open(path, "w") as f:
            f.write(f"#!{sys.executable}\n{body}")
        os.chmod(path, 0o755)
        return path

    def _reads(self, reads):
        path = os.path.join(self.dir, "reads.json")
        with open(path, "w") as f:
            json.dump(reads, f)
        os.environ["FAKE_READS"] = path

    def _run(self, reads):
        self._reads(reads)
        run = tempfile.mkdtemp(dir=self.dir)
        return bc.run_batch(self.bench, run, self.samples, "24 QR", self.items, self.texts)

    def test_load_texts(self):
        t = bc.load_texts(self.texts)
        self.assertEqual(t["a.bmp"], {sha("SN1"), sha("SN2")})
        self.assertEqual(t["b.bmp"], {sha("SN3")})
        self.assertEqual(bc.load_texts(os.path.join(self.dir, "none.csv")), {})

    def test_all_true(self):
        rows = self._run({"a.bmp": ["SN1", "SN2"], "b.bmp": ["SN3"]})
        self.assertEqual(rows["a.bmp"][2], 2)
        self.assertEqual(sum(len(r[5]) for r in rows.values()), 0)
        self.assertEqual(bc.wrong_notes(rows), [])

    def test_text_not_in_truth_is_wrong(self):
        rows = self._run({"a.bmp": ["SN1", "SN9"], "b.bmp": ["SN3"]})
        self.assertEqual([w[1] for w in rows["a.bmp"][5]], ["SN9"])
        self.assertEqual(rows["a.bmp"][2], 1)  # the wrong read is no read
        notes = bc.wrong_notes(rows)
        self.assertEqual(len(notes), 1)
        self.assertIn("SN9", notes[0])

    def test_a_true_text_of_another_image_is_wrong(self):
        rows = self._run({"a.bmp": ["SN3"], "b.bmp": []})
        self.assertEqual(len(rows["a.bmp"][5]), 1)

    def test_wrong_read_at_full_count(self):
        # every code read, plus one: the read rate gains nothing back from a wrong read
        rows = self._run({"a.bmp": ["SN1", "SN2", "SN7"], "b.bmp": ["SN3"]})
        self.assertEqual(rows["a.bmp"][2], 2)
        self.assertEqual(len(rows["a.bmp"][5]), 1)

    def test_image_without_texts_has_no_true_text(self):
        self.items.append(("c.bmp", "qr", 1))
        open(os.path.join(self.samples, "c.bmp"), "wb").close()
        rows = self._run({"c.bmp": ["SN5"]})
        self.assertEqual(len(rows["c.bmp"][5]), 1)

    def test_second_run_never_forgives(self):
        first = self._run({"a.bmp": ["SN1", "SN9"], "b.bmp": ["SN3"]})
        second = self._run({"a.bmp": ["SN1", "SN2"], "b.bmp": ["SN3", "SN8"]})
        for worst in (False, True):
            merged = bc.merge_runs(first, second, worst)
            self.assertEqual({w[1] for r in merged.values() for w in r[5]}, {"SN9", "SN8"})
        self.assertEqual(bc.merge_runs(first, second, worst=False)["a.bmp"][2], 2)
        self.assertEqual(bc.merge_runs(first, second, worst=True)["a.bmp"][2], 1)
        # the same wrong text in both runs counts once
        again = bc.merge_runs(first, first, worst=False)
        self.assertEqual(len(again["a.bmp"][5]), 1)

    def test_adversarial_batch(self):
        self._reads({"q00-clean-a.png": ["VSA-2026-L07-SN000101"], "q10-noise.png": []})
        rows = bc.run_adversarial(self.generator, self.bench, tempfile.mkdtemp(dir=self.dir))
        self.assertEqual(rows["q00-clean-a.png"][:3], (bc.ADVERSARIAL, 1, 1))
        self.assertEqual(rows["q10-noise.png"][1], 0)
        self.assertEqual(bc.wrong_notes(rows), [])
        self._reads({"q00-clean-a.png": ["VSA-2026-L07-SN000160"], "q10-noise.png": ["X"]})
        rows = bc.run_adversarial(self.generator, self.bench, tempfile.mkdtemp(dir=self.dir))
        self.assertEqual(len(bc.wrong_notes(rows)), 2)

    def test_add_texts(self):
        rows = self._run({"a.bmp": ["SN1", "SN2"], "b.bmp": ["SN3", "SN4"]})
        rows["q10-noise.png"] = (bc.ADVERSARIAL, 0, 0, 1.0, 275, ((sha("X"), "X", "plain", "1", "1"),))
        self.assertEqual(bc.add_texts(self.texts, rows), {"b.bmp": 1})
        self.assertIn(sha("SN4"), bc.load_texts(self.texts)["b.bmp"])
        self.assertNotIn("q10-noise.png", bc.load_texts(self.texts))  # adversarial reads are never truth
        self.assertEqual(bc.add_texts(self.texts, rows), {})  # nothing twice
        with open(self.texts) as f:
            self.assertNotIn("SN4", f.read())  # hashes only: no customer text in the repository
        rows = self._run({"a.bmp": ["SN1", "SN2"], "b.bmp": ["SN3", "SN4"]})
        self.assertEqual(bc.wrong_notes(rows), [])

    def test_add_texts_new_file(self):
        path = os.path.join(self.dir, "new.csv")
        rows = {"a.bmp": ("24 QR", 1, 0, 1.0, 275, ((sha("SN1"), "SN1", "plain", "1", "1"),))}
        self.assertEqual(bc.add_texts(path, rows), {"a.bmp": 1})
        with open(path, newline="") as f:
            self.assertEqual(list(csv.reader(f)), [["file", "sha256", "text"], ["a.bmp", sha("SN1"), ""]])

    def test_truth_file_on_main_is_well_formed(self):
        path = os.path.join(REPO, "truth", "codes.csv")
        if not os.path.exists(path):
            self.skipTest("no truth/codes.csv")
        files = {r[0] for r in bc.load_truth(os.path.join(REPO, "truth", "samples.csv"))}
        for name, hashes in bc.load_texts(path).items():
            self.assertIn(name, files, f"{name} is not in truth/samples.csv")
            for h in hashes:
                self.assertRegex(h, r"^[0-9a-f]{64}$", name)

    def test_sha256_self_test(self):
        gen = os.path.join(REPO, "engine", "build", "adversarial")
        if not os.access(gen, os.X_OK):
            self.skipTest("engine not built")
        self.assertEqual(subprocess.run([gen, "--self-test"], capture_output=True).returncode, 0)


if __name__ == "__main__":
    unittest.main()
