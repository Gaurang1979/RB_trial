import 'dart:async';

import 'package:flutter/foundation.dart' show ValueListenable;
import 'package:flutter/material.dart';
import 'package:plant_core/plant_core.dart';

import 'account_screen.dart';
import 'line_look.dart';
import 'ng_api.dart';
import 'ng_review_screen.dart';
import 'vision_api.dart';

/// One line: yield ring, counters, why parts were NG, open alarms, last NG parts.
/// With [ng], a tap on a part of the NG strip opens it for review, and the
/// app bar has the account button (sign in to give verdicts) and the line's NG review.
/// It follows [feed] (the floor's latest answer) for the line with the same
/// plant and name, so it does not freeze at what the person tapped; when the
/// floor's last good answer is older than [staleAfter] it says "As of HH:MM".
/// A line that drops out of the floor keeps its last known values.
class LineScreen extends StatefulWidget {
  const LineScreen({super.key, required this.line, this.feed, this.staleAfter = const Duration(seconds: 15), this.ng, this.feedback});
  final LineLive line;
  final ValueListenable<FloorSnapshot?>? feed;
  final Duration staleAfter;

  /// When both are given, the app bar offers the account and the NG review of this line, and the NG strip opens parts.
  final NgApi? ng;
  final PcFeedback? feedback;

  @override
  State<LineScreen> createState() => _LineScreenState();
}

/// How many of the line's last NG parts the strip shows.
const _strip = 10;

/// The live floor keeps one entry per NG reason; the strip shows one row per
/// part, its reasons joined (entries of one part share a result id).
List<NgItem> _parts(List<NgItem> ng) {
  final out = <NgItem>[];
  final at = <int, int>{};
  for (final n in ng) {
    final i = n.resultId == null ? null : at[n.resultId!];
    if (i == null) {
      if (n.resultId != null) at[n.resultId!] = out.length;
      out.add(n);
      continue;
    }
    final p = out[i];
    out[i] = NgItem(p.ts, p.station, '', '${p.tool.isEmpty ? '' : '${reasonWords(p.tool)}: '}${reasonWords(p.reason)}, '
        '${n.tool.isEmpty ? '' : '${reasonWords(n.tool)}: '}${reasonWords(n.reason)}', camera: p.camera, resultId: p.resultId);
  }
  return out;
}

class _LineScreenState extends State<LineScreen> {
  late LineLive _line = widget.line;
  DateTime? _at;
  bool _stale = false;
  Timer? _timer;

  @override
  void initState() {
    super.initState();
    widget.feed?.addListener(_follow);
    _sync();
    // The feed is silent when the server is unreachable, so the age is checked on a clock too.
    _timer = Timer.periodic(const Duration(seconds: 1), (_) => _checkAge());
  }

  @override
  void dispose() {
    _timer?.cancel();
    widget.feed?.removeListener(_follow);
    super.dispose();
  }

  void _follow() => setState(_sync);

  void _sync() {
    final snap = widget.feed?.value;
    if (snap == null) return;
    final mine = snap.lines.where((l) => l.plant == widget.line.plant && l.line == widget.line.line).firstOrNull;
    _at = snap.at;
    _stale = _isStale();
    if (mine != null) _line = mine;
  }

  bool _isStale() => _at != null && DateTime.now().difference(_at!) > widget.staleAfter;

  void _checkAge() {
    if (_isStale() != _stale && mounted) setState(() => _stale = !_stale);
  }

  /// One part of the NG strip; it opens in the NG review when the server named its result.
  Widget _ngTile(BuildContext context, LineLive line, NgItem n, int i) {
    final c = context.pc;
    final id = n.resultId;
    final open = id != null && widget.ng != null && widget.feedback != null;
    final when = n.ts > 0 ? hhmm(DateTime.fromMillisecondsSinceEpoch(n.ts)) : '';
    return ListTile(
      key: ValueKey('ng-strip-$i'),
      contentPadding: EdgeInsets.zero,
      leading: Text('✗', style: TextStyle(color: c.ng, fontSize: 22, fontWeight: FontWeight.w700)),
      title: Text('${n.tool.isEmpty ? '' : '${reasonWords(n.tool)}: '}${reasonWords(n.reason)}', style: TextStyle(color: c.ink)),
      subtitle: Text([n.station, if (n.camera != null) n.camera!, if (when.isNotEmpty) when].where((s) => s.isNotEmpty).join(' · '), style: TextStyle(color: c.inkMuted)),
      trailing: open ? Icon(Icons.chevron_right, color: c.inkMuted) : null,
      onTap: open
          ? () {
              widget.feedback!.tap();
              Navigator.of(context).push(MaterialPageRoute<void>(
                builder: (_) => NgReviewScreen(api: widget.ng!, feedback: widget.feedback!, plant: line.plant, line: line.line, partId: id),
              ));
            }
          : null,
    );
  }

