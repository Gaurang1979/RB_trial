import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:plant_app/modules/vision/api_auth.dart';
import 'package:plant_app/modules/vision/changeover_api.dart';
import 'package:plant_app/modules/vision/changeover_screen.dart';
import 'package:plant_app/modules/vision/floor_screen.dart';
import 'package:plant_app/modules/vision/vision_api.dart';
import 'package:plant_core/plant_core.dart';

const _code = '3fa1c2d09b7e4a5f6c8d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2b';

Map<String, Object?> record({String lot = 'L2611', String mrp = '120.00'}) => {
      'job': 'Carton 24', 'job_version': 7, 'variant': '',
      'batch': [
        {'key': 'lot', 'label': 'Lot', 'value': lot},
        {'key': 'mrp', 'label': 'MRP', 'value': mrp},
      ],
    };

Map<String, Object?> item(int id, {bool changed = true, Object? previous = 'default', String state = 'pending'}) => {
      'id': id, 'station': 'S$id', 'plant': 'P1', 'line': 'Line 1', 'code': _code, 'ask': 1, 'record': record(),
      'operator': 'ravi', 'changed_at': 1760000000000, 'asked_at': 1760000005000, 'state': state,
      'decided_name': null, 'decided_at': null, 'note': null,
      'previous': previous == 'default'
          ? {'id': 1, 'code': 'c' * 64, 'short_code': 'cccccccc', 'record': record(lot: 'L2610'), 'decided_name': 'Priya', 'decided_at': 1}
          : previous,
      'changes': changed ? [{'key': 'lot', 'label': 'Lot', 'old': 'L2610', 'new': 'L2611'}] : [],
    };

final _dead = MockClient((_) async => http.Response('', 500));

ApiAuth _auth(http.Client c, {bool signedIn = true}) =>
    ApiAuth(c, Uri.parse('https://plant.local'), key: 'key', session: signedIn ? const PcSession(token: 'tok', name: 'Priya') : null);

/// The server, as far as the screen sees it.
class FakeChangeover extends ChangeoverApi {
  FakeChangeover(this.lists, {bool signedIn = true}) : super(_dead, Uri.parse('https://plant.local'), 'key', auth: _auth(_dead, signedIn: signedIn));
  final List<List<ChangeoverItem>> lists;
  int reads = 0;
  final decisions = <(String, ChangeoverDecision, String)>[];
  Object? decideError;

  @override
  Future<List<ChangeoverItem>> list({String? plant, String? line, bool all = false, int limit = 100}) async {
    reads++;
    return lists[reads - 1 < lists.length ? reads - 1 : lists.length - 1];
  }

  @override
  Future<void> decide(String id, ChangeoverDecision decision, {String note = ''}) async {
    final e = decideError;
    if (e != null) throw e;
    decisions.add((id, decision, note.trim()));
  }
}

List<ChangeoverItem> items(List<int> ids, {bool changed = true}) => [for (final i in ids) ChangeoverItem.fromJson(item(i, changed: changed))!];

