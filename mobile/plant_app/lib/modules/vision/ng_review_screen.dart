
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:plant_core/plant_core.dart';

import 'account_screen.dart';
import 'line_look.dart';
import 'api_auth.dart';
import 'ng_api.dart';
import 'vision_api.dart';

/// The NG parts waiting for a verdict, one card at a time, newest first.
/// Swipe right to confirm NG, left to overrule (a good part, a false
/// reject), up for unsure; three buttons do the same. A verdict is sent at
/// once and never queued: when it fails the card comes back with the reason.
/// The queue is read in pages of [pageSize] with before_id; the next page is
/// read when few cards are left. [plant] and [line] limit it to one line.
/// With [partId] it shows that one part (opened from the line view's NG
/// strip), whether or not it has a verdict yet; a new verdict is added to
/// its history. A note can go with every verdict.
class NgReviewScreen extends StatefulWidget {
  const NgReviewScreen({super.key, required this.api, required this.feedback, this.plant, this.line, this.pageSize = 50, this.partId});

  final NgApi api;
  final PcFeedback feedback;
  final String? plant, line;
  final int pageSize;
  final int? partId;

  @override
  State<NgReviewScreen> createState() => _NgReviewScreenState();
}

/// How each verdict looks: colour, shape and words (never colour alone).
({PcStatus status, IconData icon, String label, String shape}) _look(NgVerdict v) => switch (v) {
      NgVerdict.confirmed => (status: PcStatus.ng, icon: Icons.close, label: 'Confirm NG', shape: '✗'),
      NgVerdict.overruled => (status: PcStatus.ok, icon: Icons.check, label: 'Good part', shape: '✓'),
      NgVerdict.unsure => (status: PcStatus.warn, icon: Icons.help_outline, label: 'Unsure', shape: '?'),
    };

class _NgReviewScreenState extends State<NgReviewScreen> {
  final _queue = <NgPart>[];
  int? _next;
  bool _loaded = false, _loading = false, _sending = false, _saved = false;
  String? _error, _notice;
  DateTime? _at;
  Offset _drag = Offset.zero;

  late final _images = NgImageCache(widget.api);
  final _note = TextEditingController();
  // Who the queue was read for: a lock, a sign-out or another person changes it.
  String? _who;
  int _gen = 0;

  static const _threshold = 90.0;

  @override
  void initState() {
    super.initState();
    _who = widget.api.auth.session?.user;
    widget.api.auth.addListener(_authChanged);
    _load(reset: true);
  }

  @override
  void dispose() {
    widget.api.auth.removeListener(_authChanged);
    _note.dispose();
    super.dispose();
  }

  // A sign-in, sign-out, lock or another person changes what the server shows
  // and who may see it: the queue and images go, and the queue is read again.
  void _authChanged() {
    final now = widget.api.auth.session?.user;
    if (now == _who) return;
    _who = now;
    _gen++;
    _images.clear();
    _note.clear();
    setState(() {
      _queue.clear();
      _next = null;
      _loaded = false;
      _loading = false;
      _saved = false;
      _drag = Offset.zero;
      _error = null;
    });
    // Locked or signed out, the server shows what it shows without a sign-in.
    _load(reset: true);
  }

  Future<void> _load({bool reset = false}) async {
    if (_loading) return;
    final gen = _gen;
    setState(() => _loading = true);
    try {
      if (widget.partId != null) {
        final p = await widget.api.part(widget.partId!);
        if (!mounted || gen != _gen) return;
        setState(() {
          _queue
            ..clear()
            ..add(p);
          _next = null;
          _loaded = true;
          _saved = false;
          _at = DateTime.now();
          _error = null;
        });
        return;
      }
      final page = await widget.api.pending(plant: widget.plant, line: widget.line, beforeId: reset ? null : _next, limit: widget.pageSize);
      if (!mounted || gen != _gen) return;
      setState(() {
        final first = _queue.firstOrNull?.id;
        if (reset) _queue.clear();
        final known = {for (final p in _queue) p.id};
        _queue.addAll(page.items.where((p) => known.add(p.id)));
        // A note was typed for the card that was on top: it never goes with another part.
        if (_queue.firstOrNull?.id != first) _note.clear();
        // A page that adds nothing must not be asked for again forever.
        _next = page.items.isEmpty ? null : page.nextBeforeId;
        _loaded = true;
        _at = DateTime.now();
        _error = null;
      });
    } catch (e) {
      if (!mounted || gen != _gen) return;
      setState(() => _error = e is VisionApiException ? e.message : 'Cannot reach the plant server.');
    } finally {
      if (mounted && gen == _gen) setState(() => _loading = false);
    }
  }

