import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:plant_app/modules/vision/floor_screen.dart';
import 'package:plant_app/modules/vision/line_look.dart';
import 'package:plant_app/modules/vision/line_screen.dart';
import 'package:plant_app/modules/vision/api_auth.dart';
import 'package:plant_app/modules/vision/ng_api.dart';
import 'package:plant_app/modules/vision/vision_api.dart';
import 'package:plant_app/src/add_server.dart';
import 'package:plant_app/src/app.dart';
import 'package:plant_core/plant_core.dart';

class Recorder implements PcSoundPlayer {
  final played = <PcSound>[];
  @override
  Future<void> play(PcSound sound) async => played.add(sound);
}

Map<String, Object?> line(String name, String state, {String plant = 'P1', double? yieldPct = 99.4, List<Map<String, Object?>> alarms = const [], List<Map<String, Object?>> ng = const []}) => {
      'plant': plant, 'line': name, 'state': state, 'job': 'Carton-30', 'good': 994, 'bad': 6, 'total': 1000,
      'yield_pct': yieldPct, 'parts_per_minute': 240, 'top_ng': ng.isEmpty ? null : ng.first['reason'],
      'last_ng': ng, 'alarms': alarms, 'stations': [{'station': 'S1', 'connected': true}],
    };

class _CountingStore extends MemoryStore {
  int tokenWrites = 0;
  @override
  Future<void> write(String key, String value) {
    if (key.startsWith('token.')) tokenWrites++;
    return super.write(key, value);
  }
}

class _TrackedClient extends http.BaseClient {
  bool closed = false;
  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) => throw UnimplementedError();
  @override
  void close() => closed = true;
}

