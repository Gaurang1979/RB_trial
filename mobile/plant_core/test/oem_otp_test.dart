import 'package:flutter_test/flutter_test.dart';
import 'package:plant_core/plant_core.dart';

void main() {
  group('OEM Daily Rolling OTP', () {
    test('today is a UTC YYYY-MM-DD date', () {
      expect(OemOtp.todayUtc(), matches(RegExp(r'^\d{4}-\d{2}-\d{2}$')));
    });

    test('a code carries the mandatory audit warning by default', () {
      const code = OemOtpCode(otp: '123456', date: '2026-11-03');
      expect(code.warningNotice, contains('Do not share OTP with other users'));
      expect(code.warningNotice, contains('It will be audited in your name - Risky'));
    });
  });
}