  Future<void> _decide(NgVerdict v) async {
    if (_sending || _queue.isEmpty || !widget.api.auth.signedIn) return;
    final part = _queue.first;
    final note = _note.text.trim();
    widget.feedback.tap();
    final gen = _gen;
    _note.clear();
    setState(() {
      _sending = true;
      _drag = Offset.zero;
      _notice = null;
      _queue.removeAt(0);
    });
    try {
      await widget.api.review(part.id, v, note: note);
      _images.drop(part.id);
      widget.feedback.confirm();
      if (mounted && gen == _gen) setState(() => _saved = true);
    } catch (e) {
      // Another person's queue since: the card is not theirs to get back.
      if (!mounted || gen != _gen) return;
      final gone = e is NgApiException && e.code == 404;
      if (gone) _images.drop(part.id);
      setState(() {
        // The card goes back for another try, with its note, unless the server no longer has it waiting.
        if (!gone) {
          _queue.insert(0, part);
          if (_note.text.isEmpty) _note.text = note;
        }
        _notice = e is VisionApiException ? (e is NgApiException && e.code == 0 ? '${e.message} The verdict was not saved.' : e.message) : 'The verdict was not saved.';
      });
    } finally {
      if (mounted) setState(() => _sending = false);
    }
    if (mounted && _next != null && _queue.length <= 5) _load();
  }

  void _dragEnd() {
    final d = _drag;
    NgVerdict? v;
    if (d.dy < -_threshold && d.dy.abs() > d.dx.abs()) {
      v = NgVerdict.unsure;
    } else if (d.dx > _threshold && d.dx.abs() >= d.dy.abs()) {
      v = NgVerdict.confirmed;
    } else if (d.dx < -_threshold && d.dx.abs() >= d.dy.abs()) {
      v = NgVerdict.overruled;
    }
    if (v == null) {
      setState(() => _drag = Offset.zero);
    } else {
      _decide(v);
    }
  }

  NgVerdict? get _leaning {
    final d = _drag;
    if (d.distance < 24) return null;
    if (d.dy < 0 && d.dy.abs() > d.dx.abs()) return NgVerdict.unsure;
    return d.dx >= 0 ? NgVerdict.confirmed : NgVerdict.overruled;
  }

  @override
  Widget build(BuildContext context) => ListenableBuilder(listenable: widget.api.auth, builder: (context, _) => _screen(context));

  Widget _screen(BuildContext context) {
    final c = context.pc;
    final part = _queue.isEmpty ? null : _queue.first;
    final title = widget.partId != null
        ? 'NG part'
        : widget.line != null && widget.line!.isNotEmpty
            ? 'NG review · ${widget.line}'
            : 'NG review';
    return Scaffold(
      appBar: AppBar(
        title: Text(title),
        actions: [
          IconButton(
            tooltip: widget.api.auth.signedIn ? 'Account: signed in as ${widget.api.auth.session!.name}' : 'Sign in',
            icon: Icon(widget.api.auth.signedIn ? Icons.account_circle : Icons.account_circle_outlined),
            onPressed: () => openAccount(context, widget.api.auth, widget.feedback),
          ),
          IconButton(tooltip: widget.partId != null ? 'Reload the part' : 'Reload the queue', icon: const Icon(Icons.refresh), onPressed: _loading || _sending ? null : () => _load(reset: true)),
        ],
      ),
      body: SafeArea(
        child: Column(children: [
          if (_error != null)
            _Note(
              status: PcStatus.warn,
              text: _at == null ? _error! : '$_error Showing the queue as of ${hhmm(_at!)}.',
            ),
          if (widget.api.auth.ended) const _Note(status: PcStatus.warn, text: ApiAuth.endedMessage),
          if (_notice != null && _notice != ApiAuth.endedMessage) _Note(status: PcStatus.ng, text: _notice!),
          Expanded(child: _body(c, part)),
          if (part != null && widget.api.auth.signedIn) _noteField(),
          if (part != null) (widget.api.auth.signedIn ? _buttons(c) : _signInPrompt(c)),
        ]),
      ),
    );
  }

