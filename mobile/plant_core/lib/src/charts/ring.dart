import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../theme.dart';
import 'chart_math.dart';

/// A ring showing one share, such as a line's yield, with the value in the
/// middle. The ring's colour is the [status]; a missing or non-finite
/// value draws an empty ring and "–".
class PcRing extends StatelessWidget {
  const PcRing({super.key, required this.fraction, required this.label, this.status = PcStatus.ok, this.size = 96, this.decimals = 1});

  final double? fraction;
  final String label;
  final PcStatus status;
  final double size;
  final int decimals;

  @override
  Widget build(BuildContext context) {
    final c = context.pc;
    final f = pcFinite(fraction);
    final value = f == null ? '–' : '${(f * 100).toStringAsFixed(decimals)}%';
    return Semantics(
      label: '$label ${pcPercent(f, decimals: decimals)}',
      excludeSemantics: true,
      child: SizedBox.square(
        dimension: size,
        child: CustomPaint(
          painter: _RingPainter(f, status.color(c), c.sunken),
          // Fitted, so large text or a long label shrinks instead of overflowing the ring.
          child: Padding(
            padding: EdgeInsets.all(size * .16),
            child: Center(
              child: FittedBox(
                child: Column(mainAxisSize: MainAxisSize.min, children: [
                  Text(value, style: TextStyle(fontSize: size * .2, fontWeight: FontWeight.w700, color: c.ink, fontFeatures: const [FontFeature.tabularFigures()])),
                  Text(label, style: TextStyle(fontSize: math.max(12, size * .11), color: c.inkMuted)),
                ]),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _RingPainter extends CustomPainter {
  _RingPainter(this.fraction, this.color, this.track);
  final double? fraction;
  final Color color, track;

  @override
  void paint(Canvas canvas, Size size) {
    final w = size.shortestSide * .1;
    final rect = Offset(w / 2, w / 2) & Size(size.width - w, size.height - w);
    final p = Paint()..style = PaintingStyle.stroke..strokeWidth = w..strokeCap = StrokeCap.round;
    canvas.drawArc(rect, 0, math.pi * 2, false, p..color = track);
    final f = pcFraction(fraction, 1);
    if (f != null && f > 0) canvas.drawArc(rect, -math.pi / 2, math.pi * 2 * f, false, p..color = color);
  }

  @override
  bool shouldRepaint(_RingPainter o) => o.fraction != fraction || o.color != color || o.track != track;
}