void main() {
  setUp(() {
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (_) async => null);
  });

  group('changeover parsing', () {
    test('a full request reads every field', () {
      final i = ChangeoverItem.fromJson({...item(5), 'decided_name': 'Priya Shah', 'decided_at': 9, 'note': 'ok', 'state': 'accepted'})!;
      expect((i.id, i.station, i.plant, i.line, i.code, i.ask, i.operator, i.changedAt, i.askedAt), ('5', 'S5', 'P1', 'Line 1', _code, 1, 'ravi', 1760000000000, 1760000005000));
      expect((i.state, i.pending, i.decidedName, i.decidedAt, i.note), ('accepted', false, 'Priya Shah', 9, 'ok'));
      expect(i.shortCode, '3fa1c2d0');
      expect((i.record.job, i.record.jobVersion, i.record.variant), ('Carton 24', 7, ''));
      expect(i.record.batch.map((f) => (f.key, f.label, f.value)), [('lot', 'Lot', 'L2611'), ('mrp', 'MRP', '120.00')]);
      expect(i.previous!.batch.first.value, 'L2610');
      expect((i.changes.single.key, i.changes.single.old, i.changes.single.now), ('lot', 'L2610', 'L2611'));
    });

    test('fields of the wrong type or missing read as empty; a request without an id is skipped; no previous is null', () {
      final i = ChangeoverItem.fromJson({'id': 3, 'record': 'x', 'changes': {'a': 1}, 'changed_at': 'soon', 'previous': null})!;
      expect((i.record.job, i.changedAt, i.previous, i.station, i.state), ('', 0, null, '', 'pending'));
      expect(i.record.batch, isEmpty);
      expect(i.changes, isEmpty);
      expect(ChangeoverItem.fromJson({'station': 'S1'}), isNull);
      expect(ChangeoverItem.fromJson('x'), isNull);
      // A new field has no old value, a removed one no new value.
      final c = ChangeoverItem.fromJson({'id': 1, 'changes': [{'key': 'a', 'new': 'x'}, {'key': 'b', 'old': 'y'}, 5, {'label': 'no key'}]})!.changes;
      expect(c.map((x) => (x.key, x.old, x.now)), [('a', null, 'x'), ('b', 'y', null)]);
    });

    test('a list is a bare list or {items}; anything else is refused', () {
      expect(ChangeoverItem.listFromJson([item(1), {'bad': 1}, item(2)]).map((i) => i.id), ['1', '2']);
      expect(ChangeoverItem.listFromJson({'items': [item(3)]}).single.id, '3');
      expect(() => ChangeoverItem.listFromJson('x'), throwsFormatException);
      expect(() => ChangeoverItem.listFromJson({'items': 5}), throwsFormatException);
    });
  });

  group('changeover api', () {
    ChangeoverApi api(http.Client c, {bool signedIn = false}) => ChangeoverApi(c, Uri.parse('https://plant.local/plant'), 'key',
        auth: ApiAuth(c, Uri.parse('https://plant.local/plant'), key: 'key', session: signedIn ? const PcSession(token: 'tok', name: 'Priya') : null));

    test('the list asks for pending requests of a line and carries the key', () async {
      late http.Request got;
      final r = await api(MockClient((q) async {
        got = q;
        return http.Response(jsonEncode([item(9)]), 200);
      })).list(plant: 'P1', line: 'Line 1', limit: 10);
      expect(got.url.path, '/plant/vision/changeover');
      expect(got.url.queryParameters, {'state': 'pending', 'limit': '10', 'plant': 'P1', 'line': 'Line 1'});
      expect(got.headers['X-Api-Key'], 'key');
      expect(r.single.id, '9');
      final all = MockClient((q) async {
        expect(q.url.queryParameters['state'], 'all');
        return http.Response('[]', 200);
      });
      await api(all).list(all: true);
    });

    test('an answer that is not a list becomes one plain message; a dead network too', () async {
      await expectLater(api(MockClient((_) async => http.Response('nope', 200))).list(),
          throwsA(isA<ChangeoverApiException>().having((e) => e.message, 'message', 'The plant server sent something unexpected.')));
      await expectLater(api(MockClient((_) async => http.Response('{}', 200))).list(), throwsA(isA<ChangeoverApiException>()));
      await expectLater(api(MockClient((_) async => throw http.ClientException('down'))).list(), throwsA(isA<ChangeoverApiException>().having((e) => e.code, 'code', 0)));
    });

    test('a decision is posted as JSON, as the signed-in person, never with the key', () async {
      late http.Request got;
      await api(MockClient((q) async {
        got = q;
        return http.Response('{"ok":true}', 200);
      }), signedIn: true).decide('12', ChangeoverDecision.refuse, note: '  lot is wrong ');
      expect(got.method, 'POST');
      expect(got.url.path, '/plant/vision/changeover/12/decide');
      expect(jsonDecode(got.body), {'decision': 'refuse', 'note': 'lot is wrong'});
      expect(got.headers['Authorization'], 'Bearer tok');
      expect(got.headers.containsKey('X-Api-Key'), isFalse);
    });

    test('without a sign-in, or a refusal without a note, nothing is sent', () async {
      var sent = 0;
      final c = MockClient((_) async {
        sent++;
        return http.Response('{}', 200);
      });
      await expectLater(api(c).decide('1', ChangeoverDecision.accept), throwsA(isA<ChangeoverApiException>().having((e) => e.code, 'code', 401)));
      for (final note in ['', '   ']) {
        await expectLater(api(c, signedIn: true).decide('1', ChangeoverDecision.refuse, note: note),
            throwsA(isA<ChangeoverApiException>().having((e) => e.message, 'message', 'A refusal needs a note.')));
      }
      expect(sent, 0);
    });

    test('403, 409 and other answers become words to show', () async {
      Future<ChangeoverApiException> fail(int status, [String body = '']) async {
        try {
          await api(MockClient((_) async => http.Response(body, status)), signedIn: true).decide('1', ChangeoverDecision.accept);
        } on ChangeoverApiException catch (e) {
          return e;
        }
        throw StateError('did not fail');
      }

      expect((await fail(403, '{"detail":"You made this change yourself."}')).message, 'You made this change yourself.');
      expect((await fail(403)).message, contains('may not decide'));
      final conflict = await fail(409, '{"detail":"decided"}');
      expect(conflict.code, 409);
      expect(conflict.message, startsWith('Already decided'));
      expect((await fail(404)).message, contains('no longer'));
      expect((await fail(500)).message, 'The plant server answered 500.');
    });
  });

  group('changeover screen', () {
    Future<void> open(WidgetTester t, FakeChangeover api) async {
      await t.pumpWidget(MaterialApp(theme: pcTheme(Brightness.dark), home: ChangeoverScreen(api: api, feedback: PcFeedback(PcLook()))));
      await t.pump();
      await t.pump();
    }

    Future<void> openFirst(WidgetTester t, FakeChangeover api) async {
      await open(t, api);
      await t.tap(find.byKey(const ValueKey('changeover-3')));
      await t.pumpAndSettle();
    }

    testWidgets('the pending list shows line, station, operator, code and the number of changes', (t) async {
      await open(t, FakeChangeover([items([3, 2])]));
      expect(find.textContaining('2 waiting'), findsOneWidget);
      expect(find.text('Line 1 · S3'), findsOneWidget);
      expect(find.text('Line 1 · S2'), findsOneWidget);
      expect(find.textContaining('by ravi'), findsNWidgets(2));
      expect(find.text('Code 3fa1c2d0'), findsNWidgets(2));
      expect(find.textContaining('1 change'), findsNWidgets(2));
    });

    testWidgets('an empty list says nothing is waiting', (t) async {
      await open(t, FakeChangeover([[]]));
      expect(find.text('No changeover is waiting for a check.'), findsOneWidget);
    });

    testWidgets('the detail shows the code, operator, every field, and the change old to new, highlighted', (t) async {
      await openFirst(t, FakeChangeover([items([3])]));
      expect(find.text('Code 3fa1c2d0'), findsOneWidget);
      expect(find.textContaining('Changed by ravi'), findsOneWidget);
      expect(find.text('Carton 24'), findsOneWidget);
      expect(find.text('120.00'), findsOneWidget); // an unchanged field shows its value
      // The changed field shows both values and is marked in words and shape, not colour alone.
      expect(find.text('L2610'), findsOneWidget);
      expect(find.text('L2611'), findsOneWidget);
      expect(find.text('! Lot · changed'), findsOneWidget);
      expect(find.text('MRP'), findsOneWidget);
      expect(find.text('! MRP · changed'), findsNothing);
      final changed = t.widget<Container>(find.ancestor(of: find.text('! Lot · changed'), matching: find.byType(Container)).first);
      expect((changed.decoration! as BoxDecoration).color, PcStatus.warn.background(PcColors.dark));
    });

    testWidgets('a field that is gone, and a first record with nothing to compare, are said plainly', (t) async {
      final gone = ChangeoverItem.fromJson({
        ...item(3),
        'changes': [{'key': 'expiry', 'label': 'Expiry', 'old': '2027-01', 'new': null}],
      })!;
      await openFirst(t, FakeChangeover([[gone]]));
      expect(find.text('Also changed'), findsOneWidget);
      expect(find.text('2027-01'), findsOneWidget);
      expect(find.text('—'), findsOneWidget);
      await t.pageBack();
      await t.pumpAndSettle();
      final first = ChangeoverItem.fromJson(item(3, changed: false, previous: null))!;
      await t.pumpWidget(const SizedBox());
      await t.pumpWidget(MaterialApp(theme: pcTheme(Brightness.dark), home: ChangeoverScreen(api: FakeChangeover([[first]]), feedback: PcFeedback(PcLook()))));
      await t.pump();
      await t.pump();
      await t.tap(find.byKey(const ValueKey('changeover-3')));
      await t.pumpAndSettle();
      expect(find.textContaining('nothing to compare'), findsOneWidget);
    });

    testWidgets('accept sends the decision, closes the request and reads the list again', (t) async {
      final api = FakeChangeover([items([3, 2]), items([2])]);
      await openFirst(t, api);
      await t.tap(find.byKey(const ValueKey('changeover-accept')));
      await t.pumpAndSettle();
      expect(api.decisions, [('3', ChangeoverDecision.accept, '')]);
      expect(api.reads, 2);
      expect(find.byKey(const ValueKey('changeover-3')), findsNothing);
      expect(find.byKey(const ValueKey('changeover-2')), findsOneWidget);
      expect(find.textContaining('Accepted: S3 · 3fa1c2d0'), findsOneWidget);
    });

    testWidgets('without a sign-in the request can be read but there is no Accept or Refuse; Sign in is offered', (t) async {
      final api = FakeChangeover([items([3])], signedIn: false);
      await openFirst(t, api);
      expect(find.text('Carton 24'), findsOneWidget);
      expect(find.byKey(const ValueKey('changeover-accept')), findsNothing);
      expect(find.byKey(const ValueKey('changeover-refuse')), findsNothing);
      expect(find.text('Sign in with your own account to accept or refuse.'), findsOneWidget);
      expect(find.byKey(const ValueKey('changeover-sign-in')), findsOneWidget);
      expect(api.decisions, isEmpty);
    });

    testWidgets('refuse opens a note field; an empty note is not sent; with a note it is', (t) async {
      final api = FakeChangeover([items([3, 2]), items([2])]);
      await openFirst(t, api);
      expect(find.byKey(const ValueKey('changeover-note')), findsNothing);
      await t.tap(find.byKey(const ValueKey('changeover-refuse')));
      await t.pumpAndSettle();
      expect(find.byKey(const ValueKey('changeover-note')), findsOneWidget);
      expect(find.byKey(const ValueKey('changeover-accept')), findsNothing); // only the refusal is on offer now
      await t.tap(find.byKey(const ValueKey('changeover-refuse-send')));
      await t.pump();
      expect(find.text('A refusal needs a note.'), findsOneWidget);
      await t.enterText(find.byKey(const ValueKey('changeover-note')), '   ');
      await t.tap(find.byKey(const ValueKey('changeover-refuse-send')));
      await t.pump();
      expect(api.decisions, isEmpty);
      await t.enterText(find.byKey(const ValueKey('changeover-note')), 'Lot is wrong');
      await t.tap(find.byKey(const ValueKey('changeover-refuse-send')));
      await t.pumpAndSettle();
      expect(api.decisions, [('3', ChangeoverDecision.refuse, 'Lot is wrong')]);
      expect(api.reads, 2);
      expect(find.textContaining('Refused: S3'), findsOneWidget);
    });

    testWidgets('409 says it was already decided, closes the request and reads the list again', (t) async {
      final api = FakeChangeover([items([3, 2]), items([2])])..decideError = ChangeoverApiException('Already decided, or replaced by a newer request.', 409);
      await openFirst(t, api);
      await t.tap(find.byKey(const ValueKey('changeover-accept')));
      await t.pumpAndSettle();
      expect(find.textContaining('Already decided'), findsOneWidget);
      expect(api.reads, 2);
      expect(api.decisions, isEmpty);
      expect(find.byKey(const ValueKey('changeover-3')), findsNothing);
      expect(find.byKey(const ValueKey('changeover-2')), findsOneWidget);
    });

    testWidgets('403 and no connection keep the request open with the reason; nothing is queued', (t) async {
      final api = FakeChangeover([items([3])])..decideError = ChangeoverApiException('You made this change yourself.', 403);
      await openFirst(t, api);
      await t.tap(find.byKey(const ValueKey('changeover-accept')));
      await t.pumpAndSettle();
      expect(find.text('You made this change yourself.'), findsOneWidget);
      expect(find.byKey(const ValueKey('changeover-accept')), findsOneWidget);
      api.decideError = ChangeoverApiException('Cannot reach the plant server.', 0);
      await t.tap(find.byKey(const ValueKey('changeover-accept')));
      await t.pumpAndSettle();
      expect(find.textContaining('The decision was not saved'), findsOneWidget);
      expect(api.decisions, isEmpty);
      expect(api.reads, 1); // still waiting: no refresh for a failure that changed nothing
      api.decideError = null;
      await t.tap(find.byKey(const ValueKey('changeover-accept')));
      await t.pumpAndSettle();
      expect(api.decisions, [('3', ChangeoverDecision.accept, '')]);
    });

    testWidgets('an unreachable server says so and, once something was shown, how old it is', (t) async {
      final api = _Flaky();
      await open(t, api);
      api.down = true;
      await t.tap(find.byTooltip('Reload the changeovers'));
      await t.pump();
      await t.pump();
      expect(find.textContaining('Showing the list as of'), findsOneWidget);
      expect(find.byKey(const ValueKey('changeover-3')), findsOneWidget);
    });

    testWidgets('large text and a narrow phone: the list and the request do not overflow', (t) async {
      t.view.physicalSize = const Size(320, 640);
      t.view.devicePixelRatio = 1;
      addTearDown(t.view.reset);
      await t.pumpWidget(MaterialApp(
        theme: pcTheme(Brightness.light),
        builder: (c, w) => MediaQuery(data: MediaQuery.of(c).copyWith(textScaler: const TextScaler.linear(1.6)), child: w!),
        home: ChangeoverScreen(api: FakeChangeover([items([3])]), feedback: PcFeedback(PcLook())),
      ));
      await t.pump();
      await t.pump();
      await t.tap(find.byKey(const ValueKey('changeover-3')));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const ValueKey('changeover-refuse')));
      await t.pumpAndSettle();
      expect(t.takeException(), isNull);
    });
  });

  group('changeover entry on the floor', () {
    testWidgets('the app bar shows how many changeovers wait; none, no badge; without the api, no entry', (t) async {
      var waiting = 2;
      final client = MockClient((r) async => r.url.path.endsWith('vision/changeover')
          ? http.Response(jsonEncode([for (var i = 0; i < waiting; i++) item(i + 1)]), 200)
          : http.Response('[]', 200));
      final base = Uri.parse('https://plant.local');
      final api = VisionApi(client, base, 'key');
      Widget floor({ChangeoverApi? changeover}) => MaterialApp(
            theme: pcTheme(Brightness.dark),
            home: FloorScreen(
              server: PcServer(id: 'a', product: PcProduct.vision, name: 'Unit 1', baseUrl: base),
              api: api,
              feedback: PcFeedback(PcLook()),
              changeover: changeover,
            ),
          );
      await t.pumpWidget(floor());
      await t.pump();
      expect(find.byIcon(Icons.swap_horiz), findsNothing);
      await t.pumpWidget(const SizedBox());
      await t.pumpWidget(floor(changeover: ChangeoverApi.of(api)));
      await t.pump();
      await t.pump();
      expect(find.byTooltip('Changeover: 2 waiting for a check'), findsOneWidget);
      expect(find.text('2'), findsOneWidget);
      // Opening it shows the list; going back reads the count again.
      waiting = 0;
      await t.tap(find.byTooltip('Changeover: 2 waiting for a check'));
      await t.pumpAndSettle();
      expect(find.text('No changeover is waiting for a check.'), findsOneWidget);
      await t.pageBack();
      await t.pumpAndSettle();
      expect(find.byTooltip('Changeover'), findsOneWidget);
      await t.pumpWidget(const SizedBox());
    });
  });
}

class _Flaky extends FakeChangeover {
  _Flaky() : super([items([3])]);
  bool down = false;
  @override
  Future<List<ChangeoverItem>> list({String? plant, String? line, bool all = false, int limit = 100}) async {
    if (down) throw ChangeoverApiException('Cannot reach the plant server.', 0);
    return super.list(plant: plant, line: line, all: all, limit: limit);
  }
}
