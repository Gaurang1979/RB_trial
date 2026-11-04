import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:plant_core/plant_core.dart';

/// A store whose writes fail, as a keystore that was reset.
class _BrokenStore extends MemoryStore {
  bool broken = false;
  @override
  Future<void> write(String key, String value) async {
    if (broken) throw StateError('keystore');
    await super.write(key, value);
  }
}

void main() {
  // Few rounds keep the tests quick; the app uses PcPinHash.defaultRounds.
  PcPinHash pin(String p) => PcPinHash.make(p, rounds: 50);
  PcSession person(String user, String p, {String token = 'tok'}) => PcSession(token: '$token-$user', name: user.toUpperCase(), user: user, pin: pin(p));
  final server = PcServer(id: 'a', product: PcProduct.vision, name: 'Unit 1', baseUrl: Uri.parse('https://plant.local'));
  final server2 = PcServer(id: 'b', product: PcProduct.vision, name: 'Unit 2', baseUrl: Uri.parse('https://plant2.local'));

  group('the PIN hash', () {
    test('matches only its own PIN, is salted, and keeps no digits', () async {
      final a = pin('2580'), b = pin('2580');
      expect(a.matches('2580'), isTrue);
      expect(a.matches('2581'), isFalse);
      expect(a.matches(''), isFalse);
      expect(a.salt, isNot(b.salt));
      expect(a.hash, isNot(b.hash));
      final again = PcPinHash.fromJson(jsonDecode(jsonEncode(a.toJson())))!;
      expect(again.matches('2580'), isTrue);
      expect(await again.matchesAsync('2580'), isTrue);
      expect(await again.matchesAsync('2581'), isFalse);
      expect(again.rounds, 50);
      expect(PcPinHash.make('2580').rounds, PcPinHash.defaultRounds);
    });

    test('is PBKDF2-HMAC-SHA256: the RFC 7914 section 11 answer for "passwd", "salt", 1 round', () {
      final known = PcPinHash(salt: utf8.encode('salt'), hash: _hex('55ac046e56e3089fec1691c22544b605f94185216dde0465e68b9d57c20dacbc'), rounds: 1);
      expect(known.matches('passwd'), isTrue);
      expect(known.matches('passwe'), isFalse);
    });

    test('a damaged hash reads as none', () {
      for (final j in [null, 1, {}, {'salt': 'x', 'hash': 'y', 'rounds': 1}, {'salt': base64Encode(List.filled(16, 1)), 'hash': base64Encode(List.filled(31, 1)), 'rounds': 1}, {'salt': base64Encode(List.filled(16, 1)), 'hash': base64Encode(List.filled(32, 1)), 'rounds': 0}]) {
        expect(PcPinHash.fromJson(j), isNull, reason: '$j');
      }
    });

    test('PIN rules: 4 to 8 digits, no repeats or straight runs', () {
      for (final ok in ['2580', '1357', '90210', '24681357']) {
        expect(pcPinProblem(ok), isNull, reason: ok);
      }
      for (final bad in ['', '123', '123456789', '12a4', ' 2580', '0000', '1234', '4321', '98765', '01234567']) {
        expect(pcPinProblem(bad), isNotNull, reason: bad);
      }
    });
  });

  group('sign-ins on a shared phone', () {
    late DateTime now;
    late MemoryStore settings, secrets;
    late PcServers s;
    late List<(String, String)> dropped;

    Future<PcServers> fresh() async {
      final x = PcServers(settings, secrets, now: () => now)..onDropped = (id, p) => dropped.add((id, p.user));
      return x;
    }

    setUp(() async {
      now = DateTime.utc(2026, 10, 31, 8);
      settings = MemoryStore();
      secrets = MemoryStore();
      dropped = [];
      s = await fresh();
      await s.add(server);
      await s.add(server2);
    });

    test('a sign-in is kept in the secret store with its PIN hash only, and comes back locked after a restart', () async {
      await s.setSession('a', person('asha', '2580'));
      expect(s.session('a')!.name, 'ASHA');
      final raw = secrets.values['signins.a']!;
      expect(raw, contains('tok-asha'));
      expect(raw, isNot(contains('2580')));
      final again = PcServers(settings, secrets, now: () => now);
      await again.load();
      expect(again.session('a'), isNull);
      expect(again.people('a').single.user, 'asha');
      expect(await again.unlock('a', 'asha', '2580'), PcUnlock.open);
      expect(again.session('a')!.token, 'tok-asha');
    });

    test('a sign-in without a PIN or a login name is never kept', () async {
      expect(() => s.setSession('a', const PcSession(token: 't', name: 'n', user: 'n')), throwsArgumentError);
      expect(() => s.setSession('a', PcSession(token: 't', name: 'n', pin: pin('2580'))), throwsArgumentError);
      expect(s.people('a'), isEmpty);
    });

    test('a store that fails keeps nothing and opens nothing', () async {
      final broken = _BrokenStore();
      final x = PcServers(MemoryStore(), broken, now: () => now);
      await x.add(server);
      broken.broken = true;
      await expectLater(x.setSession('a', person('asha', '2580')), throwsStateError);
      expect(x.session('a'), isNull);
      expect(x.people('a'), isEmpty);
    });

    test('switching person: one sign-in open on the whole phone; each opens with their own PIN', () async {
      await s.setSession('a', person('asha', '2580'));
      await s.setSession('a', person('ravi', '1397'));
      expect(s.session('a')!.user, 'ravi');
      expect(s.people('a').map((p) => p.user), ['asha', 'ravi']);
      await s.lock();
      expect(await s.unlock('a', 'asha', '1397'), PcUnlock.wrong); // Ravi's PIN does not open Asha's sign-in
      expect(s.session('a'), isNull);
      expect(await s.unlock('a', 'asha', '2580'), PcUnlock.open);
      expect(s.session('a')!.user, 'asha');
      // Opening a sign-in on another server locks this one.
      await s.setSession('b', person('ravi', '1397'));
      expect(s.session('a'), isNull);
      expect(s.session('b')!.user, 'ravi');
      // Signing in again replaces the person's older sign-in, which is ended on the server.
      await s.setSession('a', person('asha', '4826', token: 'new'));
      expect(s.people('a').where((p) => p.user == 'asha').single.token, 'new-asha');
      expect(dropped, [('a', 'asha')]);
      // Login names are kept as the server wrote them: Asha is not asha.
      await s.setSession('a', person('Asha', '4826'));
      expect(s.people('a').map((p) => p.user), containsAll(['asha', 'Asha']));
    });

    test('five wrong PINs sign the person out; the count survives a restart; a right PIN resets it', () async {
      await s.setSession('a', person('asha', '2580'));
      await s.lock();
      expect(await s.unlock('a', 'asha', '1111'), PcUnlock.wrong);
      expect(await s.unlock('a', 'asha', '2222'), PcUnlock.wrong);
      expect(s.triesLeft('a', 'asha'), 3);
      expect(await s.unlock('a', 'asha', '2580'), PcUnlock.open);
      expect(s.triesLeft('a', 'asha'), 5);
      await s.lock();
      for (var i = 0; i < 3; i++) {
        expect(await s.unlock('a', 'asha', '0000'), PcUnlock.wrong);
      }
      final again = await fresh();
      await again.load();
      expect(again.triesLeft('a', 'asha'), 2);
      expect(await again.unlock('a', 'asha', '0000'), PcUnlock.wrong);
      expect(await again.unlock('a', 'asha', '0000'), PcUnlock.signedOut);
      expect(again.people('a'), isEmpty);
      expect(dropped, [('a', 'asha')]);
      expect(await again.unlock('a', 'asha', '2580'), PcUnlock.gone);
      final third = await fresh();
      await third.load();
      expect(third.people('a'), isEmpty);
    });

    test('locks after 2 minutes untouched; a touch keeps it open', () async {
      await s.setSession('a', person('asha', '2580'));
      now = now.add(const Duration(seconds: 100));
      s.touch();
      now = now.add(const Duration(seconds: 100));
      await s.tick();
      expect(s.session('a'), isNotNull);
      now = now.add(const Duration(seconds: 21));
      await s.tick();
      expect(s.session('a'), isNull);
      expect(s.people('a'), hasLength(1));
      expect(dropped, isEmpty);
    });

    test('signed out after 15 minutes without use, at the tick or when the PIN is typed', () async {
      await s.setSession('a', person('asha', '2580'));
      await s.setSession('a', person('ravi', '1397'));
      now = now.add(const Duration(minutes: 14));
      await s.tick();
      expect(s.people('a'), hasLength(2));
      now = now.add(const Duration(minutes: 1));
      expect(await s.unlock('a', 'asha', '2580'), PcUnlock.expired);
      expect(dropped, [('a', 'asha')]);
      await s.tick();
      expect(s.people('a'), isEmpty);
      expect(dropped, [('a', 'asha'), ('a', 'ravi')]);
      expect(secrets.values['signins.a'], '');
    });

    test('a locked sign-in can be forgotten by name; ticks do not overlap', () async {
      await s.setSession('a', person('asha', '2580'));
      await s.lock();
      await s.clearSession('a', 'asha');
      expect(s.people('a'), isEmpty);
      await s.setSession('a', person('ravi', '1397'));
      now = now.add(const Duration(minutes: 20));
      await Future.wait([s.tick(), s.tick()]);
      expect(dropped, [('a', 'ravi')]);
    });

    test('a clock that went back signs everyone out', () async {
      await s.setSession('a', person('asha', '2580'));
      await s.lock();
      now = now.subtract(const Duration(minutes: 5));
      expect(await s.unlock('a', 'asha', '2580'), PcUnlock.expired);
    });

    test('a sign-in kept the old way (one per server, no PIN) is wiped at load and ended at the first tick', () async {
      secrets.values['session.a'] = const PcSession(token: 'old', name: 'Asha').toJson();
      final again = await fresh();
      await again.load();
      expect(again.session('a'), isNull);
      expect(again.people('a'), isEmpty);
      expect(secrets.values['session.a'], '');
      expect(dropped, isEmpty);
      await again.tick();
      expect(dropped, [('a', '')]);
    });

    test('removing a server ends everyone\'s sign-in on it', () async {
      await s.setSession('a', person('asha', '2580'));
      await s.setSession('a', person('ravi', '1397'));
      final listed = <bool>[];
      s.onDropped = (id, p) {
        dropped.add((id, p.user));
        listed.add(s.list.any((x) => x.id == id));
      };
      await s.remove('a');
      expect(listed, [true, true]); // still reachable when it is ended on the server
      expect(s.people('a'), isEmpty);
      expect(dropped, [('a', 'asha'), ('a', 'ravi')]);
      expect(secrets.values['signins.a'], '');
    });

    test('damaged sign-ins read as none', () async {
      for (final raw in ['{bad', '{"token":"x"}', '[{"token":"x","user":"a"}]', '[1]']) {
        secrets.values['signins.a'] = raw;
        final again = await fresh();
        await again.load();
        expect(again.people('a'), isEmpty, reason: raw);
      }
    });
  });
}

Uint8List _hex(String h) => Uint8List.fromList([for (var i = 0; i < h.length; i += 2) int.parse(h.substring(i, i + 2), radix: 16)]);
