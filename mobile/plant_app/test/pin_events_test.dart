import 'dart:convert';

import 'package:flutter/widgets.dart' show SizedBox;
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:plant_app/modules/vision/api_auth.dart';
import 'package:plant_app/src/app.dart';
import 'package:plant_core/plant_core.dart';

final _base = Uri.parse('https://plant.local/plant');
final _server = PcServer(id: 'a', product: PcProduct.vision, name: 'Unit 1', baseUrl: _base);
PcSession _asha() => PcSession(token: 'tok-1', name: 'Asha', user: 'asha', pin: PcPinHash.make('2580', rounds: 50));

Future<PcServers> _servers() async {
  final s = PcServers(MemoryStore(), MemoryStore())..device = 'Android a1b2c3d4';
  await s.add(_server, token: 'k');
  await s.setSession('a', _asha());
  await s.lock();
  return s;
}

void main() {
  testWidgets('each PIN event is posted to the server with the sign-in\'s own token and the device; a lockout goes before the logout', (t) async {
    final calls = <String>[];
    final bodies = <Map>[];
    final client = MockClient((r) async {
      calls.add('${r.url.path} ${r.headers['Authorization']}');
      if (r.url.path.endsWith('pin-events')) bodies.add(jsonDecode(r.body) as Map);
      return http.Response(jsonEncode({'ok': true, 'session': 'open', 'wrong_in_a_row': 0}), 200);
    });
    final servers = await _servers();
    final look = PcLook();
    await t.pumpWidget(PlantApp(look: look, servers: servers, feedback: PcFeedback(look), client: client));
    await t.runAsync(() async {
      await servers.unlock('a', 'asha', '1111');
      await servers.unlock('a', 'asha', '2580');
      await servers.lock();
      for (var i = 0; i < 5; i++) {
        await servers.unlock('a', 'asha', '0000');
      }
      await servers.reports.idle();
      await Future<void>.delayed(const Duration(milliseconds: 50));
    });
    expect(bodies.map((b) => b['event']), ['pin_wrong', 'pin_unlock', 'pin_wrong', 'pin_wrong', 'pin_wrong', 'pin_wrong', 'pin_lockout']);
    expect(bodies.every((b) => b['device'] == 'Android a1b2c3d4'), isTrue);
    expect(calls.first, '/plant/auth/pin-events Bearer tok-1');
    expect(calls.last, '/plant/auth/logout Bearer tok-1');
    expect(calls.indexOf('/plant/auth/logout Bearer tok-1'), calls.length - 1);
    await t.pumpWidget(const SizedBox());
  });

  testWidgets('an unreachable server queues the report; a 401 drops the sign-in and says it ended; a 500 is tried again', (t) async {
    var status = 0; // 0: no network
    final client = MockClient((r) async {
      if (status == 0) throw http.ClientException('down');
      return http.Response('{}', status);
    });
    final servers = await _servers();
    final look = PcLook();
    await t.pumpWidget(PlantApp(look: look, servers: servers, feedback: PcFeedback(look), client: client));
    final auth = ApiAuth(client, _base, key: 'k', servers: servers, serverId: 'a');
    await t.runAsync(() async {
      expect(await auth.unlock('asha', '2580'), PcUnlock.open);
      await servers.reports.idle();
      expect(auth.signedIn, isTrue); // unlock did not wait for, or depend on, the server
      expect(servers.reports.pending.single.event, PcPinEvent.unlock);
      status = 503;
      await servers.reports.flush();
      expect(servers.reports.pending, hasLength(1));
      status = 401;
      await servers.reports.flush();
    });
    expect(servers.reports.pending, isEmpty);
    expect(auth.signedIn, isFalse);
    expect(auth.ended, isTrue);
    expect(servers.people('a'), isEmpty);
    await t.pumpWidget(const SizedBox());
  });

  testWidgets('each report sends the phone time as at; a 422 to it is resent at once without at, and the lockout still ends the sign-in', (t) async {
    final bodies = <Map>[];
    final calls = <String>[];
    final client = MockClient((r) async {
      calls.add(r.url.path);
      if (!r.url.path.endsWith('pin-events')) return http.Response('{}', 200);
      final b = jsonDecode(r.body) as Map;
      bodies.add(b);
      if (b.containsKey('at')) return http.Response(jsonEncode({'detail': 'at is more than 300 s ahead of the server clock'}), 422);
      return http.Response(jsonEncode({'ok': true}), 200);
    });
    final servers = await _servers();
    final look = PcLook();
    await t.pumpWidget(PlantApp(look: look, servers: servers, feedback: PcFeedback(look), client: client));
    await t.runAsync(() async {
      for (var i = 0; i < 5; i++) {
        await servers.unlock('a', 'asha', '0000');
      }
      await servers.reports.idle();
      await Future<void>.delayed(const Duration(milliseconds: 50));
    });
    // Each event: first with at (ISO 8601 UTC), then the same without it.
    expect(bodies, hasLength(10));
    for (var i = 0; i < 10; i += 2) {
      expect(bodies[i]['at'], matches(RegExp(r'^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?Z$')));
      expect(DateTime.parse(bodies[i]['at'] as String).isUtc, isTrue);
      expect(bodies[i + 1].containsKey('at'), isFalse);
      expect(bodies[i + 1]['event'], bodies[i]['event']);
      expect(bodies[i + 1]['device'], 'Android a1b2c3d4');
    }
    expect(bodies.last['event'], 'pin_lockout');
    expect(servers.reports.pending, isEmpty);
    expect(calls.last, '/plant/auth/logout');
    await t.pumpWidget(const SizedBox());
  });

  testWidgets('a 422 while the server is then unreachable keeps the report queued without at, never dropped', (t) async {
    var down = false;
    final client = MockClient((r) async {
      if (down) throw http.ClientException('down');
      down = true; // the network goes right after the refusal
      return http.Response(jsonEncode({'detail': 'at is more than 300 s before this sign-in began'}), 422);
    });
    final servers = await _servers();
    final look = PcLook();
    await t.pumpWidget(PlantApp(look: look, servers: servers, feedback: PcFeedback(look), client: client));
    await t.runAsync(() async {
      await servers.unlock('a', 'asha', '2580');
      await servers.reports.idle();
    });
    expect(servers.reports.pending.single.event, PcPinEvent.unlock);
    expect(servers.reports.pending.single.at, isNull);
    await t.pumpWidget(const SizedBox());
  });

  test('the sign-in sends the phone\'s name as device', () async {
    late Map body;
    final servers = PcServers(MemoryStore(), MemoryStore())..device = 'Android a1b2c3d4';
    await servers.add(_server, token: 'k');
    final auth = ApiAuth(
        MockClient((r) async {
          body = jsonDecode(r.body) as Map;
          return http.Response(jsonEncode({'token': 'tok-9', 'user': {'name': 'Asha', 'username': 'asha'}}), 200);
        }),
        _base,
        key: 'k',
        servers: servers,
        serverId: 'a');
    await auth.login('asha', 'pw', pin: '2580');
    expect(body, {'username': 'asha', 'password': 'pw', 'device': 'Android a1b2c3d4'});
    expect(auth.ended, isFalse);
  });
}
