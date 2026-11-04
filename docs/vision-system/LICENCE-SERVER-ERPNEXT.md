# Licence server on ERPNext: periodic verification

Implements section 8 of the [licensing design](LICENSING.md) on the company ERPNext. Vision Studio checks its licence and the serial numbers of the installation's parts with your ERPNext
server, at start and then periodically. The ERPNext side is the Frappe app
[`erpnext/vision_licence`](../../erpnext/vision_licence); the Vision Studio side is
`dotnet/VisionCore/Licensing` and the **Licence** page.

```
Vision Studio (customer PC)                          ERPNext (licence.<your domain>)
  licence key, installation id, software version,  →  POST /api/method/vision_licence.api.verify
  part serials (cameras found + typed), nonce            Vision Licence record + Serial No records
  verify signature + nonce, store answer           ←  answer signed with the licence private key
```

## What is checked

| Check | Where | Result when it fails |
| --- | --- | --- |
| Licence key exists, status Active, not past Valid Until | Vision Licence | Production stops; message on the Run and Licence pages |
| Same PC as the first activation (installation id) | Vision Licence → Installation | "In use on another PC": clear the Installation field in ERPNext to move it |
| Each part: serial known in ERPNext, delivered, delivered to this customer, registered on the licence | Serial No + licence Parts table | The part is listed as **not licensed** with the reason; production does not start while any part is unlicensed |
| Licensed tools (barcode, ocr, locate) and number of cameras | Vision Licence | A job with an unlicensed tool or too many cameras does not start |
| Answer signed by your key and made for this request (nonce) | Vision Studio | Answer ignored |

Cameras are found automatically (serial read from the camera). Serials the software cannot read
(line controller, printer, industrial PC) are typed once on the Licence page from the nameplate and
are checked the same way.

**Offline.** After a good check the licence keeps working until the next check is due (Check
Interval, default 24 h). If the server cannot be reached it keeps working for the **Offline Grace**
days (default 7) after the last good check, retrying every hour, then production stops until a check
succeeds. Replaying stored pictures (job set-up and tests) works without a licence.

## Set up the licence server (once)

1. **Subdomain.** Point `licence.<your domain>` (DNS A record) to the ERPNext server and add it to the
   site with HTTPS:
   ```bash
   bench setup add-domain licence.example.com --site erp.example.com
   bench setup nginx && sudo service nginx reload
   sudo bench setup lets-encrypt erp.example.com --custom-domain licence.example.com
   ```
   Optional hardening: expose only the licence method on that name, e.g. in its nginx `server` block
   `location = /api/method/vision_licence.api.verify { proxy_pass http://frappe-bench-frappe; proxy_set_header Host erp.example.com; }`
   and `location / { return 404; }`.
2. **App.**
   ```bash
   bench get-app /path/to/Vision_System/erpnext/vision_licence
   bench --site erp.example.com install-app vision_licence
   ```
3. **Signing keys** (on a trusted PC, once): `tools/licence/make_keys.sh`.
   - `licence-private.pem`: `bench --site erp.example.com set-config vision_licence_private_key "$(cat licence-private.pem)"`,
     then keep the file offline in a safe place. Never put it in the repository or on a customer PC.
   - `licence-public.pem`: copy to `dotnet/VisionStudio/licence-public.pem` and commit; it ships with the
     software. A new key pair needs a new software release.

## Day to day in ERPNext

1. **Parts** are Items with your own codes and names; every unit has a **Serial No** (made on the
   Purchase Receipt or Stock Entry).
2. **Deliver** the parts to the customer with a Delivery Note: the Serial No becomes *Delivered* to that
   customer.
3. **Vision Licence** (new record): licence key, customer, valid until (empty = no end), licensed tools,
   max cameras, check interval, offline grace, and the **Parts** of this installation (kind + Serial No).
   Tick *Accept any serial delivered to this customer* to skip listing each part.
4. Give the customer the licence key and the server address; on the Licence page they enter both and
   press **Save and check**. The first check binds the licence to that PC (Installation field).
5. **Vision Licence Check** lists every check: licence, PC, software version, IP address, result and
   the **unlicensed parts** found at that site.

To suspend a licence set Status to Suspended: the PC stops at its next check (at most Check Interval
plus Offline Grace later when it is offline).

## Tests

- ERPNext rules: `cd erpnext/vision_licence && python -m unittest discover -s tests` (needs `cryptography`).
- Vision Studio: `dotnet test dotnet/VisionCore.Tests` (`LicenceTests`: an answer signed by the ERPNext
  code verifies; an edited answer or another key is rejected; offline grace and expiry).
