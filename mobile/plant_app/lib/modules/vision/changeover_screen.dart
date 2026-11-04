import 'package:flutter/material.dart';
import 'package:plant_core/plant_core.dart';

import 'account_screen.dart';
import 'api_auth.dart';
import 'changeover_api.dart';
import 'line_look.dart';
import 'vision_api.dart';

/// The changeovers waiting for a second person's check, newest first. A
/// supervisor opens one, sees the code, who changed it and when, every value
/// with what changed since the last accepted record, and accepts or refuses
/// it (a refusal needs a note). A decision is sent at once and never queued:
/// without a connection it is not saved. [plant] and [line] limit the list
/// to one line.
class ChangeoverScreen extends StatefulWidget {
  const ChangeoverScreen({super.key, required this.api, required this.feedback, this.plant, this.line});

  final ChangeoverApi api;
  final PcFeedback feedback;
  final String? plant, line;

  @override
  State<ChangeoverScreen> createState() => _ChangeoverScreenState();
}

class _ChangeoverScreenState extends State<ChangeoverScreen> {
  final _items = <ChangeoverItem>[];
  bool _loaded = false, _loading = false;
  String? _error;
  ({PcStatus status, String text})? _notice;
  DateTime? _at;
  late bool _wasSignedIn = widget.api.auth.signedIn;

  @override
  void initState() {
    super.initState();
    widget.api.auth.addListener(_authChanged);
    _load();
  }

  @override
  void dispose() {
    widget.api.auth.removeListener(_authChanged);
    super.dispose();
  }

  // A sign-in changes what the server shows this person: read the list again.
  void _authChanged() {
    final now = widget.api.auth.signedIn;
    if (now == _wasSignedIn) return;
    _wasSignedIn = now;
    if (now) _load();
  }

  Future<void> _load() async {
    if (_loading) return;
    setState(() => _loading = true);
    try {
      final items = await widget.api.list(plant: widget.plant, line: widget.line);
      if (!mounted) return;
      setState(() {
        _items
          ..clear()
          ..addAll(items);
        _loaded = true;
        _at = DateTime.now();
        _error = null;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = e is VisionApiException ? e.message : 'Cannot reach the plant server.');
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  /// Sends one decision. Returns the reason to show beside the buttons when
  /// the person should stay on the request, null when it is settled (decided,
  /// or no longer open) and the request should close.
  Future<String?> _decide(ChangeoverItem item, ChangeoverDecision d, String note) async {
    widget.feedback.tap();
    try {
      await widget.api.decide(item.id, d, note: note);
      widget.feedback.confirm();
      if (mounted) {
        setState(() => _notice = (
              status: d == ChangeoverDecision.accept ? PcStatus.ok : PcStatus.warn,
              text: '${d == ChangeoverDecision.accept ? 'Accepted' : 'Refused'}: ${item.station} · ${item.shortCode}.',
            ));
      }
    } on ChangeoverApiException catch (e) {
      if (e.code == 409 || e.code == 404) {
        // Somebody else was first, or the line asked again: this one is settled, show what is open now.
        if (mounted) setState(() => _notice = (status: PcStatus.warn, text: e.message));
      } else {
        return e.code == 0 ? '${e.message} The decision was not saved.' : e.message;
      }
    } catch (_) {
      return 'The decision was not saved.';
    }
    if (mounted) _load();
    return null;
  }

  @override
  Widget build(BuildContext context) => ListenableBuilder(listenable: widget.api.auth, builder: (context, _) => _screen(context));

  Widget _screen(BuildContext context) {
    final c = context.pc;
    final title = widget.line != null && widget.line!.isNotEmpty ? 'Changeover · ${widget.line}' : 'Changeover';
    return Scaffold(
      appBar: AppBar(
        title: Text(title),
        actions: [
          IconButton(
            tooltip: widget.api.auth.signedIn ? 'Account: signed in as ${widget.api.auth.session!.name}' : 'Sign in',
            icon: Icon(widget.api.auth.signedIn ? Icons.account_circle : Icons.account_circle_outlined),
            onPressed: () => openAccount(context, widget.api.auth, widget.feedback),
          ),
          IconButton(tooltip: 'Reload the changeovers', icon: const Icon(Icons.refresh), onPressed: _loading ? null : _load),
        ],
      ),
      body: SafeArea(
        child: Column(children: [
          if (_error != null) _Note(status: PcStatus.warn, text: _at == null ? _error! : '$_error Showing the list as of ${hhmm(_at!)}.'),
          if (widget.api.auth.ended) const _Note(status: PcStatus.warn, text: ApiAuth.endedMessage),
          if (_notice != null && _notice!.text != ApiAuth.endedMessage) _Note(status: _notice!.status, text: _notice!.text),
          Expanded(child: _body(c)),
        ]),
      ),
    );
  }

  Widget _body(PcColors c) {
    if (_items.isEmpty) {
      if (!_loaded) return _error != null ? const SizedBox.shrink() : const Center(child: CircularProgressIndicator());
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(32),
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            Text('✓', style: TextStyle(color: c.ok, fontSize: 48, fontWeight: FontWeight.w700)),
            const SizedBox(height: 8),
            Text('No changeover is waiting for a check.', textAlign: TextAlign.center, style: TextStyle(color: c.ink, fontSize: 18, fontWeight: FontWeight.w600)),
          ]),
        ),
      );
    }
    return RefreshIndicator(
      onRefresh: _load,
      child: ListView(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.all(16),
        children: [
          Padding(padding: const EdgeInsets.only(bottom: 8), child: Text('${_items.length} waiting', style: TextStyle(color: c.inkMuted, fontSize: 13))),
          for (final i in _items) _ItemCard(item: i, onTap: () => _open(i)),
        ],
      ),
    );
  }

  Future<void> _open(ChangeoverItem item) {
    widget.feedback.tap();
    return Navigator.of(context).push(MaterialPageRoute<void>(
      builder: (_) => ChangeoverDetail(item: item, api: widget.api, feedback: widget.feedback, onDecide: (d, note) => _decide(item, d, note)),
    ));
  }
}

