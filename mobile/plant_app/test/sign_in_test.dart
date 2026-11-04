import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:plant_app/modules/vision/account_screen.dart';
import 'package:plant_app/modules/vision/api_auth.dart';
import 'package:plant_app/modules/vision/floor_screen.dart';
import 'package:plant_app/modules/vision/ng_api.dart';
import 'package:plant_app/modules/vision/ng_review_screen.dart';
import 'package:plant_app/modules/vision/vision_api.dart';
import 'package:plant_app/modules/vision/vision_module.dart';
import 'package:plant_app/src/app.dart';
import 'package:plant_core/plant_core.dart';

final _base = Uri.parse('https://plant.local/plant');
const _session = PcSession(token: 'tok-1', name: 'Asha', expires: '2026-11-06 10:00');

http.Response _login(String name) => http.Response(jsonEncode({'token': 'tok-1', 'expires_at': '2026-11-06 10:00', 'user': {'name': name, 'username': 'asha', 'roles': []}}), 200);

/// A sign-in that lives only in the object (no PIN, nothing stored).
ApiAuth _auth(http.Client c, {PcSession? session, String? key = 'key'}) => ApiAuth(c, _base, key: key, session: session);

final _server = PcServer(id: 'a', product: PcProduct.vision, name: 'Unit 1', baseUrl: _base);

/// The app's servers with one server 'a' and nobody signed in.
Future<(PcServers, MemoryStore)> _servers() async {
  final secrets = MemoryStore();
  final s = PcServers(MemoryStore(), secrets);
  await s.add(_server, token: 'k');
  return (s, secrets);
}

/// A sign-in kept in [servers], as the app does: a PIN is chosen at sign-in.
ApiAuth _kept(http.Client c, PcServers servers) => ApiAuth(c, _base, key: 'key', servers: servers, serverId: 'a');

/// Asha's sign-in as kept, with a quick PIN hash.
PcSession _asha({String pin = '2580'}) => PcSession(token: 'tok-1', name: 'Asha', expires: '2026-11-06 10:00', user: 'asha', pin: PcPinHash.make(pin, rounds: 50));

/// A store whose writes fail, as a keystore that was reset.
class _BrokenStore extends MemoryStore {
  @override
  Future<void> write(String key, String value) async => throw StateError('keystore');
}

