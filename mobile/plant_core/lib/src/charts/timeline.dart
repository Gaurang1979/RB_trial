import 'package:flutter/material.dart';

import '../theme.dart';

/// One band of a [PcTimeline]: from [start] until the next band (or now).
class PcBand {
  const PcBand(this.start, this.status, this.label);
  final DateTime start;
  final PcStatus status;
  final String label;
}

/// The line's state over a shift as coloured bands (running, stopped,
/// changeover …). Bands are drawn by time; a screen reader hears each
/// band with its start time and length.
class PcTimeline extends StatelessWidget {
  const PcTimeline({super.key, required this.from, required this.to, required this.bands, this.height = 28});

  final DateTime from, to;
  final List<PcBand> bands;
  final double height;

  @override
  Widget build(BuildContext context) {
    final c = context.pc;
    final sorted = [...bands]..sort((a, b) => a.start.compareTo(b.start));
    String two(int n) => n.toString().padLeft(2, '0');
    final spoken = <String>[];
    for (var i = 0; i < sorted.length; i++) {
      final end = i + 1 < sorted.length ? sorted[i + 1].start : to;
      spoken.add('${sorted[i].label} from ${two(sorted[i].start.hour)}:${two(sorted[i].start.minute)} for ${end.difference(sorted[i].start).inMinutes} min');
    }
    return Semantics(
      label: spoken.isEmpty ? 'No state this shift' : spoken.join('; '),
      excludeSemantics: true,
      child: SizedBox(height: height, width: double.infinity, child: CustomPaint(painter: _BandsPainter(from, to, sorted, c))),
    );
  }
}

class _BandsPainter extends CustomPainter {
  _BandsPainter(this.from, this.to, this.bands, this.c);
  final DateTime from, to;
  final List<PcBand> bands;
  final PcColors c;

  @override
  void paint(Canvas canvas, Size size) {
    canvas.drawRRect(RRect.fromRectAndRadius(Offset.zero & size, const Radius.circular(6)), Paint()..color = c.sunken);
    final span = to.difference(from).inMilliseconds;
    if (span <= 0) return;
    double x(DateTime t) => (t.difference(from).inMilliseconds / span).clamp(0.0, 1.0) * size.width;
    for (var i = 0; i < bands.length; i++) {
      final end = i + 1 < bands.length ? bands[i + 1].start : to;
      final l = x(bands[i].start), r = x(end);
      if (r <= l) continue;
      canvas.drawRect(Rect.fromLTRB(l, 0, r, size.height), Paint()..color = bands[i].status.color(c));
    }
  }

  @override
  bool shouldRepaint(_BandsPainter o) => true;
}