/// "14:05", or "29-10 14:05" for another day; empty for no time.
String _stamp(int ms) {
  if (ms <= 0) return '';
  final t = DateTime.fromMillisecondsSinceEpoch(ms), n = DateTime.now();
  final today = t.year == n.year && t.month == n.month && t.day == n.day;
  return today ? hhmm(t) : '${t.day.toString().padLeft(2, '0')}-${t.month.toString().padLeft(2, '0')} ${hhmm(t)}';
}

class _ItemCard extends StatelessWidget {
  const _ItemCard({required this.item, required this.onTap});
  final ChangeoverItem item;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final c = context.pc;
    final where = [if (item.line.isNotEmpty) item.line, item.station].where((s) => s.isNotEmpty).join(' · ');
    final facts = [
      if (item.record.job.isNotEmpty) item.record.job,
      if (item.operator.isNotEmpty) 'by ${item.operator}',
      if (_stamp(item.changedAt).isNotEmpty) _stamp(item.changedAt),
    ].join(' · ');
    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: Card(
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          key: ValueKey('changeover-${item.id}'),
          onTap: onTap,
          child: Padding(
            padding: const EdgeInsets.all(14),
            child: Row(children: [
              Expanded(
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text(where, style: TextStyle(fontSize: 18, fontWeight: FontWeight.w700, color: c.ink)),
                  const SizedBox(height: 4),
                  Text(facts, style: TextStyle(color: c.inkMuted)),
                  Text('Code ${item.shortCode}', style: TextStyle(color: c.inkMuted, fontSize: 13)),
                  if (item.changes.isNotEmpty)
                    Padding(
                      padding: const EdgeInsets.only(top: 6),
                      child: Text('${PcStatus.warn.shape} ${item.changes.length} change${item.changes.length == 1 ? '' : 's'}', style: TextStyle(color: c.warn, fontWeight: FontWeight.w600)),
                    ),
                ]),
              ),
              Icon(Icons.chevron_right, color: c.inkMuted),
            ]),
          ),
        ),
      ),
    );
  }
}

/// One request in full. [onDecide] sends the decision and gives back the
/// reason to show here when the person should stay, or null when it is settled
/// and this page closes. Accept and Refuse are only offered to a signed-in
/// person; without one the request can be read but not answered.
class ChangeoverDetail extends StatefulWidget {
  const ChangeoverDetail({super.key, required this.item, required this.api, required this.feedback, required this.onDecide});

  final ChangeoverItem item;
  final ChangeoverApi api;
  final PcFeedback feedback;
  final Future<String?> Function(ChangeoverDecision decision, String note) onDecide;

  @override
  State<ChangeoverDetail> createState() => _ChangeoverDetailState();
}