void main() {
  setUp(() {
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (_) async => null);
  });

  group('sign in and out', () {
    test('a good sign-in posts the credentials to the server and keeps only the session and the PIN\'s hash', () async {
      late http.Request got;
      final (servers, secrets) = await _servers();
      final a = _kept(MockClient((r) async {
        got = r;
        return _login('Asha Rao');
      }), servers);
      await a.login(' Asha ', 'secret pw', pin: '2580');
      expect(got.url.toString(), 'https://plant.local/plant/auth/login');
      expect(jsonDecode(got.body), {'username': 'Asha', 'password': 'secret pw'});
      expect((a.session!.token, a.session!.name, a.session!.expires, a.session!.user), ('tok-1', 'Asha Rao', '2026-11-06 10:00', 'asha'));
      expect(a.headers, {'Authorization': 'Bearer tok-1'});
      final kept = secrets.values['signins.a']!;
      expect(kept, contains('tok-1'));
      expect(kept, isNot(contains('secret pw')));
      expect(kept, isNot(contains('"2580"')));
      expect(servers.people('a').single.pin!.matches('2580'), isTrue);
    });

    test('with kept sign-ins a PIN is needed and checked before anything is sent', () async {
      var sent = 0;
      final (servers, _) = await _servers();
      final a = _kept(MockClient((_) async {
        sent++;
        return _login('Asha');
      }), servers);
      await expectLater(a.login('asha', 'pw'), throwsA(isA<AuthException>().having((e) => e.message, 'message', 'Choose a PIN.')));
      await expectLater(a.login('asha', 'pw', pin: '1234'), throwsA(isA<AuthException>().having((e) => e.message, 'message', contains('harder to guess'))));
      await expectLater(a.login('asha', 'pw', pin: '12'), throwsA(isA<AuthException>()));
      expect(sent, 0);
      expect(servers.people('a'), isEmpty);
    });

    test('the server\'s sign-in limit is named, and nothing is stored', () async {
      final (servers, secrets) = await _servers();
      final a = _kept(MockClient((_) async => http.Response('{"detail":"too many wrong passwords"}', 429)), servers);
      await expectLater(a.login('asha', 'bad', pin: '2580'), throwsA(isA<AuthException>().having((e) => e.message, 'message', contains('Too many wrong passwords'))));
      expect(secrets.values.containsKey('signins.a'), isFalse);
    });

    test('a refused sign-in says so and stores nothing', () async {
      final (servers, secrets) = await _servers();
      final a = _kept(MockClient((_) async => http.Response('{"detail":"invalid username or password"}', 401)), servers);
      await expectLater(a.login('asha', 'bad', pin: '2580'), throwsA(isA<AuthException>().having((e) => e.message, 'message', 'Wrong user name or password.')));
      expect(secrets.values.containsKey('signins.a'), isFalse);
      expect(a.signedIn, isFalse);
      expect(a.headers, {'X-Api-Key': 'key'});
    });

    test('no network, an odd answer or a token-less answer: a plain message and nothing stored', () async {
      for (final c in [
        MockClient((_) async => throw http.ClientException('down')),
        MockClient((_) async => http.Response('nope', 200)),
        MockClient((_) async => http.Response('{"user":{}}', 200)),
        MockClient((_) async => http.Response('', 500)),
      ]) {
        final (servers, secrets) = await _servers();
        final a = _kept(c, servers);
        await expectLater(a.login('asha', 'pw', pin: '2580'), throwsA(isA<AuthException>()));
        expect(secrets.values.containsKey('signins.a'), isFalse);
        expect(a.signedIn, isFalse);
      }
    });

    test('a sign-in this phone cannot keep is refused and ended on the server', () async {
      final calls = <String>[];
      final servers = PcServers(MemoryStore(), _BrokenStore());
      final a = _kept(MockClient((r) async {
        calls.add('${r.url.path} ${r.headers['Authorization'] ?? ''}'.trim());
        return r.url.path.endsWith('login') ? _login('Asha') : http.Response('{}', 200);
      }), servers);
      await expectLater(a.login('asha', 'pw', pin: '2580'), throwsA(isA<AuthException>().having((e) => e.message, 'message', contains('could not keep'))));
      expect(a.signedIn, isFalse);
      expect(calls, ['/plant/auth/login', '/plant/auth/logout Bearer tok-1']);
    });

    test('sign-out tells the server with the session and deletes it', () async {
      late http.Request got;
      final (servers, secrets) = await _servers();
      await servers.setSession('a', _asha());
      final a = _kept(MockClient((r) async {
        got = r;
        return http.Response('{"ok":true}', 200);
      }), servers);
      await a.logout();
      expect((got.method, got.url.path, got.headers['Authorization']), ('POST', '/plant/auth/logout', 'Bearer tok-1'));
      expect(a.signedIn, isFalse);
      expect(servers.people('a'), isEmpty);
      expect(secrets.values['signins.a'], '');
    });

    test('sign-out deletes the session even when the call fails', () async {
      for (final c in [MockClient((_) async => throw http.ClientException('down')), MockClient((_) async => http.Response('', 500))]) {
        final (servers, _) = await _servers();
        await servers.setSession('a', _asha());
        final a = _kept(c, servers);
        await a.logout();
        expect(a.signedIn, isFalse);
        expect(servers.people('a'), isEmpty);
        expect(a.headers, {'X-Api-Key': 'key'});
      }
    });

    test('a locked sign-in is not used: calls go with the key until the right PIN opens it', () async {
      final (servers, _) = await _servers();
      await servers.setSession('a', _asha());
      final a = _kept(MockClient((_) async => http.Response('[]', 200)), servers);
      var heard = 0;
      a.addListener(() => heard++);
      await servers.lock();
      expect(a.signedIn, isFalse);
      expect(a.headers, {'X-Api-Key': 'key'});
      expect(heard, greaterThan(0));
      expect(await a.unlock('asha', '9999'), PcUnlock.wrong);
      expect(a.signedIn, isFalse);
      expect(await a.unlock('asha', '2580'), PcUnlock.open);
      expect(a.headers, {'Authorization': 'Bearer tok-1'});
      expect(a.signedIn, isTrue);
      expect(a.session!.name, 'Asha');
      a.dispose();
    });

    test('sign-out forgets the person here first, even when the phone locks while the server is slow', () async {
      final (servers, _) = await _servers();
      await servers.setSession('a', _asha());
      final reached = <String?>[];
      final a = _kept(MockClient((r) async {
        reached.add(r.headers['Authorization']);
        await servers.lock(); // the app is hidden during the call
        throw http.ClientException('down');
      }), servers);
      await a.logout();
      expect(reached, ['Bearer tok-1']);
      expect(servers.people('a'), isEmpty);
    });
  });

  group('who a call speaks for', () {
    test('every Vision call sends the session when signed in, the key otherwise', () async {
      final seen = <Map<String, String>>[];
      final c = MockClient((r) async {
        seen.add(r.headers);
        if (r.url.path.endsWith('vision/floor')) return http.Response('[]', 200);
        if (r.url.path.contains('/image/')) return http.Response.bytes([1, 2, 3], 200);
        if (r.method == 'POST') return http.Response('{"ok":true}', 200);
        return http.Response('{"items":[]}', 200);
      });
      Future<void> calls(VisionApi v) async {
        await v.floor();
        final ng = NgApi.of(v);
        await ng.pending();
        await ng.image('/vision/ng/1/image/cam1');
        if (v.auth.signedIn) await ng.review(1, NgVerdict.confirmed);
      }

      await calls(VisionApi(c, _base, 'key', auth: _auth(c)));
      expect(seen, hasLength(3));
      for (final h in seen) {
        expect(h['X-Api-Key'], 'key');
        expect(h.containsKey('Authorization'), isFalse);
      }
      seen.clear();
      await calls(VisionApi(c, _base, 'key', auth: _auth(c, session: _session)));
      expect(seen, hasLength(4));
      for (final h in seen) {
        expect(h['Authorization'], 'Bearer tok-1');
        expect(h.containsKey('X-Api-Key'), isFalse);
      }
    });

    test('a verdict is never sent with the key', () async {
      var sent = 0;
      final c = MockClient((_) async {
        sent++;
        return http.Response('{}', 200);
      });
      await expectLater(NgApi(c, _base, 'key').review(1, NgVerdict.confirmed), throwsA(isA<NgApiException>().having((e) => e.message, 'message', contains('Sign in'))));
      expect(sent, 0);
    });

    test('a 401 to a session call ends the session; later reads use the key again', () async {
      final (servers, _) = await _servers();
      await servers.setSession('a', _asha());
      var n = 0;
      final seen = <Map<String, String>>[];
      final c = MockClient((r) async {
        seen.add(r.headers);
        return n++ == 0 ? http.Response('', 401) : http.Response('[]', 200);
      });
      final a = _kept(c, servers);
      final v = VisionApi(c, _base, 'key', auth: a);
      await expectLater(v.floor(), throwsA(isA<VisionApiException>().having((e) => e.message, 'message', 'Your sign-in has ended — sign in again.')));
      expect(a.signedIn, isFalse);
      expect(a.ended, isTrue);
      expect(servers.people('a'), isEmpty);
      await v.floor();
      expect(seen.last, {'X-Api-Key': 'key'});
    });

    test('a 401 to the key keeps saying the key was refused; an old 401 does not end a newer sign-in', () async {
      final c1 = MockClient((_) async => http.Response('', 401));
      await expectLater(VisionApi(c1, _base, 'key', auth: _auth(c1)).floor(), throwsA(isA<VisionApiException>().having((e) => e.message, 'message', contains('key was not accepted'))));
      final a = _auth(c1, session: const PcSession(token: 'new', name: 'B'));
      a.onUnauthorized({'Authorization': 'Bearer old'});
      expect(a.signedIn, isTrue);
    });

    test('a 403 on a verdict shows the server\'s detail, else a plain sentence; a 401 ends the session', () async {
      Future<NgApiException> verdict(http.Response r, [ApiAuth? auth]) async {
        final c = MockClient((_) async => r);
        try {
          await NgApi(c, _base, 'key', auth: auth ?? _auth(c, session: _session)).review(1, NgVerdict.overruled);
        } on NgApiException catch (e) {
          return e;
        }
        fail('no error');
      }

      expect((await verdict(http.Response('{"detail":"quality manager role needed for line L1"}', 403))).message, 'quality manager role needed for line L1');
      expect((await verdict(http.Response('{"detail":[{"msg":"x"}]}', 403))).message, 'Your account may not give verdicts on this line.');
      expect((await verdict(http.Response('not json', 403))).message, 'Your account may not give verdicts on this line.');
      final c = MockClient((_) async => http.Response('', 401));
      final a = _auth(c, session: _session);
      expect((await verdict(http.Response('', 401), a)).message, ApiAuth.endedMessage);
      expect(a.signedIn, isFalse);
    });

    test('keys and sessions go only to the server\'s own address', () async {
      final hosts = <String>{};
      final c = MockClient((r) async {
        hosts.add('${r.url.scheme}://${r.url.host}:${r.url.port}');
        return r.url.path.contains('login') ? _login('A') : http.Response('{"items":[]}', 200);
      });
      final a = _auth(c);
      await a.login('u', 'p');
      await NgApi(c, _base, 'key', auth: a).pending();
      await a.logout();
      expect(hosts, {'https://plant.local:443'});
    });
  });

  group('the kept sign-in', () {
    testWidgets('opening the Vision module uses the open sign-in for its calls; Sign out ends it', (t) async {
      final (servers, _) = await _servers();
      await servers.setSession('a', _asha());
      final seen = <Map<String, String>>[];
      final c = MockClient((r) async {
        seen.add(r.headers);
        return http.Response('[]', 200);
      });
      final module = VisionModule(c, PcFeedback(PcLook()), servers);
      await t.pumpWidget(MaterialApp(theme: pcTheme(Brightness.dark), home: Builder(builder: (ctx) => module.home(ctx, _server, 'k'))));
      await t.pump();
      expect(seen.first, {'Authorization': 'Bearer tok-1'});
      // Signing out in the account screen deletes the kept sign-in too.
      await t.tap(find.byTooltip('Account: signed in as Asha'));
      await t.pumpAndSettle();
      await t.tap(find.text('Sign out'));
      await t.pumpAndSettle();
      expect(servers.people('a'), isEmpty);
      expect(find.text('Sign in'), findsWidgets);
      // Stop the poll timer before the test ends.
      await t.pumpWidget(const SizedBox());
    });

    testWidgets('a shared phone: lock, a wrong PIN says how many tries are left, the right one opens it', (t) async {
      final (servers, _) = await _servers();
      await servers.setSession('a', _asha());
      final module = VisionModule(MockClient((_) async => http.Response('[]', 200)), PcFeedback(PcLook()), servers);
      await t.pumpWidget(MaterialApp(theme: pcTheme(Brightness.dark), home: Builder(builder: (ctx) => module.home(ctx, _server, 'k'))));
      await t.pump();
      await t.tap(find.byTooltip('Account: signed in as Asha'));
      await t.pumpAndSettle();
      await t.tap(find.text('Lock or switch person'));
      await t.pumpAndSettle();
      expect(servers.session('a'), isNull);
      expect(find.text('Signed in on this phone'), findsOneWidget);
      expect(find.text('Someone else'), findsOneWidget);
      await t.tap(find.text('Asha'));
      await t.pumpAndSettle();
      final pin = find.widgetWithText(TextField, 'PIN');
      expect(t.widget<TextField>(pin).obscureText, isTrue);
      expect(t.widget<TextField>(pin).keyboardType, TextInputType.number);
      await t.enterText(pin, '1111');
      await t.pump();
      await t.tap(find.widgetWithText(FilledButton, 'Open'));
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 400))); // the PIN hash runs on an isolate
      await t.pumpAndSettle();
      expect(find.text('✗ Wrong PIN. 4 tries left.'), findsOneWidget);
      expect(servers.session('a'), isNull);
      await t.enterText(pin, '2580');
      await t.pump();
      await t.tap(find.widgetWithText(FilledButton, 'Open'));
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 400))); // the PIN hash runs on an isolate
      await t.pumpAndSettle();
      expect(servers.session('a')!.name, 'Asha');
      expect(find.byTooltip('Account: signed in as Asha'), findsOneWidget); // back on the floor
      await t.pumpWidget(const SizedBox());
    });

    testWidgets('the fifth wrong PIN signs the person out and says the password is needed', (t) async {
      final (servers, _) = await _servers();
      await servers.setSession('a', _asha());
      await servers.lock();
      final a = _kept(MockClient((_) async => http.Response('{}', 200)), servers);
      await t.pumpWidget(MaterialApp(theme: pcTheme(Brightness.dark), home: AccountScreen(auth: a, feedback: PcFeedback(PcLook()))));
      for (var i = 0; i < 5; i++) {
        if (find.widgetWithText(TextField, 'PIN').evaluate().isEmpty) {
          await t.tap(find.text('Asha'));
          await t.pumpAndSettle();
        }
        await t.enterText(find.widgetWithText(TextField, 'PIN'), '0000');
        await t.pump();
        await t.tap(find.widgetWithText(FilledButton, 'Open'));
        await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 400))); // the PIN hash runs on an isolate
        await t.pumpAndSettle();
      }
      expect(find.textContaining('Too many wrong PINs: Asha is signed out'), findsOneWidget);
      expect(servers.people('a'), isEmpty);
      expect(find.text('Signed in on this phone'), findsNothing);
    });
  });

  group('the app on a shared phone', () {
    testWidgets('the open sign-in locks when the app is hidden; one unused too long is ended on its server at start', (t) async {
      final (servers, secrets) = await _servers();
      final old = PcSession(token: 'tok-old', name: 'Ravi', user: 'ravi', pin: PcPinHash.make('1397', rounds: 50), lastActive: DateTime.now().subtract(const Duration(hours: 1)));
      secrets.values['signins.a'] = jsonEncode([old.toMap()]);
      await servers.load();
      await servers.setSession('a', _asha());
      final logouts = <String?>[];
      final c = MockClient((r) async {
        if (r.url.path.endsWith('auth/logout')) logouts.add(r.headers['Authorization']);
        return http.Response('[]', 200);
      });
      final look = PcLook();
      await t.pumpWidget(PlantApp(look: look, servers: servers, feedback: PcFeedback(look), client: c));
      await t.pump();
      expect(logouts, ['Bearer tok-old']);
      expect(servers.people('a').map((p) => p.user), ['asha']);
      expect(servers.session('a'), isNotNull);
      t.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
      t.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
      await t.pump();
      expect(servers.session('a'), isNull);
      expect(servers.people('a'), hasLength(1));
      await t.pumpWidget(const SizedBox());
    });
  });

  group('screens', () {
    Future<void> account(WidgetTester t, ApiAuth a) async {
      await t.pumpWidget(MaterialApp(theme: pcTheme(Brightness.dark), home: Builder(builder: (ctx) => Scaffold(body: TextButton(onPressed: () => openAccount(ctx, a, PcFeedback(PcLook())), child: const Text('open'))))));
      await t.tap(find.text('open'));
      await t.pumpAndSettle();
    }

    Future<void> fill(WidgetTester t, {String password = 'pw', String pin = '2580', String? pin2}) async {
      await t.enterText(find.widgetWithText(TextField, 'User name'), 'asha');
      await t.enterText(find.widgetWithText(TextField, 'Password'), password);
      await t.enterText(find.widgetWithText(TextField, 'New PIN (4 to 8 digits)'), pin);
      await t.enterText(find.widgetWithText(TextField, 'New PIN again'), pin2 ?? pin);
      await t.pump();
    }

    testWidgets('password and PIN are hidden; a wrong password shows the message, clears password and PINs and stores nothing', (t) async {
      final (servers, secrets) = await _servers();
      final a = _kept(MockClient((_) async => http.Response('', 401)), servers);
      await account(t, a);
      for (final f in ['Password', 'New PIN (4 to 8 digits)', 'New PIN again']) {
        expect(t.widget<TextField>(find.widgetWithText(TextField, f)).obscureText, isTrue, reason: f);
      }
      expect(t.widget<FilledButton>(find.widgetWithText(FilledButton, 'Sign in')).onPressed, isNull); // nothing typed yet
      await fill(t, password: 'bad');
      await t.tap(find.widgetWithText(FilledButton, 'Sign in'));
      await t.pumpAndSettle();
      expect(find.textContaining('Wrong user name or password'), findsOneWidget);
      for (final f in ['Password', 'New PIN (4 to 8 digits)', 'New PIN again']) {
        expect(t.widget<TextField>(find.widgetWithText(TextField, f)).controller!.text, isEmpty, reason: f);
      }
      expect(secrets.values.containsKey('signins.a'), isFalse);
    });

    testWidgets('two PINs that differ, or an easy PIN, are refused before anything is sent', (t) async {
      var sent = 0;
      final (servers, _) = await _servers();
      final a = _kept(MockClient((_) async {
        sent++;
        return _login('Asha');
      }), servers);
      await account(t, a);
      await fill(t, pin: '2580', pin2: '2581');
      await t.tap(find.widgetWithText(FilledButton, 'Sign in'));
      await t.pumpAndSettle();
      expect(find.text('✗ The two PINs differ.'), findsOneWidget);
      await fill(t, pin: '1111');
      await t.tap(find.widgetWithText(FilledButton, 'Sign in'));
      await t.pumpAndSettle();
      expect(find.textContaining('harder to guess'), findsOneWidget);
      expect(sent, 0);
      // Only digits can be typed, at most 8.
      await t.enterText(find.widgetWithText(TextField, 'New PIN again'), '12ab3456789');
      expect(t.widget<TextField>(find.widgetWithText(TextField, 'New PIN again')).controller!.text, '12345678');
    });

    testWidgets('a good sign-in keeps the session and closes the screen; the account then shows who, Lock and Sign out', (t) async {
      final (servers, _) = await _servers();
      final a = _kept(MockClient((_) async => _login('Asha Rao')), servers);
      await account(t, a);
      await fill(t);
      await t.tap(find.widgetWithText(FilledButton, 'Sign in'));
      await t.pumpAndSettle();
      expect(find.text('open'), findsOneWidget); // back
      expect(servers.session('a')!.name, 'Asha Rao');
      await t.tap(find.text('open'));
      await t.pumpAndSettle();
      expect(find.text('Signed in as Asha Rao'), findsOneWidget);
      expect(find.text('Lock or switch person'), findsOneWidget);
      expect(find.text('Sign out'), findsOneWidget);
    });

    testWidgets('without kept sign-ins (a screen on its own) no PIN is asked', (t) async {
      final a = _auth(MockClient((_) async => _login('Asha Rao')));
      await account(t, a);
      expect(find.widgetWithText(TextField, 'New PIN again'), findsNothing);
      await t.enterText(find.widgetWithText(TextField, 'User name'), 'asha');
      await t.enterText(find.widgetWithText(TextField, 'Password'), 'pw');
      await t.pump();
      await t.tap(find.widgetWithText(FilledButton, 'Sign in'));
      await t.pumpAndSettle();
      expect(a.session!.name, 'Asha Rao');
    });

    testWidgets('an ended session is said on the account screen', (t) async {
      final c = MockClient((_) async => http.Response('', 401));
      final a = _auth(c, session: _session);
      a.onUnauthorized({'Authorization': 'Bearer tok-1'});
      await account(t, a);
      expect(find.textContaining('Your sign-in has ended'), findsOneWidget);
    });

    testWidgets('not signed in: the queue shows, the verdict buttons are replaced by a sign-in prompt and swipes do nothing', (t) async {
      final api = _Queue(signedIn: false);
      await t.pumpWidget(MaterialApp(theme: pcTheme(Brightness.dark), home: NgReviewScreen(api: api, feedback: PcFeedback(PcLook()))));
      await t.pump();
      await t.pump();
      expect(find.textContaining('Part part-1'), findsOneWidget);
      expect(find.byKey(const ValueKey('ng-confirmed')), findsNothing);
      expect(find.byKey(const ValueKey('ng-overruled')), findsNothing);
      expect(find.byKey(const ValueKey('ng-unsure')), findsNothing);
      expect(find.text('Sign in with your own account to give verdicts.'), findsOneWidget);
      await t.drag(find.textContaining('Part part-'), const Offset(300, 0));
      await t.pump();
      expect(api.reviews, 0);
      expect(find.textContaining('Part part-1'), findsOneWidget);
      // The prompt leads to the account screen.
      await t.tap(find.byKey(const ValueKey('ng-sign-in')));
      await t.pumpAndSettle();
      expect(find.text('Account'), findsOneWidget);
    });

    testWidgets('signed in: the three buttons show; a 403 detail from the server is shown with the card back', (t) async {
      final api = _Queue(signedIn: true, verdictAnswer: http.Response('{"detail":"only a quality manager of line L1 may review"}', 403));
      await t.pumpWidget(MaterialApp(theme: pcTheme(Brightness.dark), home: NgReviewScreen(api: api, feedback: PcFeedback(PcLook()))));
      await t.pump();
      await t.pump();
      expect(find.byKey(const ValueKey('ng-confirmed')), findsOneWidget);
      expect(find.text('Sign in with your own account to give verdicts.'), findsNothing);
      await t.tap(find.byKey(const ValueKey('ng-confirmed')));
      await t.pump();
      await t.pump();
      expect(find.text('only a quality manager of line L1 may review'), findsOneWidget);
      expect(find.textContaining('Part part-1'), findsOneWidget);
    });

    testWidgets('a session that ends under a verdict: the card comes back, the buttons give way to the sign-in prompt', (t) async {
      final api = _Queue(signedIn: true, verdictAnswer: http.Response('', 401));
      await t.pumpWidget(MaterialApp(theme: pcTheme(Brightness.dark), home: NgReviewScreen(api: api, feedback: PcFeedback(PcLook()))));
      await t.pump();
      await t.pump();
      await t.tap(find.byKey(const ValueKey('ng-unsure')));
      await t.pump();
      await t.pump();
      expect(find.textContaining('Your sign-in has ended'), findsWidgets);
      expect(find.byKey(const ValueKey('ng-unsure')), findsNothing);
      expect(find.byKey(const ValueKey('ng-sign-in')), findsOneWidget);
      expect(find.textContaining('Part part-1'), findsOneWidget);
    });

    testWidgets('the NG queue goes when the phone locks and is read again when another person opens their sign-in', (t) async {
      final (servers, _) = await _servers();
      await servers.setSession('a', _asha());
      await servers.setSession('a', PcSession(token: 'tok-2', name: 'Ravi', user: 'ravi', pin: PcPinHash.make('1397', rounds: 50)));
      final api = _PerPerson(_kept(MockClient((_) async => http.Response('', 500)), servers));
      await t.pumpWidget(MaterialApp(theme: pcTheme(Brightness.dark), home: NgReviewScreen(api: api, feedback: PcFeedback(PcLook()))));
      await t.pump();
      await t.pump();
      expect(find.textContaining('Part ravi-1'), findsOneWidget);

      await servers.lock();
      await t.pump();
      await t.pump();
      // Read again without a sign-in: nobody's queue is left on the screen.
      expect(find.textContaining('Part ravi-1'), findsNothing);
      expect(find.textContaining('Part none-1'), findsOneWidget);

      await t.runAsync(() => servers.unlock('a', 'asha', '2580'));
      await t.pump();
      await t.pump();
      expect(find.textContaining('Part asha-1'), findsOneWidget);
      expect(find.textContaining('Part none-1'), findsNothing);
    });

    testWidgets('the floor offers sign-in in its app bar', (t) async {
      final c = MockClient((_) async => http.Response('[]', 200));
      await t.pumpWidget(MaterialApp(
        theme: pcTheme(Brightness.dark),
        home: FloorScreen(
          server: PcServer(id: 'a', product: PcProduct.vision, name: 'Unit 1', baseUrl: _base),
          api: VisionApi(c, _base, 'key'),
          feedback: PcFeedback(PcLook()),
        ),
      ));
      await t.pump();
      expect(find.byTooltip('Sign in'), findsOneWidget);
      await t.pumpWidget(const SizedBox());
    });
  });
}

