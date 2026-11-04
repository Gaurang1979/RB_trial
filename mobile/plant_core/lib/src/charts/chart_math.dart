import 'dart:math' as math;

/// A value a chart can draw as a fraction: not finite or missing is null
/// (drawn as a gap), otherwise clamped to 0..1.
double? pcFraction(num? v, num max) {
  if (v == null || max <= 0) return null;
  final d = v.toDouble() / max;
  return d.isFinite ? d.clamp(0.0, 1.0) : null;
}

/// A grid step of 1, 2 or 5 times a power of ten, at least [raw].
double pcNiceStep(double raw) {
  if (!raw.isFinite || raw <= 0) return 1;
  final mag = math.pow(10, (math.log(raw) / math.ln10).floor()).toDouble();
  for (final m in const [1.0, 2.0, 5.0, 10.0]) {
    if (m * mag >= raw) return m * mag;
  }
  return 10 * mag;
}

/// [v] when it is a real number; null for missing, NaN and infinity, so
/// a bad value from a server shows as "no data" and never as "NaN".
double? pcFinite(double? v) => v != null && v.isFinite ? v : null;

/// A percentage for a screen reader and a label: "99.2%", or "no data"
/// for a missing or non-finite value.
String pcPercent(double? fraction, {int decimals = 1}) {
  final f = pcFinite(fraction);
  return f == null ? 'no data' : '${(f * 100).toStringAsFixed(decimals)}%';
}
