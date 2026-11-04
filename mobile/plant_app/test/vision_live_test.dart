import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:plant_app/modules/vision/api_auth.dart';
import 'package:plant_app/modules/vision/floor_screen.dart';
import 'package:plant_app/modules/vision/vision_api.dart';
import 'package:plant_core/plant_core.dart';

import 'app_test.dart' show Recorder, line;

// The vision floor on the plant server's WebSocket, with polling as its
// fallback (requirements 17.3, LV-05).

/// A socket the test drives: [push] a floor, [end] it with a close code.
class FakeSocket implements PcSocket {
  final _c = StreamController<dynamic>();
  int? _code;
  bool closed = false;
  @override
  Stream<dynamic> get messages => _c.stream;
  @override
  int? get closeCode => _code;
  @override
  Future<void> close() async => closed = true;
  void push(List<Map<String, Object?>> lines) => _c.add(jsonEncode({'type': 'floor', 'lines': lines}));
  void end([int? code]) {
    _code = code;
    _c.close();
  }
}

class FakeServer {
  final opened = <Uri>[];
  final sockets = <FakeSocket>[];
  bool down = false;
  Future<PcSocket> connect(Uri url) async {
    opened.add(url);
    if (down) throw StateError('no route');
    final s = FakeSocket();
    sockets.add(s);
    return s;
  }

  FakeSocket get last => sockets.last;
}

final _base = Uri.parse('https://plant.local/plant');

/// A socket event reaches the screen in a microtask after a frame: the next frame shows it.
Future<void> settle(WidgetTester t) async {
  await t.pump();
  await t.pump();
}

