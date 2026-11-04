import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:plant_core/plant_core.dart';

import 'grade_api.dart';
import 'line_look.dart';
import 'vision_api.dart';

/// The print-quality trend: for each line, station and tool the mean grade
/// per time bucket and the worst grade of the bucket, against the lowest
/// grade, with the drift status. Parts that could not be graded are shown,
/// never left out (they count as F). Read only. [plant] and [line] limit it
/// to one line.
class GradeScreen extends StatefulWidget {
  const GradeScreen({
    super.key,
    required this.api,
    required this.feedback,
    this.plant,
    this.line,
  });

  final GradeApi api;
  final PcFeedback feedback;
  final String? plant, line;

  @override
  State<GradeScreen> createState() => _GradeScreenState();
}

const _periods = <(String, Duration, int)>[
  ('1 h', Duration(hours: 1), 60),
  ('6 h', Duration(hours: 6), 300),
  ('24 h', Duration(hours: 24), 3600),
  ('7 d', Duration(days: 7), 21600),
];

class _GradeScreenState extends State<GradeScreen> {
  GradeTrend? _trend;
  bool _loading = false;
  String? _error;
  DateTime? _at;
  int _period = 2;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    if (_loading) return;
    setState(() => _loading = true);
    final p = _periods[_period];
    try {
      final t = await widget.api.trend(
        plant: widget.plant,
        line: widget.line,
        span: p.$2,
        bucketSeconds: p.$3,
      );
      if (!mounted) return;
      setState(() {
        _trend = t;
        _at = DateTime.now();
        _error = null;
      });
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _error = e is VisionApiException
            ? e.message
            : 'Cannot reach the plant server.',
      );
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = context.pc;
    final t = _trend;
    return Scaffold(
      appBar: AppBar(
        title: Text(
          widget.line != null && widget.line!.isNotEmpty
              ? 'Print quality · ${widget.line}'
              : 'Print quality',
        ),
        actions: [
          IconButton(
            tooltip: 'Reload the print quality',
            icon: const Icon(Icons.refresh),
            onPressed: _loading ? null : _load,
          ),
        ],
      ),
      body: SafeArea(
        child: Column(
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(12, 8, 12, 4),
              child: SegmentedButton<int>(
                segments: [
                  for (var i = 0; i < _periods.length; i++)
                    ButtonSegment(value: i, label: Text(_periods[i].$1)),
                ],
                selected: {_period},
                onSelectionChanged: (s) {
                  widget.feedback.tap();
                  setState(() => _period = s.first);
                  _load();
                },
              ),
            ),
            if (_error != null)
              Padding(
                padding: const EdgeInsets.all(12),
                child: Text(
                  '${PcStatus.warn.shape} ${_at == null ? _error! : '$_error Showing the trend as of ${hhmm(_at!)}.'}',
                  style: TextStyle(color: c.warn, fontWeight: FontWeight.w600),
                ),
              ),
            Expanded(
              child: t == null
                  ? (_error != null
                        ? const SizedBox.shrink()
                        : const Center(child: CircularProgressIndicator()))
                  : t.series.isEmpty
                  ? Center(
                      child: Text(
                        'No graded parts in this period.',
                        style: TextStyle(color: c.inkMuted),
                      ),
                    )
                  : ListView(
                      padding: const EdgeInsets.all(12),
                      children: [
                        for (final s in t.series)
                          _SeriesCard(series: s, trend: t),
                      ],
                    ),
            ),
          ],
        ),
      ),
    );
  }
}

(PcStatus, String) _driftLook(String state) => switch (state) {
  'drift' => (PcStatus.ng, 'Drifting to the lowest grade'),
  'ok' => (PcStatus.ok, 'Steady'),
  _ => (PcStatus.idle, 'Too few graded parts yet'),
};

class _SeriesCard extends StatelessWidget {
  const _SeriesCard({required this.series, required this.trend});
  final GradeSeries series;
  final GradeTrend trend;

