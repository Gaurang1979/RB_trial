import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:plant_app/modules/vision/api_auth.dart';
import 'package:plant_app/modules/vision/ng_api.dart';
import 'package:plant_app/modules/vision/ng_review_screen.dart';
import 'package:plant_core/plant_core.dart';

// A 1x1 PNG, enough for Image.memory to decode.
final _png = base64Decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==');

Map<String, Object?> part(int id, {String state = 'ready', List<Map<String, Object?>>? images, String station = 'S1'}) => {
      'id': id, 'plant': 'P1', 'line': 'Line 1', 'station': station, 'ts': 1760000000000 + id, 'camera': 'cam1', 'part_id': 'part-$id',
      'image_id': 'img$id', 'job': 'Carton-30', 'reasons': [{'tool': 'code_read', 'reason': 'no_code'}], 'cycle_ms': 210,
      'images': images ?? [{'camera': 'cam1', 'url': '/vision/ng/$id/image/cam1'}], 'image_state': state,
      'verdict': 'pending', 'reviewed_by': null, 'reviewed_at': null, 'note': null,
    };

final _dead = MockClient((_) async => http.Response('', 500));

/// The server, as far as the screen sees it.
class FakeNg extends NgApi {
  /// Signed in as a person unless [signedIn] is false: only a person gives verdicts.
  FakeNg(this.pages, {bool signedIn = true})
      : super(_dead, Uri.parse('https://plant.local'), 'key',
            auth: ApiAuth(_dead, Uri.parse('https://plant.local'), key: 'key', session: signedIn ? const PcSession(token: 'tok', name: 'Asha') : null));
  final List<NgPage> pages;
  final calls = <int?>[];
  final reviews = <(int, NgVerdict)>[];
  final notes = <String>[];
  final parts = <int, NgPart>{};
  Object? reviewError;
  int imageReads = 0;

  @override
  Future<NgPart> part(int id) async => parts[id] ?? (throw NgApiException('This part is not on the plant server.', 404));

  @override
  Future<NgPage> pending({String? plant, String? line, int? beforeId, int limit = 50}) async {
    calls.add(beforeId);
    return pages[calls.length - 1 < pages.length ? calls.length - 1 : pages.length - 1];
  }

  @override
  Future<Uint8List> image(String url) async {
    imageReads++;
    return _png;
  }

  @override
  Future<void> review(int id, NgVerdict verdict, {String note = ''}) async {
    final e = reviewError;
    if (e != null) throw e;
    reviews.add((id, verdict));
    notes.add(note);
  }
}

NgPage page(List<int> ids, [int? next]) => NgPage([for (final i in ids) NgPart.fromJson(part(i))!], next);