  Widget _body(PcColors c, NgPart? part) {
    if (part == null) {
      if (!_loaded) {
        return _error != null ? const SizedBox.shrink() : const Center(child: CircularProgressIndicator());
      }
      if (_next != null || _loading) return const Center(child: CircularProgressIndicator());
      final done = widget.partId != null ? (_saved ? 'Verdict saved.' : 'This part is no longer waiting for review.') : 'Nothing is waiting for review.';
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(32),
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            Text('✓', style: TextStyle(color: c.ok, fontSize: 48, fontWeight: FontWeight.w700)),
            const SizedBox(height: 8),
            Text(done, textAlign: TextAlign.center, style: TextStyle(color: c.ink, fontSize: 18, fontWeight: FontWeight.w600)),
          ]),
        ),
      );
    }
    final lean = _leaning;
    final signedIn = widget.api.auth.signedIn;
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 8),
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        Padding(
          padding: const EdgeInsets.only(bottom: 8),
          child: Text(
            '${widget.partId != null ? (part.verdict == null ? 'Waiting for review' : 'Reviewed; a new verdict is added to its history') : '${_queue.length}${_next != null ? '+' : ''} waiting'}'
            '${signedIn ? ' · swipe right to confirm, left for a good part, up if unsure' : ''}',
            style: TextStyle(color: c.inkMuted, fontSize: 13),
          ),
        ),
        Expanded(
          child: GestureDetector(
            onPanUpdate: _sending || !signedIn ? null : (d) => setState(() => _drag += d.delta),
            onPanEnd: _sending || !signedIn ? null : (_) => _dragEnd(),
            onPanCancel: () => setState(() => _drag = Offset.zero),
            child: Transform.translate(
              offset: _drag,
              child: Transform.rotate(
                angle: (_drag.dx / 1200).clamp(-0.3, 0.3),
                child: Stack(children: [
                  Positioned.fill(child: _PartCard(key: ValueKey(part.id), part: part, images: _images)),
                  if (lean != null) Positioned(top: 12, left: 12, right: 12, child: _Stamp(verdict: lean, armed: _drag.distance >= _threshold)),
                ]),
              ),
            ),
          ),
        ),
      ]),
    );
  }

  /// A few words that go with the verdict (kept by the server in the part's history).
  Widget _noteField() => Padding(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 0),
        child: TextField(
          key: const ValueKey('ng-note'),
          controller: _note,
          enabled: !_sending,
          // Counted as the server counts (code points), not as letters on screen: an emoji can be several.
          inputFormatters: [_CodePointLimit(NgApi.maxNote)],
          minLines: 1,
          maxLines: 3,
          textInputAction: TextInputAction.done,
          decoration: const InputDecoration(labelText: 'Note with the verdict (optional)', isDense: true, border: OutlineInputBorder()),
        ),
      );

  /// Without a person's sign-in the queue can be looked at but no verdict given.
  Widget _signInPrompt(PcColors c) => Padding(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 12),
        child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
          Text('Sign in with your own account to give verdicts.', textAlign: TextAlign.center, style: TextStyle(color: c.ink, fontWeight: FontWeight.w600)),
          const SizedBox(height: 8),
          FilledButton.icon(
            key: const ValueKey('ng-sign-in'),
            icon: const Icon(Icons.login),
            label: const Text('Sign in'),
            onPressed: () => openAccount(context, widget.api.auth, widget.feedback),
          ),
        ]),
      );

  Widget _buttons(PcColors c) {
    Widget b(NgVerdict v) {
      final l = _look(v);
      return Expanded(
        child: Semantics(
          button: true,
          label: l.label,
          child: OutlinedButton(
            key: ValueKey('ng-${v.name}'),
            onPressed: _sending ? null : () => _decide(v),
            style: OutlinedButton.styleFrom(
              minimumSize: const Size(48, 76),
              padding: const EdgeInsets.symmetric(horizontal: 4),
              foregroundColor: l.status.color(c),
              backgroundColor: l.status.background(c),
              side: BorderSide(color: l.status.color(c), width: 2),
            ),
            child: Column(mainAxisAlignment: MainAxisAlignment.center, children: [
              Icon(l.icon, size: 28),
              const SizedBox(height: 2),
              Text(l.label, textAlign: TextAlign.center, style: const TextStyle(fontWeight: FontWeight.w700)),
            ]),
          ),
        ),
      );
    }

    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 4, 16, 12),
      child: Row(children: [
        b(NgVerdict.overruled),
        const SizedBox(width: 8),
        b(NgVerdict.unsure),
        const SizedBox(width: 8),
        b(NgVerdict.confirmed),
      ]),
    );
  }
}

