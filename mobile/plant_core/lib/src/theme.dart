import 'package:flutter/material.dart';

/// The plant app's colours, in a dark version (the default: the floor
/// look of the Plant Insight design) and a light one, with the same roles
/// in both (docs/vision-system/PLANT-DASHBOARD-AND-MOBILE.md 6.2). The
/// roles follow secure_me's console so both products look like one
/// family. Every text colour is at least 4.5:1 and every border that must
/// be seen ([lineStrong]) is at least 3:1 on [card] and [surface] in both
/// versions (test/theme_test.dart); [line] is only for dividers.
class PcColors extends ThemeExtension<PcColors> {
  const PcColors({
    required this.surface, required this.card, required this.sunken, required this.ink, required this.inkMuted,
    required this.line, required this.lineStrong, required this.brand, required this.onBrand,
    required this.ok, required this.okBg, required this.warn, required this.warnBg, required this.ng, required this.ngBg,
    required this.violet, required this.teal,
  });

  /// [surface] is the page, [card] a panel, [sunken] a field or sheet.
  /// [ok], [warn] and [ng] are the result colours (always shown with a
  /// shape too: ✓ ! ✗); [violet] and [teal] are extra chart series.
  final Color surface, card, sunken, ink, inkMuted, line, lineStrong, brand, onBrand,
      ok, okBg, warn, warnBg, ng, ngBg, violet, teal;

  static const dark = PcColors(
    surface: Color(0xFF0B1117), card: Color(0xFF131C25), sunken: Color(0xFF1A2633),
    ink: Color(0xFFE6EDF3), inkMuted: Color(0xFF9FB0C0), line: Color(0xFF22303D), lineStrong: Color(0xFF5A7088),
    brand: Color(0xFF7CB7FF), onBrand: Color(0xFF06111F),
    ok: Color(0xFF3FB950), okBg: Color(0xFF12301B), warn: Color(0xFFE3B341), warnBg: Color(0xFF2A2412),
    ng: Color(0xFFFF7B72), ngBg: Color(0xFF3A1D1B), violet: Color(0xFFB39DFA), teal: Color(0xFF2BD0F0),
  );

  static const light = PcColors(
    surface: Color(0xFFEEF2F7), card: Color(0xFFFFFFFF), sunken: Color(0xFFE6ECF4),
    ink: Color(0xFF0E1A2B), inkMuted: Color(0xFF4F6178), line: Color(0xFFD5DEE9), lineStrong: Color(0xFF6B7E98),
    brand: Color(0xFF1B4F9C), onBrand: Color(0xFFFFFFFF),
    ok: Color(0xFF0B7A3B), okBg: Color(0xFFDDF3E4), warn: Color(0xFF8A5300), warnBg: Color(0xFFFCEFD3),
    ng: Color(0xFFC8283A), ngBg: Color(0xFFFBE3E5), violet: Color(0xFF6D4FD8), teal: Color(0xFF0B7A99),
  );

  @override
  PcColors copyWith() => this;

  // A real blend, so switching dark and light fades instead of jumping.
  @override
  PcColors lerp(ThemeExtension<PcColors>? other, double t) {
    if (other is! PcColors) return this;
    Color l(Color a, Color b) => Color.lerp(a, b, t)!;
    return PcColors(
      surface: l(surface, other.surface), card: l(card, other.card), sunken: l(sunken, other.sunken),
      ink: l(ink, other.ink), inkMuted: l(inkMuted, other.inkMuted), line: l(line, other.line),
      lineStrong: l(lineStrong, other.lineStrong), brand: l(brand, other.brand), onBrand: l(onBrand, other.onBrand),
      ok: l(ok, other.ok), okBg: l(okBg, other.okBg), warn: l(warn, other.warn), warnBg: l(warnBg, other.warnBg),
      ng: l(ng, other.ng), ngBg: l(ngBg, other.ngBg), violet: l(violet, other.violet), teal: l(teal, other.teal),
    );
  }
}

extension PcColorsOf on BuildContext {
  PcColors get pc => Theme.of(this).extension<PcColors>()!;
}

/// The result of a part, a line or a check, with its colour and shape.
enum PcStatus { ok, warn, ng, idle }

extension PcStatusLook on PcStatus {
  String get shape => switch (this) { PcStatus.ok => '✓', PcStatus.warn => '!', PcStatus.ng => '✗', PcStatus.idle => '–' };
  Color color(PcColors c) => switch (this) { PcStatus.ok => c.ok, PcStatus.warn => c.warn, PcStatus.ng => c.ng, PcStatus.idle => c.inkMuted };
  Color background(PcColors c) => switch (this) { PcStatus.ok => c.okBg, PcStatus.warn => c.warnBg, PcStatus.ng => c.ngBg, PcStatus.idle => c.sunken };
}

/// The app's theme for one brightness: Material 3 on the [PcColors]
/// roles, buttons at least 48 tall, panels with a visible border.
ThemeData pcTheme(Brightness b) {
  final c = b == Brightness.dark ? PcColors.dark : PcColors.light;
  final scheme = ColorScheme.fromSeed(seedColor: c.brand, brightness: b).copyWith(
    primary: c.brand, onPrimary: c.onBrand, surface: c.card, onSurface: c.ink, error: c.ng,
    outline: c.lineStrong, outlineVariant: c.line,
  );
  return ThemeData(
    useMaterial3: true,
    brightness: b,
    colorScheme: scheme,
    scaffoldBackgroundColor: c.surface,
    extensions: [c],
    appBarTheme: AppBarTheme(backgroundColor: c.surface, foregroundColor: c.ink, elevation: 0, scrolledUnderElevation: 0),
    cardTheme: CardThemeData(
      color: c.card, elevation: 0, margin: EdgeInsets.zero,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14), side: BorderSide(color: c.lineStrong)),
    ),
    filledButtonTheme: FilledButtonThemeData(style: FilledButton.styleFrom(minimumSize: const Size(48, 52))),
    outlinedButtonTheme: OutlinedButtonThemeData(style: OutlinedButton.styleFrom(minimumSize: const Size(48, 52))),
    navigationBarTheme: NavigationBarThemeData(backgroundColor: c.card, indicatorColor: c.sunken),
    dividerTheme: DividerThemeData(color: c.line, space: 1),
  );
}