/// One part, with the real NgApi talking to a fake server, so the verdict
/// call, the headers and the error words are the real ones.
class _Queue extends NgApi {
  _Queue({required bool signedIn, this.verdictAnswer})
      : super(_server(verdictAnswer), _base, 'key', auth: ApiAuth(_server(verdictAnswer), _base, key: 'key', session: signedIn ? _session : null));
  final http.Response? verdictAnswer;
  int reviews = 0;

  static http.Client _server(http.Response? verdict) => MockClient((r) async {
        if (r.method == 'POST') return verdict ?? http.Response('{"ok":true}', 200);
        if (r.url.path.contains('/image/')) return http.Response.bytes(base64Decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=='), 200);
        return http.Response(jsonEncode({'items': [{'id': 1, 'plant': 'P1', 'line': 'L1', 'station': 'S1', 'ts': 1760000000000, 'camera': 'cam1', 'part_id': 'part-1', 'reasons': [], 'images': [], 'image_state': 'none'}], 'next_before_id': null}), 200);
      });

  @override
  Future<void> review(int id, NgVerdict verdict, {String note = ''}) {
    reviews++;
    return super.review(id, verdict, note: note);
  }
}

/// The queue of whoever is signed in: parts named after the person.
class _PerPerson extends NgApi {
  _PerPerson(ApiAuth auth) : super(MockClient((_) async => http.Response('', 500)), _base, 'key', auth: auth);

  @override
  Future<NgPage> pending({String? plant, String? line, int? beforeId, int limit = 50}) async {
    final who = auth.session?.user ?? 'none';
    return NgPage([NgPart.fromJson({'id': 1, 'plant': 'P1', 'line': 'L1', 'station': 'S1', 'ts': 1, 'part_id': '$who-1', 'reasons': [], 'images': [], 'image_state': 'none'})!], null);
  }
}
