import unittest

from vision_licence.core import (
    AUDIT_WARNING_NOTICE,
    calculate_daily_otp,
    calculate_factory_otp,
    verify_daily_otp,
    verify_factory_otp,
)

# Test-only secrets. The real ones live in site_config.json and on the line PC, never in the repository.
SERVICE = "test-only-secret-0123456789"
FACTORY = "test-only-factory-0123456789"
LIC = "VS-PRO-GJ04-98214"
DAY = "2026-11-03"


class DailyOtpTests(unittest.TestCase):
    def test_matches_shared_vectors(self):
        # The same vectors are in dotnet/VisionCore.Tests/OemOtpVerifierTests.cs: both sides must give these codes.
        self.assertEqual(calculate_daily_otp(LIC, DAY, SERVICE), "131100")
        self.assertEqual(calculate_daily_otp(LIC, "2026-11-04", SERVICE), "387853")
        self.assertEqual(calculate_factory_otp(LIC, DAY, SERVICE), "206240")

    def test_rotates_across_dates_and_licences(self):
        self.assertNotEqual(calculate_daily_otp(LIC, DAY, SERVICE), calculate_daily_otp(LIC, "2026-11-04", SERVICE))
        self.assertNotEqual(calculate_daily_otp(LIC, DAY, SERVICE), calculate_daily_otp("VS-PRO-TN01-54321", DAY, SERVICE))

    def test_verify(self):
        otp = calculate_daily_otp(LIC, DAY, SERVICE)
        self.assertTrue(verify_daily_otp(LIC, otp, DAY, SERVICE))
        self.assertTrue(verify_daily_otp(LIC, f" {otp} ", DAY, SERVICE))
        for bad in ("000000" if otp != "000000" else "111111", "", "invalid", otp + "0"):
            self.assertFalse(verify_daily_otp(LIC, bad, DAY, SERVICE))
        self.assertFalse(verify_daily_otp(LIC, otp, "2026-11-04", SERVICE))

    def test_fails_closed_without_a_usable_secret(self):
        for secret in (None, "", "short"):
            self.assertIsNone(calculate_daily_otp(LIC, DAY, secret))
            self.assertIsNone(calculate_factory_otp(LIC, DAY, secret))
            self.assertFalse(verify_daily_otp(LIC, "000000", DAY, secret))
        self.assertIsNone(calculate_daily_otp("", DAY, SERVICE))
        self.assertIsNone(calculate_daily_otp(LIC, "03-11-2026", SERVICE))

    def test_factory_code_is_its_own(self):
        service_otp = calculate_daily_otp(LIC, DAY, SERVICE)
        factory_otp = calculate_factory_otp(LIC, DAY, FACTORY)
        self.assertNotEqual(service_otp, factory_otp)
        self.assertTrue(verify_factory_otp(LIC, factory_otp, DAY, FACTORY))
        self.assertFalse(verify_factory_otp(LIC, service_otp, DAY, FACTORY))
        # same secret, other tier: still a different code
        self.assertNotEqual(calculate_factory_otp(LIC, DAY, SERVICE), service_otp)

    def test_audit_warning_notice_content(self):
        self.assertIn("Do not share OTP with other users", AUDIT_WARNING_NOTICE)
        self.assertIn("It will be audited in your name - Risky", AUDIT_WARNING_NOTICE)


if __name__ == "__main__":
    unittest.main()
