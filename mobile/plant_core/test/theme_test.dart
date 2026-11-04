import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:plant_core/plant_core.dart';

double _lum(Color c) {
  double ch(double v) => v <= .03928 ? v / 12.92 : math.pow((v + .055) / 1.055, 2.4).toDouble();
  return .2126 * ch(c.r) + .7152 * ch(c.g) + .0722 * ch(c.b);
}

double contrast(Color a, Color b) {
  final x = _lum(a), y = _lum(b);
  return (math.max(x, y) + .05) / (math.min(x, y) + .05);
}

void main() {
  for (final (name, c) in [('dark', PcColors.dark), ('light', PcColors.light)]) {
    group('$name colours', () {
      for (final (bgName, bg) in [('surface', c.surface), ('card', c.card)]) {
        test('text is at least 4.5:1 on $bgName', () {
          for (final (n, fg) in [('ink', c.ink), ('inkMuted', c.inkMuted), ('brand', c.brand), ('ok', c.ok), ('warn', c.warn), ('ng', c.ng)]) {
            expect(contrast(fg, bg), greaterThanOrEqualTo(4.5), reason: '$n on $bgName');
          }
        });
        test('strong lines and chart colours are at least 3:1 on $bgName', () {
          for (final (n, fg) in [('lineStrong', c.lineStrong), ('violet', c.violet), ('teal', c.teal)]) {
            expect(contrast(fg, bg), greaterThanOrEqualTo(3), reason: '$n on $bgName');
          }
        });
      }
      test('result colours are readable on their own backgrounds', () {
        for (final s in [PcStatus.ok, PcStatus.warn, PcStatus.ng]) {
          expect(contrast(s.color(c), s.background(c)), greaterThanOrEqualTo(4.5), reason: s.name);
        }
      });
    });
  }

  test('cards have a border that can be seen', () {
    for (final b in Brightness.values) {
      final c = b == Brightness.dark ? PcColors.dark : PcColors.light;
      final shape = pcTheme(b).cardTheme.shape! as RoundedRectangleBorder;
      expect(shape.side.color, c.lineStrong);
    }
  });

  test('every status has a shape, not only a colour', () {
    expect(PcStatus.values.map((s) => s.shape).toSet(), hasLength(PcStatus.values.length));
  });

  test('switching modes blends the colours', () {
    final mid = PcColors.dark.lerp(PcColors.light, .5);
    expect(mid.surface, isNot(PcColors.dark.surface));
    expect(mid.surface, isNot(PcColors.light.surface));
  });

  test('both themes carry the colour roles', () {
    expect(pcTheme(Brightness.dark).extension<PcColors>(), PcColors.dark);
    expect(pcTheme(Brightness.light).extension<PcColors>(), PcColors.light);
  });
}
