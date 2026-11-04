import 'dart:async';

import 'package:flutter/foundation.dart' show ValueListenable;
import 'package:flutter/material.dart';
import 'package:plant_core/plant_core.dart';

import 'account_screen.dart';
import 'api_auth.dart';
import 'changeover_api.dart';
import 'changeover_screen.dart';
import 'grade_api.dart';
import 'grade_screen.dart';
import 'line_look.dart';
import 'line_screen.dart';
import 'ng_api.dart';
import 'ng_review_screen.dart';
import 'vision_api.dart';

/// Every line of one plant server, worst first. With [live] the server
/// pushes the floor over `WS /vision/ws` (requirements 17.3, LV-05); while
/// that socket is closed, not yet sending or dropped, and without [live],
/// it is read every [every]. A new alarm plays its sound by priority (only
/// alarms do; an NG part makes no sound), whichever way the floor came. One
/// read at a time: a tick or a pull while one is still out joins it instead
/// of starting another, and a read overtaken by a push is dropped, so an old
/// answer can never land after a newer one.
/// With [changeover] the app bar also offers the changeovers waiting for a
/// check, with how many there are (read after each floor read).
class FloorScreen extends StatefulWidget {
  const FloorScreen({super.key, required this.server, required this.api, required this.feedback, this.every = const Duration(seconds: 5), this.changeover, this.live});

  final PcServer server;
  final VisionApi api;
  final PcFeedback feedback;
  final Duration every;
  final ChangeoverApi? changeover;

  /// Opens the live socket; null: polling only.
  final PcSocketConnect? live;

  @override
  State<FloorScreen> createState() => _FloorScreenState();
}

class _FloorScreenState extends State<FloorScreen> {
  // The latest good answer; the line screens follow it.
  final _feed = ValueNotifier<FloorSnapshot?>(null);
  String? _error;
  Set<String>? _knownAlarms;
  Timer? _timer;
  Future<void>? _inFlight;
  int _waiting = 0, _pushes = 0;
  bool _counting = false;
  PcLiveSocket? _socket;

  @override
  void initState() {
    super.initState();
    _load();
    _timer = Timer.periodic(widget.every, (_) => _tick());
    final connect = widget.live;
    if (connect != null) {
      _socket = PcLiveSocket(connect: connect, url: widget.api.liveUrl, onMessage: _pushed, onClose: _socketClosed)..start();
      widget.api.auth.addListener(_authChanged);
    }
  }

  @override
  void dispose() {
    _timer?.cancel();
    if (_socket != null) widget.api.auth.removeListener(_authChanged);
    _socket?.stop();
    _feed.dispose();
    super.dispose();
  }

  // While the socket is open the server tells of every change (within 5 s,
  // and the ping finds a dead link), so what is shown is current: only its
  // time moves on. Otherwise the floor is read.
  void _tick() {
    final snap = _feed.value;
    if (_socket?.live == true && snap != null) {
      _feed.value = FloorSnapshot(snap.lines, DateTime.now());
    } else {
      _load();
    }
  }

  // A sign-in, sign-out, lock or ended session: the socket is opened again
  // at once with the new credential, so a person's token never stays on it.
  void _authChanged() {
    final s = _socket!;
    if (s.current != null && s.current != widget.api.liveUrl()) s.start();
  }

  // A session refused on the socket has ended, as a 401 to a call says
  // (the auth change then reconnects with the key). A refused key: the
  // reads say so, and the socket tries again later.
  void _socketClosed(Uri url, int? code) {
    if (code != 4401 || !mounted || VisionApi.sentWith(url)['Authorization'] == null) return;
    widget.api.auth.onUnauthorized(VisionApi.sentWith(url));
    setState(() {}); // the "sign-in has ended" banner
  }

  void _pushed(String message) {
    final lines = VisionApi.pushedFloor(message);
    if (lines == null || !mounted) return;
    _pushes++;
    _apply(lines);
  }

