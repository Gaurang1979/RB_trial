import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:plant_app/modules/vision/oem_otp_screen.dart';
import 'package:plant_core/plant_core.dart';

class _FakeSource implements OemOtpSource {
  _FakeSource(this.reply);
  final Future<OemOtpCode> Function(String licence) reply;
  final asked = <String>[];

  @override
  Future<OemOtpCode> fetch(String licenceKey, {bool factory = false}) {
    asked.add(licenceKey);
    return reply(licenceKey);
  }
}

void main() {
  testWidgets('without a licence-server source the screen shows no code', (tester) async {
    await tester.pumpWidget(MaterialApp(
      home: OemOtpScreen(feedback: PcFeedback(PcLook()), initialLicence: 'VS-PRO-GJ04-98214'),
    ));
    expect(find.text('OEM Service OTP (REQ-14)'), findsOneWidget);
    expect(find.textContaining('Not connected to the licence server'), findsOneWidget);
    expect(find.text('Copy 6-Digit OTP'), findsNothing);
  });

  testWidgets('shows the code the licence server issues, with the audit warning', (tester) async {
    final source = _FakeSource((_) async => const OemOtpCode(otp: '482913', date: '2026-11-03'));
    await tester.pumpWidget(MaterialApp(
      home: OemOtpScreen(feedback: PcFeedback(PcLook()), source: source, initialLicence: 'VS-PRO-GJ04-98214'),
    ));
    await tester.tap(find.text('Get today\'s OTP'));
    await tester.pumpAndSettle();
    expect(source.asked, ['VS-PRO-GJ04-98214']);
    expect(find.text('482913'), findsOneWidget);
    expect(find.text('Copy 6-Digit OTP'), findsOneWidget);
    expect(find.text(OemOtp.auditWarningNotice), findsOneWidget);
  });

  testWidgets('a refusal shows no code', (tester) async {
    final source = _FakeSource((_) async => throw Exception('no role'));
    await tester.pumpWidget(MaterialApp(
      home: OemOtpScreen(feedback: PcFeedback(PcLook()), source: source, initialLicence: 'VS-PRO-GJ04-98214'),
    ));
    await tester.tap(find.text('Get today\'s OTP'));
    await tester.pumpAndSettle();
    expect(find.textContaining('did not issue a code'), findsOneWidget);
    expect(find.text('Copy 6-Digit OTP'), findsNothing);
  });
}