void main() {
  setUp(() {
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (_) async => null);
  });

  Future<(PcLook, PcServers, Recorder)> start(WidgetTester t, {http.Client? client}) async {
    final look = PcLook(), servers = PcServers(MemoryStore(), MemoryStore()), rec = Recorder();
    await t.pumpWidget(PlantApp(look: look, servers: servers, feedback: PcFeedback(look, player: rec), client: client ?? MockClient((_) async => http.Response('[]', 200))));
    return (look, servers, rec);
  }

  testWidgets('starts dark; Settings switches to light and back, and turns sounds off', (t) async {
    final (look, _, rec) = await start(t);
    expect(Theme.of(t.element(find.text('Plants').first)).brightness, Brightness.dark);
    await t.tap(find.text('Settings').last);
    await t.pumpAndSettle();
    await t.tap(find.text('Light'));
    await t.pumpAndSettle();
    expect(look.mode, ThemeMode.light);
    expect(Theme.of(t.element(find.text('Colour mode'))).brightness, Brightness.light);
    await t.tap(find.byType(Switch));
    await t.pumpAndSettle();
    expect(look.sound, isFalse);
    final before = rec.played.length;
    await t.tap(find.text('High'));
    await t.pump(const Duration(seconds: 1)); // the three vibrations
    expect(rec.played.length, before); // sounds off: nothing plays
  });

  testWidgets('alarm sound buttons play each level', (t) async {
    final (_, _, rec) = await start(t);
    await t.tap(find.text('Settings').last);
    await t.pumpAndSettle();
    rec.played.clear();
    for (final l in ['Low', 'Medium', 'High']) {
      await t.tap(find.text(l));
      await t.pump(const Duration(seconds: 1));
    }
    expect(rec.played, [PcSound.notice, PcSound.attention, PcSound.critical]);
  });

  testWidgets('adding a Vision server lists it; Production is shown but not yet available', (t) async {
    final (_, servers, _) = await start(t);
    await t.tap(find.text('Add a server'));
    await t.pumpAndSettle();
    final production = t.widget<SegmentedButton<PcProduct>>(find.byType(SegmentedButton<PcProduct>)).segments.firstWhere((s) => s.value == PcProduct.production);
    expect(production.enabled, isFalse);
    await t.enterText(find.widgetWithText(TextFormField, 'Name'), 'Unit 1');
    await t.enterText(find.widgetWithText(TextFormField, 'Address'), 'https://plant.local');
    await t.enterText(find.widgetWithText(TextFormField, 'Key'), 'k1');
    await t.tap(find.text('Save'));
    await t.pumpAndSettle();
    expect(find.text('Unit 1'), findsOneWidget);
    expect(await servers.token(servers.list.single.id), 'k1');
  });

  testWidgets('a typed certificate fingerprint is checked and kept with the server (MOB-TLS-04)', (t) async {
    final (_, servers, _) = await start(t);
    await t.tap(find.text('Add a server'));
    await t.pumpAndSettle();
    await t.enterText(find.widgetWithText(TextFormField, 'Name'), 'Unit 1');
    await t.enterText(find.widgetWithText(TextFormField, 'Address'), 'https://plant.local');
    await t.enterText(find.widgetWithText(TextFormField, 'Key'), 'k1');
    final fp = find.byKey(const ValueKey('fingerprint'));
    await t.enterText(fp, 'AB:CD');
    await t.ensureVisible(find.text('Save'));
    await t.tap(find.text('Save'));
    await t.pumpAndSettle();
    expect(servers.list, isEmpty);
    expect(find.textContaining('64 hex digits'), findsOneWidget);
    final hex = List.generate(32, (i) => (i * 7 % 256).toRadixString(16).padLeft(2, '0').toUpperCase());
    await t.enterText(fp, hex.join(':'));
    await t.ensureVisible(find.text('Save'));
    await t.tap(find.text('Save'));
    await t.pumpAndSettle();
    expect(servers.list.single.fingerprint, hex.join().toLowerCase());
  });

  test('server addresses must be https, except this PC', () {
    expect(serverAddressProblem('https://plant.local'), isNull);
    expect(serverAddressProblem('http://plant.local'), isNotNull);
    expect(serverAddressProblem('http://localhost:5000'), isNull);
    expect(serverAddressProblem('plant.local'), isNotNull);
  });

  group('floor', () {
    Future<Recorder> floor(WidgetTester t, List<List<Map<String, Object?>>> answers, {int status = 200}) async {
      var i = 0;
      final client = MockClient((r) async {
        expect(r.headers['X-Api-Key'], 'key');
        final body = answers[i < answers.length ? i : answers.length - 1];
        i++;
        return http.Response(jsonEncode(body), status);
      });
      final rec = Recorder();
      final look = PcLook();
      await t.pumpWidget(MaterialApp(
        theme: pcTheme(Brightness.dark),
        home: FloorScreen(
          server: PcServer(id: 'a', product: PcProduct.vision, name: 'Unit 1', baseUrl: Uri.parse('https://plant.local')),
          api: VisionApi(client, Uri.parse('https://plant.local'), 'key'),
          feedback: PcFeedback(look, player: rec),
          every: const Duration(seconds: 5),
        ),
      ));
      await t.pump();
      return rec;
    }

    testWidgets('lines are shown worst first, with state words and shapes', (t) async {
      await floor(t, [[line('Line 1', 'running'), line('Line 5', 'stopped'), line('Line 3', 'changeover')]]);
      final ys = ['Line 5', 'Line 3', 'Line 1'].map((n) => t.getTopLeft(find.text(n)).dy).toList();
      expect(ys, orderedEquals([...ys]..sort()));
      expect(find.text('✗ Stopped'), findsOneWidget);
      expect(find.text('✓ Running'), findsOneWidget);
    });

    testWidgets('a new alarm sounds by priority; alarms open at the first load do not', (t) async {
      final rec = await floor(t, [
        [line('Line 1', 'running', alarms: [{'station': 'S1', 'code': 'focus', 'priority': 'low', 'since': 1}])],
        [line('Line 1', 'stopped', alarms: [
          {'station': 'S1', 'code': 'focus', 'priority': 'low', 'since': 1},
          {'station': 'S1', 'code': 'plc_lost', 'priority': 'high', 'since': 2},
        ])],
      ]);
      expect(rec.played, isEmpty);
      await t.pump(const Duration(seconds: 5));
      await t.pump(const Duration(seconds: 1));
      expect(rec.played, [PcSound.critical]);
      expect(find.text('! 2 open alarms'), findsOneWidget);
    });

    testWidgets('a slow answer is not overtaken: ticks and a pull join the read in flight', (t) async {
      final gates = <Completer<http.Response>>[];
      final client = MockClient((r) {
        final g = Completer<http.Response>();
        gates.add(g);
        return g.future;
      });
      final rec = Recorder();
      await t.pumpWidget(MaterialApp(
        theme: pcTheme(Brightness.dark),
        home: FloorScreen(
          server: PcServer(id: 'a', product: PcProduct.vision, name: 'Unit 1', baseUrl: Uri.parse('https://plant.local')),
          api: VisionApi(client, Uri.parse('https://plant.local'), 'key'),
          feedback: PcFeedback(PcLook(), player: rec),
        ),
      ));
      await t.pump();
      expect(gates, hasLength(1));
      gates[0].complete(http.Response(jsonEncode([line('Line 1', 'running')]), 200)); // the first read only learns
      await t.pump();
      expect(find.text('✓ Running'), findsOneWidget);

      await t.pump(const Duration(seconds: 5)); // tick: read 2 starts, and is slow
      expect(gates, hasLength(2));
      await t.pump(const Duration(seconds: 5)); // tick while in flight: skipped
      final pulled = t.widget<RefreshIndicator>(find.byType(RefreshIndicator)).onRefresh(); // the pull joins it too
      expect(gates, hasLength(2));
      gates[1].complete(http.Response(jsonEncode([line('Line 1', 'stopped', alarms: [{'station': 'S1', 'code': 'plc_lost', 'priority': 'high', 'since': 2}])]), 200));
      await pulled;
      await t.pump();
      expect(rec.played, [PcSound.critical]);
      expect(find.text('✗ Stopped'), findsOneWidget);

      await t.pump(const Duration(seconds: 5)); // read 3: the same alarm again must not sound again
      expect(gates, hasLength(3));
      gates[2].complete(http.Response(jsonEncode([line('Line 1', 'stopped', alarms: [{'station': 'S1', 'code': 'plc_lost', 'priority': 'high', 'since': 2}])]), 200));
      await t.pump();
      expect(rec.played, [PcSound.critical]);
    });

    testWidgets('the same station alarm on another line is a new alarm', (t) async {
      final a = {'station': 'S1', 'code': 'focus', 'priority': 'low', 'since': 1};
      final rec = await floor(t, [
        [line('Line 1', 'running', alarms: [a])],
        [line('Line 1', 'running', alarms: [a]), line('Line 2', 'running', alarms: [a])],
      ]);
      await t.pump(const Duration(seconds: 5));
      await t.pump(const Duration(seconds: 1));
      expect(rec.played, [PcSound.notice]);
    });

    testWidgets('a floor of one plant shows plain line names', (t) async {
      await floor(t, [[line('Line 1', 'running'), line('Line 2', 'running')]]);
      expect(find.text('Line 1'), findsOneWidget);
      expect(find.text('P1 · Line 1'), findsNothing);
    });

    testWidgets('the plant name leads the line name when the floor holds more than one plant', (t) async {
      await floor(t, [[line('Line 1', 'running', plant: 'P1'), line('Line 1', 'running', plant: 'P2')]]);
      expect(find.text('P1 · Line 1'), findsOneWidget);
      expect(find.text('P2 · Line 1'), findsOneWidget);
    });

    testWidgets('a line view opened from the floor follows later reads', (t) async {
      await floor(t, [[line('Line 1', 'running')], [line('Line 1', 'stopped')]]);
      await t.tap(find.text('Line 1'));
      await t.pumpAndSettle();
      final inLine = find.descendant(of: find.byType(LineScreen), matching: find.text('✓ Running'));
      expect(inLine, findsOneWidget);
      await t.pump(const Duration(seconds: 5));
      await t.pump(const Duration(seconds: 1));
      expect(find.descendant(of: find.byType(LineScreen), matching: find.text('✗ Stopped')), findsOneWidget);
      expect(inLine, findsNothing);
    });

    testWidgets('large text and a narrow phone: the line cards do not overflow', (t) async {
      await t.binding.setSurfaceSize(const Size(360, 2000));
      addTearDown(() => t.binding.setSurfaceSize(null));
      final lines = [
        line('Line 1', 'starved', alarms: [{'station': 'S1', 'code': 'focus', 'priority': 'low', 'since': 1}], ng: [{'ts': 1, 'station': 'S1', 'tool': 'caliper', 'reason': 'date_unreadable'}]),
        line('Line 2', 'running', plant: 'Plant two'),
      ];
      await t.pumpWidget(MaterialApp(
        theme: pcTheme(Brightness.dark),
        builder: (context, child) => MediaQuery(data: MediaQuery.of(context).copyWith(textScaler: const TextScaler.linear(2.0)), child: child!),
        home: FloorScreen(
          server: PcServer(id: 'a', product: PcProduct.vision, name: 'Unit 1', baseUrl: Uri.parse('https://plant.local')),
          api: VisionApi(MockClient((_) async => http.Response(jsonEncode(lines), 200)), Uri.parse('https://plant.local'), 'key'),
          feedback: PcFeedback(PcLook(), player: Recorder()),
        ),
      ));
      await t.pump();
      expect(find.byType(PcRing), findsNWidgets(2));
      expect(t.takeException(), isNull);
    });

    testWidgets('a refused key says what to do', (t) async {
      await floor(t, [[]], status: 401);
      expect(find.textContaining('key was not accepted'), findsOneWidget);
    });
  });

  testWidgets('the line view counts NG reasons and lists alarms', (t) async {
    final l = LineLive.fromJson(line('Line 4', 'running', ng: [
      {'ts': 1, 'station': 'S1', 'tool': 'caliper', 'reason': 'too_large'},
      {'ts': 2, 'station': 'S1', 'tool': 'caliper', 'reason': 'too_large'},
      {'ts': 3, 'station': 'S1', 'tool': 'ocv', 'reason': 'date_unreadable'},
    ], alarms: [{'station': 'S2', 'code': 'cam_dark', 'priority': 'medium', 'since': 5, 'acknowledged_by': 'ravi'}]));
    await t.pumpWidget(MaterialApp(theme: pcTheme(Brightness.light), home: LineScreen(line: l)));
    expect(find.text('Too large'), findsOneWidget);
    expect(find.text('2'), findsOneWidget);
    expect(find.text('Cam dark'), findsOneWidget);
    expect(find.textContaining('ravi is on it'), findsOneWidget);
  });

  testWidgets('an alarm shows the server\'s catalogue title, fix and station name', (t) async {
    final l = LineLive.fromJson(line('Line 4', 'running', alarms: [
      {'station': 'S2', 'station_name': 'Case packer 2', 'code': 'parent_camera_error', 'priority': 'high', 'since': 5,
       'title': 'Case camera not working', 'fix': 'Check its cable.'},
    ]));
    await t.pumpWidget(MaterialApp(theme: pcTheme(Brightness.light), home: LineScreen(line: l)));
    expect(find.text('Case camera not working'), findsOneWidget);
    expect(find.textContaining('Case packer 2 · Check its cable.'), findsOneWidget);
    expect(find.textContaining('S2'), findsNothing);
  });

  test('a line with odd fields still reads', () {
    final l = LineLive.fromJson({'line': 'L', 'yield_pct': 'x', 'job': 7, 'alarms': [1, {'code': 'a', 'acknowledged_by': 3}], 'last_ng': null});
    expect((l.line, l.yieldPct, l.job, l.alarms.single.code, l.alarms.single.acknowledgedBy), ('L', null, null, 'a', null));
    expect(l.lastNg, isEmpty);
    expect(lineLook('nonsense').$2, 'Unknown');
  });

  testWidgets('the line view follows the floor, and says "As of" when the floor is old', (t) async {
    LineLive mk(String state, {String plant = 'P1'}) => LineLive.fromJson(line('Line 1', state, plant: plant));
    final feed = ValueNotifier<FloorSnapshot?>(FloorSnapshot([mk('running')], DateTime.now()));
    await t.pumpWidget(MaterialApp(theme: pcTheme(Brightness.dark), home: LineScreen(line: mk('running'), feed: feed)));
    expect(find.text('✓ Running'), findsOneWidget);
    expect(find.textContaining('As of'), findsNothing);

    // The same name in another plant is another line.
    feed.value = FloorSnapshot([mk('fault', plant: 'P2'), mk('stopped')], DateTime.now());
    await t.pump();
    expect(find.text('✗ Stopped'), findsOneWidget);
    expect(find.text('✗ Fault'), findsNothing);

    final old = DateTime.now().subtract(const Duration(seconds: 30));
    feed.value = FloorSnapshot([mk('stopped')], old);
    await t.pump();
    expect(find.text('As of ${hhmm(old)}'), findsOneWidget);

    feed.value = FloorSnapshot([mk('running')], DateTime.now());
    await t.pump();
    expect(find.textContaining('As of'), findsNothing);
    expect(find.text('✓ Running'), findsOneWidget);

    // A line that leaves the floor keeps its last values.
    feed.value = FloorSnapshot(const [], DateTime.now());
    await t.pump();
    expect(find.text('✓ Running'), findsOneWidget);
  });

  testWidgets('the line view notices by itself when the floor goes quiet', (t) async {
    final feed = ValueNotifier<FloorSnapshot?>(FloorSnapshot([LineLive.fromJson(line('Line 1', 'running'))], DateTime.now()));
    await t.pumpWidget(MaterialApp(
      theme: pcTheme(Brightness.dark),
      // 2 s, well over a cold start of the test (300 ms was not when the test ran alone).
      home: LineScreen(line: LineLive.fromJson(line('Line 1', 'running')), feed: feed, staleAfter: const Duration(seconds: 2)),
    ));
    expect(find.textContaining('As of'), findsNothing);
    await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 2100)));
    await t.pump(const Duration(seconds: 2));
    expect(find.textContaining('As of'), findsOneWidget);
  });

  testWidgets('the NG strip opens a part the server named; one without a result id does not open', (t) async {
    final asked = <String>[];
    final client = MockClient((q) async {
      asked.add(q.url.path);
      if (q.url.path == '/vision/ng/41') {
        return http.Response(jsonEncode({'id': 41, 'plant': 'P1', 'line': 'Line 1', 'station': 'S2', 'ts': 1, 'camera': 'cam2',
          'reasons': [{'tool': 'code_read', 'reason': 'no_code'}], 'images': [], 'image_state': 'none', 'verdict': 'pending'}), 200);
      }
      return http.Response('', 404);
    });
    final base = Uri.parse('https://plant.local');
    final ng = NgApi(client, base, 'key', auth: ApiAuth(client, base, key: 'key'));
    final l = LineLive.fromJson(line('Line 1', 'running', ng: [
      {'ts': 2, 'station': 'S2', 'camera': 'cam2', 'tool': 'code_read', 'reason': 'no_code', 'result_id': 41},
      {'ts': 1, 'station': 'S1', 'tool': 'caliper', 'reason': 'too_large', 'result_id': null},
    ]));
    expect([for (final n in l.lastNg) (n.resultId, n.camera)], [(41, 'cam2'), (null, null)]);
    await t.pumpWidget(MaterialApp(theme: pcTheme(Brightness.dark), home: LineScreen(line: l, ng: ng, feedback: PcFeedback(PcLook()))));
    expect(find.byTooltip('Sign in'), findsOneWidget); // the account button
    await t.scrollUntilVisible(find.byKey(const ValueKey('ng-strip-1')), 100);
    expect(find.text('Last NG parts'), findsOneWidget);
    await t.ensureVisible(find.byKey(const ValueKey('ng-strip-1')));
    await t.pumpAndSettle();
    await t.tap(find.byKey(const ValueKey('ng-strip-1')));
    await t.pumpAndSettle();
    expect(asked, isEmpty);
    await t.ensureVisible(find.byKey(const ValueKey('ng-strip-0')));
    await t.pumpAndSettle();
    await t.tap(find.byKey(const ValueKey('ng-strip-0')));
    await t.pumpAndSettle();
    expect(find.text('NG part'), findsOneWidget);
    expect(asked, contains('/vision/ng/41'));
  });

  testWidgets('a part with two reasons is one row of the strip', (t) async {
    final l = LineLive.fromJson(line('Line 1', 'running', ng: [
      {'ts': 2, 'station': 'S1', 'tool': 'code_read', 'reason': 'no_code', 'result_id': 7},
      {'ts': 2, 'station': 'S1', 'tool': 'caliper', 'reason': 'too_large', 'result_id': 7},
      {'ts': 1, 'station': 'S1', 'tool': 'caliper', 'reason': 'too_large', 'result_id': 6},
    ]));
    await t.pumpWidget(MaterialApp(theme: pcTheme(Brightness.dark), home: LineScreen(line: l)));
    await t.scrollUntilVisible(find.byKey(const ValueKey('ng-strip-1')), 100);
    expect(find.text('Code read: No code, Caliper: Too large'), findsOneWidget);
    expect(find.byKey(const ValueKey('ng-strip-2')), findsNothing);
  });

  testWidgets('without the NG review the strip shows the parts but opens none, and there is no account button', (t) async {
    final l = LineLive.fromJson(line('Line 1', 'running', ng: [{'ts': 2, 'station': 'S2', 'tool': 't', 'reason': 'r', 'result_id': 41}]));
    await t.pumpWidget(MaterialApp(theme: pcTheme(Brightness.dark), home: LineScreen(line: l)));
    expect(find.byTooltip('Sign in'), findsNothing);
    await t.scrollUntilVisible(find.byKey(const ValueKey('ng-strip-0')), 100);
    expect(t.widget<ListTile>(find.byKey(const ValueKey('ng-strip-0'))).onTap, isNull);
  });

  group('vision api', () {
    VisionApi api(http.Client c, [String base = 'https://plant.local']) => VisionApi(c, Uri.parse(base), 'key');

    test('list fields of the wrong type read as empty, not as a failure', () async {
      final lines = await api(MockClient((_) async => http.Response(jsonEncode([
            {'plant': 'P1', 'line': 'L', 'alarms': {'x': 1}, 'stations': 'nope', 'last_ng': 5},
          ]), 200))).floor();
      expect(lines.single.alarms, isEmpty);
      expect(lines.single.lastNg, isEmpty);
      expect((lines.single.stations, lines.single.stationsOnline), (0, 0));
    });

    test('an answer that is not the floor becomes one plain message', () async {
      for (final body in ['not json', '{"a":1}', '\u0000']) {
        await expectLater(
          api(MockClient((_) async => http.Response(body, 200))).floor(),
          throwsA(isA<VisionApiException>().having((e) => e.message, 'message', 'The plant server sent something unexpected.')),
          reason: body,
        );
      }
      await expectLater(
        api(MockClient((_) async => http.Response.bytes([0xff, 0xfe, 0xfd], 200))).floor(),
        throwsA(isA<VisionApiException>()),
      );
    });

    test('paths resolve below a base that has a path', () async {
      for (final (base, want) in [
        ('https://host/plant', 'https://host/plant/vision/floor'),
        ('https://host/plant/', 'https://host/plant/vision/floor'),
        ('https://host', 'https://host/vision/floor'),
      ]) {
        Uri? got;
        await api(MockClient((r) async {
          got = r.url;
          return http.Response('[]', 200);
        }), base).floor();
        expect(got.toString(), want, reason: base);
      }
    });

    test('an alarm is named by plant, line, station, code and start', () {
      final l = LineLive.fromJson(line('Line 1', 'running', alarms: [{'station': 'S1', 'code': 'focus', 'priority': 'low', 'since': 7}]));
      expect(l.alarms.single.key, 'P1/Line 1/S1/focus/7');
    });
  });

  testWidgets('a server gets a new key, or is removed after asking', (t) async {
    final (_, servers, _) = await start(t);
    await servers.add(PcServer(id: 'a', product: PcProduct.vision, name: 'Unit 1', baseUrl: Uri.parse('https://plant.local')), token: 'old');
    await t.pumpAndSettle();

    await t.tap(find.byIcon(Icons.more_vert));
    await t.pumpAndSettle();
    await t.tap(find.text('Change key'));
    await t.pumpAndSettle();
    expect(t.widget<TextField>(find.byType(TextField)).obscureText, isTrue);
    expect(t.widget<FilledButton>(find.widgetWithText(FilledButton, 'Save')).onPressed, isNull); // nothing typed yet
    await t.enterText(find.byType(TextField), 'new-key');
    await t.pump();
    await t.tap(find.widgetWithText(FilledButton, 'Save'));
    await t.pumpAndSettle();
    expect(await servers.token('a'), 'new-key');
    expect((servers.list.single.id, servers.list.single.name, servers.list.single.baseUrl.toString()), ('a', 'Unit 1', 'https://plant.local'));

    await t.tap(find.byIcon(Icons.more_vert));
    await t.pumpAndSettle();
    await t.tap(find.text('Remove'));
    await t.pumpAndSettle();
    expect(find.text('Remove Unit 1?'), findsOneWidget);
    await t.tap(find.text('Cancel'));
    await t.pumpAndSettle();
    expect(servers.list, hasLength(1));

    await t.tap(find.byIcon(Icons.more_vert));
    await t.pumpAndSettle();
    await t.tap(find.text('Remove'));
    await t.pumpAndSettle();
    await t.tap(find.widgetWithText(FilledButton, 'Remove'));
    await t.pumpAndSettle();
    expect(servers.list, isEmpty);
    expect(await servers.token('a'), isNull);
    expect(find.text('Unit 1'), findsNothing);
  });

  // Requirements 17.3, MOB-TLS-05.
  testWidgets('a saved server shows its fingerprint shortened or in full, and takes a new one checked as when added', (t) async {
    final (_, servers, _) = await start(t);
    final old = 'ab' * 32, renewed = '0123456789abcdef' * 4;
    await servers.add(PcServer(id: 'a', product: PcProduct.vision, name: 'Unit 1', baseUrl: Uri.parse('https://plant.local'), fingerprint: old), token: 'key');
    await t.pumpAndSettle();
    Future<void> open() async {
      await t.tap(find.byIcon(Icons.more_vert));
      await t.pumpAndSettle();
      await t.tap(find.text('Certificate fingerprint'));
      await t.pumpAndSettle();
    }

    String shown() => t.widget<SelectableText>(find.byKey(const ValueKey('current-fingerprint'))).data!;
    FilledButton save() => t.widget<FilledButton>(find.widgetWithText(FilledButton, 'Save'));

    await open();
    expect(shown(), 'AB:AB:AB:AB … AB:AB:AB:AB');
    await t.tap(find.text('Show in full'));
    await t.pump();
    expect(shown(), List.filled(32, 'AB').join(':'));
    await t.tap(find.text('Show less'));
    await t.pump();
    expect(shown(), 'AB:AB:AB:AB … AB:AB:AB:AB');

    // Not a fingerprint, or the same one again: nothing to save.
    expect(save().onPressed, isNull);
    await t.enterText(find.byKey(const ValueKey('new-fingerprint')), '12:34');
    await t.pump();
    expect(find.text('A SHA-256 fingerprint is 64 hex digits (0-9, A-F); colons are fine'), findsOneWidget);
    expect(save().onPressed, isNull);
    await t.enterText(find.byKey(const ValueKey('new-fingerprint')), old.toUpperCase());
    await t.pump();
    expect(save().onPressed, isNull);

    // Cancel keeps the old one.
    await t.tap(find.text('Cancel'));
    await t.pumpAndSettle();
    expect(servers.list.single.fingerprint, old);

    // A renewed certificate's fingerprint, typed with colons and capitals, is saved; the key stays.
    await open();
    final typed = [for (var i = 0; i < 64; i += 2) renewed.substring(i, i + 2)].join(':').toUpperCase();
    await t.enterText(find.byKey(const ValueKey('new-fingerprint')), typed);
    await t.pump();
    expect(find.text('A SHA-256 fingerprint is 64 hex digits (0-9, A-F); colons are fine'), findsNothing);
    await t.tap(find.widgetWithText(FilledButton, 'Save'));
    await t.pumpAndSettle();
    final s = servers.list.single;
    expect((s.id, s.name, s.baseUrl.toString(), s.fingerprint), ('a', 'Unit 1', 'https://plant.local', renewed));
    expect(await servers.token('a'), 'key');

    // It can be removed; then none is shown.
    await open();
    expect(shown(), '01:23:45:67 … 89:AB:CD:EF');
    await t.tap(find.text('Remove fingerprint'));
    await t.pumpAndSettle();
    expect(servers.list.single.fingerprint, isNull);
    await open();
    expect(find.byKey(const ValueKey('current-fingerprint')), findsNothing);
    expect(find.text('None. Only a server with its own certificate needs one.'), findsOneWidget);
    expect(find.text('Remove fingerprint'), findsNothing);
  });

  testWidgets('two quick taps on Save add one server', (t) async {
    final secrets = _CountingStore();
    final servers = PcServers(MemoryStore(), secrets);
    final look = PcLook();
    await t.pumpWidget(PlantApp(look: look, servers: servers, feedback: PcFeedback(look, player: Recorder()), client: MockClient((_) async => http.Response('[]', 200))));
    await t.tap(find.text('Add a server'));
    await t.pumpAndSettle();
    await t.enterText(find.widgetWithText(TextFormField, 'Name'), 'Unit 1');
    await t.enterText(find.widgetWithText(TextFormField, 'Address'), 'https://plant.local');
    await t.enterText(find.widgetWithText(TextFormField, 'Key'), 'k1');
    // Two taps before the screen has rebuilt both reach the same handler.
    final save = t.widget<FilledButton>(find.widgetWithText(FilledButton, 'Save')).onPressed!;
    save();
    save();
    await t.pumpAndSettle();
    expect(servers.list, hasLength(1));
    expect(secrets.tokenWrites, 1);
  });

  testWidgets('a client passed to the app is not closed by it', (t) async {
    final client = _TrackedClient();
    final look = PcLook();
    await t.pumpWidget(PlantApp(look: look, servers: PcServers(MemoryStore(), MemoryStore()), feedback: PcFeedback(look), client: client));
    await t.pumpWidget(const SizedBox());
    expect(client.closed, isFalse);
  });
}