void main() {
  setUp(() {
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (_) async => null);
  });

  group('ng parsing', () {
    test('a full part reads every field', () {
      final p = NgPart.fromJson({...part(7), 'verdict': 'unsure', 'reviewed_by': 'asha', 'reviewed_at': 5, 'note': 'blurry'})!;
      expect((p.id, p.plant, p.line, p.station, p.camera, p.partId, p.imageId, p.job, p.cycleMs), (7, 'P1', 'Line 1', 'S1', 'cam1', 'part-7', 'img7', 'Carton-30', 210));
      expect((p.reasons.single.tool, p.reasons.single.reason), ('code_read', 'no_code'));
      expect((p.images.single.camera, p.images.single.url), ('cam1', '/vision/ng/7/image/cam1'));
      expect((p.imageState, p.verdict, p.verdictBy, p.verdictAt, p.note), ('ready', 'unsure', 'asha', 5, 'blurry'));
    });

    test('fields of the wrong type or missing read as empty; a part without an id is skipped', () {
      final p = NgPart.fromJson({'id': 3, 'reasons': 'x', 'images': {'a': 1}, 'ts': 'soon', 'image_state': 5, 'station': null})!;
      expect(p.reasons, isEmpty);
      expect(p.images, isEmpty);
      expect((p.ts, p.imageState, p.station, p.job), (0, 'none', '', null));
      expect(NgPart.fromJson({'plant': 'P1'}), isNull);
      expect(NgPart.fromJson('x'), isNull);
      expect(NgPart.fromJson({'id': '12'})!.id, 12);
      // An image entry with no address cannot be loaded and is left out.
      expect(NgPart.fromJson({'id': 1, 'images': [{'camera': 'c'}, 5, {'camera': 'd', 'url': '/u'}]})!.images.map((i) => i.camera), ['d']);
    });

    test('a page keeps the usable parts and next_before_id', () {
      final pg = NgPage.fromJson({'items': [part(5), {'bad': 1}, 7, part(4)], 'next_before_id': 4});
      expect(pg.items.map((p) => p.id), [5, 4]);
      expect(pg.nextBeforeId, 4);
      expect(NgPage.fromJson({'items': []}).nextBeforeId, isNull);
      expect(() => NgPage.fromJson([]), throwsFormatException);
      expect(() => NgPage.fromJson({'items': 'x'}), throwsFormatException);
    });
  });

  group('ng api', () {
    NgApi api(http.Client c, [String base = 'https://plant.local/plant']) => NgApi(c, Uri.parse(base), 'key');
    NgApi person(http.Client c) => NgApi(c, Uri.parse('https://plant.local/plant'), 'key',
        auth: ApiAuth(c, Uri.parse('https://plant.local/plant'), key: 'key', session: const PcSession(token: 'tok', name: 'Asha')));

    test('the queue is asked for pending parts of a line, with before_id, and carries the key', () async {
      late http.Request got;
      final r = await api(MockClient((q) async {
        got = q;
        return http.Response(jsonEncode({'items': [part(9)], 'next_before_id': 9}), 200);
      })).pending(plant: 'P1', line: 'Line 1', beforeId: 20, limit: 10);
      expect(got.url.path, '/plant/vision/ng');
      expect(got.url.queryParameters, {'verdict': 'pending', 'limit': '10', 'plant': 'P1', 'line': 'Line 1', 'before_id': '20'});
      expect(got.headers['X-Api-Key'], 'key');
      expect(r.items.single.id, 9);
    });

    test('an answer that is not a queue becomes one plain message; a dead network too', () async {
      await expectLater(api(MockClient((_) async => http.Response('nope', 200))).pending(),
          throwsA(isA<NgApiException>().having((e) => e.message, 'message', 'The plant server sent something unexpected.')));
      await expectLater(api(MockClient((_) async => http.Response('[]', 200))).pending(), throwsA(isA<NgApiException>()));
      await expectLater(api(MockClient((_) async => throw http.ClientException('down'))).pending(),
          throwsA(isA<NgApiException>().having((e) => e.code, 'code', 0)));
    });

    test('an image is read from below the base path with the key; another host is refused', () async {
      final seen = <Uri>[];
      final a = api(MockClient((q) async {
        seen.add(q.url);
        expect(q.headers['X-Api-Key'], 'key');
        return http.Response.bytes(_png, 200);
      }));
      expect(await a.image('/vision/ng/1/image/cam1'), _png);
      expect(await a.image('vision/ng/2/image/cam1'), _png);
      expect(seen.map((u) => u.toString()), ['https://plant.local/plant/vision/ng/1/image/cam1', 'https://plant.local/plant/vision/ng/2/image/cam1']);
      // Nothing with a scheme, an authority or a backslash is sent anywhere: the key stays with the plant server.
      for (final bad in ['https://evil.example/x.jpg', 'https://plant.local/x.jpg', '//evil.com/x', r'\\evil.com\x', r'/\evil.com/x', 'http:x', '']) {
        await expectLater(a.image(bad), throwsA(isA<NgApiException>()), reason: bad);
      }
      expect(seen, hasLength(2));
    });

    test('one part is read by its id; an unknown one says it is not on the server', () async {
      late Uri got;
      final ok = person(MockClient((q) async {
        got = q.url;
        return http.Response(jsonEncode({...part(5), 'verdict': 'overruled', 'note': 'label fine', 'history': []}), 200);
      }));
      final p = await ok.part(5);
      expect(got.path, '/plant/vision/ng/5');
      expect((p.id, p.verdict, p.note), (5, 'overruled', 'label fine'));
      await expectLater(person(MockClient((_) async => http.Response('{"detail":"no NG part 5"}', 404))).part(5),
          throwsA(isA<NgApiException>().having((e) => e.message, 'message', 'This part is not on the plant server.')));
      // an answer about another part is not taken for this one
      await expectLater(person(MockClient((_) async => http.Response(jsonEncode(part(6)), 200))).part(5), throwsA(isA<NgApiException>()));
    });

    test('a note goes with the verdict; one over 500 characters is not sent', () async {
      final sent = <String>[];
      final a = person(MockClient((q) async {
        sent.add(q.body);
        return http.Response('{}', 200);
      }));
      await a.review(3, NgVerdict.confirmed, note: 'smudge on the label');
      expect(jsonDecode(sent.single), {'verdict': 'confirmed', 'note': 'smudge on the label'});
      await expectLater(a.review(3, NgVerdict.confirmed, note: '😀' * 501), throwsA(isA<NgApiException>()));
      expect(sent, hasLength(1));
    });

    test('a verdict is posted as JSON, as the signed-in person', () async {
      late http.Request got;
      await person(MockClient((q) async {
        got = q;
        return http.Response('{"ok":true}', 200);
      })).review(12, NgVerdict.overruled);
      expect(got.method, 'POST');
      expect(got.url.path, '/plant/vision/ng/12/review');
      expect(jsonDecode(got.body), {'verdict': 'overruled', 'note': ''});
      expect(got.headers['Authorization'], 'Bearer tok');
      expect(got.headers.containsKey('X-Api-Key'), isFalse);
    });
  });

  group('ng image cache', () {
    test('keeps at most maxParts parts, least recently used first; a repeat read is served from the cache', () async {
      final api = FakeNg([page([])]);
      final cache = NgImageCache(api, maxParts: 3);
      for (var i = 1; i <= 3; i++) {
        await cache.get(i, '/u$i');
      }
      await cache.get(1, '/u1'); // 1 is used again: 2 is now the oldest
      expect(api.imageReads, 3);
      await cache.get(4, '/u4');
      expect(cache.parts, 3);
      await cache.get(1, '/u1');
      expect(api.imageReads, 4); // still cached
      await cache.get(2, '/u2');
      expect(api.imageReads, 5); // was dropped, read again
    });

    test('drop forgets a part; a failed read is not kept', () async {
      final api = FakeNg([page([])]);
      final cache = NgImageCache(api);
      await cache.get(1, '/u');
      cache.drop(1);
      await cache.get(1, '/u');
      expect(api.imageReads, 2);
      final bad = _FailingImages();
      final c2 = NgImageCache(bad);
      await expectLater(c2.get(5, '/u'), throwsA(isA<NgApiException>()));
      await Future<void>.delayed(Duration.zero);
      bad.fail = false;
      await c2.get(5, '/u');
      expect(bad.imageReads, 2);
    });

    testWidgets('a decided part loses its images, and one that comes back after a failure keeps them', (t) async {
      final api = FakeNg([page([2, 1])]);
      await t.pumpWidget(MaterialApp(
        theme: pcTheme(Brightness.dark),
        home: NgReviewScreen(api: api, feedback: PcFeedback(PcLook())),
      ));
      await t.pump();
      await t.pump();
      expect(api.imageReads, 1);
      api.reviewError = NgApiException('x', 0);
      await t.tap(find.byKey(const ValueKey('ng-confirmed')));
      await t.pump();
      await t.pump();
      expect(api.imageReads, 1); // the card came back: no second read
      api.reviewError = null;
      await t.tap(find.byKey(const ValueKey('ng-confirmed')));
      await t.pump();
      await t.pump();
      expect(api.imageReads, 2); // part 1 loaded; part 2's images were dropped
    });
  });

  group('ng review screen', () {
    Future<void> open(WidgetTester t, FakeNg api, {String? line, int? partId}) async {
      await t.pumpWidget(MaterialApp(
        theme: pcTheme(Brightness.dark),
        home: NgReviewScreen(api: api, feedback: PcFeedback(PcLook()), line: line, pageSize: 4, partId: partId),
      ));
      await t.pump();
      await t.pump();
    }

    testWidgets('a typed note goes with the verdict and the field is empty for the next card', (t) async {
      final api = FakeNg([page([2, 1])]);
      await open(t, api);
      await t.enterText(find.byKey(const ValueKey('ng-note')), '  label torn  ');
      await t.tap(find.byKey(const ValueKey('ng-overruled')));
      await t.pump();
      expect(api.reviews.single, (2, NgVerdict.overruled));
      expect(api.notes.single, 'label torn');
      expect(t.widget<TextField>(find.byKey(const ValueKey('ng-note'))).controller!.text, isEmpty);
      await t.tap(find.byKey(const ValueKey('ng-confirmed')));
      await t.pump();
      expect(api.notes.last, '');
    });

    testWidgets('a verdict that fails gives the note back with the card', (t) async {
      final api = FakeNg([page([1])])..reviewError = NgApiException('The plant server answered 500.', 500);
      await open(t, api);
      await t.enterText(find.byKey(const ValueKey('ng-note')), 'blurred');
      await t.tap(find.byKey(const ValueKey('ng-confirmed')));
      await t.pump();
      expect(t.widget<TextField>(find.byKey(const ValueKey('ng-note'))).controller!.text, 'blurred');
    });

    testWidgets('a reload that brings another part on top empties the note; the note counts code points', (t) async {
      final api = FakeNg([page([2, 1]), page([1])]);
      await open(t, api);
      await t.enterText(find.byKey(const ValueKey('ng-note')), 'for part 2');
      await t.tap(find.byTooltip('Reload the queue'));
      await t.pump();
      await t.pump();
      expect(t.widget<TextField>(find.byKey(const ValueKey('ng-note'))).controller!.text, isEmpty);
      // 👍🏽 is one letter on screen but two code points: 250 of them reach the server's 500, 251 do not.
      await t.enterText(find.byKey(const ValueKey('ng-note')), '👍🏽' * 251);
      expect(t.widget<TextField>(find.byKey(const ValueKey('ng-note'))).controller!.text, isEmpty);
      await t.enterText(find.byKey(const ValueKey('ng-note')), '👍🏽' * 250);
      expect(t.widget<TextField>(find.byKey(const ValueKey('ng-note'))).controller!.text.runes.length, 500);
    });

    testWidgets('without a sign-in there is no note field', (t) async {
      await open(t, FakeNg([page([1])], signedIn: false));
      expect(find.byKey(const ValueKey('ng-note')), findsNothing);
    });

    testWidgets('one part by id shows its verdict and note; a new verdict is saved', (t) async {
      final api = FakeNg([page([])]);
      api.parts[9] = NgPart.fromJson({...part(9), 'verdict': 'unsure', 'reviewed_by': 'ravi', 'reviewed_at': 1760000000000, 'note': 'check the lot'})!;
      await open(t, api, partId: 9);
      expect(find.text('NG part'), findsOneWidget);
      expect(find.textContaining('Verdict: unsure by ravi'), findsOneWidget);
      expect(find.text('Note: check the lot'), findsOneWidget);
      expect(api.calls, isEmpty); // the queue is not read
      await t.enterText(find.byKey(const ValueKey('ng-note')), 'lot is fine');
      await t.tap(find.byKey(const ValueKey('ng-overruled')));
      await t.pump();
      expect((api.reviews.single, api.notes.single), ((9, NgVerdict.overruled), 'lot is fine'));
      expect(find.text('Verdict saved.'), findsOneWidget);
    });

    testWidgets('a part the server does not have says so', (t) async {
      await open(t, FakeNg([page([])]), partId: 4);
      expect(find.text('This part is not on the plant server.'), findsOneWidget);
    });

    testWidgets('the card shows reasons, station, camera, job and the image; three labelled buttons', (t) async {
      await open(t, FakeNg([page([3, 2, 1])]));
      expect(find.textContaining('No code'), findsOneWidget);
      expect(find.textContaining('S1 · cam1'), findsOneWidget);
      expect(find.textContaining('Carton-30'), findsOneWidget);
      expect(find.byType(Image), findsOneWidget);
      for (final w in ['Confirm NG', 'Good part', 'Unsure']) {
        expect(find.widgetWithText(OutlinedButton, w), findsOneWidget);
      }
      expect(find.textContaining('3 waiting'), findsOneWidget);
    });

    testWidgets('each button gives its verdict and the next card shows', (t) async {
      final api = FakeNg([page([3, 2, 1])]);
      await open(t, api);
      await t.tap(find.byKey(const ValueKey('ng-confirmed')));
      await t.pump();
      await t.pump();
      expect(find.text('Part part-2'), findsOneWidget);
      await t.tap(find.byKey(const ValueKey('ng-overruled')));
      await t.pump();
      await t.pump();
      await t.tap(find.byKey(const ValueKey('ng-unsure')));
      await t.pump();
      await t.pump();
      expect(api.reviews, [(3, NgVerdict.confirmed), (2, NgVerdict.overruled), (1, NgVerdict.unsure)]);
      expect(find.text('Nothing is waiting for review.'), findsOneWidget);
    });

    testWidgets('swipe right confirms, left overrules, up is unsure; a short drag does nothing', (t) async {
      final api = FakeNg([page([4, 3, 2, 1])]);
      await open(t, api);
      Future<void> swipe(Offset by) async {
        await t.drag(find.textContaining('Part part-'), by);
        await t.pump();
        await t.pump();
      }

      await swipe(const Offset(30, 0));
      expect(api.reviews, isEmpty);
      await swipe(const Offset(250, 0));
      await swipe(const Offset(-250, 0));
      await swipe(const Offset(0, -250));
      expect(api.reviews, [(4, NgVerdict.confirmed), (3, NgVerdict.overruled), (2, NgVerdict.unsure)]);
      // A mostly sideways drag that also rises is a sideways swipe, not "unsure".
      await swipe(const Offset(250, -120));
      expect(api.reviews.last, (1, NgVerdict.confirmed));
    });

    testWidgets('a failed verdict puts the card back and says why; a 403 says the role may not', (t) async {
      final api = FakeNg([page([2, 1])])..reviewError = NgApiException('You may not give a verdict for this line. Only an admin or a plant, production or quality manager can.', 403);
      await open(t, api);
      await t.tap(find.byKey(const ValueKey('ng-confirmed')));
      await t.pump();
      await t.pump();
      expect(find.textContaining('may not give a verdict'), findsOneWidget);
      expect(find.text('Part part-2'), findsOneWidget); // back on top
      expect(api.reviews, isEmpty);
      // Retrying after the cause is gone works and the note goes away.
      api.reviewError = null;
      await t.tap(find.byKey(const ValueKey('ng-confirmed')));
      await t.pump();
      await t.pump();
      expect(find.textContaining('may not give a verdict'), findsNothing);
      expect(api.reviews, [(2, NgVerdict.confirmed)]);
    });

    testWidgets('no connection: the card comes back and the verdict is not saved', (t) async {
      final api = FakeNg([page([1])])..reviewError = NgApiException('Cannot reach the plant server.', 0);
      await open(t, api);
      await t.tap(find.byKey(const ValueKey('ng-unsure')));
      await t.pump();
      await t.pump();
      expect(find.textContaining('The verdict was not saved'), findsOneWidget);
      expect(find.text('Part part-1'), findsOneWidget);
    });

    testWidgets('a part the server no longer holds is dropped, with the reason', (t) async {
      final api = FakeNg([page([2, 1])])..reviewError = NgApiException('This part is no longer waiting for review.', 404);
      await open(t, api);
      await t.tap(find.byKey(const ValueKey('ng-confirmed')));
      await t.pump();
      await t.pump();
      expect(find.textContaining('no longer waiting'), findsOneWidget);
      expect(find.text('Part part-1'), findsOneWidget);
    });

    testWidgets('the next page is read with before_id when few cards are left', (t) async {
      final api = FakeNg([page([9, 8, 7, 6], 6), page([5, 4], null)]);
      await open(t, api);
      expect(api.calls, [null]);
      await t.tap(find.byKey(const ValueKey('ng-confirmed'))); // 3 left
      await t.pump();
      await t.pump();
      await t.pump();
      expect(api.calls, [null, 6]);
      expect(find.textContaining('5 waiting'), findsOneWidget);
    });

    testWidgets('a part not yet on the server shows its image state instead of a picture', (t) async {
      final api = FakeNg([
        NgPage([NgPart.fromJson(part(1, state: 'waiting', images: []))!, NgPart.fromJson(part(2, state: 'missing', images: []))!, NgPart.fromJson(part(3, state: 'expired', images: []))!, NgPart.fromJson(part(4, state: 'none', images: []))!], null),
      ]);
      await open(t, api);
      for (final words in ['not on the server yet', 'no longer has', 'never sent', 'No image was kept']) {
        expect(find.textContaining(words), findsOneWidget);
        expect(find.byType(Image), findsNothing);
        await t.tap(find.byKey(const ValueKey('ng-confirmed')));
        await t.pump();
        await t.pump();
      }
      expect(api.imageReads, 0);
    });

    testWidgets('an unreachable server says so and, once something was shown, how old it is', (t) async {
      final api = _Flaky();
      await open(t, api);
      api.down = true;
      await t.tap(find.byTooltip('Reload the queue'));
      await t.pump();
      await t.pump();
      expect(find.textContaining('Showing the queue as of'), findsOneWidget);
    });

    testWidgets('an empty queue says nothing is waiting', (t) async {
      await open(t, FakeNg([page([])]));
      expect(find.text('Nothing is waiting for review.'), findsOneWidget);
    });

    testWidgets('large text and a narrow phone: the card and buttons do not overflow', (t) async {
      t.view.physicalSize = const Size(320, 640);
      t.view.devicePixelRatio = 1;
      addTearDown(t.view.reset);
      await t.pumpWidget(MaterialApp(
        theme: pcTheme(Brightness.light),
        builder: (c, w) => MediaQuery(data: MediaQuery.of(c).copyWith(textScaler: const TextScaler.linear(1.6)), child: w!),
        home: NgReviewScreen(api: FakeNg([page([1])]), feedback: PcFeedback(PcLook())),
      ));
      await t.pump();
      await t.pump();
      expect(t.takeException(), isNull);
    });
  });
}

class _Flaky extends FakeNg {
  _Flaky() : super([NgPage([NgPart.fromJson(part(1))!], null)]);
  bool down = false;
  @override
  Future<NgPage> pending({String? plant, String? line, int? beforeId, int limit = 50}) async {
    if (down) throw NgApiException('Cannot reach the plant server.', 0);
    return super.pending(plant: plant, line: line, beforeId: beforeId, limit: limit);
  }
}

class _FailingImages extends FakeNg {
  _FailingImages() : super([NgPage([], null)]);
  bool fail = true;
  @override
  Future<Uint8List> image(String url) async {
    imageReads++;
    if (fail) throw NgApiException('no', 0);
    return _png;
  }
}
