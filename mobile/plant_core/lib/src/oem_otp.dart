/// Daily rolling OTP for the Service (Tier 3) and OEM Factory (Tier 4) logins (Req 17.10 REQ-14, REQ-21 /
/// PROT-29, PROT-36). The app holds no secret and makes no code: it asks the licence server (ERPNext,
/// `vision_licence.api.get_oem_daily_otp` / `get_factory_setting_otp`), which issues codes to signed-in
/// staff with the right role and logs who got which.
library;

/// One code the licence server issued.
class OemOtpCode {
  const OemOtpCode({required this.otp, required this.date, this.warningNotice = OemOtp.auditWarningNotice});

  final String otp;

  /// The UTC day (YYYY-MM-DD) the code is valid for.
  final String date;
  final String warningNotice;
}

/// Where the app gets codes from. The ERPNext source needs the app's ERPNext sign-in, which is not built yet.
abstract class OemOtpSource {
  /// Today's code for [licenceKey]; throws when the server refuses (no role, no secret set up, offline).
  Future<OemOtpCode> fetch(String licenceKey, {bool factory = false});
}

class OemOtp {
  static const String auditWarningNotice = 'Do not share OTP with other users as change log is generated with values, It will be audited in your name - Risky.';

  /// Returns current UTC date formatted as YYYY-MM-DD.
  static String todayUtc() {
    final now = DateTime.now().toUtc();
    final y = now.year.toString().padLeft(4, '0');
    final m = now.month.toString().padLeft(2, '0');
    final d = now.day.toString().padLeft(2, '0');
    return '$y-$m-$d';
  }
}
