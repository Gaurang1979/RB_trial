import 'package:flutter/material.dart';

import '../theme.dart';

/// A small line without axes, for a line card (yield or rate over the last
/// hour). Gaps stay gaps.
class PcSparkline extends StatelessWidget {
  const PcSparkline({super.key, required this.values, required this.label, this.status = PcStatus.ok, this.height = 32});

  final List<double?> values;
  final String label;
  final PcStatus status;
  final double height;

  @override
  Widget build(BuildContext context) {
    final c = context.pc;
    final f = values.whereType<double>().where((v) => v.isFinite).toList();
    return Semantics(
      label: f.isEmpty ? '$label: no data' : '$label: from ${f.first.toStringAsFixed(1)} to ${f.last.toStringAsFixed(1)}',
      excludeSemantics: true,
      child: SizedBox(height: height, width: double.infinity, child: CustomPaint(painter: _SparkPainter(values, status.color(c)))),
    );
  }
}

class _SparkPainter extends CustomPainter {
  _SparkPainter(this.values, this.color);
  final List<double?> values;
  final Color color;

  @override
  void paint(Canvas canvas, Size size) {
    final f = values.whereType<double>().where((v) => v.isFinite);
    if (f.isEmpty || values.length < 2) return;
    var lo = f.reduce((a, b) => a < b ? a : b), hi = f.reduce((a, b) => a > b ? a : b);
    if (hi == lo) { hi += 1; lo -= 1; }
    final p = Paint()..color = color..strokeWidth = 2..style = PaintingStyle.stroke..strokeCap = StrokeCap.round;
    Offset? prev;
    for (var i = 0; i < values.length; i++) {
      final v = values[i];
      if (v == null || !v.isFinite) { prev = null; continue; }
      final o = Offset(i / (values.length - 1) * size.width, size.height - (v - lo) / (hi - lo) * (size.height - 4) - 2);
      if (prev != null) canvas.drawLine(prev, o, p);
      prev = o;
    }
  }

  @override
  bool shouldRepaint(_SparkPainter o) => true;
}