  @override
  Widget build(BuildContext context) {
    final c = context.pc;
    final d = series.drift;
    final lowest = d?.lowest ?? trend.lowestGrade;
    final (status, label) = _driftLook(d?.state ?? 'too_few');
    final meta = StringBuffer();
    if (d?.mean != null) {
      final slope = d!.slope ?? 0;
      meta.write(
        'Latest ${d.samples} parts: mean ${d.mean!.toStringAsFixed(2)} (${gradeLetter(d.mean!)}), ${slope >= 0 ? '+' : ''}${slope.toStringAsFixed(3)} per part. ',
      );
    }
    meta.write(
      'Lowest grade ${gradeLetter(lowest)} (${_g(lowest)}, ${d?.lowestFromStation == true ? 'set at the station' : 'the plant rule'}). ${series.parts} parts in this period',
    );
    if (series.ungraded > 0) {
      meta.write(', ${series.ungraded} not gradable (counted as F)');
    }
    meta.write('.');
    return Card(
      margin: const EdgeInsets.only(bottom: 12),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              '${series.line} · ${series.station} · ${series.tool}',
              style: const TextStyle(fontWeight: FontWeight.w700),
            ),
            const SizedBox(height: 2),
            Text(
              '${status.shape} $label',
              style: TextStyle(
                color: status.color(c),
                fontWeight: FontWeight.w600,
              ),
            ),
            const SizedBox(height: 8),
            Semantics(
              label: 'Grade trend of ${series.tool}',
              child: SizedBox(
                height: 140,
                width: double.infinity,
                child: CustomPaint(
                  painter: _TrendPainter(
                    series,
                    trend.since,
                    trend.until,
                    lowest,
                    c,
                  ),
                ),
              ),
            ),
            const SizedBox(height: 6),
            Text('$meta', style: TextStyle(color: c.inkMuted, fontSize: 12)),
          ],
        ),
      ),
    );
  }

  static String _g(double v) =>
      v == v.roundToDouble() ? '${v.toInt()}' : v.toStringAsFixed(1);
}

/// Mean grade as a line, the worst grade of each bucket as a dot (red below
/// the lowest grade), the lowest grade as a dashed red line and an orange ring
/// at F in a bucket with an ungraded part.
class _TrendPainter extends CustomPainter {
  _TrendPainter(this.s, this.since, this.until, this.lowest, this.c);
  final GradeSeries s;
  final int since, until;
  final double lowest;
  final PcColors c;

  @override
  void paint(Canvas canvas, Size size) {
    const left = 22.0, top = 6.0, bottom = 16.0;
    final w = size.width - left - 6, h = size.height - top - bottom;
    final span = math.max(1, until - since);
    double x(int t) => left + w * ((t - since) / span).clamp(0.0, 1.0);
    double y(double g) => top + h * (1 - g / 4);
    final grid = Paint()..color = c.line;
    for (var g = 0; g <= 4; g++) {
      canvas.drawLine(
        Offset(left, y(g.toDouble())),
        Offset(left + w, y(g.toDouble())),
        grid,
      );
      final tp = TextPainter(
        text: TextSpan(
          text: gradeLetter(g.toDouble()),
          style: TextStyle(color: c.inkMuted, fontSize: 9),
        ),
        textDirection: TextDirection.ltr,
      )..layout();
      tp.paint(
        canvas,
        Offset(left - 6 - tp.width, y(g.toDouble()) - tp.height / 2),
      );
    }
    final low = Paint()
      ..color = c.ng
      ..strokeWidth = 1.5;
    for (var px = left; px < left + w; px += 8) {
      canvas.drawLine(
        Offset(px, y(lowest)),
        Offset(math.min(px + 5, left + w), y(lowest)),
        low,
      );
    }
    final pts = [
      for (final b in s.buckets)
        if (b.mean != null) Offset(x(b.start), y(b.mean!)),
    ];
    if (pts.length > 1) {
      canvas.drawPath(
        Path()..addPolygon(pts, false),
        Paint()
          ..color = c.brand
          ..style = PaintingStyle.stroke
          ..strokeWidth = 2,
      );
    }
    for (final b in s.buckets) {
      final cx = x(b.start);
      if (b.worst != null) {
        canvas.drawCircle(
          Offset(cx, y(b.worst!)),
          3,
          Paint()..color = b.worst! < lowest ? c.ng : c.ok,
        );
      }
      if (b.ungraded > 0) {
        canvas.drawCircle(
          Offset(cx, y(0)),
          5,
          Paint()
            ..color = c.warn
            ..style = PaintingStyle.stroke
            ..strokeWidth = 2,
        );
      }
    }
  }

  @override
  bool shouldRepaint(_TrendPainter old) =>
      old.s != s ||
      old.lowest != lowest ||
      old.since != since ||
      old.until != until;
}