void main() {
  setUp(() {
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (_) async => null);
  });

  group('socket address', () {
    test('wss below the server\'s path, with the session while one is open, else the key', () {
      final c = MockClient((_) async => http.Response('[]', 200));
      final key = VisionApi(c, _base, 'k1');
      expect(key.liveUrl().toString(), 'wss://plant.local/plant/vision/ws?lang=en&api_key=k1');
      final signed = VisionApi(c, _base, 'k1', auth: ApiAuth(c, _base, key: 'k1', session: const PcSession(token: 't 1', name: 'Asha')));
      final u = signed.liveUrl();
      expect(u.queryParameters, {'lang': 'en', 'token': 't 1'});
      expect(VisionApi.sentWith(u), {'Authorization': 'Bearer t 1'});
      expect(VisionApi.sentWith(key.liveUrl()), {'X-Api-Key': 'k1'});
      expect(VisionApi(c, Uri.parse('http://10.0.0.5:8000'), null).liveUrl().toString(), 'ws://10.0.0.5:8000/vision/ws?lang=en');
    });

    test('only floor messages are read', () {
      expect(VisionApi.pushedFloor(jsonEncode({'type': 'floor', 'lines': [line('Line 1', 'running')]}))!.single.line, 'Line 1');
      expect(VisionApi.pushedFloor(jsonEncode({'type': 'other', 'lines': []})), isNull);
      expect(VisionApi.pushedFloor('not json'), isNull);
      expect(VisionApi.pushedFloor(jsonEncode([1, 2])), isNull);
    });
  });

  group('live floor', () {
    late int reads;
    late List<Map<String, Object?>> answer;

    Future<(FakeServer, Recorder)> open(WidgetTester t, {ApiAuth Function(http.Client c)? auth, FakeServer? server, http.Client? client}) async {
      reads = 0;
      final c = client ??
          MockClient((r) async {
            reads++;
            return http.Response(jsonEncode([answer.first]), 200);
          });
      final s = server ?? FakeServer();
      final rec = Recorder();
      await t.pumpWidget(MaterialApp(
        theme: pcTheme(Brightness.dark),
        home: FloorScreen(
          server: PcServer(id: 'a', product: PcProduct.vision, name: 'Unit 1', baseUrl: _base),
          api: VisionApi(c, _base, 'key', auth: auth?.call(c)),
          feedback: PcFeedback(PcLook(), player: rec),
          live: s.connect,
        ),
      ));
      await t.pump();
      return (s, rec);
    }

    Future<void> stop(WidgetTester t) async {
      await t.pumpWidget(const SizedBox());
      await t.pump(const Duration(minutes: 2));
    }

    testWidgets('a push shows at once and sounds a new alarm; no reads while the socket sends', (t) async {
      answer = [line('Line 1', 'running')];
      final (server, rec) = await open(t);
      expect(reads, 1);
      expect(server.opened.single.queryParameters['api_key'], 'key');
      server.last.push([line('Line 1', 'running')]);
      await settle(t);
      server.last.push([line('Line 1', 'fault', alarms: [{'station': 'S1', 'code': 'plc_lost', 'priority': 'high', 'since': 2}])]);
      await settle(t);
      expect(find.text('! 1 open alarm'), findsOneWidget);
      expect(rec.played, [PcSound.critical]);
      await t.pump(const Duration(seconds: 30));
      expect(reads, 1, reason: 'the open socket replaces polling');
      await stop(t);
    });

    testWidgets('while the socket is down or closed the floor is polled; when it is back the polling stops', (t) async {
      answer = [line('Line 1', 'running')];
      final server = FakeServer()..down = true;
      await open(t, server: server);
      await t.pump(const Duration(seconds: 5));
      await t.pump();
      expect(reads, 2, reason: 'no socket: read every 5 s');
      server.down = false;
      await t.pump(const Duration(seconds: 1)); // the second try, 2 s after the first
      expect(server.sockets, hasLength(1));
      server.last.push([line('Line 1', 'stopped')]);
      await settle(t);
      expect(find.text('✗ Stopped'), findsOneWidget);
      final before = reads;
      await t.pump(const Duration(seconds: 20));
      expect(reads, before);
      // Dropped: polling again at the next tick, and the socket is tried again after 2 s.
      server.last.end(1006);
      answer = [line('Line 1', 'running')];
      await t.pump(const Duration(seconds: 5));
      await t.pump();
      expect(reads, before + 1);
      expect(find.text('✓ Running'), findsOneWidget);
      expect(server.sockets, hasLength(2));
      await stop(t);
    });

    testWidgets('a read overtaken by a push is dropped', (t) async {
      final gates = <Completer<http.Response>>[];
      final client = MockClient((_) {
        final g = Completer<http.Response>();
        gates.add(g);
        return g.future;
      });
      final (server, _) = await open(t, client: client);
      server.last.push([line('Line 1', 'stopped')]);
      await settle(t);
      gates[0].complete(http.Response(jsonEncode([line('Line 1', 'running')]), 200)); // asked before the push
      await t.pump();
      expect(find.text('✗ Stopped'), findsOneWidget);
      expect(find.text('✓ Running'), findsNothing);
      await stop(t);
    });

    testWidgets('a sign-out reconnects with the key at once and closes the socket that carried the session', (t) async {
      answer = [line('Line 1', 'running')];
      late ApiAuth a;
      final (server, _) = await open(t, auth: (c) => a = ApiAuth(c, _base, key: 'key', session: const PcSession(token: 'tok-1', name: 'Asha')));
      expect(server.opened.single.queryParameters['token'], 'tok-1');
      await a.logout();
      await settle(t);
      expect(server.sockets[0].closed, isTrue);
      expect(server.opened, hasLength(2));
      expect(server.opened[1].queryParameters, {'lang': 'en', 'api_key': 'key'});
      await stop(t);
    });

    testWidgets('a session the socket refuses (4401) has ended: it says so and goes on with the key', (t) async {
      answer = [line('Line 1', 'running')];
      late ApiAuth a;
      final (server, _) = await open(t, auth: (c) => a = ApiAuth(c, _base, key: 'key', session: const PcSession(token: 'tok-1', name: 'Asha')));
      server.last.end(4401);
      await settle(t);
      expect(a.signedIn, isFalse);
      expect(a.ended, isTrue);
      expect(find.text(ApiAuth.endedMessage), findsOneWidget);
      expect(server.opened, hasLength(2));
      expect(server.opened[1].queryParameters['api_key'], 'key');
      await stop(t);
    });

    testWidgets('a refused key is not a session: nothing is signed out, the socket backs off', (t) async {
      answer = [line('Line 1', 'running')];
      final (server, _) = await open(t);
      server.last.end(4401);
      await settle(t);
      expect(server.opened, hasLength(1));
      await t.pump(const Duration(seconds: 2));
      expect(server.opened, hasLength(2));
      await stop(t);
    });
  });
}