  @override
  Widget build(BuildContext context) {
    final line = _line;
    final c = context.pc;
    final (status, words) = lineLook(line.state);
    final y = line.yieldPct == null ? null : line.yieldPct! / 100;
    final reasons = <String, int>{};
    for (final n in line.lastNg) {
      reasons[n.reason] = (reasons[n.reason] ?? 0) + 1;
    }
    Widget panel(String title, Widget child) => Padding(
          padding: const EdgeInsets.only(bottom: 12),
          child: Card(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                Text(title, style: TextStyle(fontWeight: FontWeight.w600, fontSize: 16, color: c.ink)),
                const SizedBox(height: 10),
                child,
              ]),
            ),
          ),
        );
    Widget count(String label, int v, Color color) => Expanded(
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(label, style: TextStyle(color: c.inkMuted, fontSize: 13)),
            Text('$v', style: TextStyle(color: color, fontSize: 24, fontWeight: FontWeight.w700, fontFeatures: const [FontFeature.tabularFigures()])),
          ]),
        );
    return Scaffold(
      // the plant too when the server has several, so same-named lines are told apart
      appBar: AppBar(title: Text((widget.feed?.value?.lines.map((l) => l.plant).toSet().length ?? 1) > 1 ? '${line.plant} · ${line.line}' : line.line),
        actions: [
          if (widget.ng != null && widget.feedback != null)
            ListenableBuilder(
              listenable: widget.ng!.auth,
              builder: (context, _) => IconButton(
                tooltip: widget.ng!.auth.signedIn ? 'Account: signed in as ${widget.ng!.auth.session!.name}' : 'Sign in',
                icon: Icon(widget.ng!.auth.signedIn ? Icons.account_circle : Icons.account_circle_outlined),
                onPressed: () => openAccount(context, widget.ng!.auth, widget.feedback!),
              ),
            ),
          if (widget.ng != null && widget.feedback != null)
            IconButton(
              tooltip: 'NG review of this line',
              icon: const Icon(Icons.fact_check_outlined),
              onPressed: () {
                widget.feedback!.tap();
                Navigator.of(context).push(MaterialPageRoute<void>(
                  builder: (_) => NgReviewScreen(api: widget.ng!, feedback: widget.feedback!, plant: line.plant, line: line.line),
                ));
              },
            ),
        ],
      ),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          if (_stale && _at != null)
            Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: Text('As of ${hhmm(_at!)}', style: TextStyle(color: c.inkMuted)),
            ),
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
            decoration: BoxDecoration(color: status.background(c), borderRadius: BorderRadius.circular(12)),
            child: Text('${status.shape} $words${line.stateReason != null ? ' · ${reasonWords(line.stateReason!)}' : ''}',
                style: TextStyle(color: status.color(c), fontWeight: FontWeight.w700, fontSize: 16)),
          ),
          const SizedBox(height: 12),
          panel(
            'This shift',
            Row(children: [
              PcRing(fraction: y, label: 'Yield', size: 104, status: y == null ? PcStatus.idle : y >= .99 ? PcStatus.ok : y >= .97 ? PcStatus.warn : PcStatus.ng),
              const SizedBox(width: 16),
              Expanded(
                child: Column(children: [
                  Row(children: [count('Good', line.good, c.ok), count('NG', line.bad, c.ng)]),
                  const SizedBox(height: 8),
                  Row(children: [count('Per min', line.partsPerMinute, c.ink), count('Stations on', line.stationsOnline, c.ink)]),
                ]),
              ),
            ]),
          ),
          panel('Why parts were NG (last ${line.lastNg.length})', PcBars(bars: [for (final e in reasons.entries) PcBar(reasonWords(e.key), e.value)])),
          if (line.alarms.isNotEmpty)
            panel(
              'Open alarms',
              Column(children: [
                for (final a in line.alarms)
                  ListTile(
                    contentPadding: EdgeInsets.zero,
                    leading: Text(a.priority == 'high' ? '✗' : '!', style: TextStyle(color: a.priority == 'high' ? c.ng : c.warn, fontSize: 22, fontWeight: FontWeight.w700)),
                    title: Text(a.title ?? reasonWords(a.code), style: TextStyle(color: c.ink)),
                    subtitle: Text('${a.stationName ?? a.station}${a.fix != null && a.fix!.isNotEmpty ? ' · ${a.fix}' : ''}${a.acknowledgedBy != null ? ' · ${a.acknowledgedBy} is on it' : ' · not acknowledged'}', style: TextStyle(color: c.inkMuted)),
                  ),
              ]),
            ),
          if (line.lastNg.isNotEmpty)
            panel(
              'Last NG parts',
              Column(children: [
                for (final (i, n) in _parts(line.lastNg).take(_strip).indexed) _ngTile(context, line, n, i),
              ]),
            ),
        ],
      ),
    );
  }
}