class _Note extends StatelessWidget {
  const _Note({required this.status, required this.text});
  final PcStatus status;
  final String text;

  @override
  Widget build(BuildContext context) {
    final c = context.pc;
    return Container(
      width: double.infinity,
      margin: const EdgeInsets.fromLTRB(16, 4, 16, 4),
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

/// The label that follows a swipe, so the person sees what letting go will do.
class _Stamp extends StatelessWidget {
  const _Stamp({required this.verdict, required this.armed});
  final NgVerdict verdict;
  final bool armed;

  @override
  Widget build(BuildContext context) {
    final c = context.pc;
    final l = _look(verdict);
    return Align(
      alignment: switch (verdict) { NgVerdict.confirmed => Alignment.topLeft, NgVerdict.overruled => Alignment.topRight, NgVerdict.unsure => Alignment.topCenter },
      child: Opacity(
        opacity: armed ? 1 : .6,
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
          decoration: BoxDecoration(color: l.status.background(c), border: Border.all(color: l.status.color(c), width: 3), borderRadius: BorderRadius.circular(10)),
          child: Row(mainAxisSize: MainAxisSize.min, children: [
            Icon(l.icon, color: l.status.color(c)),
            const SizedBox(width: 6),
            Text(l.label, style: TextStyle(color: l.status.color(c), fontWeight: FontWeight.w800, fontSize: 18)),
          ]),
        ),
      ),
    );
  }
}

String _when(int ts) {
  if (ts <= 0) return '';
  final t = DateTime.fromMillisecondsSinceEpoch(ts), n = DateTime.now();
  final today = t.year == n.year && t.month == n.month && t.day == n.day;
  return today ? hhmm(t) : '${t.day.toString().padLeft(2, '0')}-${t.month.toString().padLeft(2, '0')} ${hhmm(t)}';
}

/// Why there may be no picture, by the server's image state.
String _imageWords(String state) => switch (state) {
      'waiting' => 'The image is not on the server yet. The server has asked the line PC for it; reload in a moment.',
      'missing' => 'The line PC no longer has this image.',
      'expired' => 'The image was never sent and is no longer asked for.',
      'removed' => 'The image was removed from the server to keep it within its size limit.',
      'none' => 'No image was kept for this part.',
      _ => 'No image available (state: $state).',
    };

/// The images read for the parts being reviewed, so a card that comes back
/// does not read them again. A part's images go when its card is decided,
/// and at most [maxParts] parts are kept (the least recently used go first).
class NgImageCache {
  NgImageCache(this.api, {this.maxParts = 10});
  final NgApi api;
  final int maxParts;

  // Insertion order is use order: a part that is used again moves to the end.
  final _byPart = <int, Map<String, Future<Uint8List>>>{};

  int get parts => _byPart.length;

  Future<Uint8List> get(int partId, String url) {
    final images = _byPart.remove(partId) ?? {};
    _byPart[partId] = images;
    while (_byPart.length > maxParts) {
      _byPart.remove(_byPart.keys.first);
    }
    return images.putIfAbsent(url, () {
      final f = api.image(url);
      // A failed read is not kept: the next time the card shows, it asks again.
      f.then<void>((_) {}, onError: (Object _) {
        _byPart[partId]?.remove(url);
      });
      return f;
    });
  }

  void drop(int partId) => _byPart.remove(partId);

  void clear() => _byPart.clear();
}

class _PartCard extends StatefulWidget {
  const _PartCard({super.key, required this.part, required this.images});
  final NgPart part;
  final NgImageCache images;

  @override
  State<_PartCard> createState() => _PartCardState();
}

class _PartCardState extends State<_PartCard> {
  int _shown = 0;

  @override
  Widget build(BuildContext context) {
    final c = context.pc;
    final p = widget.part;
    final shown = p.images.isEmpty ? null : p.images[_shown.clamp(0, p.images.length - 1)];
    final reasons = p.reasons.isEmpty ? 'No reason sent' : p.reasons.map((r) => '${r.tool.isEmpty ? '' : '${reasonWords(r.tool)}: '}${reasonWords(r.reason)}').join('\n');
    final facts = [
      if (p.station.isNotEmpty) p.station,
      if (p.camera.isNotEmpty) p.camera,
      if (_when(p.ts).isNotEmpty) _when(p.ts),
      if (p.job != null) p.job!,
    ].join(' · ');
    return Card(
      clipBehavior: Clip.antiAlias,
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        Expanded(
          child: Container(
            color: c.sunken,
            child: shown == null
                ? _Placeholder(text: _imageWords(p.imageState))
                : FutureBuilder<Uint8List>(
                    key: ValueKey(shown.url),
                    future: widget.images.get(p.id, shown.url),
                    builder: (context, snap) {
                      if (snap.hasError) return const _Placeholder(text: 'The image could not be loaded.');
                      if (!snap.hasData) return const Center(child: CircularProgressIndicator());
                      return Image.memory(
                        snap.data!,
                        fit: BoxFit.contain,
                        gaplessPlayback: true,
                        semanticLabel: 'NG image from ${shown.camera}',
                        errorBuilder: (_, _, _) => const _Placeholder(text: 'The image could not be shown.'),
                      );
                    },
                  ),
          ),
        ),
        if (p.images.length > 1)
          Padding(
            padding: const EdgeInsets.fromLTRB(12, 8, 12, 0),
            child: Wrap(spacing: 8, children: [
              for (var i = 0; i < p.images.length; i++)
                ChoiceChip(
                  label: Text(p.images[i].camera.isEmpty ? 'Camera ${i + 1}' : p.images[i].camera),
                  selected: i == _shown,
                  onSelected: (_) => setState(() => _shown = i),
                ),
            ]),
          ),
        // Scrolls when large text leaves too little room, so nothing overflows.
        Flexible(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(14),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text('✗ $reasons', style: TextStyle(color: c.ng, fontWeight: FontWeight.w700, fontSize: 17)),
              const SizedBox(height: 4),
              Text(facts, style: TextStyle(color: c.inkMuted)),
              if (p.partId != null) Text('Part ${p.partId}', style: TextStyle(color: c.inkMuted, fontSize: 13)),
              if (p.verdict != null) ...[
                const SizedBox(height: 6),
                Text(
                  'Verdict: ${switch (p.verdict) { 'confirmed' => 'NG confirmed', 'overruled' => 'good part', 'unsure' => 'unsure', _ => p.verdict! }}'
                  '${p.verdictBy != null ? ' by ${p.verdictBy}' : ''}${p.verdictAt != null && _when(p.verdictAt!).isNotEmpty ? ' · ${_when(p.verdictAt!)}' : ''}',
                  style: TextStyle(color: c.ink, fontWeight: FontWeight.w600),
                ),
                if (p.note != null) Text('Note: ${p.note}', style: TextStyle(color: c.inkMuted)),
              ],
            ]),
          ),
        ),
      ]),
    );
  }
}

class _Placeholder extends StatelessWidget {
  const _Placeholder({required this.text});
  final String text;

  @override
  Widget build(BuildContext context) {
    final c = context.pc;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          Icon(Icons.image_not_supported_outlined, size: 40, color: c.inkMuted),
          const SizedBox(height: 8),
          Text(text, textAlign: TextAlign.center, style: TextStyle(color: c.inkMuted)),
        ]),
      ),
    );
  }
}

/// Keeps a text within [max] code points, the server's count of a note's length.
class _CodePointLimit extends TextInputFormatter {
  _CodePointLimit(this.max);
  final int max;

  @override
  TextEditingValue formatEditUpdate(TextEditingValue oldValue, TextEditingValue newValue) =>
      newValue.text.runes.length <= max ? newValue : oldValue;
}
