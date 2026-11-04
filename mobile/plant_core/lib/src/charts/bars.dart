import 'package:flutter/material.dart';

import '../theme.dart';

/// One bar of [PcBars]: a reason and its count.
class PcBar {
  const PcBar(this.label, this.count);
  final String label;
  final int count;
}

/// Horizontal bars, largest first (a Pareto of NG reasons or losses), each
/// with its label and count as text, so the numbers can be read without
/// the bars.
class PcBars extends StatelessWidget {
  const PcBars({super.key, required this.bars, this.status = PcStatus.ng, this.max = 6});

  final List<PcBar> bars;
  final PcStatus status;
  final int max;

  @override
  Widget build(BuildContext context) {
    final c = context.pc;
    final sorted = [...bars.where((b) => b.count > 0)]..sort((a, b) => b.count.compareTo(a.count));
    final shown = sorted.take(max).toList();
    if (shown.isEmpty) return Text('No NG parts', style: TextStyle(color: c.inkMuted));
    final top = shown.first.count;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        for (final b in shown)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 6),
            child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
              Row(children: [
                Expanded(child: Text(b.label, style: TextStyle(color: c.ink, fontSize: 14), overflow: TextOverflow.ellipsis)),
                Text('${b.count}', style: TextStyle(color: c.ink, fontSize: 14, fontFeatures: const [FontFeature.tabularFigures()])),
              ]),
              const SizedBox(height: 4),
              ClipRRect(
                borderRadius: BorderRadius.circular(4),
                child: LinearProgressIndicator(value: b.count / top, minHeight: 8, color: status.color(c), backgroundColor: c.sunken),
              ),
            ]),
          ),
      ],
    );
  }
}
