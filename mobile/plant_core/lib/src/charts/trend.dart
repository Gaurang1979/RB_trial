import 'package:flutter/material.dart';

import '../theme.dart';
import 'chart_math.dart';

/// A trend line with optional control limits and a centre line: the
/// line-view and SPC chart. Points outside the limits are drawn in NG
/// colour, points in a warned run (see [warnFrom]) in warning colour, the
/// others in brand colour. Missing or non-finite values leave a gap.
class PcTrend extends StatelessWidget {
  const PcTrend({super.key, required this.values, this.upper, this.lower, this.centre, this.warnFrom, this.height = 180, required this.label, this.unit = ''});

  final List<double?> values;
  /// Limits that are null or not finite are left out.
  final double? upper, lower, centre;
  /// The index from which points belong to a rule break (a rising run);
  /// null when there is none.
  final int? warnFrom;
  final double height;
  final String label, unit;

  @override
  Widget build(BuildContext context) {
    final c = context.pc;
    final up = pcFinite(upper), lo = pcFinite(lower), mid = pcFinite(centre);
    final finite = values.whereType<double>().where((v) => v.isFinite).toList();
    final out = finite.where((v) => (up != null && v > up) || (lo != null && v < lo)).length;
    final last = finite.isEmpty ? 'no data' : '${finite.last.toStringAsFixed(2)}$unit';
    return Semantics(
      label: '$label: ${finite.length} points, last $last'
          '${up != null ? ', upper limit ${up.toStringAsFixed(2)}$unit' : ''}'
          '${lo != null ? ', lower limit ${lo.toStringAsFixed(2)}$unit' : ''}'
          ', $out outside the limits${warnFrom != null ? ', a trend warning' : ''}',
      excludeSemantics: true,
      child: SizedBox(
        height: height,
        width: double.infinity,
        child: CustomPaint(painter: _TrendPainter(values, up, lo, mid, warnFrom, c)),
      ),
    );
  }
}

class _TrendPainter extends CustomPainter {
  _TrendPainter(this.values, this.upper, this.lower, this.centre, this.warnFrom, this.c);
  final List<double?> values;
  final double? upper, lower, centre;
  final int? warnFrom;
  final PcColors c;

  @override
  void paint(Canvas canvas, Size size) {
    final pts = values.map((v) => v != null && v.isFinite ? v : null).toList();
    final all = [...pts.whereType<double>(), ?upper, ?lower, ?centre];
    if (all.isEmpty || pts.length < 2) return;
    var lo = all.reduce((a, b) => a < b ? a : b), hi = all.reduce((a, b) => a > b ? a : b);
    if (hi == lo) { hi += 1; lo -= 1; }
    final pad = (hi - lo) * .1;
    lo -= pad; hi += pad;
    double y(double v) => size.height - (v - lo) / (hi - lo) * size.height;
    double x(int i) => i / (pts.length - 1) * size.width;

    final dash = Paint()..color = c.ng..strokeWidth = 1.5;
    for (final l in [upper, lower]) {
      if (l == null) continue;
      for (var px = 0.0; px < size.width; px += 10) {
        canvas.drawLine(Offset(px, y(l)), Offset((px + 6).clamp(0, size.width), y(l)), dash);
      }
    }
    if (centre != null) canvas.drawLine(Offset(0, y(centre!)), Offset(size.width, y(centre!)), Paint()..color = c.lineStrong);

    final line = Paint()..color = c.brand..strokeWidth = 2..style = PaintingStyle.stroke;
    Path? path;
    for (var i = 0; i < pts.length; i++) {
      final v = pts[i];
      if (v == null) { path = null; continue; }
      final o = Offset(x(i), y(v));
      if (path == null) {
        path = Path()..moveTo(o.dx, o.dy);
      } else {
        path.lineTo(o.dx, o.dy);
        canvas.drawPath(path, line);
        path = Path()..moveTo(o.dx, o.dy);
      }
    }
    for (var i = 0; i < pts.length; i++) {
      final v = pts[i];
      if (v == null) continue;
      final outside = (upper != null && v > upper!) || (lower != null && v < lower!);
      final color = outside ? c.ng : warnFrom != null && i >= warnFrom! ? c.warn : c.brand;
      canvas.drawCircle(Offset(x(i), y(v)), outside || (warnFrom != null && i >= warnFrom!) ? 4.5 : 3, Paint()..color = color);
    }
  }

  @override
  bool shouldRepaint(_TrendPainter o) => true;
}