  Future<void> _load() => _inFlight ??= _read().whenComplete(() => _inFlight = null);

  Future<void> _read() async {
    final pushes = _pushes;
    try {
      final lines = await widget.api.floor();
      // A push came while this read was out: it is newer.
      if (!mounted || pushes != _pushes) return;
      _apply(lines);
    } catch (e) {
      if (!mounted || pushes != _pushes) return;
      setState(() => _error = e is VisionApiException ? e.message : 'Cannot reach the plant server.');
    }
  }

  void _apply(List<LineLive> lines) {
    final alarms = {for (final l in lines) for (final a in l.alarms) a.key: a};
    // The first load only learns what is open; later loads sound the new ones, worst first.
    if (_knownAlarms != null) {
      final fresh = alarms.keys.where((k) => !_knownAlarms!.contains(k)).map((k) => alarms[k]!).toList();
      const order = {'high': 0, 'medium': 1};
      fresh.sort((a, b) => (order[a.priority] ?? 2).compareTo(order[b.priority] ?? 2));
      if (fresh.isNotEmpty) widget.feedback.alarm(fresh.first.priority);
    }
    setState(() {
      _knownAlarms = alarms.keys.toSet();
      _feed.value = FloorSnapshot([...lines]..sort((a, b) => lineRank(a).compareTo(lineRank(b))), DateTime.now());
      _error = null;
    });
    _countWaiting();
  }

  // How many changeovers wait for a check. Best effort: when it cannot be
  // read the last count stays, and the floor itself is not held up.
  Future<void> _countWaiting() async {
    final api = widget.changeover;
    if (api == null || _counting) return;
    _counting = true;
    try {
      final n = (await api.list(limit: 200)).length;
      if (mounted && n != _waiting) setState(() => _waiting = n);
    } catch (_) {
      // Keeps the last count.
    } finally {
      _counting = false;
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = context.pc;
    final snap = _feed.value;
    final lines = snap?.lines;
    final manyPlants = lines != null && lines.map((l) => l.plant).toSet().length > 1;
    return Scaffold(
      appBar: AppBar(
        title: Text(widget.server.name),
        actions: [
          ListenableBuilder(
            listenable: widget.api.auth,
            builder: (context, _) => IconButton(
              tooltip: widget.api.auth.signedIn ? 'Account: signed in as ${widget.api.auth.session!.name}' : 'Sign in',
              icon: Icon(widget.api.auth.signedIn ? Icons.account_circle : Icons.account_circle_outlined),
              onPressed: () => openAccount(context, widget.api.auth, widget.feedback),
            ),
          ),
          if (widget.changeover != null)
            IconButton(
              tooltip: _waiting > 0 ? 'Changeover: $_waiting waiting for a check' : 'Changeover',
              icon: Badge(label: Text(_waiting > 99 ? '99+' : '$_waiting'), isLabelVisible: _waiting > 0, child: const Icon(Icons.swap_horiz)),
              onPressed: () async {
                widget.feedback.tap();
                await Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => ChangeoverScreen(api: widget.changeover!, feedback: widget.feedback)));
                _countWaiting();
              },
            ),
          IconButton(
            tooltip: 'Print quality',
            icon: const Icon(Icons.show_chart),
            onPressed: () {
              widget.feedback.tap();
              Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => GradeScreen(api: GradeApi.of(widget.api), feedback: widget.feedback)));
            },
          ),
          IconButton(
            tooltip: 'NG review',
            icon: const Icon(Icons.fact_check_outlined),
            onPressed: () {
              widget.feedback.tap();
              Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => NgReviewScreen(api: NgApi.of(widget.api), feedback: widget.feedback)));
            },
          ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(
          // pull-to-refresh works with only a few lines too
          physics: const AlwaysScrollableScrollPhysics(),
          padding: const EdgeInsets.all(16),
          children: [
            if (_error != null)
              Padding(
                padding: const EdgeInsets.only(bottom: 12),
                child: _Banner(
                  status: PcStatus.warn,
                  text: lines == null ? _error! : '$_error Showing what was known at ${hhmm(snap!.at)}.',
                ),
              ),
            if (widget.api.auth.ended)
              const Padding(padding: EdgeInsets.only(bottom: 12), child: _Banner(status: PcStatus.warn, text: ApiAuth.endedMessage)),
            if (lines == null && _error == null) const Center(child: Padding(padding: EdgeInsets.all(32), child: CircularProgressIndicator())),
            if (lines != null && lines.isEmpty) Text('No lines yet. Stations appear here when they start sending.', style: TextStyle(color: c.inkMuted)),
            for (final l in lines ?? const <LineLive>[]) _LineCard(line: l, feedback: widget.feedback, feed: _feed, showPlant: manyPlants, ng: NgApi.of(widget.api)),
          ],
        ),
      ),
    );
  }
}

