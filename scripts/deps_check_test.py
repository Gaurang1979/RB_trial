#!/usr/bin/env python3
"""Offline tests of scripts/deps-check.py (no registry is called)."""
import datetime as dt
import importlib.util
import unittest
from pathlib import Path

_spec = importlib.util.spec_from_file_location("deps_check", Path(__file__).with_name("deps-check.py"))
dc = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(dc)


class Gap(unittest.TestCase):
    def test_levels(self):
        self.assertEqual(dc.gap("8.0.11", "10.0.14"), "major")
        self.assertEqual(dc.gap("0.110", "0.162.0"), "minor")
        self.assertEqual(dc.gap("2.9.2", "2.9.3"), "patch")
        self.assertEqual(dc.gap("6.8.1", "6.8.1"), "current")
        self.assertEqual(dc.gap("2", "2.0.22", floating=True), "current")   # a floating tag already takes its patches
        self.assertEqual(dc.gap("16", "18.6", floating=True), "major")
        self.assertEqual(dc.gap("3.12-slim", "3.15.2-slim", floating=True), "minor")
        self.assertEqual(dc.gap("2", "2.0.22"), "patch")       # a pinned minimum is not floating
        self.assertEqual(dc.gap("1.0", None), "?")


class Support(unittest.TestCase):
    day = dt.date(2026, 10, 29)

    def note(self, kind, name, version):
        return dc.support_note({"kind": kind, "name": name, "version": version}, self.day)

    def test_dotnet_8_is_urgent(self):
        self.assertIn("support ends 2026-11-10 (12 days)", self.note("dotnet", ".NET", "8"))

    def test_past_and_far(self):
        self.assertIn("support ends 2026-10-31 (2 days)", self.note("python", "Python in CI", "3.10"))
        self.assertIn("out of support since 2026-11-10", dc.support_note({"kind": "dotnet", "name": ".NET", "version": "8"}, dt.date(2026, 12, 1)))
        self.assertEqual(self.note("docker", "postgres", "16"), "supported until 2028-11-09")
        self.assertEqual(self.note("docker", "python", "3.12-slim"), "supported until 2028-10-31")
        self.assertEqual(self.note("docker", "timescale/timescaledb", "2.31.0-pg16"), "supported until 2028-11-09")
        self.assertEqual(self.note("nuget", "xunit", "2.9.2"), "")


class Inventory(unittest.TestCase):
    def test_reads_every_kind_of_manifest(self):
        rows = dc.used()
        kinds = {r["kind"] for r in rows}
        self.assertTrue({"dotnet", "nuget", "pypi", "pub", "docker", "action", "vcpkg"} <= kinds, kinds)
        self.assertFalse(any(len(r["version"]) == 40 for r in rows if r["kind"] == "action"), "hash pins are left out")

    def test_report_survives_unreachable_registries(self):
        # A .NET 8 row of its own: the repository itself is on .NET 10 now,
        # so its manifests alone have nothing near end of support.
        real, real_used = dc._get, dc.used
        dc._get = lambda *a, **k: (_ for _ in ()).throw(OSError("offline"))
        dc.used = lambda: real_used() + [{"area": ".NET (line PC)", "kind": "dotnet", "name": ".NET runtime and SDK",
                                          "version": "8", "where": ["old.csproj"]}]
        try:
            text, rows = dc.report(dt.date(2026, 10, 29))
        finally:
            dc._get, dc.used = real, real_used
        self.assertIn("# Dependency report", text)
        self.assertTrue(all(r["gap"] == "?" for r in rows))
        self.assertIn("Act now", text)   # end of support needs no registry


if __name__ == "__main__":
    unittest.main()
