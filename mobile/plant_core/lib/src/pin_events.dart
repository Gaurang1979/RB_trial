import 'dart:async';
import 'dart:convert';
import 'dart:math';

import 'look.dart';

/// What the phone tells the plant server about a PIN (requirements PT11-08a;
/// PLANT-DASHBOARD-AND-MOBILE 6): the names are the server's own.
enum PcPinEvent {
  unlock('pin_unlock'),
  wrong('pin_wrong'),
  lockout('pin_lockout');

  const PcPinEvent(this.wire);
  final String wire;
}

/// How the server took one report: [sent] (done), [ended] (a 401: that
/// sign-in has already ended on the server), [retry] (could not be sent
/// now: offline, a server fault; try again later) or [badTime] (a 422 to a
/// report with a phone time: the phone clock is too far off, PT11-08b; the
/// report goes again at once without it, never dropped). Any other report
/// the server refuses as bad is [sent]: sending it again would not change
/// the answer.
enum PcReportResult { sent, ended, retry, badTime }

/// One report waiting to be sent, with the sign-in's own token and [at], the
/// phone's time (UTC) when the PIN was typed (PT11-08c).
class PcPinReport {
  const PcPinReport({required this.serverId, required this.token, required this.event, this.device, this.at});

  final String serverId, token;
  final PcPinEvent event;
  final String? device;
  final DateTime? at;

  /// The same report without its phone time, for a server that refused it.
  PcPinReport withoutAt() => PcPinReport(serverId: serverId, token: token, event: event, device: device);

  /// [at] as the server takes it: ISO 8601 in UTC with `Z`.
  String? get atWire => at?.toUtc().toIso8601String();

  Map<String, Object> toJson() => {'server': serverId, 'token': token, 'event': event.name, 'device': ?device, 'at': ?atWire};

  static PcPinReport? fromJson(Object? j) {
    if (j is! Map) return null;
    final server = j['server'], token = j['token'];
    final event = PcPinEvent.values.where((e) => e.name == j['event']).firstOrNull;
    if (server is! String || token is! String || token.isEmpty || event == null) return null;
    // A report queued by an older app has no time: it is sent without one.
    final at = j['at'] is String ? DateTime.tryParse(j['at'] as String)?.toUtc() : null;
    return PcPinReport(serverId: server, token: token, event: event, device: j['device'] is String ? j['device'] as String : null, at: at);
  }
}

/// The PIN reports of this phone, sent in order. A report that cannot be
/// sent (no network) waits in a queue kept on the phone (it holds sign-in
/// tokens, so it lives in the secure store) and goes out later; sending
/// never delays an unlock or a lockout, which are decided on the phone
/// alone. A queued report whose sign-in has ended is dropped; one whose
/// phone time the server refuses is sent again without it, never dropped.
class PcPinReports {
  PcPinReports(this._store);

  static const _key = 'pinreports', maxQueued = 200;

  final KeyValueStore _store;
  final _queue = <PcPinReport>[];
  Future<void>? _running;

  /// Sends one report; set by the app. Without it reports only wait.
  Future<PcReportResult> Function(PcPinReport report)? send;

  /// Called when the server answered 401 for [token]: that sign-in is over.
  void Function(String serverId, String token)? onEnded;

  /// Reports not sent yet.
  List<PcPinReport> get pending => List.unmodifiable(_queue);

  /// Reads the queue kept by an earlier run. Unreadable: no queue.
  Future<void> load() async {
    try {
      final raw = await _store.read(_key);
      final j = raw == null || raw.isEmpty ? null : jsonDecode(raw);
      if (j is List) {
        _queue
          ..clear()
          ..addAll(j.map(PcPinReport.fromJson).whereType<PcPinReport>());
      }
    } catch (_) {}
  }

  /// Queues a report and starts sending. Returns when the queue has been
  /// tried; callers do not need to wait for it.
  Future<void> add(PcPinReport r) {
    _queue.add(r);
    if (_queue.length > maxQueued) _queue.removeRange(0, _queue.length - maxQueued);
    return flush();
  }

  /// Tries the queue in order; stops at the first report that cannot be sent.
  Future<void> flush() {
    // One run at a time; a report added meanwhile is picked up by that run.
    return _running ??= _run().whenComplete(() => _running = null);
  }

  /// Completes when the run in progress (if any) is over.
  Future<void> idle() => _running ?? Future.value();

  Future<void> _run() async {
    final send = this.send;
    if (send != null) {
      while (_queue.isNotEmpty) {
        final r = _queue.first;
        PcReportResult res;
        try {
          res = await send(r);
        } catch (_) {
          res = PcReportResult.retry;
        }
        if (res == PcReportResult.retry) break;
        if (res == PcReportResult.badTime && r.at != null) {
          // The phone clock is off: the event still counts, by its arrival.
          final i = _queue.indexOf(r);
          if (i >= 0) _queue[i] = r.withoutAt();
          continue;
        }
        _queue.remove(r);
        if (res == PcReportResult.ended) {
          _queue.removeWhere((x) => x.token == r.token);
          try {
            onEnded?.call(r.serverId, r.token);
          } catch (_) {}
        }
      }
    }
    await _save();
  }

  Future<void> _save() async {
    try {
      await _store.write(_key, _queue.isEmpty ? '' : jsonEncode(_queue.map((r) => r.toJson()).toList()));
    } catch (_) {
      // A queue that cannot be kept is still sent from memory.
    }
  }
}

/// A name for this phone for the audit trail: the system and a short id made
/// once and kept, so the same phone is the same in every entry ("Android
/// a1b2c3d4"). The id is random, not a hardware number.
Future<String> pcDeviceName(KeyValueStore store, String system, {Random? random}) async {
  const key = 'device.id';
  String? id;
  try {
    id = await store.read(key);
  } catch (_) {}
  if (id == null || !RegExp(r'^[0-9a-f]{8}$').hasMatch(id)) {
    final r = random ?? Random.secure();
    id = List.generate(8, (_) => r.nextInt(16).toRadixString(16)).join();
    try {
      await store.write(key, id);
    } catch (_) {}
  }
  return '$system $id';
}
