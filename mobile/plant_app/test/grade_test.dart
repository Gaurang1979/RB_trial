import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:plant_app/modules/vision/grade_api.dart';
import 'package:plant_app/modules/vision/grade_screen.dart';
import 'package:plant_core/plant_core.dart';

Map<String, Object?> series({
  String state = 'ok',
  double lowest = 2.0,
  String from = 'rule',
  int ungraded = 0,
  bool drift = true,
}) => {
  'plant': 'P1',
  'line': 'Line 1',
  'station': 'S1',
  'tool': 'Read code',
  'buckets': [
    {'start': 1000, 'samples': 10, 'mean': 3.5, 'worst': 3.0, 'ungraded': 0},
    {
      'start': 2000,
      'samples': 10,
      'mean': 2.4,
      'worst': 1.0,
      'ungraded': ungraded,
    },
  ],
  'drift': drift
      ? {
          'state': state,
          'samples': 20,
          'lowest': lowest,
          'lowest_from': from,
          'mean': 2.4,
          'slope': -0.05,
        }
      : null,
};

Map<String, Object?> body(List<Map<String, Object?>> s) => {
  'since': 0,
  'until': 3000,
  'bucket_s': 3600,
  'rule': {'lowest_grade': 2.0},
  'series': s,
};

GradeApi apiFor(http.Client c) =>
    GradeApi(c, Uri.parse('https://plant.local'), 'key');

Widget app(GradeApi api) => MaterialApp(
  theme: ThemeData(extensions: [PcColors.dark]),
  home: GradeScreen(api: api, feedback: PcFeedback(PcLook())),
);

void main() {
  test('the trend reads buckets, the lowest grade and the drift status', () {
    final t = GradeTrend.fromJson(
      body([series(state: 'drift', lowest: 3, from: 'station', ungraded: 2)]),
    );
    final s = t.series.single;
    expect((s.parts, s.ungraded, s.buckets.length), (20, 2, 2));
    expect(
      (s.drift!.state, s.drift!.lowest, s.drift!.lowestFromStation),
      ('drift', 3.0, true),
    );
    expect(t.lowestGrade, 2.0);
    expect(gradeLetter(3.9), 'B');
    expect(gradeLetter(-1), 'F');
  });

  test('the request carries the period, the line and the key', () async {
    Uri? seen;
    Map<String, String>? hdr;
    final api = apiFor(
      MockClient((r) async {
        seen = r.url;
        hdr = r.headers;
        return http.Response(jsonEncode(body([])), 200);
      }),
    );
    await api.trend(
      plant: 'P1',
      line: 'L1',
      span: const Duration(hours: 1),
      bucketSeconds: 60,
      now: DateTime.fromMillisecondsSinceEpoch(7200000),
    );
    expect(seen!.path, '/vision/grades/trend');
    expect(seen!.queryParameters, {
      'since': '3600000',
      'until': '7200000',
      'bucket_s': '60',
      'plant': 'P1',
      'line': 'L1',
    });
    expect(hdr!.values, contains('key'));
  });

  test('a refused, failed or unexpected answer is one plain message', () async {
    Future<GradeApiException> failing(http.Response r) async {
      try {
        await apiFor(MockClient((_) async => r)).trend();
      } on GradeApiException catch (e) {
        return e;
      }
      fail('expected an exception');
    }

    expect(
      (await failing(http.Response('', 403))).message,
      contains('cannot see'),
    );
    expect((await failing(http.Response('', 500))).message, contains('500'));
    expect(
      (await failing(http.Response('[1]', 200))).message,
      contains('unexpected'),
    );
    expect(
      (await failing(http.Response('nope', 200))).message,
      contains('unexpected'),
    );
    final down = apiFor(MockClient((_) async => throw Exception('down')));
    await expectLater(
      down.trend(),
      throwsA(isA<GradeApiException>().having((e) => e.code, 'code', 0)),
    );
  });

  testWidgets(
    'the screen shows the drift status, the lowest grade and the ungraded parts',
    (tester) async {
      await tester.pumpWidget(
        app(
          apiFor(
            MockClient(
              (_) async => http.Response(
                jsonEncode(body([series(state: 'drift', ungraded: 3)])),
                200,
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('Line 1 · S1 · Read code'), findsOneWidget);
      expect(
        find.textContaining('Drifting to the lowest grade'),
        findsOneWidget,
      );
      expect(
        find.textContaining('Lowest grade C (2, the plant rule)'),
        findsOneWidget,
      );
      expect(find.textContaining('3 not gradable'), findsOneWidget);
      expect(
        find.byWidgetPredicate(
          (w) =>
              w is Semantics &&
              w.properties.label == 'Grade trend of Read code',
        ),
        findsOneWidget,
      );
    },
  );

  testWidgets('no graded parts and a server error say so and draw no chart', (
    tester,
  ) async {
    await tester.pumpWidget(
      app(
        apiFor(
          MockClient((_) async => http.Response(jsonEncode(body([])), 200)),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('No graded parts in this period.'), findsOneWidget);

    await tester.pumpWidget(const SizedBox());
    await tester.pumpWidget(
      app(apiFor(MockClient((_) async => http.Response('', 500)))),
    );
    await tester.pumpAndSettle();
    expect(find.textContaining('answered 500'), findsOneWidget);
    expect(
      find.byWidgetPredicate(
        (w) =>
            w is Semantics &&
            (w.properties.label ?? '').startsWith('Grade trend'),
      ),
      findsNothing,
    );
  });

  testWidgets('a tool with no recent graded part is shown as too few parts', (
    tester,
  ) async {
    await tester.pumpWidget(
      app(
        apiFor(
          MockClient(
            (_) async =>
                http.Response(jsonEncode(body([series(drift: false)])), 200),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.textContaining('Too few graded parts yet'), findsOneWidget);
  });
}
