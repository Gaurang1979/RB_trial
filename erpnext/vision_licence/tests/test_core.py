"""Licence rules without Frappe: python -m unittest discover -s tests (needs the cryptography package)."""
import base64
import json
import sys
import unittest
from datetime import date, datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from vision_licence.core import LicenceRecord, SerialInfo, sign, verify  # noqa: E402

ERP = {
    "CAM-0001": SerialInfo("CAM-0001", "VS-CAM-5M", "Vision camera 5 MP", "Delivered", "ACME Foods"),
    "CAM-0002": SerialInfo("CAM-0002", "VS-CAM-5M", "Vision camera 5 MP", "Delivered", "Other Co"),
    "CAM-0003": SerialInfo("CAM-0003", "VS-CAM-5M", "Vision camera 5 MP", "Active", None),
    "PLC-0101": SerialInfo("PLC-0101", "VS-LC-16", "Line controller 16 I/O", "Delivered", "ACME Foods"),
}
NOW = datetime(2026, 10, 29, 12, 0, tzinfo=timezone.utc)


def lic(**kw):
    base = dict(licence_key="L-1", customer="ACME Foods", tools=["barcode", "ocr"], max_cameras=2,
                devices=[("camera", "CAM-0001"), ("plc", "PLC-0101")])
    base.update(kw)
    return LicenceRecord(**base)


class CoreTests(unittest.TestCase):
    def test_active_licence_binds_installation_and_checks_parts(self):
        devices = [{"kind": "camera", "serial": "CAM-0001"}, {"kind": "camera", "serial": "MARKET-99"},
                   {"kind": "camera", "serial": "CAM-0002"}, {"kind": "camera", "serial": "CAM-0003"},
                   {"kind": "plc", "serial": "PLC-0101"}]
        p, bind = verify(lic(), "L-1", "VS-AAA", "n1", devices, ERP.get, NOW)
        self.assertEqual("active", p["status"])
        self.assertEqual("VS-AAA", bind)
        v = {d["serial"]: d for d in p["devices"]}
        self.assertTrue(v["CAM-0001"]["ok"])
        self.assertTrue(v["PLC-0101"]["ok"])
        self.assertIn("not registered with the supplier", v["MARKET-99"]["reason"])
        self.assertIn("another customer", v["CAM-0002"]["reason"])
        self.assertIn("not delivered", v["CAM-0003"]["reason"])

    def test_other_pc_expired_suspended_unknown(self):
        self.assertEqual("installation_mismatch", verify(lic(installation="VS-OTHER"), "L-1", "VS-AAA", "n", [], ERP.get, NOW)[0]["status"])
        self.assertEqual("expired", verify(lic(valid_until=date(2026, 10, 1)), "L-1", "VS-AAA", "n", [], ERP.get, NOW)[0]["status"])
        self.assertEqual("suspended", verify(lic(status="Suspended"), "L-1", "VS-AAA", "n", [], ERP.get, NOW)[0]["status"])
        self.assertEqual("unknown", verify(None, "L-X", "VS-AAA", "n", [], ERP.get, NOW)[0]["status"])

    def test_customer_serials_accepted_when_allowed(self):
        p, _ = verify(lic(devices=[], allow_customer_serials=True), "L-1", "VS-AAA", "n",
                      [{"kind": "camera", "serial": "CAM-0001"}], ERP.get, NOW)
        self.assertTrue(p["devices"][0]["ok"])

    def test_signature_verifies_with_the_public_key(self):
        from cryptography.hazmat.primitives import hashes, serialization
        from cryptography.hazmat.primitives.asymmetric import ec
        key = ec.generate_private_key(ec.SECP256R1())
        pem = key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()).decode()
        p, _ = verify(lic(), "L-1", "VS-AAA", "n", [], ERP.get, NOW)
        s = sign(p, pem)
        data, sig = base64.b64decode(s["payload"]), base64.b64decode(s["signature"])
        key.public_key().verify(sig, data, ec.ECDSA(hashes.SHA256()))   # raises when wrong
        self.assertEqual("L-1", json.loads(data)["licence"])


if __name__ == "__main__":
    unittest.main()
