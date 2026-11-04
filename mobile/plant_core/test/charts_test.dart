import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:plant_core/plant_core.dart';

Widget _host(Widget child, {Brightness b = Brightness.dark}) =>
    MaterialApp(theme: pcTheme(b), home: Scaffold(body: SingleChildScrollView(child: Padding(padding: const EdgeInsets.all(16), child: child))));

void main() {
  for (final b in Brightness.values) {
    testWidgets('every chart draws in ${b.name} mode, with gaps and odd values', (t) async {
      await t.pumpWidget(_host(Column(children: [
        const PcRing(fraction: .992, label: 'Yield'),
        const PcRing(fraction: null, label: 'Yield'),
        const PcOeeGauge(availability: .88, performance: .9, quality: .99),
        const PcTrend(values: [0.1, null, 0.2, double.nan, 0.6, 0.3], upper: .5, lower: -.5, centre: 0, warnFrom: 4, label: 'Offset', unit: ' mm'),
        const PcBars(bars: [PcBar('Too large', 162), PcBar('Date unreadable', 104), PcBar('None', 0)]),
        PcTimeline(from: DateTime(2026, 10, 29, 14), to: DateTime(2026, 10, 29, 22), bands: [
          PcBand(DateTime(2026, 10, 29, 14), PcStatus.ok, 'Running'),
          PcBand(DateTime(2026, 10, 29, 16), PcStatus.ng, 'Stopped'),
          PcBand(DateTime(2026, 10, 29, 16, 20), PcStatus.ok, 'Running'),
        ]),
        const PcSparkline(values: [1, 2, null, 4, double.infinity, 3], label: 'Rate'),
      ]), b: b));
      expect(t.takeException(), isNull);
    });
  }

  testWidgets('charts speak their values', (t) async {
    final h = t.ensureSemantics();
    await t.pumpWidget(_host(Column(children: [
      const PcRing(fraction: .992, label: 'Yield'),
      const PcOeeGauge(availability: .9, performance: .9, quality: 1),
      const PcTrend(values: [0.1, 0.7], upper: .5, label: 'Offset', unit: ' mm'),
      PcTimeline(from: DateTime(2026, 1, 1, 14), to: DateTime(2026, 1, 1, 15), bands: [PcBand(DateTime(2026, 1, 1, 14), PcStatus.ok, 'Running')]),
    ])));
    expect(find.bySemanticsLabel('Yield 99.2%'), findsOneWidget);
    expect(find.bySemanticsLabel(RegExp(r'^OEE 81%: availability 90%')), findsOneWidget);
    expect(find.bySemanticsLabel(RegExp(r'1 outside the limits')), findsOneWidget);
    expect(find.bySemanticsLabel('Running from 14:00 for 60 min'), findsOneWidget);
    h.dispose();
  });

  testWidgets('a missing value shows a dash, never 0%', (t) async {
    await t.pumpWidget(_host(const PcRing(fraction: null, label: 'Yield')));
    expect(find.text('–'), findsOneWidget);
    expect(find.text('0.0%'), findsNothing);
  });

  testWidgets('bars list the largest first and leave zero out', (t) async {
    await t.pumpWidget(_host(const PcBars(bars: [PcBar('Small', 2), PcBar('Big', 9), PcBar('None', 0)])));
    expect(t.getTopLeft(find.text('Big')).dy, lessThan(t.getTopLeft(find.text('Small')).dy));
    expect(find.text('None'), findsNothing);
  });

  testWidgets('not-a-number and infinity are missing data: a dash and "no data", never NaN', (t) async {
    final h = t.ensureSemantics();
    await t.pumpWidget(_host(Column(children: [
      const PcOeeGauge(availability: double.nan, performance: .9, quality: 1),
      const PcOeeGauge(availability: .9, performance: double.infinity, quality: 1),
      const PcRing(fraction: double.nan, label: 'Nan ring'),
      const PcRing(fraction: double.infinity, label: 'Inf ring'),
      const PcTrend(values: [0.1, 0.2, 0.3], upper: double.nan, lower: double.negativeInfinity, centre: double.nan, label: 'Offset', unit: ' mm'),
    ])));
    expect(t.takeException(), isNull);
    expect(find.text('–'), findsNWidgets(4)); // two gauges and two rings
    expect(find.textContaining('NaN'), findsNothing);
    expect(find.bySemanticsLabel(RegExp(r'^OEE no data: availability no data, performance 90%')), findsOneWidget);
    expect(find.bySemanticsLabel(RegExp(r'^OEE no data: availability 90%, performance no data')), findsOneWidget);
    expect(find.bySemanticsLabel('Nan ring no data'), findsOneWidget);
    expect(find.bySemanticsLabel('Inf ring no data'), findsOneWidget);
    expect(find.bySemanticsLabel(RegExp(r'^Offset: 3 points, last 0.30 mm, 0 outside the limits$')), findsOneWidget);
    h.dispose();
    expect(pcPercent(double.nan), 'no data');
    expect(pcPercent(double.infinity), 'no data');
  });

  testWidgets('large text shrinks inside the ring and the gauge instead of overflowing', (t) async {
    for (final scale in [1.0, 2.0, 3.0]) {
      await t.pumpWidget(MaterialApp(
        theme: pcTheme(Brightness.dark),
        builder: (context, child) => MediaQuery(data: MediaQuery.of(context).copyWith(textScaler: TextScaler.linear(scale)), child: child!),
        home: const Scaffold(
          body: SingleChildScrollView(
            child: Column(children: [
              PcRing(fraction: .992, label: 'Yield', size: 72),
              PcRing(fraction: .5, label: 'A long label', size: 48),
              PcOeeGauge(availability: .88, performance: .9, quality: .99),
              PcOeeGauge(availability: 1, performance: 1, quality: 1, width: 140),
            ]),
          ),
        ),
      ));
      expect(t.takeException(), isNull, reason: 'text scale $scale');
    }
    final label = t.widget<Text>(find.text('Yield'));
    expect(label.style!.fontSize, greaterThanOrEqualTo(12));
  });

  test('nice steps', () {
    expect([pcNiceStep(3), pcNiceStep(7), pcNiceStep(0.03), pcNiceStep(double.nan)], [5, 10, 0.05, 1]);
  });
}