class _Banner extends StatelessWidget {
  const _Banner({required this.status, required this.text});
  final PcStatus status;
  final String text;

  @override
  Widget build(BuildContext context) {
    final c = context.pc;
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(color: status.background(c), borderRadius: BorderRadius.circular(12)),
      child: Row(children: [
        Text(status.shape, style: TextStyle(color: status.color(c), fontWeight: FontWeight.w700, fontSize: 18)),
        const SizedBox(width: 10),
        Expanded(child: Text(text, style: TextStyle(color: c.ink))),
      ]),
    );
  }
}

class _LineCard extends StatelessWidget {
  const _LineCard({required this.line, required this.feedback, required this.feed, required this.showPlant, required this.ng});
  final LineLive line;
  final NgApi ng;
  final PcFeedback feedback;
  final ValueListenable<FloorSnapshot?> feed;

  /// The floor holds more than one plant: line names alone could be ambiguous.
  final bool showPlant;

  @override
  Widget build(BuildContext context) {
    final c = context.pc;
    final (status, words) = lineLook(line.state);
    final y = line.yieldPct == null ? null : line.yieldPct! / 100;
    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: Card(
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: () {
            feedback.tap();
            Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => LineScreen(line: line, feed: feed, ng: ng, feedback: feedback)));
          },
          child: Container(
            decoration: BoxDecoration(border: Border(left: BorderSide(color: status.color(c), width: 5))),
            padding: const EdgeInsets.all(14),
            child: Row(children: [
              PcRing(fraction: y, label: 'Yield', size: 72, status: y == null ? PcStatus.idle : y >= .99 ? PcStatus.ok : y >= .97 ? PcStatus.warn : PcStatus.ng),
              const SizedBox(width: 14),
              Expanded(
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  // A Wrap, not a Row: with large text the state words drop below the name instead of overflowing.
                  Wrap(alignment: WrapAlignment.spaceBetween, crossAxisAlignment: WrapCrossAlignment.center, spacing: 8, children: [
                    Text(showPlant && line.plant.isNotEmpty ? '${line.plant} · ${line.line}' : line.line, style: TextStyle(fontSize: 18, fontWeight: FontWeight.w700, color: c.ink)),
                    Text('${status.shape} $words', style: TextStyle(color: status.color(c), fontWeight: FontWeight.w600)),
                  ]),
                  const SizedBox(height: 4),
                  Text(
                    [if (line.job != null) line.job!, '${line.partsPerMinute} per min'].join(' · '),
                    style: TextStyle(color: c.inkMuted),
                  ),
                  if (line.alarms.isNotEmpty || line.topNg != null) const SizedBox(height: 6),
                  if (line.alarms.isNotEmpty)
                    Text('! ${line.alarms.length} open alarm${line.alarms.length == 1 ? '' : 's'}', style: TextStyle(color: c.warn, fontWeight: FontWeight.w600)),
                  if (line.topNg != null) Text('Top NG: ${reasonWords(line.topNg!)}', style: TextStyle(color: c.inkMuted)),
                ]),
              ),
            ]),
          ),
        ),
      ),
    );
  }
}
