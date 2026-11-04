import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:plant_core/plant_core.dart';

final _server = PcServer(id: 'a', product: PcProduct.vision, name: 'Unit 1', baseUrl: Uri.parse('https://plant.local'));
PcSession _asha({String token = 'tok-1'}) => PcSession(token: token, name: 'Asha', user: 'asha', pin: PcPinHash.make('2580', rounds: 50));

/// Servers with Asha signed in, a recording sender, and the sender's answer.
Future<(PcServers, List<PcPinReport>, MemoryStore, PcReportResult Function())> _setup({PcReportResult Function()? answer}) async {
  final secrets = MemoryStore();
  final s = PcServers(MemoryStore(), secrets)..device = 'Android a1b2c3d4';
  await s.add(_server, token: 'k');
  final got = <PcPinReport>[];
  var current = answer ?? () => PcReportResult.sent;
  s.reports.send = (r) async {
    got.add(r);
    return current();
  };
  await s.setSession('a', _asha());
  return (s, got, secrets, () => current());
}

void main() {
  test('unlock, wrong PIN and lockout are each reported with the sign-in\'s own token and the device', () async {
    final (s, got, _, _) = await _setup();
    await s.lock();
    expect(await s.unlock('a', 'asha', '1111'), PcUnlock.wrong);
    expect(await s.unlock('a', 'asha', '2580'), PcUnlock.open);
    await s.reports.idle();
    expect(got.map((r) => r.event), [PcPinEvent.wrong, PcPinEvent.unlock]);
    expect(got.every((r) => r.token == 'tok-1' && r.serverId == 'a' && r.device == 'Android a1b2c3d4'), isTrue);
    expect(got.map((r) => r.event.wire), ['pin_wrong', 'pin_unlock']);
    got.clear();
    await s.lock();
    for (var i = 0; i < 4; i++) {
      expect(await s.unlock('a', 'asha', '0000'), PcUnlock.wrong);
    }
    expect(await s.unlock('a', 'asha', '0000'), PcUnlock.signedOut);
    await s.reports.idle();
    expect(got.map((r) => r.event), [PcPinEvent.wrong, PcPinEvent.wrong, PcPinEvent.wrong, PcPinEvent.wrong, PcPinEvent.lockout]);
  });

  test('a lockout is reported before the sign-in is dropped (the drop callback runs after the report is sent)', () async {
    final (s, got, _, _) = await _setup();
    await s.lock();
    var reportsSeenAtDrop = -1;
    s.onDropped = (_, _) => reportsSeenAtDrop = got.length;
    for (var i = 0; i < 4; i++) {
      await s.unlock('a', 'asha', '0000');
    }
    await s.unlock('a', 'asha', '0000');
    expect(got.last.event, PcPinEvent.lockout);
    expect(reportsSeenAtDrop, 5);
  });

  test('an offline phone queues the reports, unlock still opens, and they go out in order later', () async {
    var online = false;
    final (s, got, secrets, _) = await _setup(answer: () => online ? PcReportResult.sent : PcReportResult.retry);
    await s.lock();
    expect(await s.unlock('a', 'asha', '1111'), PcUnlock.wrong);
    expect(await s.unlock('a', 'asha', '2580'), PcUnlock.open);
    await s.reports.idle();
    expect(s.session('a'), isNotNull);
    expect(s.reports.pending.map((r) => r.event), [PcPinEvent.wrong, PcPinEvent.unlock]);
    expect(secrets.values['pinreports'], contains('unlock'));
    got.clear();
    online = true;
    await s.tick();
    await s.reports.idle();
    expect(got.map((r) => r.event), [PcPinEvent.wrong, PcPinEvent.unlock]);
    expect(s.reports.pending, isEmpty);
    expect(secrets.values['pinreports'], '');
  });

  test('the queue survives a restart of the app', () async {
    final (s, _, secrets, _) = await _setup(answer: () => PcReportResult.retry);
    await s.lock();
    await s.unlock('a', 'asha', '1111');
    await s.reports.idle();
    final again = PcServers(MemoryStore(), secrets);
    await again.load();
    expect(again.reports.pending.single.event, PcPinEvent.wrong);
    expect(again.reports.pending.single.token, 'tok-1');
    expect(again.reports.pending.single.device, 'Android a1b2c3d4');
  });

  test('a send that throws is a retry, and an unlock never waits for a slow server', () async {
    final (s, _, _, _) = await _setup();
    final hold = Completer<PcReportResult>();
    s.reports.send = (_) => hold.future;
    await s.lock();
    expect(await s.unlock('a', 'asha', '2580'), PcUnlock.open); // returns while the report is still in flight
    expect(s.session('a'), isNotNull);
    hold.completeError(StateError('boom'));
    await s.reports.idle();
    expect(s.reports.pending.single.event, PcPinEvent.unlock);
  });

  test('a 401 means the sign-in has ended: it is dropped on the phone, its queued reports are discarded, another person is kept', () async {
    final (s, got, _, _) = await _setup(answer: () => PcReportResult.ended);
    await s.setSession('a', PcSession(token: 'tok-2', name: 'Ben', user: 'ben', pin: PcPinHash.make('1357', rounds: 50)));
    await s.lock();
    expect(await s.unlock('a', 'asha', '2580'), PcUnlock.open);
    await s.reports.idle();
    expect(got.single.token, 'tok-1');
    expect(s.people('a').map((p) => p.user), ['ben']);
    expect(s.session('a'), isNull);
    expect(s.endedOn('a'), isTrue);
    expect(s.reports.pending, isEmpty);
    // a new sign-in clears the note
    await s.setSession('a', _asha(token: 'tok-3'));
    expect(s.endedOn('a'), isFalse);
  });

  test('a report that is not sent because the reports have no sender yet waits in the queue', () async {
    final s = PcServers(MemoryStore(), MemoryStore());
    await s.add(_server, token: 'k');
    await s.setSession('a', _asha());
    await s.lock();
    await s.unlock('a', 'asha', '2580');
    await s.reports.idle();
    expect(s.reports.pending.single.event, PcPinEvent.unlock);
  });

  test('a lockout after the phone lost the session is still dropped on the phone alone: no server needed', () async {
    final (s, _, _, _) = await _setup(answer: () => PcReportResult.retry);
    await s.lock();
    for (var i = 0; i < 4; i++) {
      await s.unlock('a', 'asha', '0000');
    }
    expect(await s.unlock('a', 'asha', '0000'), PcUnlock.signedOut);
    expect(s.people('a'), isEmpty);
  });

  test('the queue is bounded and an unreadable one is no queue', () async {
    final store = MemoryStore()..values['pinreports'] = 'not json';
    final r = PcPinReports(store);
    await r.load();
    expect(r.pending, isEmpty);
    for (var i = 0; i < PcPinReports.maxQueued + 5; i++) {
      await r.add(PcPinReport(serverId: 'a', token: 't$i', event: PcPinEvent.wrong));
    }
    expect(r.pending.length, PcPinReports.maxQueued);
    expect(r.pending.first.token, 't5');
  });

  test('each report carries the phone time (UTC) when the PIN was typed, and keeps it across a restart', () async {
    final secrets = MemoryStore();
    final typed = DateTime(2026, 11, 4, 10, 30, 15);
    final s = PcServers(MemoryStore(), secrets, now: () => typed);
    await s.add(_server, token: 'k');
    await s.setSession('a', _asha());
    await s.lock();
    expect(await s.unlock('a', 'asha', '1111'), PcUnlock.wrong);
    await s.reports.idle();
    expect(s.reports.pending.single.at, typed.toUtc());
    expect(s.reports.pending.single.at!.isUtc, isTrue);
    expect(s.reports.pending.single.atWire, endsWith('Z'));
    final again = PcServers(MemoryStore(), secrets);
    await again.load();
    expect(again.reports.pending.single.at, typed.toUtc());
  });

  test('a phone time the server refuses (badTime) is sent again at once without it, in order, and recorded', () async {
    final r = PcPinReports(MemoryStore());
    final got = <PcPinReport>[];
    r.send = (x) async {
      got.add(x);
      return x.at != null ? PcReportResult.badTime : PcReportResult.sent;
    };
    final at = DateTime.utc(2026, 11, 4, 9);
    await r.add(PcPinReport(serverId: 'a', token: 't', event: PcPinEvent.wrong, device: 'd', at: at));
    await r.add(PcPinReport(serverId: 'a', token: 't', event: PcPinEvent.lockout, at: at));
    expect(got.map((x) => (x.event, x.at)), [
      (PcPinEvent.wrong, at),
      (PcPinEvent.wrong, null),
      (PcPinEvent.lockout, at),
      (PcPinEvent.lockout, null),
    ]);
    expect(got[1].device, 'd');
    expect(got[1].token, 't');
    expect(r.pending, isEmpty);
  });

  test('a report is never dropped for its phone time: offline after a badTime, it waits without the time', () async {
    final store = MemoryStore();
    final r = PcPinReports(store);
    var online = true;
    r.send = (x) async => !online ? PcReportResult.retry : (x.at != null ? PcReportResult.badTime : PcReportResult.sent);
    var first = true;
    final inner = r.send!;
    r.send = (x) async {
      final res = await inner(x);
      if (first) {
        first = false;
        online = false; // the network goes right after the refusal
      }
      return res;
    };
    await r.add(PcPinReport(serverId: 'a', token: 't', event: PcPinEvent.lockout, at: DateTime.utc(2026, 11, 4, 9)));
    expect(r.pending.single.event, PcPinEvent.lockout);
    expect(r.pending.single.at, isNull);
    expect(store.values['pinreports'], isNot(contains('"at"')));
    final again = PcPinReports(store);
    await again.load();
    expect(again.pending.single.event, PcPinEvent.lockout);
    online = true;
    again.send = r.send;
    await again.flush();
    expect(again.pending, isEmpty);
  });

  test('a badTime answer to a report with no time is not looped on: it counts as taken', () async {
    final r = PcPinReports(MemoryStore());
    var calls = 0;
    r.send = (_) async {
      calls++;
      return PcReportResult.badTime;
    };
    await r.add(const PcPinReport(serverId: 'a', token: 't', event: PcPinEvent.unlock));
    expect(calls, 1);
    expect(r.pending, isEmpty);
  });

  test('a report queued by an older app without a time is read and sent without one', () async {
    final store = MemoryStore()..values['pinreports'] = '[{"server":"a","token":"t","event":"unlock"}]';
    final r = PcPinReports(store);
    await r.load();
    expect(r.pending.single.at, isNull);
    expect(r.pending.single.toJson().containsKey('at'), isFalse);
  });

  test('the device name is kept: the same phone is the same in every entry', () async {
    final store = MemoryStore();
    final a = await pcDeviceName(store, 'Android');
    expect(a, matches(RegExp(r'^Android [0-9a-f]{8}$')));
    expect(await pcDeviceName(store, 'Android'), a);
  });
}
