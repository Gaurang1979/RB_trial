import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../theme.dart';
import 'chart_math.dart';

/// OEE as a half-circle gauge, with its three parts (availability,
/// performance, quality) as thin arcs inside, each with its own value.
class PcOeeGauge extends StatelessWidget {
  const PcOeeGauge({super.key, required this.availability, required this.performance, required this.quality, this.target = .8, this.width = 220});

  final double? availability, performance, quality;
  final double target, width;

  /// Availability x performance x quality; null when any part is missing or not finite.
  double? get oee {
    final a = pcFinite(availability), p = pcFinite(performance), q = pcFinite(quality);
    return a == null || p == null || q == null ? null : pcFinite(a * p * q);
  }

  @override
  Widget build(BuildContext context) {
    final c = context.pc;
    final o = oee;
    final a = pcFinite(availability), p = pcFinite(performance), q = pcFinite(quality);
    final status = o == null ? PcStatus.idle : o >= target ? PcStatus.ok : o >= target * .85 ? PcStatus.warn : PcStatus.ng;
    return Semantics(
      label: 'OEE ${pcPercent(o, decimals: 0)}: availability ${pcPercent(a, decimals: 0)}, '
          'performance ${pcPercent(p, decimals: 0)}, quality ${pcPercent(q, decimals: 0)}; target ${pcPercent(target, decimals: 0)}',
      excludeSemantics: true,
      child: SizedBox(
        width: width,
        height: width * .62,
        child: CustomPaint(
          painter: _GaugePainter([o, a, p, q], [status.color(c), c.brand, c.violet, c.teal], c.sunken, target, c.inkMuted),
          // Fitted into the space under the arcs, so large text shrinks instead of overflowing.
          child: Align(
            alignment: Alignment.bottomCenter,
            child: SizedBox(
              width: width * .5,
              height: width * .26,
              child: FittedBox(
                alignment: Alignment.bottomCenter,
                child: Column(mainAxisSize: MainAxisSize.min, children: [
                  Text(o == null ? '–' : '${(o * 100).round()}%', style: TextStyle(fontSize: width * .15, fontWeight: FontWeight.w700, color: c.ink)),
                  Text('OEE', style: TextStyle(fontSize: math.max(12, width * .06), color: c.inkMuted)),
                ]),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _GaugePainter extends CustomPainter {
  _GaugePainter(this.values, this.colors, this.track, this.target, this.tick);
  final List<double?> values;
  final List<Color> colors;
  final Color track, tick;
  final double target;

  @override
  void paint(Canvas canvas, Size size) {
    final r0 = size.width / 2;
    final centre = Offset(size.width / 2, r0);
    for (var i = 0; i < values.length; i++) {
      final w = i == 0 ? r0 * .14 : r0 * .05;
      final r = i == 0 ? r0 - w / 2 : r0 * .78 - (i - 1) * r0 * .09;
      final rect = Rect.fromCircle(center: centre, radius: r);
      final p = Paint()..style = PaintingStyle.stroke..strokeWidth = w..strokeCap = StrokeCap.butt;
      canvas.drawArc(rect, math.pi, math.pi, false, p..color = track);
      final f = pcFraction(values[i], 1);
      if (f != null && f > 0) canvas.drawArc(rect, math.pi, math.pi * f, false, p..color = colors[i]);
    }
    // The target as a tick across the outer arc.
    final a = math.pi + math.pi * target.clamp(0, 1);
    final dir = Offset(math.cos(a), math.sin(a));
    canvas.drawLine(centre + dir * r0 * .82, centre + dir * r0, Paint()..color = tick..strokeWidth = 2);
  }

  @override
  bool shouldRepaint(_GaugePainter o) => true;
}