class _ChangeoverDetailState extends State<ChangeoverDetail> {
  final _note = TextEditingController();
  bool _refusing = false, _sending = false;
  String? _problem;

  @override
  void dispose() {
    _note.dispose();
    super.dispose();
  }

  Future<void> _send(ChangeoverDecision d) async {
    if (_sending) return;
    final note = _note.text.trim();
    if (d == ChangeoverDecision.refuse && note.isEmpty) {
      setState(() => _problem = ChangeoverApi.refusalNeedsNote);
      return;
    }
    setState(() {
      _sending = true;
      _problem = null;
    });
    final problem = await widget.onDecide(d, note);
    if (!mounted) return;
    if (problem == null) {
      Navigator.of(context).pop();
    } else {
      setState(() {
        _sending = false;
        _problem = problem;
      });
    }
  }

  @override
  Widget build(BuildContext context) => ListenableBuilder(listenable: widget.api.auth, builder: (context, _) => _page(context));

  Widget _page(BuildContext context) {
    final c = context.pc;
    final item = widget.item;
    final r = item.record;
    final shown = <String>{};
    ChangeoverChange? changeOf(String key) {
      shown.add(key);
      return item.changes.where((x) => x.key == key).firstOrNull;
    }

    final rows = <Widget>[
      _Row(label: 'Job', value: r.job, change: changeOf('job')),
      _Row(label: 'Job version', value: '${r.jobVersion}', change: changeOf('job_version')),
      if (r.variant.isNotEmpty || item.changes.any((x) => x.key == 'variant')) _Row(label: 'Variant', value: r.variant, change: changeOf('variant')),
      for (final f in r.batch) _Row(label: f.label, value: f.value, change: changeOf(f.key)),
    ];
    // Changes that no row above shows, such as a field that is gone now.
    final others = [for (final x in item.changes) if (!shown.contains(x.key)) x];
    final who = [
      if (item.operator.isNotEmpty) 'Changed by ${item.operator}' else 'Nobody was signed in at the station',
      if (_stamp(item.changedAt).isNotEmpty) _stamp(item.changedAt),
    ].join(' · ');
    final signedIn = widget.api.auth.signedIn;
    return Scaffold(
      appBar: AppBar(title: Text([if (item.line.isNotEmpty) item.line, item.station].where((s) => s.isNotEmpty).join(' · '))),
      body: SafeArea(
        child: Column(children: [
          Expanded(
            child: ListView(padding: const EdgeInsets.all(16), children: [
              Text('Code ${item.shortCode}', key: const ValueKey('changeover-code'), style: TextStyle(color: c.ink, fontSize: 26, fontWeight: FontWeight.w800, letterSpacing: 1)),
              const SizedBox(height: 2),
              Text('The same code shows on the line PC. Accept only what you see there.', style: TextStyle(color: c.inkMuted, fontSize: 13)),
              const SizedBox(height: 8),
              Text(who, style: TextStyle(color: c.ink)),
              if (item.previous == null)
                Padding(
                  padding: const EdgeInsets.only(top: 4),
                  child: Text('Nothing was accepted before on this station, so there is nothing to compare with.', style: TextStyle(color: c.inkMuted)),
                )
              else if (item.changes.isEmpty)
                Padding(padding: const EdgeInsets.only(top: 4), child: Text('Nothing changed since the last accepted record.', style: TextStyle(color: c.inkMuted))),
              const SizedBox(height: 12),
              ...rows,
              if (others.isNotEmpty) ...[
                const SizedBox(height: 8),
                Text('Also changed', style: TextStyle(color: c.ink, fontWeight: FontWeight.w700)),
                for (final x in others) _Row(label: x.label, value: '', change: x),
              ],
              if (!item.pending)
                Padding(
                  padding: const EdgeInsets.only(top: 12),
                  child: Text(
                    '${item.state[0].toUpperCase()}${item.state.substring(1)}${item.decidedName != null ? ' by ${item.decidedName}' : ''}${item.note != null ? ': ${item.note}' : ''}',
                    style: TextStyle(color: c.inkMuted),
                  ),
                ),
            ]),
          ),
          if (_problem != null) _Note(status: PcStatus.ng, text: _problem!),
          if (widget.api.auth.ended) const _Note(status: PcStatus.warn, text: ApiAuth.endedMessage),
          if (item.pending) (signedIn ? _buttons(c) : _signInPrompt(c)),
        ]),
      ),
    );
  }

