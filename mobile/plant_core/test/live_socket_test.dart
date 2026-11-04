import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:plant_core/plant_core.dart';

/// A socket the test drives: [send] a message, [end] it with a close code.
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
  void send(String m) => _c.add(m);
  void end([int? code]) {
    _code = code;
    _c.close();
  }
}

/// Opens a [FakeSocket] per attempt, or fails while [down].
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
}

void main() {
  final a = Uri.parse('wss://plant.local/vision/ws?token=a'), b = Uri.parse('wss://plant.local/vision/ws?api_key=k');

  testWidgets('messages arrive; it is live from the first message until the close, and the close reports its code', (t) async {
    final server = FakeServer(), got = <String>[], closes = <int?>[];
    final s = PcLiveSocket(connect: server.connect, url: () => a, onMessage: got.add, onClose: (u, c) => closes.add(c))..start();
    await t.pump();
    expect(server.opened, [a]);
    expect(s.live, isFalse, reason: 'open but nothing sent yet');
    server.sockets[0].send('one');
    await t.pump();
    expect(got, ['one']);
    expect(s.live, isTrue);
    server.sockets[0].end(4401);
    await t.pump();
    expect(s.live, isFalse);
    expect(closes, [4401]);
    s.stop();
  });

  testWidgets('reconnects after 2, 4, 8 s, at most 60 s; a message starts the wait again from 2 s', (t) async {
    final server = FakeServer()..down = true;
    final s = PcLiveSocket(connect: server.connect, url: () => a, onMessage: (_) {})..start();
    await t.pump();
    expect(server.opened, hasLength(1));
    for (final (wait, n) in [(2, 2), (4, 3), (8, 4), (16, 5), (32, 6), (60, 7), (60, 8)]) {
      await t.pump(Duration(seconds: wait) - const Duration(milliseconds: 1));
      expect(server.opened, hasLength(n - 1), reason: 'not before $wait s');
      await t.pump(const Duration(milliseconds: 1));
      expect(server.opened, hasLength(n), reason: 'after $wait s');
    }
    server.down = false;
    await t.pump(const Duration(seconds: 60));
    expect(server.sockets, hasLength(1));
    // Accepted and closed at once, with nothing sent: still backing off.
    server.sockets[0].end(4401);
    await t.pump(const Duration(seconds: 59));
    expect(server.sockets, hasLength(1));
    await t.pump(const Duration(seconds: 1));
    expect(server.sockets, hasLength(2));
    server.sockets[1].send('floor');
    await t.pump();
    server.sockets[1].end();
    await t.pump(const Duration(seconds: 2));
    expect(server.sockets, hasLength(3), reason: 'a socket that worked is tried again after 2 s');
    s.stop();
  });

  testWidgets('start opens the socket again with the new address and closes the old; stop ends the retries', (t) async {
    final server = FakeServer();
    var url = a;
    final got = <String>[];
    final s = PcLiveSocket(connect: server.connect, url: () => url, onMessage: got.add)..start();
    await t.pump();
    expect(s.current, a);
    url = b;
    s.start();
    await t.pump();
    expect(server.opened, [a, b]);
    expect(server.sockets[0].closed, isTrue);
    expect(s.current, b);
    server.sockets[0].send('late, from the old one');
    await t.pump();
    expect(got, isEmpty);
    s.stop();
    expect(server.sockets[1].closed, isTrue);
    server.down = true;
    await t.pump(const Duration(minutes: 5));
    expect(server.opened, hasLength(2));
  });

  testWidgets('no address: nothing is opened', (t) async {
    final server = FakeServer();
    final s = PcLiveSocket(connect: server.connect, url: () => null, onMessage: (_) {})..start();
    await t.pump(const Duration(minutes: 2));
    expect(server.opened, isEmpty);
    s.stop();
  });
}