  /// Without a person's sign-in the request can be looked at but not answered.
  Widget _signInPrompt(PcColors c) => Padding(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 12),
        child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
          Text('Sign in with your own account to accept or refuse.', textAlign: TextAlign.center, style: TextStyle(color: c.ink, fontWeight: FontWeight.w600)),
          const SizedBox(height: 8),
          FilledButton.icon(
            key: const ValueKey('changeover-sign-in'),
            icon: const Icon(Icons.login),
            label: const Text('Sign in'),
            onPressed: () => openAccount(context, widget.api.auth, widget.feedback),
          ),
        ]),
      );

  Widget _buttons(PcColors c) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 4, 16, 12),
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        if (_refusing) ...[
          TextField(
            key: const ValueKey('changeover-note'),
            controller: _note,
            enabled: !_sending,
            maxLines: 3,
            minLines: 2,
            textInputAction: TextInputAction.newline,
            decoration: const InputDecoration(labelText: 'Why is it refused? (required)', border: OutlineInputBorder()),
          ),
          const SizedBox(height: 8),
        ],
        Row(children: [
          Expanded(
            child: OutlinedButton.icon(
              key: ValueKey(_refusing ? 'changeover-refuse-send' : 'changeover-refuse'),
              onPressed: _sending ? null : () => _refusing ? _send(ChangeoverDecision.refuse) : setState(() => _refusing = true),
              icon: const Icon(Icons.close),
              label: Text(_refusing ? 'Send refusal' : 'Refuse'),
              style: OutlinedButton.styleFrom(
                minimumSize: const Size(48, 56),
                foregroundColor: PcStatus.ng.color(c),
                backgroundColor: PcStatus.ng.background(c),
                side: BorderSide(color: PcStatus.ng.color(c), width: 2),
              ),
            ),
          ),
          if (!_refusing) ...[
            const SizedBox(width: 8),
            Expanded(
              child: FilledButton.icon(
                key: const ValueKey('changeover-accept'),
                onPressed: _sending ? null : () => _send(ChangeoverDecision.accept),
                icon: const Icon(Icons.check),
                label: const Text('Accept'),
                style: FilledButton.styleFrom(minimumSize: const Size(48, 56), backgroundColor: PcStatus.ok.color(c), foregroundColor: c.card),
              ),
            ),
          ] else ...[
            const SizedBox(width: 8),
            TextButton(key: const ValueKey('changeover-cancel'), onPressed: _sending ? null : () => setState(() => _refusing = false), child: const Text('Back')),
          ],
        ]),
      ]),
    );
  }
}

/// One value; a changed one is highlighted and shows old → new (shape and
/// words as well as colour).
class _Row extends StatelessWidget {
  const _Row({required this.label, required this.value, this.change});
  final String label, value;
  final ChangeoverChange? change;

  @override
  Widget build(BuildContext context) {
    final c = context.pc;
    final x = change;
    String shown(String? v) => v == null || v.isEmpty ? '—' : v;
    final body = x == null
        ? Text(shown(value), style: TextStyle(color: c.ink, fontSize: 17, fontWeight: FontWeight.w600))
        : Wrap(crossAxisAlignment: WrapCrossAlignment.center, spacing: 8, children: [
            Text(shown(x.old), style: TextStyle(color: c.inkMuted, fontSize: 17, decoration: TextDecoration.lineThrough)),
            Icon(Icons.arrow_forward, size: 18, color: c.warn),
            Text(shown(x.now), style: TextStyle(color: c.ink, fontSize: 17, fontWeight: FontWeight.w800)),
          ]);
    return Semantics(
      container: true,
      label: x == null ? '$label: ${shown(value)}' : '$label changed from ${shown(x.old)} to ${shown(x.now)}',
      child: ExcludeSemantics(
        child: Container(
          width: double.infinity,
          margin: const EdgeInsets.only(bottom: 6),
          padding: const EdgeInsets.all(10),
          decoration: BoxDecoration(
            color: x == null ? null : PcStatus.warn.background(c),
            border: Border.all(color: x == null ? c.lineStrong : PcStatus.warn.color(c), width: x == null ? 1 : 2),
            borderRadius: BorderRadius.circular(10),
          ),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(x == null ? label : '${PcStatus.warn.shape} $label · changed', style: TextStyle(color: x == null ? c.inkMuted : c.warn, fontSize: 13, fontWeight: FontWeight.w600)),
            const SizedBox(height: 2),
            body,
          ]),
        ),
      ),
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
